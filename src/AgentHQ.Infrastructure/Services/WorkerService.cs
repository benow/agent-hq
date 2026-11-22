using System.Text.Json;
using AgentHQ.Client;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;
using Serilog;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// The spoke side of the v1 contract (part 3, distributed-agent-hq): registration and
/// identity binding, heartbeats/presence, event-delta ingest (the three-case rule), and
/// snapshot application. One worker record per `worker` identity, created when the identity
/// is created — re-registration refreshes the declaration and returns the same workerId.
/// REST and MCP sit on the same services; these are the ones the spoke routes call.
/// </summary>
public sealed class WorkerService
{
    private readonly IWorkerStore _workers;
    private readonly IEventStore _events;
    private readonly ILogger _logger;

    public WorkerService(IWorkerStore workers, IEventStore events, ILogger? logger = null)
    {
        _workers = workers;
        _events = events;
        _logger = logger ?? Log.ForContext<WorkerService>();
    }

    /// <summary>The identity-binding rule: a `worker`-role identity owns exactly one worker
    /// record. Called wherever a worker identity is created (pairing approval, the admin
    /// identity endpoint for tests/headless setups).</summary>
    public async Task<WorkerRow> EnsureForIdentityAsync(string identityId)
    {
        var existing = await _workers.GetByIdentityAsync(identityId);
        if (existing is not null) return existing;
        var row = await _workers.CreateAsync(identityId);
        _logger.Information("workers: worker {WorkerId} created for identity {IdentityId}", row.Id, identityId);
        return row;
    }

    public Task<WorkerRow?> GetByIdAsync(string id) => _workers.GetByIdAsync(id);
    public Task<WorkerRow?> GetByIdentityAsync(string identityId) => _workers.GetByIdentityAsync(identityId);

    public sealed record RegisterOutcome(bool Ok, string? ConflictWorkerId, WorkerRow Row);

    /// <summary>Idempotent registration (rule: a spoke restart must not mint a new worker and
    /// orphan its projections and queue). Refreshes the declaration, returns the same id; a
    /// capability claiming a repository another worker owns is refused with that owner named
    /// (ownership is exclusive, rule 5) — never a UNIQUE violation.</summary>
    public async Task<RegisterOutcome> RegisterAsync(string workerId, RegisterRequest request)
    {
        var row = await _workers.GetByIdAsync(workerId)
                  ?? throw new InvalidOperationException($"unknown worker {workerId}");
        await _workers.UpdateRegistrationAsync(workerId, request.name, request.machine,
            request.version, JsonSerializer.Serialize(request.capabilities, Dtos.Json));
        // The declaration includes repository ownership (plan-engine's repositories, rule 7):
        // routing works from the moment of registration; snapshots refresh the same table.
        var repos = request.capabilities.Where(c => c.kind == "plan-engine" && c.repositories is not null)
            .SelectMany(c => c.repositories!)
            .Select(name => new WorkerRepoRow { WorkerId = workerId, RepoName = name, RootPath = "" })
            .ToList();
        if (repos.Count > 0)
        {
            var conflict = await _workers.SetReposAsync(workerId, repos);
            if (conflict is not null)
            {
                _logger.Warning(
                    "workers: {WorkerId} registration claims a repository owned by {Owner} — declaration refreshed but repository claims unchanged",
                    workerId, conflict);
                return new RegisterOutcome(false, conflict, row);
            }
        }
        _logger.Information(
            "workers: {WorkerId} registered as '{Name}' on '{Machine}' v{Version} with [{Capabilities}]",
            workerId, request.name, request.machine, request.version,
            string.Join(", ", request.capabilities.Select(c => c.kind)));
        return new RegisterOutcome(true, null, (await _workers.GetByIdAsync(workerId))!);
    }

