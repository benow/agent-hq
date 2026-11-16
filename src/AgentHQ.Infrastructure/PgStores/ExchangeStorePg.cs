using System.Data.Common;
using Dapper;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.PgStores;

/// <summary>Exchange rows on PostgreSQL. Deliberate dialect ports from the SQLite store:
/// <c>GREATEST</c> replaces the two-argument scalar <c>MAX</c>, the rate count casts to
/// <c>int</c> (COUNT is BIGINT in PostgreSQL and the interface promises <c>int</c>), and the
/// <c>ON CONFLICT</c> upserts are native.</summary>
public sealed class ExchangeStorePg(AgentHqPgDb db) : IExchangeStore
{
    public async Task<ExchangeItemRow> InsertAsync(ExchangeItemRow row, DbTransaction? tx = null)
    {
        await using var conn = tx is null ? await db.OpenAsync() : null;
        var c = tx?.Connection ?? conn!;
        await c.ExecuteAsync(
            @"INSERT INTO exchange_items (id, kind, state, latency_class, thread_id, hop_count,
                  sender_actor_type, sender_actor_id, recipient_actor_type, recipient_actor_id,
                  repository_id, subject, body, context_refs, purpose, claimed_by_session_id, claimed_at,
                  created_at, expires_at, answered_at, acknowledged_at, killed_at, idempotency_key,
                  blob_path, blob_size, blob_content_type, blob_sha256)
              VALUES (@Id, @Kind, @State, @LatencyClass, @ThreadId, @HopCount,
                  @SenderActorType, @SenderActorId, @RecipientActorType, @RecipientActorId,
                  @RepositoryId, @Subject, @Body, @ContextRefs, @Purpose, @ClaimedBySessionId, @ClaimedAt,
                  @CreatedAt, @ExpiresAt, @AnsweredAt, @AcknowledgedAt, @KilledAt, @IdempotencyKey,
                  @BlobPath, @BlobSize, @BlobContentType, @BlobSha256)",
            new
            {
                row.Id, row.Kind, row.State, row.LatencyClass, row.ThreadId, row.HopCount,
                row.SenderActorType, row.SenderActorId, row.RecipientActorType, row.RecipientActorId,
                row.RepositoryId, row.Subject, row.Body, row.ContextRefs, row.Purpose,
                row.ClaimedBySessionId, row.ClaimedAt, row.CreatedAt, row.ExpiresAt,
                row.AnsweredAt, row.AcknowledgedAt, row.KilledAt, row.IdempotencyKey,
                row.BlobPath, row.BlobSize, row.BlobContentType, row.BlobSha256
            }, tx);
        return row;
    }

    public async Task<ExchangeItemRow?> GetAsync(string id, DbTransaction? tx = null)
    {
        await using var conn = tx is null ? await db.OpenAsync() : null;
        var c = tx?.Connection ?? conn!;
        return await c.QuerySingleOrDefaultAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + " FROM exchange_items WHERE id = @Id", new { Id = id }, tx);
    }

    public async Task<ExchangeItemRow?> FindByIdempotencyAsync(ExchangeActorKey sender, string key, DbTransaction tx)
    {
        return await tx.Connection!.QuerySingleOrDefaultAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
              WHERE sender_actor_type = @T AND sender_actor_id = @I AND idempotency_key = @K",
            new { T = sender.Type, I = sender.Id, K = key }, tx);
    }

    public async Task<IReadOnlyList<ExchangeItemRow>> ThreadAsync(string threadId)
    {
        await using var conn = await db.OpenAsync();
        return (await conn.QueryAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + " FROM exchange_items WHERE thread_id = @T ORDER BY created_at, id",
            new { T = threadId })).ToList();
    }

    public async Task<IReadOnlyList<ExchangeItemRow>> InboxAsync(ExchangeActorKey actor,
        IReadOnlyList<string>? repositoryIds, IReadOnlyList<string>? states, string? kinds, int limit)
    {
        await using var conn = await db.OpenAsync();
        // Dapper's automatic list expansion (IN @list) emits positional syntax Npgsql rejects,
        // so every list filter expands explicitly.
        var p = new DynamicParameters(new { T = actor.Type, I = actor.Id });
        var sql = "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
            WHERE (recipient_actor_type = @T AND recipient_actor_id = @I)";
        if (repositoryIds is { Count: > 0 })
        {
            var names = new List<string>(repositoryIds.Count);
            for (var i = 0; i < repositoryIds.Count; i++)
            {
                var name = "repo" + i;
                names.Add("@" + name);
                p.Add(name, repositoryIds[i]);
            }
            sql += " OR (recipient_actor_type = 'zcode' AND recipient_actor_id = '*' AND repository_id IN (" +
                   string.Join(", ", names) + "))";
        }
        if (states is { Count: > 0 })
        {
            var names = new List<string>(states.Count);
            for (var i = 0; i < states.Count; i++)
            {
                var name = "state" + i;
                names.Add("@" + name);
                p.Add(name, states[i]);
            }
            sql += " AND state IN (" + string.Join(", ", names) + ")";
        }
        if (kinds is not null)
        {
            p.Add("Kinds", kinds);
            sql += " AND kind = @Kinds";
        }
        p.Add("Limit", limit <= 0 ? 500 : limit);
        sql += " ORDER BY created_at, id LIMIT @Limit";
        return (await conn.QueryAsync<ExchangeItemRow>(sql, p)).ToList();
    }

    public async Task<IReadOnlyList<ExchangeItemRow>> DigestItemsAsync(string sessionId,
        string repositoryId, int limit = 200)
    {
        await using var conn = await db.OpenAsync();
        return (await conn.QueryAsync<ExchangeItemRow>(
            @"WITH roots AS (
                  SELECT id FROM exchange_items
                  WHERE (recipient_actor_type = 'zcode' AND recipient_actor_id = @I)
                     OR (recipient_actor_type = 'zcode' AND recipient_actor_id = '*' AND repository_id = @Repo)
              )
              SELECT " + Columns.WithPrefix(Columns.ExchangeItem, "i.") + @" FROM exchange_items i
              WHERE (i.recipient_actor_type = 'zcode' AND i.recipient_actor_id = @I)
                 OR (i.recipient_actor_type = 'zcode' AND i.recipient_actor_id = '*' AND i.repository_id = @Repo)
                 OR (i.kind = 'answer' AND i.thread_id IN (SELECT id FROM roots))
              ORDER BY i.created_at, i.id LIMIT @Limit",
            new { I = sessionId, Repo = repositoryId, Limit = limit })).ToList();
    }

    public async Task<IReadOnlyList<ExchangeItemRow>> OpenQuestionsAsync(string? repositoryId)
    {
        await using var conn = await db.OpenAsync();
        var sql = "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
            WHERE kind = 'question' AND state IN ('queued','claimed')";
        if (repositoryId is not null) sql += " AND repository_id = @Repo";
        sql += " ORDER BY created_at, id";
        return (await conn.QueryAsync<ExchangeItemRow>(sql, new { Repo = repositoryId })).ToList();
    }

    public async Task<IReadOnlyList<ExchangeItemRow>> DueForExpiryAsync(long now, int limit = 500)
    {
        await using var conn = await db.OpenAsync();
        return (await conn.QueryAsync<ExchangeItemRow>(
            @"SELECT " + Columns.ExchangeItem + @" FROM exchange_items
              WHERE expires_at IS NOT NULL AND expires_at <= @Now
                AND state IN ('queued','claimed','delivered','answered')
              ORDER BY expires_at LIMIT @Limit", new { Now = now, Limit = limit })).ToList();
    }

    public async Task<bool> TransitionAsync(string id, string fromState, string toState,
        string? claimedBy, long at, bool claim = false, bool answered = false, bool acknowledged = false,
        bool killed = false, DbTransaction? tx = null)
    {
        await using var conn = tx is null ? await db.OpenAsync() : null;
        var c = tx?.Connection ?? conn!;
        var updated = await c.ExecuteAsync(
            @"UPDATE exchange_items SET state = @To,
                  claimed_by_session_id = CASE WHEN @Claim THEN @ClaimedBy ELSE claimed_by_session_id END,
                  claimed_at = CASE WHEN @Claim THEN @At ELSE claimed_at END,
                  answered_at = CASE WHEN @Answered THEN @At ELSE answered_at END,
                  acknowledged_at = CASE WHEN @Acked THEN @At ELSE acknowledged_at END,
                  killed_at = CASE WHEN @Killed THEN @At ELSE killed_at END
              WHERE id = @Id AND state = @From",
            new { Id = id, From = fromState, To = toState, Claim = claim, ClaimedBy = claimedBy,
                  At = at, Answered = answered, Acked = acknowledged, Killed = killed }, tx);
        return updated > 0;
    }

    public async Task SetBlobAsync(string id, string path, long size, string contentType, string sha256)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            @"UPDATE exchange_items SET blob_path = @Path, blob_size = @Size,
                  blob_content_type = @ContentType, blob_sha256 = @Sha256 WHERE id = @Id",
            new { Id = id, Path = path, Size = size, ContentType = contentType, Sha256 = sha256 });
    }

    // ---- presence ----

    public async Task UpsertPresenceAsync(ExchangeActorKey actor, string latencyClass, long now)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO exchange_presence (actor_type, actor_id, last_seen_at, latency_class)
              VALUES (@T, @I, @Now, @Latency)
              ON CONFLICT(actor_type, actor_id) DO UPDATE SET last_seen_at = @Now, latency_class = @Latency",
            new { T = actor.Type, I = actor.Id, Now = now, Latency = latencyClass });
    }

    public async Task<IReadOnlyList<ExchangePresenceRow>> PresenceAsync()
    {
        await using var conn = await db.OpenAsync();
        return (await conn.QueryAsync<ExchangePresenceRow>(
            "SELECT " + Columns.ExchangePresence + " FROM exchange_presence ORDER BY actor_type, actor_id")).ToList();
    }

    // ---- cursors ----

    public async Task<ExchangeCursorRow?> GetCursorAsync(ExchangeActorKey actor)
    {
        await using var conn = await db.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<ExchangeCursorRow>(
            "SELECT " + Columns.ExchangeCursor + " FROM exchange_cursors WHERE actor_type = @T AND actor_id = @I",
            new { T = actor.Type, I = actor.Id });
    }

    public async Task SetCursorAsync(ExchangeActorKey actor, long seq, long now)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            @"INSERT INTO exchange_cursors (actor_type, actor_id, last_seq, updated_at)
              VALUES (@T, @I, @Seq, @Now)
              ON CONFLICT(actor_type, actor_id) DO UPDATE SET
                  last_seq = GREATEST(last_seq, @Seq), updated_at = @Now",
            new { T = actor.Type, I = actor.Id, Seq = seq, Now = now });
    }

    public async Task ClearCursorAsync(ExchangeActorKey actor)
    {
        await using var conn = await db.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM exchange_cursors WHERE actor_type = @T AND actor_id = @I",
            new { T = actor.Type, I = actor.Id });
    }

    // ---- rate ledger ----

    public async Task<int> CountRecentPostsAsync(ExchangeActorKey sender, long since, DbTransaction tx)
    {
        return await tx.Connection!.ExecuteScalarAsync<int>(
            @"SELECT COUNT(1)::int FROM exchange_rate
              WHERE sender_actor_type = @T AND sender_actor_id = @I AND at >= @Since",
            new { T = sender.Type, I = sender.Id, Since = since }, tx);
    }

    public async Task RecordPostAsync(ExchangeActorKey sender, long at, long pruneBefore, DbTransaction tx)
    {
        await tx.Connection!.ExecuteAsync(
            @"INSERT INTO exchange_rate (sender_actor_type, sender_actor_id, at) VALUES (@T, @I, @At);
              DELETE FROM exchange_rate WHERE at < @Prune",
            new { T = sender.Type, I = sender.Id, At = at, Prune = pruneBefore }, tx);
    }

    public async Task<long?> OldestRecentPostAsync(ExchangeActorKey sender, long since, DbTransaction tx)
    {
        return await tx.Connection!.ExecuteScalarAsync<long?>(
            @"SELECT MIN(at) FROM exchange_rate
              WHERE sender_actor_type = @T AND sender_actor_id = @I AND at >= @Since",
            new { T = sender.Type, I = sender.Id, Since = since }, tx);
    }
}
