using System.Data.Common;

namespace AgentHQ.Infrastructure;

/// <summary>
/// Provider-neutral access to the hub's own database: connection factory + migration.
/// Implemented by <see cref="AgentHqDb"/> (SQLite — spoke, and the default hub on the laptop)
/// and <see cref="AgentHqPgDb"/> (PostgreSQL — the hub on builds). Services never see the
/// provider; the SQL itself stays in the per-provider store implementations.
/// </summary>
public interface IAgentHqConnectionFactory
{
    Task<DbConnection> OpenAsync();
    void Migrate(string? startupLogPath = null);
}
