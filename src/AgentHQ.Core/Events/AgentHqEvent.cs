using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHQ.Core.Events;

/// <summary>An Agent HQ event: one row in the events table, one SSE message.</summary>
public sealed record AgentHqEvent(
    long Seq,
    string Type,
    JsonElement Payload,
    DateTimeOffset At)
{
    public static AgentHqEvent Create(long seq, string type, object payload, DateTimeOffset at) =>
        new(seq, type,
            JsonSerializer.SerializeToElement(payload, JsonOpts),
            at);

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class EventTypes
{
    public const string SessionCreated = "session.created";
    public const string SessionUpdated = "session.updated";
    public const string PlanCreated = "plan.created";
    public const string PlanUpdated = "plan.updated";
    public const string PlanStatusChanged = "plan.status_changed";
    public const string PlanSessionLinked = "plan.session_linked";
    public const string PlanBlocked = "plan.blocked";
    public const string PlanUnblocked = "plan.unblocked";
    public const string HookRejected = "hook.rejected";

    // Plan identity (D11): the registration moved to another file, or was deleted. Agent HQ
    // never deletes the file on disk, and neither event carries file content.
    public const string PlanFileChanged = "plan.file_changed";
    public const string PlanDeleted = "plan.deleted";

    // Exchange (D20). Payloads carry metadata only — id, kind, state, sender, recipient,
    // thread, hop — never bodies or bytes. Unknown types already render in the activity panel;
    // only the UI's describe() formatting reads these.
    public const string ExchangeCreated = "exchange.created";
    public const string ExchangeClaimed = "exchange.claimed";
    public const string ExchangeAnswered = "exchange.answered";
    public const string ExchangeAcknowledged = "exchange.acknowledged";
    public const string ExchangeExpired = "exchange.expired";
    public const string ExchangeKilled = "exchange.killed";
    public const string ExchangeResync = "exchange.resync";
    public const string ExchangePresence = "exchange.presence";

    public static readonly IReadOnlyList<string> ExchangeTypes = new[]
    {
        ExchangeCreated, ExchangeClaimed, ExchangeAnswered, ExchangeAcknowledged,
        ExchangeExpired, ExchangeKilled, ExchangeResync, ExchangePresence
    };
}
