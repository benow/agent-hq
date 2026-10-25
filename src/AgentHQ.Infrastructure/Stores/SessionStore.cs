using Dapper;
using AgentHQ.Core.Domain;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>Mirrors provider session metadata. Messages are read through, never mirrored.</summary>
public sealed class SessionStore
{
    private readonly AgentHqDb _db;

    public SessionStore(AgentHqDb db) => _db = db;

    public async Task UpsertAsync(SessionInfo s, string? repositoryId, string? excerpt)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO sessions (provider, external_id, repository_id, title, task_type, last_outcome,
                  external_time_created, external_time_updated, first_user_message_excerpt, archived, deleted, ingested_at)
              VALUES (@Provider, @ExternalId, @RepositoryId, @Title, @TaskType, @LastOutcome,
                  @Created, @Updated, @Excerpt, @Archived, @Deleted, @IngestedAt)
              ON CONFLICT(provider, external_id) DO UPDATE SET
                  repository_id = COALESCE(excluded.repository_id, sessions.repository_id),
                  title = excluded.title, task_type = excluded.task_type,
                  last_outcome = excluded.last_outcome,
                  external_time_created = excluded.external_time_created,
                  external_time_updated = excluded.external_time_updated,
                  first_user_message_excerpt = COALESCE(excluded.first_user_message_excerpt, sessions.first_user_message_excerpt),
                  archived = excluded.archived, deleted = excluded.deleted, ingested_at = excluded.ingested_at",
            new
            {
                Provider = "zcode",
                ExternalId = s.Id,
                RepositoryId = repositoryId,
                Title = s.Title,
                TaskType = s.TaskType,
                LastOutcome = s.LastOutcome,
                Created = s.TimeCreated.ToUnixTimeMilliseconds(),
                Updated = s.TimeUpdated.ToUnixTimeMilliseconds(),
                Excerpt = excerpt,
                Archived = s.Archived ? 1 : 0,
                Deleted = s.Deleted ? 1 : 0,
                IngestedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
    }

    public async Task<IReadOnlyList<SessionRow>> ListAsync(string? repoId = null, DateTimeOffset? updatedSince = null, string? status = null)
    {
        await using var conn = _db.Open();
        var sql = "SELECT " + Columns.Session + " FROM sessions WHERE provider = 'zcode'";
        if (repoId is not null) sql += " AND repository_id = @repoId";
        if (updatedSince is not null) sql += " AND external_time_updated >= @since";
        sql += " ORDER BY external_time_updated DESC LIMIT 500";
        var rows = (await conn.QueryAsync<SessionRow>(sql,
            new { repoId, since = updatedSince?.ToUnixTimeMilliseconds() })).ToList();
        if (status is not null)
        {
            var now = DateTimeOffset.UtcNow;
            rows = rows.Where(r => DeriveStatus(r, now) == status).ToList();
        }
        return rows;
    }

    public static string DeriveStatus(SessionRow r, DateTimeOffset now)
    {
        if (r.DeletedBool) return "deleted";
        if (r.ArchivedBool) return "archived";
        return now - r.Updated <= TimeSpan.FromMinutes(30) ? "active" : "idle";
    }

    public async Task<SessionRow?> GetAsync(string externalId)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<SessionRow>(
            "SELECT " + Columns.Session + " FROM sessions WHERE provider = 'zcode' AND external_id = @externalId", new { externalId });
    }

    public async Task<DateTimeOffset> MaxExternalUpdatedAsync()
    {
        await using var conn = _db.Open();
        var max = await conn.ExecuteScalarAsync<long?>(
            "SELECT MAX(external_time_updated) FROM sessions WHERE provider = 'zcode'");
        return max is null ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeMilliseconds(max.Value);
    }
}
