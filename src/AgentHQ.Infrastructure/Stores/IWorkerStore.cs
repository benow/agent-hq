using AgentHQ.Core.Events;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>One spoke event as it arrives: origin sequence, the spoke's own type, the
/// enriched payload (already carries workerId/originSeq/actor/transportedBy), timestamp.</summary>
public sealed record IncomingEvent(long OriginSeq, string Type, string PayloadJson, long At);

public enum BatchOutcome { Applied, Trimmed, Rejected }

public sealed record BatchApplyResult(
    BatchOutcome Outcome,
    int AppliedCount,
    long GlobalSeqFrom,
    long NextExpected,
    /// <summary>The rows applied (post-trim), with their assigned global seqs — the service
    /// fans them out to live subscribers after the commit.</summary>
    IReadOnlyList<AgentHqEvent> Applied);

public sealed record SnapshotOutcome(
    bool RepoConflict,
    string? ConflictWorkerId,
    long NextExpected,
    long LastAcceptedSeq);

/// <summary>
/// Workers, per-worker projections, the command queue and pairing nonces (part 3,
/// distributed-agent-hq). Provider-neutral; SQLite (spoke/rollback topology) and PostgreSQL
/// (hub) implementations. The atomicity here IS contract rule 2: a batch's event rows and the
/// worker's cursor commit together, or an ack-lost resend would double-apply.
/// </summary>
public interface IWorkerStore
{
    // ---- workers ----
    Task<WorkerRow?> GetByIdAsync(string id);
    Task<WorkerRow?> GetByIdentityAsync(string identityId);
    Task<WorkerRow> CreateAsync(string identityId);
    Task UpdateRegistrationAsync(string id, string name, string machine, string version, string capabilitiesJson);
    Task TouchHeartbeatAsync(string id, string version, long at);
    Task<IReadOnlyList<WorkerRow>> ListAsync();

    // ---- repository ownership ----
    Task<IReadOnlyList<WorkerRepoRow>> ListReposAsync(string workerId);
    /// <summary>Replaces this worker's claims. Returns the owning workerId when a repo is
    /// already claimed by a different worker (ownership is exclusive, rule 5); null on success.</summary>
    Task<string?> SetReposAsync(string workerId, IReadOnlyList<WorkerRepoRow> repos);

    // ---- event ingest (rule 2 + rule 3, atomic) ----
    Task<BatchApplyResult> ApplyEventBatchAsync(string workerId, long fromOriginSeq,
        IReadOnlyList<IncomingEvent> events);

    // ---- snapshot (rule 5, atomic replace + cursor + merged tail) ----
    Task<SnapshotOutcome> ApplySnapshotAsync(string workerId,
        IReadOnlyList<WorkerRepoRow> repos,
        IReadOnlyList<WorkerPlanRow> plans,
        IReadOnlyList<WorkerHistoryRow> history,
        IReadOnlyList<WorkerSessionRow> sessions,
        IReadOnlyList<WorkerLessonRow> lessons,
        IReadOnlyList<IncomingEvent> eventTail);
    Task<int> CountProjectionRowsAsync(string workerId);

    // ---- command queue (rule 4) ----
    Task<WorkerCommandRow> InsertCommandAsync(WorkerCommandRow row);
    Task<WorkerCommandRow?> GetCommandAsync(string id);
    /// <summary>Claims up to <paramref name="max"/> queued (un-leased, no result) envelopes
    /// and sets the lease; returns them with LeaseUntil set.</summary>
    Task<IReadOnlyList<WorkerCommandRow>> ClaimAsync(string workerId, long now, long leaseUntil, int max);
    /// <summary>Un-finished envelopes a claim would return right now: un-leased or past
    /// <paramref name="now"/>. Must share ClaimAsync's predicate or a crashed lease reads as zero.</summary>
    Task<int> PendingCountAsync(string workerId, long now);
    /// <summary>Records the first result; false when a result already exists (idempotent by
    /// commandId, rule 4 — the caller warns on a divergent hash instead of re-applying).</summary>
    Task<bool> TrySaveResultAsync(string id, bool ok, string? error, string resultHash, long finishedAt);
    /// <summary>The worker that owns <paramref name="repoName"/> (command routing, rule 7);
    /// null when no worker claims the repository.</summary>
    Task<string?> RouteByRepoAsync(string repoName);

    // ---- pairing (rule 10) ----
    Task InsertPairingAsync(WorkerPairingRow row);
    Task<WorkerPairingRow?> GetPairingAsync(string nonce);
    Task UpdatePairingAsync(WorkerPairingRow row);

    // ---- hub state (key/value; the release.version cursor lives here) ----
    Task<string?> GetStateAsync(string key);
    Task SetStateAsync(string key, string value);
}
