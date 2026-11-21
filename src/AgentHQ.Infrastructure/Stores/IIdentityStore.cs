using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>
/// Identities, hashed tokens and the auth audit trail (part 2, distributed-agent-hq).
/// Provider-neutral; each dialect has its own implementation (SQLite spoke / PostgreSQL hub).
/// The audit trail is append-only: logins, failures, denials — no updates, no deletes.
/// </summary>
public interface IIdentityStore
{
    Task<IdentityRow?> GetByUsernameAsync(string username);
    Task<IdentityRow?> GetAsync(string id);
    Task<IdentityRow> CreateAsync(string username, string role, string passwordHash);
    Task<IReadOnlyList<IdentityRow>> ListAsync();

    /// <summary>Deletes the identity (never its audit rows) and revokes every token it holds.</summary>
    Task<bool> DeleteAsync(string username);

    Task<AuthTokenRow> CreateTokenAsync(string identityId, string tokenHash, string? label);

    /// <summary>Resolves an unrevoked token to its identity; null for unknown or revoked.</summary>
    Task<(IdentityRow Identity, AuthTokenRow Token)?> AuthenticateAsync(string tokenHash);

    Task<bool> RevokeTokenAsync(string tokenId);
    Task<int> RevokeAllTokensAsync(string identityId);

    Task AppendAuditAsync(string kind, string? username, string? route, string? source, string? detail);
    Task<IReadOnlyList<AuthAuditRow>> AuditAsync(int take = 100);
}
