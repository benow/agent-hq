using System.Reflection;
using DbUp;
using DbUp.Sqlite;
using Microsoft.Data.Sqlite;

namespace AgentHQ.Infrastructure;

/// <summary>Owns the bridge's SQLite database: DbUp migrations + connection factory.</summary>
public sealed class AgentHqDb
{
    private readonly string _connectionString;
    private readonly Serilog.ILogger _logger;

    public AgentHqDb(string dbPath, Serilog.ILogger? logger = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        _logger = logger ?? Serilog.Log.ForContext<AgentHqDb>();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Migrate(string? startupLogPath = null)
    {
        var up = DeployChanges.To
            .SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            // Cold-start noise: DbUp's per-script console lines go to the startup log file
            // ($AGENTHQ_DATA/logs) instead of stdout/journal, where they buried real warnings.
            .LogTo(startupLogPath is null
                ? new DbUp.Engine.Output.ConsoleUpgradeLog()
                : new FileUpgradeLog(startupLogPath))
            .Build();

        if (!up.IsUpgradeRequired()) return;
        var result = up.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Bridge db migration failed: " + result.Error.Message, result.Error);
        _logger.Information("Bridge db migrated");
    }

    /// <summary>DbUp's log, appended one line per entry to a file (never to the journal).</summary>
    private sealed class FileUpgradeLog(string path) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogInformation(string format, params object[] args) => Write("INF", format, args);
        public void LogError(string format, params object[] args) => Write("ERR", format, args);
        public void LogWarning(string format, params object[] args) => Write("WRN", format, args);
        public void LogDebug(string format, params object[] args) => Write("DBG", format, args);
        public void LogTrace(string format, params object[] args) => Write("TRC", format, args);
        public void LogError(Exception ex, string format, params object[] args) =>
            Write("ERR", format + " — " + ex.GetType().Name + ": " + ex.Message, args);

        private void Write(string level, string format, object[] args)
        {
            try
            {
                var line = args.Length == 0 ? format : string.Format(format, args);
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {line}\n");
            }
            catch (IOException)
            {
                // A log line is never worth failing a migration for.
            }
        }
    }
}
