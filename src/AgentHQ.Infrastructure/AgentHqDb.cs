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

    public void Migrate()
    {
        var up = DeployChanges.To
            .SqliteDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogToConsole()
            .Build();

        if (!up.IsUpgradeRequired()) return;
        var result = up.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Bridge db migration failed: " + result.Error.Message, result.Error);
        _logger.Information("Bridge db migrated");
    }
}
