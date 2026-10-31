namespace AgentHQ.Core.Exchange;

/// <summary>Item kind — one row in <c>exchange_items</c>. Wire form is the constant itself.</summary>
public static class ExchangeKinds
{
    public const string Question = "question";
    public const string Answer = "answer";
    public const string Message = "message";
    public const string File = "file";
    public const string Status = "status";

    public static readonly IReadOnlyList<string> All = new[] { Question, Answer, Message, File, Status };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>Kinds a caller may post directly; answers are created only by a reply.</summary>
    public static bool IsPostable(string? kind) => kind is Question or Message or File or Status;
}

/// <summary>Item state — a trigger state machine's states, unrelated to plan statuses.</summary>
public static class ExchangeStates
{
    public const string Queued = "queued";
    public const string Delivered = "delivered";
    public const string Claimed = "claimed";
    public const string Answered = "answered";
    public const string Acknowledged = "acknowledged";
    public const string Expired = "expired";
    public const string Killed = "killed";

    public static readonly IReadOnlyList<string> All =
        new[] { Queued, Delivered, Claimed, Answered, Acknowledged, Expired, Killed };

    public static bool IsKnown(string? state) => state is not null && All.Contains(state);

    /// <summary>"Non-terminal" is exactly "not acknowledged" (plan phase 2.2): expired and killed
    /// items are still acknowledged afterwards; a terminal item takes no new transitions.</summary>
    public static bool IsTerminal(string state) => state == Acknowledged;
}

/// <summary>Delivery latency class: the receiver's expected reaction time.</summary>
public static class ExchangeLatency
{
    public const string Realtime = "realtime";
    public const string Turn = "turn";
    public const string Minutes = "minutes";
    public const string Human = "human";

    public static readonly IReadOnlyList<string> All = new[] { Realtime, Turn, Minutes, Human };

    public static bool IsKnown(string? value) => value is not null && All.Contains(value);
}
