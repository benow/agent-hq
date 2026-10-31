using Dapper;
using AgentHQ.Core.Plans;

namespace AgentHQ.Infrastructure.Stores;

public sealed class RepositoryStore
{
    private readonly AgentHqDb _db;
    private readonly string? _worktreesRoot;

    public RepositoryStore(AgentHqDb db, string? worktreesRoot = null)
    {
        _db = db;
        _worktreesRoot = string.IsNullOrWhiteSpace(worktreesRoot) ? null : Path.GetFullPath(worktreesRoot).TrimEnd('/');
    }

    public async Task<RepositoryRow> RegisterAsync(string rootPath, string provider, string? externalId,
        bool isDefault = false, string plansDir = "docs/plans")
    {
        var id = PlanSlug.ForRootPath(rootPath);
        var full = Path.GetFullPath(rootPath);
        await using var conn = _db.Open();
        var existing = await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id });
        if (existing is not null)
        {
            if (isDefault)
                await conn.ExecuteAsync(
                    "UPDATE repositories SET is_default = (id = @id), external_id = COALESCE(@externalId, external_id) WHERE id = @id",
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

    private static async Task<RepositoryRow> GetRequiredAsync(System.Data.Common.DbConnection conn, string id)
    {
        var row = await conn.QuerySingleAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id });
        return row;
    }

    public async Task<IReadOnlyList<RepositoryRow>> ListAsync()
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories ORDER BY is_default DESC, root_path")).ToList();
    }

    public async Task<RepositoryRow?> GetAsync(string idOrRootPath)
    {
        await using var conn = _db.Open();
        var byId = await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id = idOrRootPath });
        if (byId is not null) return byId;
        var slug = PlanSlug.ForRootPath(idOrRootPath);
        return await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE id = @id", new { id = slug });
    }

    /// <summary>Resolves a repository from a working directory: the longest registered root
    /// path that is a prefix of <paramref name="cwd"/>; or, when the cwd is inside a ZCode
    /// worktree (<c>&lt;worktrees-root&gt;/&lt;repo-directory-name&gt;/&lt;worktree&gt;</c>),
    /// the registered repository whose root directory has that name. The worktree mapping is a
    /// fallback: a repository registered at a worktree path still resolves by prefix first.
    /// With two registrations sharing a directory name, the longer root path wins.</summary>
    public async Task<RepositoryRow?> ResolveByCwdAsync(string cwd)
    {
        var all = await ListAsync();
        var normalized = cwd.TrimEnd('/');  // keep leading
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
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<RepositoryRow>(
            "SELECT " + Columns.Repository + " FROM repositories WHERE is_default = 1");
    }
}
