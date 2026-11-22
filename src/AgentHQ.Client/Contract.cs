namespace AgentHQ.Client;

/// <summary>The wire contract's fixed surface (plan 03, distributed-agent-hq). Additive
/// changes are minor versions; renaming or removing anything here is breaking and must go
/// through a v2 route prefix with the hub accepting one major back.</summary>
public static class AgentHqContract
{
    /// <summary>The contract header every request to <c>/workers/</c> and <c>/ci/</c> carries.</summary>
    public const string HeaderName = "X-AgentHQ-Contract";

    /// <summary>This package's contract major.</summary>
    public const string Version = "1";

    /// <summary>Lease rule (rule 4), fixed by the contract: claim batch and visibility.</summary>
    public const int ClaimBatchSize = 10;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);

    /// <summary>Presence rule (rule 8): beat every 300 s; offline after three missed beats.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(900);

    /// <summary>Pairing rule (rule 10): nonce TTL, and the create rate limit (per source,
    /// enforced hub-side) is 10 per 10 minutes.</summary>
    public static readonly TimeSpan PairingTtl = TimeSpan.FromMinutes(5);

    /// <summary>The long-poll fallback's server wait (rule 0).</summary>
    public static readonly TimeSpan LongPollWait = TimeSpan.FromSeconds(25);

    /// <summary>The command kinds the v1 contract names (additive list — new kinds may appear
    /// with minor versions; consumers must ignore kinds they do not know).</summary>
    public const string CommandKindPlan = "plan.command";
    public const string CommandKindHealthCheck = "health.check";

    /// <summary>The down-signal event types a spoke's SSE subscription filters for (rule 0).</summary>
    public const string EventTypeCommandsPending = "commands.pending";
    public const string EventTypeReleaseAvailable = "release.available";
}
