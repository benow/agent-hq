using Microsoft.Data.Sqlite;

namespace AgentHQ.Infrastructure.Watching;

/// <summary>
/// Watches the ZCode db directory for changes to db.sqlite/-wal/-shm, debounces ~500 ms,
/// then checks PRAGMA data_version on a dedicated long-lived connection (data_version is
/// per-connection; a fresh connection would reset the check — this connection is never
/// pooled). A one-minute timer is the backstop for dropped inotify events.
/// </summary>
public sealed class ZCodeDbWatcher : IDisposable
{
    private readonly string _dbPath;
    private readonly Func<Task> _onChange;
    private readonly Serilog.ILogger _log;
    private readonly TimeSpan _debounce;
    private readonly int _dataVersionCheckMs;
    private FileSystemWatcher? _fsw;
    private SqliteConnection? _probe;
    private CancellationTokenSource? _cts;
    private int _lastDataVersion = -1;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ZCodeDbWatcher(string dbPath, Func<Task> onChange, TimeSpan? debounce = null,
        int? dataVersionCheckMs = null, Serilog.ILogger? logger = null)
    {
        _dbPath = dbPath;
        _onChange = onChange;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _dataVersionCheckMs = dataVersionCheckMs ?? 60_000;
        _log = (logger ?? Serilog.Log.ForContext<ZCodeDbWatcher>()).ForContext("Subsystem", "watcher");
    }

    public void Start()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        if (!Directory.Exists(dir) || !File.Exists(_dbPath))
        {
            _log.Warning("ZCode db {Db} is not present; watcher not started (sessions come from the mirror only)", _dbPath);
            return;
        }

        _probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        _probe.Open();
        _lastDataVersion = ReadDataVersion(_probe);
        _lastCheckUtc = DateTime.UtcNow;

        _fsw = new FileSystemWatcher(dir)
        {
            Filter = "db.sqlite*",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _fsw.Changed += (_, _) => _ = DebouncedAsync();
        _fsw.Created += (_, _) => _ = DebouncedAsync();
        _fsw.Renamed += (_, _) => _ = DebouncedAsync();

        _cts = new CancellationTokenSource();
        _ = BackstopLoopAsync(_cts.Token);
        _log.Information("ZCode db watcher started on {Dir}", dir);
    }

    private DateTime _nextDebounceUtc = DateTime.MinValue;
    private async Task DebouncedAsync()
    {
        var until = DateTime.UtcNow + _debounce;
        if (until > _nextDebounceUtc) _nextDebounceUtc = until;
        await Task.Delay(_debounce);
        if (DateTime.UtcNow < _nextDebounceUtc) return;
        await CheckAndNotifyAsync();
    }

    private async Task BackstopLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_dataVersionCheckMs, ct);
                await CheckAndNotifyAsync();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.Debug(ex, "backstop check failed"); }
        }
    }

    /// <summary>Runs the data_version probe; invokes the callback when the db changed.</summary>
    public async Task CheckAndNotifyAsync()
    {
        if (_probe is null) return;
        await _gate.WaitAsync();
        try
        {
            var current = ReadDataVersion(_probe);
            _lastCheckUtc = DateTime.UtcNow;
            if (current != _lastDataVersion)
            {
                _lastDataVersion = current;
                _log.Debug("zcode db data_version changed to {Version}", current);
                await _onChange();
            }
        }
        finally { _gate.Release(); }
    }

    private static int ReadDataVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA data_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _fsw?.Dispose();
        _probe?.Dispose();
        _gate.Dispose();
    }
}
