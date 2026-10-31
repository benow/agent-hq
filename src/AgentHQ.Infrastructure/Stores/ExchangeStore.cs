using Dapper;
using System.Data.Common;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>
/// Exchange rows: items, presence, cursors and the rate-limit ledger. Dapper with explicit
/// parameters; state moves only through UPDATEs guarded by the state they move from, so two
/// concurrent claimants cannot both win (the second updates nothing and is refused).
/// </summary>
public sealed class ExchangeStore
{
    private readonly AgentHqDb _db;

    public ExchangeStore(AgentHqDb db) => _db = db;

    public async Task<ExchangeItemRow> InsertAsync(ExchangeItemRow row, DbTransaction? tx = null)
    {
        await using var conn = tx is null ? _db.Open() : null;
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
        await using var conn = tx is null ? _db.Open() : null;
        var c = tx?.Connection ?? conn!;
        return await c.QuerySingleOrDefaultAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + " FROM exchange_items WHERE id = @Id", new { Id = id }, tx);
    }

    /// <summary>The original item of a repeat post, in the caller's transaction.</summary>
    public async Task<ExchangeItemRow?> FindByIdempotencyAsync(ExchangeActorKey sender, string key,
        DbTransaction tx)
    {
        return await tx.Connection!.QuerySingleOrDefaultAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
              WHERE sender_actor_type = @T AND sender_actor_id = @I AND idempotency_key = @K",
            new { T = sender.Type, I = sender.Id, K = key }, tx);
    }

    public sealed record ExchangeActorKey(string Type, string Id);

    /// <summary>Items in a thread, oldest first.</summary>
    public async Task<IReadOnlyList<ExchangeItemRow>> ThreadAsync(string threadId)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<ExchangeItemRow>(
            "SELECT " + Columns.ExchangeItem + " FROM exchange_items WHERE thread_id = @T ORDER BY created_at, id",
            new { T = threadId })).ToList();
    }

    /// <summary>
    /// A consumer's inbox: items addressed to the exact actor, or role-addressed items in one of
    /// the given repositories. <paramref name="states"/> empty means every state.
    /// </summary>
    public async Task<IReadOnlyList<ExchangeItemRow>> InboxAsync(ExchangeActorKey actor,
        IReadOnlyList<string>? repositoryIds, IReadOnlyList<string>? states, string? kinds, int limit)
    {
        await using var conn = _db.Open();
        var sql = "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
            WHERE (recipient_actor_type = @T AND recipient_actor_id = @I)
               OR (recipient_actor_type = 'zcode' AND recipient_actor_id = '*' AND repository_id IN @Repos)";
        if (states is { Count: > 0 }) sql += " AND state IN @States";
        if (kinds is not null) sql += " AND kind = @Kinds";
        sql += " ORDER BY created_at, id LIMIT @Limit";
        return (await conn.QueryAsync<ExchangeItemRow>(sql, new
        {
            T = actor.Type,
            I = actor.Id,
            Repos = repositoryIds is { Count: > 0 } ? repositoryIds.ToArray() : new[] { "\u0000none" },
            States = states?.ToArray(),
            Kinds = kinds,
            Limit = limit <= 0 ? 500 : limit
        })).ToList();
    }

    /// <summary>The rows a digest is built from for one ZCode session in one repository.</summary>
    public async Task<IReadOnlyList<ExchangeItemRow>> DigestItemsAsync(string sessionId,
        string repositoryId, int limit = 200)
    {
        await using var conn = _db.Open();
        // Items are included when they address the session, when they are role-addressed in the
        // session's repository, or when they hang off a thread one of those roots belongs to —
        // the answer to a session's question reaches it even though the answer names the asker.
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

    /// <summary>Open questions: queued or claimed, optionally limited to one repository.</summary>
    public async Task<IReadOnlyList<ExchangeItemRow>> OpenQuestionsAsync(string? repositoryId)
    {
        await using var conn = _db.Open();
        var sql = "SELECT " + Columns.ExchangeItem + @" FROM exchange_items
            WHERE kind = 'question' AND state IN ('queued','claimed')";
        if (repositoryId is not null) sql += " AND repository_id = @Repo";
        sql += " ORDER BY created_at, id";
        return (await conn.QueryAsync<ExchangeItemRow>(sql, new { Repo = repositoryId })).ToList();
    }

    /// <summary>Non-acknowledged items whose expiry has passed.</summary>
    public async Task<IReadOnlyList<ExchangeItemRow>> DueForExpiryAsync(long now, int limit = 500)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<ExchangeItemRow>(
            @"SELECT " + Columns.ExchangeItem + @" FROM exchange_items
              WHERE expires_at IS NOT NULL AND expires_at <= @Now
                AND state IN ('queued','claimed','delivered','answered')
              ORDER BY expires_at LIMIT @Limit", new { Now = now, Limit = limit })).ToList();
    }

    /// <summary>
    /// Moves an item from <paramref name="fromState"/> to <paramref name="toState"/>, setting the
    /// extra columns of that edge (claimed_at, answered_at, …). Returns false when the row was
    /// not in <paramref name="fromState"/> any more — the caller lost a race and must re-read.
    /// </summary>
    public async Task<bool> TransitionAsync(string id, string fromState, string toState,
        string? claimedBy, long at, bool claim = false, bool answered = false, bool acknowledged = false,
        bool killed = false, DbTransaction? tx = null)
    {
        await using var conn = tx is null ? _db.Open() : null;
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
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"UPDATE exchange_items SET blob_path = @Path, blob_size = @Size,
                  blob_content_type = @ContentType, blob_sha256 = @Sha256 WHERE id = @Id",
            new { Id = id, Path = path, Size = size, ContentType = contentType, Sha256 = sha256 });
    }

    // ---- presence ----

    public async Task UpsertPresenceAsync(ExchangeActorKey actor, string latencyClass, long now)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO exchange_presence (actor_type, actor_id, last_seen_at, latency_class)
              VALUES (@T, @I, @Now, @Latency)
              ON CONFLICT(actor_type, actor_id) DO UPDATE SET last_seen_at = @Now, latency_class = @Latency",
            new { T = actor.Type, I = actor.Id, Now = now, Latency = latencyClass });
    }

    public async Task<IReadOnlyList<ExchangePresenceRow>> PresenceAsync()
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<ExchangePresenceRow>(
            "SELECT " + Columns.ExchangePresence + " FROM exchange_presence ORDER BY actor_type, actor_id")).ToList();
    }

    // ---- cursors ----

    public async Task<ExchangeCursorRow?> GetCursorAsync(ExchangeActorKey actor)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<ExchangeCursorRow>(
            "SELECT " + Columns.ExchangeCursor + " FROM exchange_cursors WHERE actor_type = @T AND actor_id = @I",
            new { T = actor.Type, I = actor.Id });
    }

    /// <summary>Advances a consumer's cursor; a cursor never moves backwards.</summary>
    public async Task SetCursorAsync(ExchangeActorKey actor, long seq, long now)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            @"INSERT INTO exchange_cursors (actor_type, actor_id, last_seq, updated_at)
              VALUES (@T, @I, @Seq, @Now)
              ON CONFLICT(actor_type, actor_id) DO UPDATE SET
                  last_seq = MAX(last_seq, @Seq), updated_at = @Now",
            new { T = actor.Type, I = actor.Id, Seq = seq, Now = now });
    }

    /// <summary>Clears a consumer's cursor (a request to replay from the start).</summary>
    public async Task ClearCursorAsync(ExchangeActorKey actor)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "DELETE FROM exchange_cursors WHERE actor_type = @T AND actor_id = @I",
            new { T = actor.Type, I = actor.Id });
    }

    // ---- rate ledger ----

    /// <summary>How many posts this sender made at or after <paramref name="since"/>.</summary>
    public async Task<int> CountRecentPostsAsync(ExchangeActorKey sender, long since, DbTransaction tx)
    {
        return await tx.Connection!.ExecuteScalarAsync<int>(
            @"SELECT COUNT(1) FROM exchange_rate
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

    /// <summary>The oldest post at or after <paramref name="since"/> (when the rate window frees up).</summary>
    public async Task<long?> OldestRecentPostAsync(ExchangeActorKey sender, long since, DbTransaction tx)
    {
        return await tx.Connection!.ExecuteScalarAsync<long?>(
            @"SELECT MIN(at) FROM exchange_rate
              WHERE sender_actor_type = @T AND sender_actor_id = @I AND at >= @Since",
            new { T = sender.Type, I = sender.Id, Since = since }, tx);
    }
}