    public async Task<HeartbeatResponse> HeartbeatAsync(string workerId, string spokeVersion)
    {
        var worker = await _workers.GetByIdAsync(workerId)
                     ?? throw new InvalidOperationException($"unknown worker {workerId}");
        await _workers.TouchHeartbeatAsync(workerId, spokeVersion, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return new HeartbeatResponse(worker.LastAcceptedSeq, await _workers.PendingCountAsync(workerId),
            HubVersion, AgentHqContract.Version);
    }

    /// <summary>The hub's own version — the self-update signal on the heartbeat response and
    /// the release.available fan-out. Assembly version, not informational (tags append noise).</summary>
    public static string HubVersion =>
        typeof(WorkerService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Presence rule 8: offline after three missed 300 s beats.</summary>
    public static bool IsOnline(WorkerRow worker) =>
        worker.LastHeartbeatAt is { } v &&
        DateTimeOffset.FromUnixTimeMilliseconds(v) > DateTimeOffset.UtcNow - AgentHqContract.OfflineAfter;

    public async Task<IReadOnlyList<object>> ListPresenceAsync()
    {
        var list = new List<object>();
        foreach (var w in await _workers.ListAsync())
        {
            var repos = await _workers.ListReposAsync(w.Id);
            var caps = ParseCapabilities(w.Capabilities);
            list.Add(new
            {
                id = w.Id,
                name = w.Name,
                machine = w.Machine,
                version = w.Version,
                online = IsOnline(w),
                lastHeartbeat = w.LastHeartbeat,
                capabilities = caps.Select(c => new { kind = c.kind, fields = c.fields }),
                repositories = repos.Select(r => r.RepoName),
                commandsPending = await _workers.PendingCountAsync(w.Id)
            });
        }
        return list;
    }

    // ---- event ingest (rules 1–3) ----

    /// <summary>Applies one delta batch: the three-case rule (trim below / apply equal /
    /// reject ahead), then fans the applied rows out to live subscribers post-commit.</summary>
    public async Task<(EventsAck Ack, IReadOnlyList<AgentHqEvent> Applied)?> SendEventsAsync(
        string workerId, EventsBatch batch)
    {
        var incoming = batch.events.Select(e => new IncomingEvent(e.originSeq, e.type,
            SerializeEnriched(workerId, e), e.occurredAt.ToUnixTimeMilliseconds())).ToList();
        var result = await _workers.ApplyEventBatchAsync(workerId, batch.fromOriginSeq, incoming);
        if (result.Outcome == BatchOutcome.Rejected)
        {
            _logger.Warning(
                "workers: batch from {WorkerId} ahead of cursor — expected {Expected}, got {From}; spoke must rewind and resend",
                workerId, result.NextExpected, batch.fromOriginSeq);
            return null;
        }
        // Fan out after commit: subscribers write to stream bodies and must never see a
        // rolled-back row (the EventStore's own rule).
        foreach (var evt in result.Applied)
            await _events.PublishedAfterCommitAsync(evt);
        if (result.Outcome == BatchOutcome.Trimmed)
            _logger.Debug("workers: batch from {WorkerId} trimmed to tail of {Count} row(s) (ack-lost resend)",
                workerId, result.AppliedCount);
        return (new EventsAck(true, result.GlobalSeqFrom, result.NextExpected), result.Applied);
    }

    /// <summary>The wire delta → global-stream payload: the spoke's own type is preserved,
    /// the payload carries the original fields plus the passthrough metadata.</summary>
    public static string SerializeEnriched(string workerId, EventDelta e)
    {
        using var doc = JsonDocument.Parse(e.payload.GetRawText());
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("workerId", workerId);
            writer.WriteNumber("originSeq", e.originSeq);
            writer.WriteString("actor", e.actor);
            writer.WriteString("transportedBy", e.transportedBy);
            writer.WriteString("occurredAt", e.occurredAt);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                    prop.WriteTo(writer);
            }
            else
            {
                writer.WritePropertyName("data");
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // ---- snapshot (rule 5) ----

    public sealed record SnapshotApplied(bool Ok, string? ConflictWorkerId, long NextExpected);

    public async Task<SnapshotApplied> ApplySnapshotAsync(string workerId, Snapshot snapshot)
    {
        var repos = snapshot.repos.Select(r => new WorkerRepoRow
        {
            WorkerId = workerId, RepoName = r.name, RootPath = r.rootPath
        }).ToList();
        var plans = snapshot.plans.Select(p => JsonSerializer.Deserialize<WorkerPlanRow>(p.plan.GetRawText(), Dtos.Json))
            .Select(p => { p!.WorkerId = workerId; return p; }).ToList();
        var history = snapshot.history.Select(h => JsonSerializer.Deserialize<WorkerHistoryRow>(h.GetRawText(), Dtos.Json))
            .Select(h => { h!.WorkerId = workerId; return h; }).ToList();
        var sessions = snapshot.sessions.Select(s => JsonSerializer.Deserialize<WorkerSessionRow>(s.GetRawText(), Dtos.Json))
            .Select(s => { s!.WorkerId = workerId; return s; }).ToList();
        var lessons = snapshot.lessons.Select(l => JsonSerializer.Deserialize<WorkerLessonRow>(l.GetRawText(), Dtos.Json))
            .Select(l => { l!.WorkerId = workerId; return l; }).ToList();
        var tail = snapshot.events.Select(e => new IncomingEvent(e.originSeq, e.type,
            SerializeEnriched(workerId, e), e.occurredAt.ToUnixTimeMilliseconds())).ToList();

        var outcome = await _workers.ApplySnapshotAsync(workerId, repos,
            plans!, history!, sessions!, lessons!, tail);
        if (outcome.RepoConflict)
        {
            _logger.Warning("workers: snapshot from {WorkerId} rejected — repository owned by {OtherWorker}",
                workerId, outcome.ConflictWorkerId);
            return new SnapshotApplied(false, outcome.ConflictWorkerId, 0);
        }
        _logger.Information(
            "workers: snapshot from {WorkerId} applied — {Plans} plans, {History} history rows, {Sessions} sessions, {Lessons} lessons, {Events} tail events, cursor at {NextExpected}",
            workerId, plans.Count, history.Count, sessions.Count, lessons.Count, tail.Count, outcome.NextExpected);
        return new SnapshotApplied(true, null, outcome.NextExpected);
    }

    // ---- helpers ----

    public static IReadOnlyList<(string kind, JsonElement fields)> ParseCapabilities(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<(string, JsonElement)>();
            return doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("kind", out _))
                .Select(e => (e.GetProperty("kind").GetString() ?? "", e.Clone()))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<(string, JsonElement)>();
        }
    }

    /// <summary>Capabilities a worker declares of one kind (command routing, rule 7).</summary>
    public static bool HasCapability(WorkerRow worker, string kind) =>
        ParseCapabilities(worker.Capabilities).Any(c => c.kind == kind);
}
