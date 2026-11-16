using AgentHQ.Core.Domain;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>Mirrored provider session metadata, implemented for SQLite and PostgreSQL.
/// (DeriveStatus is a static on <see cref="SessionStore"/> — provider-independent.)</summary>
public interface ISessionStore
{
    Task UpsertAsync(SessionInfo s, string? repositoryId, string? excerpt);
    Task<IReadOnlyList<SessionRow>> ListAsync(string? repoId = null, DateTimeOffset? updatedSince = null,
        string? status = null);
    Task<SessionRow?> GetAsync(string externalId);
    Task<DateTimeOffset> MaxExternalUpdatedAsync();
}
