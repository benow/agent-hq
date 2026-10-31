namespace AgentHQ.Core.Exchange;

/// <summary>
/// The actor types an Exchange write may use. Every write validates against this set. The
/// plan-lifecycle actor type <c>session</c> is deliberately absent: it records /plan commands
/// and is not a conversation identity (see <see cref="ValidateActorType"/> for the message).
/// </summary>
public static class ExchangeActorTypes
{
    public const string Andy = "andy";
    public const string OpenClaw = "openclaw";
    public const string LindaV1 = "linda_v1";
    public const string ZCode = "zcode";

    /// <summary>The recipient id meaning "any live session in the repository" (D13).</summary>
    public const string RoleWildcard = "*";

    public static readonly IReadOnlyList<string> All = new[] { Andy, OpenClaw, LindaV1, ZCode };

    public static bool IsKnown(string type) => All.Contains(type);

    /// <summary>
    /// Validates an actor type for an Exchange write. The failure for <c>session</c> says why,
    /// because that is the mistake a caller arriving from the plan lifecycle will make.
    /// </summary>
    public static bool ValidateActorType(string? type, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(type))
        {
            error = "actor type is required";
            return false;
        }
        if (type == "session")
        {
            error = "'session' is a plan-lifecycle actor; an exchange sender must be one of andy, openclaw, " +
                    "linda_v1, zcode (with the session id as the id)";
            return false;
        }
        if (!IsKnown(type))
        {
            error = $"unknown actor type '{type}'; an exchange sender must be one of andy, openclaw, " +
                    "linda_v1, zcode (with the session id as the id)";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The canonical single-actor form for a type: andy → andy:andy, openclaw → openclaw:linda_v2,
    /// linda_v1 → linda_v1:linda_v1. <c>zcode</c> has no canonical id and must carry one.
    /// </summary>
    public static ExchangeActor? Canonical(string type) => type switch
    {
        Andy => ExchangeActor.Andy,
        OpenClaw => ExchangeActor.OpenClaw,
        LindaV1 => ExchangeActor.LindaV1,
        _ => null
    };
}
