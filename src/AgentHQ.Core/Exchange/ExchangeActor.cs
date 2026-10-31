namespace AgentHQ.Core.Exchange;

/// <summary>
/// Who posts or receives in the Exchange: a pair <c>(ActorType, ActorId)</c>. The wire and
/// display form is <c>type:id</c> — <c>zcode:ses_…</c>, <c>openclaw:linda_v2</c>,
/// <c>linda_v1:linda_v1</c>, <c>andy:andy</c>. Comparison is canonical (ordinal, both parts).
/// </summary>
public readonly record struct ExchangeActor(string ActorType, string ActorId)
    : IComparable<ExchangeActor>
{
    public static readonly ExchangeActor Andy = new(ExchangeActorTypes.Andy, ExchangeActorTypes.Andy);
    public static readonly ExchangeActor OpenClaw = new(ExchangeActorTypes.OpenClaw, "linda_v2");
    public static readonly ExchangeActor LindaV1 = new(ExchangeActorTypes.LindaV1, ExchangeActorTypes.LindaV1);

    /// <summary>Any live ZCode session in the repository (role addressing, D13).</summary>
    public static ExchangeActor ZCodeRole => new(ExchangeActorTypes.ZCode, ExchangeActorTypes.RoleWildcard);

    public static ExchangeActor ZCode(string sessionId) => new(ExchangeActorTypes.ZCode, sessionId);

    /// <summary>True when this actor is the role-addressed recipient <c>zcode:*</c>.</summary>
    public bool IsZCodeRole => ActorType == ExchangeActorTypes.ZCode && ActorId == ExchangeActorTypes.RoleWildcard;

    public bool IsAndy => ActorType == ExchangeActorTypes.Andy;

    public string ToDisplayString() => $"{ActorType}:{ActorId}";

    /// <summary>
    /// Parses <c>type:id</c>. The type must be a writable actor type (see
    /// <see cref="ExchangeActorTypes.ValidateActorType"/>); the id is non-empty and never
    /// contains ':'. The single exception is the role recipient <c>zcode:*</c>.
    /// </summary>
    public static bool TryParse(string? text, out ExchangeActor actor, out string? error)
    {
        actor = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "actor is required (form: type:id, e.g. zcode:ses_…, openclaw:linda_v2, andy:andy)";
            return false;
        }
        var value = text.Trim();
        var colon = value.IndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            error = $"actor '{value}' is not of the form type:id";
            return false;
        }
        var type = value[..colon];
        var id = value[(colon + 1)..];
        if (!ExchangeActorTypes.ValidateActorType(type, out error)) return false;
        if (id.Contains(':'))
        {
            error = $"actor id '{id}' contains ':' — the id never contains a colon";
            return false;
        }
        actor = new ExchangeActor(type, id);
        return true;
    }

    public static ExchangeActor Parse(string text)
    {
        if (TryParse(text, out var actor, out var error)) return actor;
        throw new FormatException(error);
    }

    public int CompareTo(ExchangeActor other)
    {
        var t = string.CompareOrdinal(ActorType, other.ActorType);
        return t != 0 ? t : string.CompareOrdinal(ActorId, other.ActorId);
    }

    public override string ToString() => ToDisplayString();
}
