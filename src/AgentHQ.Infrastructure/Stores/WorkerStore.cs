using System.Text.Json;
using Dapper;
using AgentHQ.Core.Events;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>SQLite (spoke / rollback-topology) worker store. The event-batch and snapshot
/// methods run their writes in one transaction — the cursor and the rows commit together,
/// which is what makes contract rule 2's ack-lost resend a trimmed duplicate instead of a
/// double application.</summary>
public sealed class WorkerStore : IWorkerStore
{
    private readonly AgentHqDb _db;

    public WorkerStore(AgentHqDb db) => _db = db;

    public async Task<WorkerRow?> GetByIdAsync(string id)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers WHERE id = @id", new { id });
    }

    public async Task<WorkerRow?> GetByIdentityAsync(string identityId)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers WHERE identity_id = @identityId", new { identityId });
    }

    public async Task<WorkerRow> CreateAsync(string identityId)
    {
        var row = new WorkerRow
        {
            Id = Guid.NewGuid().ToString(),
            IdentityId = identityId,
            RegisteredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "INSERT INTO workers (id, identity_id, registered_at) VALUES (@Id, @IdentityId, @RegisteredAt)",
            new { row.Id, row.IdentityId, row.RegisteredAt });
        return row;
    }

    public async Task UpdateRegistrationAsync(string id, string name, string machine, string version,
        string capabilitiesJson)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            UPDATE workers SET name = @name, machine = @machine, version = @version,
            capabilities = @capabilities, registered_at = @registeredAt WHERE id = @id
            """,
            new { id, name, machine, version, capabilities = capabilitiesJson,
                registeredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    }

    public async Task TouchHeartbeatAsync(string id, string version, long at)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            "UPDATE workers SET last_heartbeat_at = @at, version = @version WHERE id = @id",
            new { id, version, at });
    }

    public async Task<IReadOnlyList<WorkerRow>> ListAsync()
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers ORDER BY registered_at")).ToList();
    }

    // ---- repository ownership ----

    public async Task<IReadOnlyList<WorkerRepoRow>> ListReposAsync(string workerId)
    {
        await using var conn = _db.Open();
        return (await conn.QueryAsync<WorkerRepoRow>(
            "SELECT " + Columns.WorkerRepo + " FROM worker_repos WHERE worker_id = @workerId ORDER BY repo_name",
            new { workerId })).ToList();
    }

    public async Task<string?> SetReposAsync(string workerId, IReadOnlyList<WorkerRepoRow> repos)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();
        // Ownership is exclusive on registration too, not only on snapshot (rule 5): a second
        // worker claiming an owned repository gets the conflict, never a UNIQUE violation.
        foreach (var r in repos)
        {
            var owner = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT worker_id FROM worker_repos WHERE repo_name = @repo",
                new { repo = r.RepoName }, tx);
            if (owner is not null && owner != workerId)
            {
                tx.Rollback();
                return owner;
            }
        }
        await conn.ExecuteAsync("DELETE FROM worker_repos WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var r in repos)
            await conn.ExecuteAsync(
                "INSERT INTO worker_repos (worker_id, repo_name, root_path) VALUES (@WorkerId, @RepoName, @RootPath)",
                r, tx);
        tx.Commit();
        return null;
    }

    // ---- event ingest ----

    /// <summary>Claims the event's (worker_id, origin_seq) key; false when it was already
    /// applied. The key IS rule 2's uniqueness, so a concurrent batch for the same worker —
    /// which can read the same cursor — still applies each originSeq once.</summary>
    private static async Task<bool> ClaimOriginKeyAsync(System.Data.IDbConnection conn, string workerId,
        long originSeq, System.Data.IDbTransaction tx) =>
        await conn.ExecuteAsync(
            "INSERT INTO worker_event_keys (worker_id, origin_seq) VALUES (@workerId, @originSeq) " +
            "ON CONFLICT DO NOTHING",
            new { workerId, originSeq }, tx) == 1;

    public async Task<BatchApplyResult> ApplyEventBatchAsync(string workerId, long fromOriginSeq,
        IReadOnlyList<IncomingEvent> events)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();
        var worker = await conn.QuerySingleOrDefaultAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers WHERE id = @workerId", new { workerId }, tx);
        if (worker is null) throw new InvalidOperationException($"unknown worker {workerId}");

        var nextExpected = worker.NextExpectedSeq;
        if (fromOriginSeq > nextExpected)
        {
            tx.Commit();
            return new BatchApplyResult(BatchOutcome.Rejected, 0, 0, nextExpected, Array.Empty<AgentHqEvent>());
        }

        // Below: trim the overlapping prefix, apply the tail. Equal: apply all. Per event the
        // origin key is the final guard — a row already applied (concurrent batch, snapshot
        // tail) is skipped even if this batch's read of the cursor was stale.
        var toApply = events.Where(e => e.OriginSeq >= nextExpected)
            .OrderBy(e => e.OriginSeq).ToList();

        var applied = new List<AgentHqEvent>(toApply.Count);
        long globalSeqFrom = 0, lastOrigin = nextExpected - 1;
        foreach (var e in toApply)
        {
            lastOrigin = e.OriginSeq;
            if (!await ClaimOriginKeyAsync(conn, workerId, e.OriginSeq, tx)) continue;
            var seq = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at); SELECT last_insert_rowid();",
                new { type = e.Type, payload = e.PayloadJson, at = e.At }, tx);
            if (applied.Count == 0) globalSeqFrom = seq;
            applied.Add(new AgentHqEvent(seq, e.Type, JsonSerializer.Deserialize<JsonElement>(e.PayloadJson),
                DateTimeOffset.FromUnixTimeMilliseconds(e.At)));
        }

        // MAX, never backwards: a losing concurrent batch must not rewind the winner's cursor.
        await conn.ExecuteAsync(
            "UPDATE workers SET next_expected_seq = MAX(next_expected_seq, @nextExpected), " +
            "last_accepted_seq = MAX(last_accepted_seq, @lastAccepted) WHERE id = @workerId",
            new { nextExpected = lastOrigin + 1,
                lastAccepted = applied.Count > 0 ? applied[^1].Seq : worker.LastAcceptedSeq, workerId }, tx);
        var committed = await conn.ExecuteScalarAsync<long>(
            "SELECT next_expected_seq FROM workers WHERE id = @workerId", new { workerId }, tx);
        tx.Commit();
        var outcome = fromOriginSeq < worker.NextExpectedSeq || applied.Count < toApply.Count
            ? BatchOutcome.Trimmed
            : BatchOutcome.Applied;
        return new BatchApplyResult(outcome, applied.Count, globalSeqFrom, committed, applied);
    }

    // ---- snapshot ----

    public async Task<SnapshotOutcome> ApplySnapshotAsync(string workerId,
        IReadOnlyList<WorkerRepoRow> repos,
        IReadOnlyList<WorkerPlanRow> plans,
        IReadOnlyList<WorkerHistoryRow> history,
        IReadOnlyList<WorkerSessionRow> sessions,
        IReadOnlyList<WorkerLessonRow> lessons,
        IReadOnlyList<IncomingEvent> eventTail)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();

        // Ownership is exclusive (rule 5): name any repo claimed by another worker, inside the
        // same transaction that would write the claim, so a concurrent snapshot cannot win.
        foreach (var repo in repos)
        {
            var owner = await conn.QuerySingleOrDefaultAsync<WorkerRepoRow>(
                "SELECT " + Columns.WorkerRepo + " FROM worker_repos WHERE repo_name = @repo",
                new { repo = repo.RepoName }, tx);
            if (owner is not null && owner.WorkerId != workerId)
            {
                tx.Rollback();
                return new SnapshotOutcome(true, owner.WorkerId, 0, 0);
            }
        }

        await conn.ExecuteAsync("DELETE FROM worker_repos WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var r in repos)
            await conn.ExecuteAsync(
                "INSERT INTO worker_repos (worker_id, repo_name, root_path) VALUES (@WorkerId, @RepoName, @RootPath)",
                r, tx);

        await conn.ExecuteAsync("DELETE FROM worker_plans WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var p in plans)
            await conn.ExecuteAsync(
                """
                INSERT INTO worker_plans (worker_id, id, slug, name, repository_id, file_path, status,
                priority, is_blocked, blocked_reason, session_external_id, implemented_by_session_id,
                content_hash, checked, total, version, created_at, updated_at, markdown)
                VALUES (@WorkerId, @Id, @Slug, @Name, @RepositoryId, @FilePath, @Status, @Priority,
                @IsBlocked, @BlockedReason, @SessionExternalId, @ImplementedBySessionId, @ContentHash,
                @Checked, @Total, @Version, @CreatedAt, @UpdatedAt, @Markdown)
                """, p, tx);

        await conn.ExecuteAsync("DELETE FROM worker_history WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var h in history)
            await conn.ExecuteAsync(
                """
                INSERT INTO worker_history (worker_id, seq, plan_id, from_status, to_status, actor_type,
                actor_session_id, command, git_head, note, separation_of_duties_warning, via_shortcut, at)
                VALUES (@WorkerId, @Seq, @PlanId, @FromStatus, @ToStatus, @ActorType, @ActorSessionId,
                @Command, @GitHead, @Note, @SeparationOfDutiesWarning, @ViaShortcut, @At)
                """, h, tx);

        await conn.ExecuteAsync("DELETE FROM worker_sessions WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var s in sessions)
            await conn.ExecuteAsync(
                """
                INSERT INTO worker_sessions (worker_id, provider, external_id, repository_id, title,
                task_type, last_outcome, external_time_created, external_time_updated,
                first_user_message_excerpt, archived, deleted, ingested_at)
                VALUES (@WorkerId, @Provider, @ExternalId, @RepositoryId, @Title, @TaskType, @LastOutcome,
                @ExternalTimeCreated, @ExternalTimeUpdated, @FirstUserMessageExcerpt, @Archived, @Deleted,
                @IngestedAt)
                """, s, tx);

        await conn.ExecuteAsync("DELETE FROM worker_lessons WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var l in lessons)
            await conn.ExecuteAsync(
                """
                INSERT INTO worker_lessons (worker_id, entry_id, title, category, source_plan_id,
                content_hash, first_seen_at) VALUES (@WorkerId, @EntryId, @Title, @Category,
                @SourcePlanId, @ContentHash, @FirstSeenAt)
                """, l, tx);

        // The event tail merges into the global stream here (rule 5): the cursor lands on the
        // tail's end in the same transaction, so a later delta carrying lower originSeq trims.
        // The origin key skips a tail row that was already applied, so a re-snapshot cannot
        // double-merge the same event.
        long lastGlobal = 0, lastOrigin = 0;
        foreach (var e in eventTail.OrderBy(e => e.OriginSeq))
        {
            lastOrigin = Math.Max(lastOrigin, e.OriginSeq);
            if (!await ClaimOriginKeyAsync(conn, workerId, e.OriginSeq, tx)) continue;
            lastGlobal = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at); SELECT last_insert_rowid();",
                new { type = e.Type, payload = e.PayloadJson, at = e.At }, tx);
        }

        await conn.ExecuteAsync(
            """
            UPDATE workers SET next_expected_seq = MAX(next_expected_seq, @nextExpected),
            last_accepted_seq = MAX(last_accepted_seq, @lastAccepted)
            WHERE id = @workerId
            """,
            new { nextExpected = lastOrigin + 1, lastAccepted = lastGlobal, workerId }, tx);
        var committedCursor = await conn.ExecuteScalarAsync<long>(
            "SELECT next_expected_seq FROM workers WHERE id = @workerId", new { workerId }, tx);
        tx.Commit();
        return new SnapshotOutcome(false, null, committedCursor, lastGlobal);
    }

    public async Task<int> CountProjectionRowsAsync(string workerId)
    {
        await using var conn = _db.Open();
        var total = 0L;
        total += await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_plans WHERE worker_id = @workerId", new { workerId });
        total += await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_history WHERE worker_id = @workerId", new { workerId });
        total += await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_sessions WHERE worker_id = @workerId", new { workerId });
        total += await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_lessons WHERE worker_id = @workerId", new { workerId });
        total += await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_repos WHERE worker_id = @workerId", new { workerId });
        return (int)total;
    }

    // ---- command queue ----

    public async Task<WorkerCommandRow> InsertCommandAsync(WorkerCommandRow row)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO worker_commands (id, worker_id, kind, repo, actor, payload, issued_at)
            VALUES (@Id, @WorkerId, @Kind, @Repo, @Actor, @Payload, @IssuedAt)
            """, row);
        return row;
    }

    public async Task<WorkerCommandRow?> GetCommandAsync(string id)
    {
        await using var conn = _db.Open();
        return await conn.QuerySingleOrDefaultAsync<WorkerCommandRow>(
            "SELECT " + Columns.WorkerCommand + " FROM worker_commands WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<WorkerCommandRow>> ClaimAsync(string workerId, long now, long leaseUntil, int max)
    {
        await using var conn = _db.Open();
        await using var tx = conn.BeginTransaction();
        var ids = (await conn.QueryAsync<string>(
            """
            SELECT id FROM worker_commands
            WHERE worker_id = @workerId AND lease_until IS NULL AND result_ok IS NULL
            ORDER BY issued_at LIMIT @max
            """, new { workerId, max }, tx)).ToList();
        var claimed = new List<WorkerCommandRow>();
        foreach (var id in ids)
        {
            await conn.ExecuteAsync(
                "UPDATE worker_commands SET lease_until = @leaseUntil WHERE id = @id",
                new { leaseUntil, id }, tx);
        }
        if (ids.Count > 0)
        {
            claimed = (await conn.QueryAsync<WorkerCommandRow>(
                "SELECT " + Columns.WorkerCommand + " FROM worker_commands WHERE id IN @ids",
                new { ids }, tx)).ToList();
        }
        tx.Commit();
        return claimed;
    }

    public async Task<int> PendingCountAsync(string workerId)
    {
        await using var conn = _db.Open();
        var n = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_commands WHERE worker_id = @workerId AND lease_until IS NULL AND result_ok IS NULL",
            new { workerId });
        return (int)n;
    }

    public async Task<bool> TrySaveResultAsync(string id, bool ok, string? error, string resultHash, long finishedAt)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteAsync(
            """
            UPDATE worker_commands SET result_ok = @ok, result_error = @error, result_hash = @hash,
            finished_at = @finishedAt, lease_until = NULL
            WHERE id = @id AND result_ok IS NULL
            """,
            new { id, ok = ok ? 1L : 0L, error, hash = resultHash, finishedAt }) > 0;
    }

    public async Task<string?> RouteByRepoAsync(string repoName)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT worker_id FROM worker_repos WHERE repo_name = @repoName", new { repoName });
    }

    // ---- pairing ----

    public async Task InsertPairingAsync(WorkerPairingRow row)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO worker_pairing (nonce, created_at, expires_at, state)
            VALUES (@Nonce, @CreatedAt, @ExpiresAt, @State)
            """, row);
    }

    public async Task<WorkerPairingRow?> GetPairingAsync(string nonce)
    {
        await using var conn = _db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<WorkerPairingRow>(
            "SELECT " + Columns.WorkerPairing + " FROM worker_pairing WHERE nonce = @nonce", new { nonce });
        if (row is { State: "pending" } && row.IsExpired)
        {
            row.State = "expired";
            await conn.ExecuteAsync(
                "UPDATE worker_pairing SET state = 'expired' WHERE nonce = @nonce", new { nonce });
        }
        return row;
    }

    public async Task UpdatePairingAsync(WorkerPairingRow row)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            UPDATE worker_pairing SET state = @State, identity_id = @IdentityId,
            token_raw = @TokenRaw, token_id = @TokenId WHERE nonce = @Nonce
            """, row);
    }

    // ---- hub state ----

    public async Task<string?> GetStateAsync(string key)
    {
        await using var conn = _db.Open();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM hub_state WHERE key = @key", new { key });
    }

    public async Task SetStateAsync(string key, string value)
    {
        await using var conn = _db.Open();
        await conn.ExecuteAsync(
            """
            INSERT INTO hub_state (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value
            """, new { key, value });
    }
}
