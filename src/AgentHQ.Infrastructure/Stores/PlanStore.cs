using Dapper;
using AgentHQ.Core.Plans;

namespace AgentHQ.Infrastructure.Stores;

public sealed class PlanStore
{
    private readonly AgentHqDb _db;

    public PlanStore(AgentHqDb db) => _db = db;

    public async Task<PlanRow> InsertAsync(PlanRow plan)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO plans (id, slug, name, repository_id, file_path, status, priority, is_blocked,
                  content_hash, checked, total, version, created_at, updated_at)
              VALUES (@Id, @Slug, @Name, @RepositoryId, @FilePath, @Status, @Priority, @IsBlocked,
                  @ContentHash, @Checked, @Total, @Version, @CreatedAt, @UpdatedAt)",
            new
            {
                plan.Id, plan.Slug, plan.Name, plan.RepositoryId, plan.FilePath, plan.Status,
                plan.Priority, plan.IsBlocked, plan.ContentHash, plan.Checked, plan.Total,
                plan.Version, plan.CreatedAt, plan.UpdatedAt
            });
        return plan;
    }

    public async Task<PlanRow?> GetAsync(string id)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<PlanRow>(
            "SELECT " + Columns.Plan + " FROM plans WHERE id = @id", new { id });
    }

    public async Task<PlanRow?> GetBySlugAsync(string repositoryId, string slug)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<PlanRow>(
            "SELECT " + Columns.Plan + " FROM plans WHERE repository_id = @repositoryId AND slug = @slug",
            new { repositoryId, slug });
    }

    public async Task<IReadOnlyList<PlanRow>> ListAsync(string? repoId = null, string? status = null,
        bool? ready = null, bool? blocked = null)
    {
        await using var conn = _db.Open();
        var sql = "SELECT " + Columns.Plan + " FROM plans WHERE 1=1";
        if (repoId is not null) sql += " AND repository_id = @repoId";
        if (status is not null) sql += " AND status = @status";
        if (blocked is not null) sql += " AND is_blocked = @blockedVal";
        sql += " ORDER BY priority DESC, updated_at DESC";
        var rows = (await conn.QueryAsync<PlanRow>(sql,
            new { repoId, status, blockedVal = blocked == true ? 1 : 0 })).ToList();

        if (ready == true)
        {
            var deps = (await conn.QueryAsync<(string planId, string requires)>(
                "SELECT plan_id, requires_plan_id FROM plan_dependencies")).ToLookup(d => d.planId);
            rows = rows.Where(p =>
                    p.Status == PlanStatusCodes.FromCode("in_review").Code() &&
                    !p.IsBlockedBool &&
                    deps[p.Id].All(d =>
                        conn.ExecuteScalar<string>("SELECT status FROM plans WHERE id = @id", new { id = d.requires })
                            is "verified" or "done"))
                .ToList();
        }
        return rows;
    }

    public async Task<bool> SlugTakenAsync(string repositoryId, string slug)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT COUNT(1) FROM plans WHERE repository_id = @repositoryId AND slug = @slug",
            new { repositoryId, slug });
    }

    /// <summary>Resolves the &lt;file&gt; argument of a /plan command against a repository:
    /// slug, then repo-relative path, then absolute path.</summary>
    public async Task<IReadOnlyList<PlanRow>> ResolveFileArgAsync(RepositoryRow repo, string fileArg)
    {
        await using var conn = _db.Open();
        var results = new List<PlanRow>();

        var bySlug = await conn.QuerySingleOrDefaultAsync<PlanRow>(
            "SELECT " + Columns.Plan + " FROM plans WHERE repository_id = @repoId AND slug = @slug",
            new { repoId = repo.Id, slug = fileArg });
        if (bySlug is not null) results.Add(bySlug);

        var absCandidates = new List<string>();
        if (!Path.IsPathRooted(fileArg))
        {
            absCandidates.Add(Path.GetFullPath(Path.Combine(repo.RootPath, fileArg)));
            absCandidates.Add(Path.GetFullPath(Path.Combine(repo.RootPath, repo.PlansDir, fileArg)));
        }
        else
        {
            absCandidates.Add(Path.GetFullPath(fileArg));
        }
        foreach (var abs in absCandidates.Where(File.Exists).Distinct())
        {
            var byPath = await conn.QueryAsync<PlanRow>(
                "SELECT " + Columns.Plan + " FROM plans WHERE repository_id = @repoId AND file_path = @fp",
                new { repoId = repo.Id, fp = abs });
            results.AddRange(byPath);
        }

        return results.DistinctBy(p => p.Id).ToList();
    }

    /// <summary>
    /// Records the hash of the content the caller just wrote. Compare-and-set on the current
    /// hash: a burst of watcher events for one edit has the same hash every time, so only the
    /// first updates and bumps the version (self-writes pass externalEdit=false and never bump).
    /// Returns the row as updated (new version and progress), or null when nothing changed.
    /// </summary>
    public async Task<PlanRow?> UpdateContentAsync(string id, string contentHash, PlanProgress progress, bool externalEdit)
    {
        await using var conn = _db.Open();
        var updated = await conn.ExecuteAsync(
            @"UPDATE plans SET content_hash = @Hash, checked = @Checked, total = @Total,
                  version = version + @Bump, updated_at = @Now
              WHERE id = @Id AND content_hash != @Hash",
            new
            {
                Id = id,
                Hash = contentHash,
                Checked = progress.Checked,
                Total = progress.Total,
                Bump = externalEdit ? 1 : 0,
                Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        if (updated == 0) return null;
        return await conn.QuerySingleOrDefaultAsync<PlanRow>(
            "SELECT " + Columns.Plan + " FROM plans WHERE id = @Id", new { Id = id });
    }

    public async Task UpdateMetaAsync(string id, long? priority = null, string? sessionExternalId = null)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"UPDATE plans SET
                  priority = COALESCE(@priority, priority),
                  session_external_id = COALESCE(@sessionExternalId, session_external_id),
                  updated_at = @now
              WHERE id = @id",
            new { id, priority, sessionExternalId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task SetStatusAsync(string id, string status)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "UPDATE plans SET status = @status, updated_at = @now WHERE id = @id",
            new { id, status, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task SetBlockedAsync(string id, bool blocked, string? reason)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "UPDATE plans SET is_blocked = @blocked, blocked_reason = @reason, updated_at = @now WHERE id = @id",
            new { id, blocked = blocked ? 1 : 0, reason, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task SetImplementedByAsync(string id, string? sessionId)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "UPDATE plans SET implemented_by_session_id = @sessionId, updated_at = @now WHERE id = @id",
            new { id, sessionId, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task AddBlockAsync(string planId, string reason, string actor)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "INSERT INTO plan_blocks (plan_id, reason, blocked_at, actor) VALUES (@planId, @reason, @at, @actor)",
            new { planId, reason, actor, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task ResolveOpenBlockAsync(string planId, string? resolution)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"UPDATE plan_blocks SET unblocked_at = @at, resolution = @resolution
              WHERE plan_id = @planId AND unblocked_at IS NULL",
            new { planId, resolution, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task AddHistoryAsync(HistoryRow row)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO plan_status_history (plan_id, from_status, to_status, actor_type, actor_session_id,
                  command, git_head, note, separation_of_duties_warning, at)
              VALUES (@PlanId, @FromStatus, @ToStatus, @ActorType, @ActorSessionId, @Command, @GitHead,
                  @Note, @SodWarning, @At)",
            new
            {
                row.PlanId, row.FromStatus, row.ToStatus, row.ActorType, row.ActorSessionId,
                row.Command, row.GitHead, row.Note,
                SodWarning = row.SodWarning ? 1 : 0,
                At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
    }

    public async Task<IReadOnlyList<HistoryRow>> HistoryAsync(string planId)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<HistoryRow>(
            "SELECT " + Columns.History + " FROM plan_status_history WHERE plan_id = @planId ORDER BY seq", new { planId })).ToList();
    }

    public async Task<bool> AddDependencyAsync(string planId, string requiresPlanId)
    {
        if (planId == requiresPlanId) return false;
        await using var conn = _db.Open();
        // Cycle check: does requiresPlanId (transitively) require planId already? Adding
        // planId → requiresPlanId would then close the chain into a loop.
        var cycle = await conn.ExecuteScalarAsync<long>(
            @"WITH RECURSIVE chain(x) AS (
                  SELECT @requiresPlanId
                  UNION
                  SELECT pd.requires_plan_id FROM plan_dependencies pd JOIN chain ON pd.plan_id = chain.x
              )
              SELECT COUNT(1) FROM chain WHERE x = @planId", new { planId, requiresPlanId });
        if (cycle > 0) return false;
        var inserted = await conn.ExecuteAsync(
            "INSERT OR IGNORE INTO plan_dependencies (plan_id, requires_plan_id) VALUES (@planId, @requiresPlanId)",
            new { planId, requiresPlanId });
        return inserted > 0;
    }

    public async Task<IReadOnlyList<PlanRow>> DependenciesAsync(string planId)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<PlanRow>(
            "SELECT " + Columns.WithPrefix(Columns.Plan, "p.") + @" FROM plans p JOIN plan_dependencies d ON d.requires_plan_id = p.id
              WHERE d.plan_id = @planId", new { planId })).ToList();
    }

    public async Task UpsertLessonsAsync(string repositoryId, IReadOnlyList<AgentHQ.Core.Plans.LessonEntry> entries, string contentHash)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync("DELETE FROM lessons WHERE repository_id = @RepoId", new { RepoId = repositoryId });
        foreach (var e in entries)
        {
            await conn.ExecuteAsync(
                @"INSERT INTO lessons (repository_id, entry_id, title, category, source_plan_id, content_hash, first_seen_at)
                  VALUES (@RepoId, @EntryId, @Title, @Category, @SourcePlanId, @Hash, @Now)",
                Params(
                    ("RepoId", repositoryId), ("EntryId", e.EntryId), ("Title", e.Title),
                    ("Category", e.Category), ("SourcePlanId", e.SourcePlanId),
                    ("Hash", contentHash),
                    ("Now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        }
    }

    private static DynamicParameters Params(params (string Name, object? Value)[] pairs)
    {
        var p = new DynamicParameters();
        var sqlite = p;
        foreach (var (name, value) in pairs) sqlite.Add(name, value);
        return sqlite;
    }

    public async Task<IReadOnlyList<LessonRow>> LessonsAsync(string repositoryId)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<LessonRow>(
            "SELECT " + Columns.Lesson + " FROM lessons WHERE repository_id = @RepoId ORDER BY entry_id",
            new { RepoId = repositoryId })).ToList();
    }

}
