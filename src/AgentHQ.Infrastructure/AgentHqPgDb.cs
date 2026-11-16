using System.Data.Common;
using System.Reflection;
using DbUp;
using DbUp.Postgresql;
using Npgsql;

namespace AgentHQ.Infrastructure;

/// <summary>
/// The hub's PostgreSQL database (container mode on builds): DbUp migrations from
/// <c>Migrations.Pg</c> (its own journal, distinct from the spoke's SQLite set) + connection
/// factory. Configure with <c>AGENTHQ_DB_PROVIDER=postgres</c> and <c>AGENTHQ_PG_CONNSTR</c>.
/// </summary>
public sealed class AgentHqPgDb : IAgentHqConnectionFactory
{
    private readonly string _connectionString;
    private readonly Serilog.ILogger _logger;

    public AgentHqPgDb(string connectionString, Serilog.ILogger? logger = null)
    {
        _connectionString = connectionString;
        _logger = logger ?? Serilog.Log.ForContext<AgentHqPgDb>();
    }

    public NpgsqlConnection Open()
    {
        var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    async Task<DbConnection> IAgentHqConnectionFactory.OpenAsync() => await OpenAsync();

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public void Migrate(string? startupLogPath = null)
    {
        // Only the Migrations.Pg scripts: the assembly also embeds the spoke's SQLite set, and
        // running SQLite DDL against Postgres would fail half-applied. Journaled by filename,
        // same convention as the SQLite side.
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.Pg.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var name = n[(n.IndexOf(".Migrations.Pg.", StringComparison.Ordinal) + ".Migrations.Pg.".Length)..];
                return new DbUp.Engine.SqlScript(name, reader.ReadToEnd());
            })
            .ToList();

        var up = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScripts(scripts)
            .JournalToPostgresqlTable("public", "AgentHqPgSchemaVersions")
            .LogTo(startupLogPath is null
                ? new DbUp.Engine.Output.ConsoleUpgradeLog()
                : new AgentHqDb.FileUpgradeLog(startupLogPath))
            .Build();

        if (!up.IsUpgradeRequired()) return;
        var result = up.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Hub Pg migration failed: " + result.Error.Message, result.Error);
        _logger.Information("Hub Pg database migrated");
    }
}
