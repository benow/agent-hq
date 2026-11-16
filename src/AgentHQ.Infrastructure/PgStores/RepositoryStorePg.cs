using AgentHQ.Infrastructure.Stores;
using Npgsql;
using Dapper;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.PgStores;

/// <summary>Repositories on PostgreSQL. Deliberate dialect ports from the SQLite store:
/// the boolean-as-integer update uses CASE (no <c>(id = @id)</c> boolean expression).</summary>
public sealed class RepositoryStorePg : IRepositoryStore
{
    private readonly AgentHqPgDb _db;
    private readonly string? _worktreesRoot;

    public RepositoryStorePg(AgentHqPgDb db, string? worktreesRoot = null)
    {
        _db = db;
        _worktreesRoot = string.IsNullOrWhiteSpace(worktreesRoot) ? null : Path.GetFullPath(worktreesRoot).TrimEnd('/');
    }

    public async Task<RepositoryRow> RegisterAsync(string rootPath, string provider, string? externalId,
        bool isDefault = false, string plansDir = "docs/plans")
    {
        var id = Core.Plans.PlanSlug.ForRootPath(rootPath);
        var full = Path.GetFullPath(rootPath);
        await using var conn = await _db.OpenAsync();
        var existing = await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id });
        if (existing is not null)
        {
            if (isDefault)
                await conn.ExecuteAsync(
                    @"UPDATE repositories SET is_default = CASE WHEN id = @id THEN 1 ELSE 0 END,
                          external_id = COALESCE(@externalId, external_id) WHERE id = @id",
                    new { id, externalId });
            return await GetRequiredAsync(conn, id);
        }
        await conn.ExecuteAsync(
            @"INSERT INTO repositories (id, root_path, provider, external_id, plans_dir, is_default, first_seen_at)
              VALUES (@id, @rootPath, @provider, @externalId, @plansDir, @isDefault, @firstSeenAt)",
            new
            {
                id,
                rootPath = full,
                provider,
                externalId,
                plansDir,
                isDefault = isDefault ? 1 : 0,
                firstSeenAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        return await GetRequiredAsync(conn, id);
    }

    private static async Task<RepositoryRow> GetRequiredAsync(NpgsqlConnection conn, string id)
    {
        var row = await conn.QuerySingleAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id });
        return row;
    }

    public async Task<IReadOnlyList<RepositoryRow>> ListAsync()
    {
        await using var conn = await _db.OpenAsync();
        return (await conn.QueryAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories ORDER BY is_default DESC, root_path")).ToList();
    }

    public async Task<RepositoryRow?> GetAsync(string idOrRootPath)
    {
        await using var conn = await _db.OpenAsync();
        var byId = await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id = idOrRootPath });
        if (byId is not null) return byId;
        var slug = Core.Plans.PlanSlug.ForRootPath(idOrRootPath);
        return await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id = slug });
    }

    /// <summary>Same resolution rule as the SQLite store: longest registered root prefix of the
    /// cwd; or, inside a ZCode worktree, the repository whose root directory shares the leaf
    /// name. Keep the normalisation identical or <c>/foo</c> will match <c>/foobar</c>.</summary>
    public async Task<RepositoryRow?> ResolveByCwdAsync(string cwd)
    {
        var all = await ListAsync();
        var normalized = cwd.TrimEnd('/');
        var direct = all
            .Where(r => normalized == r.RootPath || normalized.StartsWith(r.RootPath + "/"))
            .OrderByDescending(r => r.RootPath.Length)
            .FirstOrDefault();
        if (direct is not null || _worktreesRoot is null || !normalized.StartsWith(_worktreesRoot + "/"))
            return direct;

        var leaf = normalized[(_worktreesRoot.Length + 1)..].Split('/')[0];
        if (leaf.Length == 0) return null;
        return all
            .Where(r => Path.GetFileName(r.RootPath.TrimEnd('/')) == leaf)
            .OrderByDescending(r => r.RootPath.Length)
            .FirstOrDefault();
    }

    public async Task<RepositoryRow?> GetDefaultAsync()
    {
        await using var conn = await _db.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE is_default = 1");
    }
}
