using System.Text.Json;
using Dapper;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.PgStores;

/// <summary>PostgreSQL (hub on builds) worker store — deliberate port, not shared SQL. The
/// event-batch and snapshot methods run their writes in one transaction: the cursor and the
/// rows commit together, which is what makes contract rule 2's ack-lost resend a trimmed
/// duplicate instead of a double application.</summary>
public sealed class WorkerStorePg : IWorkerStore
{
    private readonly AgentHqPgDb _db;

    public WorkerStorePg(AgentHqPgDb db) => _db = db;

    public async Task<WorkerRow?> GetByIdAsync(string id)
    {
        await using var conn = await _db.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers WHERE id = @id", new { id });
    }

    public async Task<WorkerRow?> GetByIdentityAsync(string identityId)
    {
        await using var conn = await _db.OpenAsync();
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
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            "INSERT INTO workers (id, identity_id, registered_at) VALUES (@Id, @IdentityId, @RegisteredAt)",
            new { row.Id, row.IdentityId, row.RegisteredAt });
        return row;
    }

    public async Task UpdateRegistrationAsync(string id, string name, string machine, string version,
        string capabilitiesJson)
    {
        await using var conn = await _db.OpenAsync();
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
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE workers SET last_heartbeat_at = @at, version = @version WHERE id = @id",
            new { id, version, at });
    }

    public async Task<IReadOnlyList<WorkerRow>> ListAsync()
    {
        await using var conn = await _db.OpenAsync();
        return (await conn.QueryAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers ORDER BY registered_at")).ToList();
    }

    // ---- repository ownership ----

    public async Task<IReadOnlyList<WorkerRepoRow>> ListReposAsync(string workerId)
    {
        await using var conn = await _db.OpenAsync();
        return (await conn.QueryAsync<WorkerRepoRow>(
            "SELECT " + Columns.WorkerRepo + " FROM worker_repos WHERE worker_id = @workerId ORDER BY repo_name",
            new { workerId })).ToList();
    }

    public async Task SetReposAsync(string workerId, IReadOnlyList<WorkerRepoRow> repos)
    {
        await using var conn = await _db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("DELETE FROM worker_repos WHERE worker_id = @workerId", new { workerId }, tx);
        foreach (var r in repos)
            await conn.ExecuteAsync(
                "INSERT INTO worker_repos (worker_id, repo_name, root_path) VALUES (@WorkerId, @RepoName, @RootPath)",
                r, tx);
        await tx.CommitAsync();
    }

    // ---- event ingest ----

    public async Task<BatchApplyResult> ApplyEventBatchAsync(string workerId, long fromOriginSeq,
        IReadOnlyList<IncomingEvent> events)
    {
        await using var conn = await _db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var worker = await conn.QuerySingleOrDefaultAsync<WorkerRow>(
            "SELECT " + Columns.Worker + " FROM workers WHERE id = @workerId", new { workerId }, tx);
        if (worker is null) throw new InvalidOperationException($"unknown worker {workerId}");

        var nextExpected = worker.NextExpectedSeq;
        if (fromOriginSeq > nextExpected)
        {
            await tx.CommitAsync();
            return new BatchApplyResult(BatchOutcome.Rejected, 0, 0, nextExpected, Array.Empty<AgentHqEvent>());
        }

        var toApply = events.Where(e => e.OriginSeq >= nextExpected)
            .OrderBy(e => e.OriginSeq).ToList();

        var applied = new List<AgentHqEvent>(toApply.Count);
        long globalSeqFrom = 0, lastOrigin = nextExpected - 1;
        foreach (var e in toApply)
        {
            var seq = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at) RETURNING seq",
                new { type = e.Type, payload = e.PayloadJson, at = e.At }, tx);
            if (applied.Count == 0) globalSeqFrom = seq;
            applied.Add(new AgentHqEvent(seq, e.Type, JsonSerializer.Deserialize<JsonElement>(e.PayloadJson),
                DateTimeOffset.FromUnixTimeMilliseconds(e.At)));
            lastOrigin = e.OriginSeq;
        }

        var newNextExpected = lastOrigin + 1;
        var newLastAccepted = applied.Count > 0 ? applied[^1].Seq : worker.LastAcceptedSeq;
        await conn.ExecuteAsync(
            "UPDATE workers SET next_expected_seq = @nextExpected, last_accepted_seq = @lastAccepted WHERE id = @workerId",
            new { nextExpected = newNextExpected, lastAccepted = newLastAccepted, workerId }, tx);
        await tx.CommitAsync();
        var outcome = fromOriginSeq < worker.NextExpectedSeq ? BatchOutcome.Trimmed : BatchOutcome.Applied;
        return new BatchApplyResult(outcome, applied.Count, globalSeqFrom, newNextExpected, applied);
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
        await using var conn = await _db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var repo in repos)
        {
            var owner = await conn.QuerySingleOrDefaultAsync<WorkerRepoRow>(
                "SELECT " + Columns.WorkerRepo + " FROM worker_repos WHERE repo_name = @repo",
                new { repo = repo.RepoName }, tx);
            if (owner is not null && owner.WorkerId != workerId)
            {
                await tx.RollbackAsync();
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

        long lastGlobal = 0, lastOrigin = 0;
        foreach (var e in eventTail.OrderBy(e => e.OriginSeq))
        {
            lastGlobal = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at) RETURNING seq",
                new { type = e.Type, payload = e.PayloadJson, at = e.At }, tx);
            lastOrigin = e.OriginSeq;
        }

        var nowUnused = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await conn.ExecuteAsync(
            """
            UPDATE workers SET next_expected_seq = @nextExpected, last_accepted_seq = @lastAccepted
            WHERE id = @workerId
            """,
            new { nextExpected = lastOrigin + 1, lastAccepted = lastGlobal, workerId }, tx);
        await tx.CommitAsync();
        return new SnapshotOutcome(false, null, lastOrigin + 1, lastGlobal);
    }

    public async Task<int> CountProjectionRowsAsync(string workerId)
    {
        await using var conn = await _db.OpenAsync();
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
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO worker_commands (id, worker_id, kind, repo, actor, payload, issued_at)
            VALUES (@Id, @WorkerId, @Kind, @Repo, @Actor, @Payload, @IssuedAt)
            """, row);
        return row;
    }

    public async Task<WorkerCommandRow?> GetCommandAsync(string id)
    {
        await using var conn = await _db.OpenAsync();
        return await conn.QuerySingleOrDefaultAsync<WorkerCommandRow>(
            "SELECT " + Columns.WorkerCommand + " FROM worker_commands WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<WorkerCommandRow>> ClaimAsync(string workerId, long now, long leaseUntil, int max)
    {
        await using var conn = await _db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        var ids = (await conn.QueryAsync<string>(
            """
            SELECT id FROM worker_commands
            WHERE worker_id = @workerId AND lease_until IS NULL AND result_ok IS NULL
            ORDER BY issued_at LIMIT @max
            """, new { workerId, max }, tx)).ToList();
        foreach (var id in ids)
            await conn.ExecuteAsync(
                "UPDATE worker_commands SET lease_until = @leaseUntil WHERE id = @id",
                new { leaseUntil, id }, tx);
        var claimed = new List<WorkerCommandRow>();
        if (ids.Count > 0)
        {
            // Named-parameter expansion: Dapper's automatic IN expansion emits positional
            // syntax Npgsql rejects (Infrastructure AGENTS.md).
            var expand = string.Join(", ", ids.Select((_, i) => $"@id{i}"));
            var parameters = new DynamicParameters();
            for (var i = 0; i < ids.Count; i++) parameters.Add($"id{i}", ids[i]);
            claimed = (await conn.QueryAsync<WorkerCommandRow>(
                $"SELECT {Columns.WorkerCommand} FROM worker_commands WHERE id IN ({expand})",
                parameters, tx)).ToList();
        }
        await tx.CommitAsync();
        return claimed;
    }

    public async Task<int> PendingCountAsync(string workerId)
    {
        await using var conn = await _db.OpenAsync();
        var n = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(1) FROM worker_commands WHERE worker_id = @workerId AND lease_until IS NULL AND result_ok IS NULL",
            new { workerId });
        return (int)n;
    }

    public async Task<bool> TrySaveResultAsync(string id, bool ok, string? error, string resultHash, long finishedAt)
    {
        await using var conn = await _db.OpenAsync();
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
        await using var conn = await _db.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT worker_id FROM worker_repos WHERE repo_name = @repoName", new { repoName });
    }

    // ---- pairing ----

    public async Task InsertPairingAsync(WorkerPairingRow row)
    {
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO worker_pairing (nonce, created_at, expires_at, state)
            VALUES (@Nonce, @CreatedAt, @ExpiresAt, @State)
            """, row);
    }

    public async Task<WorkerPairingRow?> GetPairingAsync(string nonce)
    {
        await using var conn = await _db.OpenAsync();
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
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE worker_pairing SET state = @State, identity_id = @IdentityId,
            token_raw = @TokenRaw, token_id = @TokenId WHERE nonce = @Nonce
            """, row);
    }

    // ---- hub state ----

    public async Task<string?> GetStateAsync(string key)
    {
        await using var conn = await _db.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT value FROM hub_state WHERE key = @key", new { key });
    }

    public async Task SetStateAsync(string key, string value)
    {
        await using var conn = await _db.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO hub_state (key, value) VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE SET value = @value
            """, new { key, value });
    }
}
