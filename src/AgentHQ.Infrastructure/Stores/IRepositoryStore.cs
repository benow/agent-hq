using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>Registered repositories, implemented for SQLite and PostgreSQL.</summary>
public interface IRepositoryStore
{
    Task<RepositoryRow> RegisterAsync(string rootPath, string provider, string? externalId,
        bool isDefault = false, string plansDir = "docs/plans");
    Task<IReadOnlyList<RepositoryRow>> ListAsync();
    Task<RepositoryRow?> GetAsync(string idOrRootPath);
    Task<RepositoryRow?> ResolveByCwdAsync(string cwd);
    Task<RepositoryRow?> GetDefaultAsync();
}
