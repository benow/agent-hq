using System.Security.Cryptography;
using System.Text;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// Watches a repository's plans directory. On change: hash the file; a hash equal to the
/// indexed content_hash is a self-write (bridge-initiated) and is ignored; otherwise the
/// plan's version bumps, progress recomputes, lessons re-index, and a callback fires so the
/// host can emit plan.updated.
/// </summary>
public sealed class PlanDirWatcher : IDisposable
{
    private readonly RepositoryRow _repo;
    private readonly PlanStore _plans;
    private readonly Func<PlanRow, Task> _onExternalChange;
    private readonly Serilog.ILogger _log;
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _fsw;
    private DateTime _settleUntilUtc = DateTime.MinValue;

    public PlanDirWatcher(RepositoryRow repo, PlanStore plans,
        Func<PlanRow, Task> onExternalChange, TimeSpan? debounce = null, Serilog.ILogger? logger = null)
    {
        _repo = repo;
        _plans = plans;
        _onExternalChange = onExternalChange;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(500);
        _log = (logger ?? Serilog.Log.ForContext<PlanDirWatcher>()).ForContext("Subsystem", "plans");
    }

    public string PlansDirAbsolute => Path.Combine(_repo.RootPath, _repo.PlansDir);

    /// <summary>Bridge writes call this to stretch the settle window, suppressing the
    /// watcher burst they cause beyond the hash check.</summary>
    public void NoteSelfWrite() => _settleUntilUtc = DateTime.UtcNow + _debounce + TimeSpan.FromMilliseconds(200);

    public void Start()
    {
        Directory.CreateDirectory(PlansDirAbsolute);
        _fsw = new FileSystemWatcher(PlansDirAbsolute)
        {
            Filter = "*.md",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _fsw.Changed += (_, e) => _ = OnChangeAsync(e.FullPath);
        _fsw.Created += (_, e) => _ = OnChangeAsync(e.FullPath);
        _fsw.Renamed += (_, e) => _ = OnChangeAsync(e.FullPath);
        _log.Information("plan dir watcher started on {Dir}", PlansDirAbsolute);
    }

    private async Task OnChangeAsync(string fullPath)
    {
        try
        {
            await Task.Delay(_debounce);
            if (DateTime.UtcNow < _settleUntilUtc)
            {
                // Still verify via hash — suppression is hash-based, the window only limits churn.
            }
            if (!File.Exists(fullPath)) return;
            var plan = await FindPlanForFileAsync(fullPath);
            if (plan is null) return;

            var content = await File.ReadAllTextAsync(fullPath);
            var hash = ContentHash(content);
            if (hash == plan.ContentHash) return;   // self-write suppression

            // Compare-and-set inside the UPDATE: the hash and progress (and the version this
            // edit produced) come back from the same statement that wrote them, so a newer
            // row cannot be read back in their place by a concurrent writer.
            var progress = PlanDocument.Progress(content);
            var updated = await _plans.UpdateContentAsync(plan.Id, hash, progress, externalEdit: true);
            if (updated is not null) await _onExternalChange(updated);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "plan watcher change handling failed for {Path}", fullPath);
        }
    }

    private async Task<PlanRow?> FindPlanForFileAsync(string fullPath)
    {
        var all = await _plans.ListAsync(repoId: _repo.Id);
        var plan = all.FirstOrDefault(p =>
            string.Equals(p.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (plan is null)
            plan = all.FirstOrDefault(p => Path.GetFileName(p.FilePath) == Path.GetFileName(fullPath));
        return plan;
    }

    public static string ContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Dispose() => _fsw?.Dispose();
}
