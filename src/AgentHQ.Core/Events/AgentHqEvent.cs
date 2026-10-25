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
}
