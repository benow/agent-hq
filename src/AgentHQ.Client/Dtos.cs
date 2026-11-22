using System.Text.Json;

namespace AgentHQ.Client;

// The contract's pinned payload shapes (plan 03). Authored camelCase so the code reads like
// the wire — the serializer applies the naming policy regardless of authored casing, and
// PascalCase assertions can never pass. These DTOs are the contract: additive fields are
// minor versions; renaming or removing is breaking (v2 route prefix).

public static class Dtos
{
    /// <summary>Shared JSON options for the contract's payloads: camelCase, nulls omitted —
    /// the same policy the hub host applies.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Typed capability entry (rule 7). Field sets per kind are pinned:
/// plan-engine → repositories; zcode-harness → schemaVersion + pluginVersion;
/// build-ci → loopbackPort; build-health → checks. Unknown kinds are stored and
/// surfaced but never routed (forward-compatible).</summary>
public sealed record Capability(
    string kind,
    string[]? repositories = null,
    string? schemaVersion = null,
    string? pluginVersion = null,
    int? loopbackPort = null,
    string[]? checks = null);

public sealed record RegisterRequest(
    string name, string machine, string version, Capability[] capabilities);

public sealed record RegisterResponse(string workerId, DateTimeOffset serverTime, string contractVersion);

public sealed record HeartbeatRequest(string version);

public sealed record HeartbeatResponse(
    long lastAcceptedSeq, int commandsPending, string serverVersion, string contractVersion);

/// <summary>One element of the delta batch (rule 1/2). actor passthrough: the original
/// session id, an identity, or a CI run — never the worker.</summary>
public sealed record EventDelta(
    long originSeq, string type, DateTimeOffset occurredAt,
    string actor, string transportedBy, JsonElement payload);

public sealed record EventsBatch(long fromOriginSeq, EventDelta[] events);

public sealed record EventsAck(bool accepted, long globalSeqFrom, long nextExpected);

/// <summary>Ahead-of-cursor rejection (rule 2, the 422 body names the expected value).</summary>
public sealed record EventsRejected(long expected);

public sealed record CommandEnvelope(
    string commandId, string kind, string? repo, string actor,
    JsonElement payload, DateTimeOffset issuedAt, DateTimeOffset? leaseUntil);

public sealed record CommandResult(
    string commandId, bool ok, string? error, string resultHash, DateTimeOffset finishedAt);

/// <summary>Result acknowledgement: duplicate:true means a result already existed and this
/// one was recorded nowhere (rule 4).</summary>
public sealed record CommandResultAck(bool ok, bool duplicate);

public sealed record SnapshotRepo(string name, string rootPath);

/// <summary>One plan projection plus its full markdown (rows AND content, rule 5).</summary>
public sealed record SnapshotPlan(JsonElement plan, string markdown);

public sealed record Snapshot(
    SnapshotRepo[] repos,
    SnapshotPlan[] plans,
    JsonElement[] history,
    JsonElement[] sessions,
    JsonElement[] lessons,
    EventDelta[] events);

public sealed record CiEvent(
    string workflow, long run, string repo, string conclusion, string url, DateTimeOffset timestamp);

public sealed record CiAck(bool accepted, long seq);

public sealed record PairingCreated(string nonce, string pairUrl, DateTimeOffset expiresAt);

/// <summary>Poll state machine (rule 10): pending → approved (token once) → consumed;
/// expired after TTL; unknown for a bad nonce. Anything but pending ends the poll loop.</summary>
public sealed record PairingPoll(string state, string? token);
