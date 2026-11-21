using Dapper;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>SQLite (spoke / laptop) identity store.</summary>
public sealed class IdentityStore : IIdentityStore
{
    private readonly AgentHqDb _db;

    public IdentityStore(AgentHqDb db) => _db = db;

    public async Task<IdentityRow?> GetByUsernameAsync(string username)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<IdentityRow>(
            "SELECT " + Columns.Identity + " FROM identities WHERE username = @username",
            new { username });
    }

    public async Task<IdentityRow?> GetAsync(string id)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<IdentityRow>(
            "SELECT " + Columns.Identity + " FROM identities WHERE id = @id", new { id });
    }

    public async Task<IdentityRow> CreateAsync(string username, string role, string passwordHash)
    {
        var row = new IdentityRow
        {
            Id = Guid.NewGuid().ToString(),
            Username = username,
            Role = role,
            PasswordHash = passwordHash,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO identities (id, username, role, password_hash, created_at)
            VALUES (@Id, @Username, @Role, @PasswordHash, @CreatedAt)
            """,
            new { row.Id, row.Username, row.Role, row.PasswordHash, row.CreatedAt });
        return row;
    }

    public async Task<IReadOnlyList<IdentityRow>> ListAsync()
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<IdentityRow>(
            "SELECT " + Columns.Identity + " FROM identities ORDER BY created_at")).ToList();
    }

    public async Task<bool> DeleteAsync(string username)
    {
        await using var conn = _db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<IdentityRow>(
            "SELECT id FROM identities WHERE username = @username", new { username });
        if (row is null) return false;
        // Tokens are deleted with the identity (the FK demands it); the audit trail keeps the
        // history, per-identity revocation of *other* identities' tokens keeps the rows.
        await conn.ExecuteAsync("DELETE FROM auth_tokens WHERE identity_id = @id", new { id = row.Id });
        await conn.ExecuteAsync("DELETE FROM identities WHERE id = @id", new { id = row.Id });
        return true;
    }

    public async Task<AuthTokenRow> CreateTokenAsync(string identityId, string tokenHash, string? label)
    {
        var row = new AuthTokenRow
        {
            Id = Guid.NewGuid().ToString(),
            IdentityId = identityId,
            TokenHash = tokenHash,
            Label = label,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO auth_tokens (id, identity_id, token_hash, label, created_at)
            VALUES (@Id, @IdentityId, @TokenHash, @Label, @CreatedAt)
            """,
            new { row.Id, row.IdentityId, row.TokenHash, row.Label, row.CreatedAt });
        return row;
    }

    public async Task<(IdentityRow Identity, AuthTokenRow Token)?> AuthenticateAsync(string tokenHash)
    {
        await using var conn = _db.Open();
        var token = await conn.QuerySingleOrDefaultAsync<AuthTokenRow>(
            "SELECT " + Columns.AuthToken + " FROM auth_tokens WHERE token_hash = @tokenHash",
            new { tokenHash });
        if (token is null || token.RevokedBool) return null;
        var identity = await conn.QuerySingleOrDefaultAsync<IdentityRow>(
            "SELECT " + Columns.Identity + " FROM identities WHERE id = @id", new { id = token.IdentityId });
        return identity is null ? null : (identity, token);
    }

    public async Task<bool> RevokeTokenAsync(string tokenId)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteAsync(
            "UPDATE auth_tokens SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL",
            new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id = tokenId }) > 0;
    }

    public async Task<int> RevokeAllTokensAsync(string identityId)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteAsync(
            "UPDATE auth_tokens SET revoked_at = @now WHERE identity_id = @id AND revoked_at IS NULL",
            new { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), id = identityId });
    }

    public async Task AppendAuditAsync(string kind, string? username, string? route, string? source, string? detail)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO auth_audit (at, kind, username, route, source, detail)
            VALUES (@At, @Kind, @Username, @Route, @Source, @Detail)
            """,
            new
            {
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Kind = kind,
                Username = username,
                Route = route,
                Source = source,
                Detail = detail
            });
    }

    public async Task<IReadOnlyList<AuthAuditRow>> AuditAsync(int take = 100)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<AuthAuditRow>(
            "SELECT " + Columns.AuthAudit + " FROM auth_audit ORDER BY seq DESC LIMIT @take",
            new { take })).ToList();
    }
}
