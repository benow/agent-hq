namespace AgentHQ.Core.Exchange;

/// <summary>
/// The trigger state machine for Exchange items (D4). It is driven by actions, not commands:
/// every edge in the plan's phase-2.2 table is one row here, and the service moves no state
/// except through <see cref="Evaluate"/>. This is <b>not</b> the plan lifecycle — plan statuses
/// and item states are unrelated names in unrelated tables, and nothing here goes through
/// <c>PlanStatusCodes</c>.
/// </summary>
public static class ExchangeStateMachine
{
    public static class Actions
    {
        public const string Post = "post";
        public const string Claim = "claim";
        public const string Reply = "reply";
        public const string Ack = "ack";
        public const string Kill = "kill";
        public const string Expire = "expire";
    }

    /// <summary>Error codes the refusal names (the shared code list of phase 2.3).</summary>
    public static class Codes
    {
        public const string IllegalTransition = "illegal_transition";
        public const string UnknownActor = "unknown_actor";
        public const string PurposeRequired = "purpose_required";
        public const string HopLimit = "hop_limit";
        public const string RateLimited = "rate_limited";
        public const string AlreadyClaimed = "already_claimed";
        public const string ThreadKilled = "thread_killed";
        public const string ScopeDenied = "scope_denied";
        public const string HashMismatch = "hash_mismatch";
        public const string NotFound = "not_found";
        public const string BodyTooLarge = "body_too_large";
        public const string BlobTooLarge = "blob_too_large";
        public const string BadIdempotencyKey = "bad_idempotency_key";
    }

    public sealed record Decision(bool Allowed, string? To, string? Code, string? Detail)
    {
        public static Decision Allow(string to) => new(true, to, null, null);
        public static Decision Refuse(string code, string detail) => new(false, null, code, detail);
    }

    /// <summary>
    /// Evaluates one action against the item's current state.
    /// <paramref name="now"/> is the clock the expiry rule reads.
    /// </summary>
    /// <param name="isThreadKilled">true when the thread root is killed: no reply may enter it.</param>
    public static Decision Evaluate(ExchangeItem item, string action, DateTimeOffset now, bool isThreadKilled)
    {
        var state = item.State;
        var term = $"{item.Kind} #{item.Id} in state '{state}'";

        switch (action)
        {
            case Actions.Claim:
                if (ExchangeStates.IsTerminal(state))
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} is terminal; nothing to claim");
                if (item.Kind != ExchangeKinds.Question)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} cannot be claimed — only questions can");
                if (!item.IsRoleAddressed)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} is addressed to {item.Recipient.ToDisplayString()}; only role-addressed questions can be claimed");
                if (state != ExchangeStates.Queued)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} cannot be claimed from this state");
                return Decision.Allow(ExchangeStates.Claimed);

            case Actions.Reply:
                if (isThreadKilled || state == ExchangeStates.Killed)
                    return Decision.Refuse(Codes.ThreadKilled, $"thread {item.ThreadId} is killed; no reply may enter it");
                if (state is ExchangeStates.Queued or ExchangeStates.Claimed)
                    return Decision.Allow(ExchangeStates.Answered);
                return Decision.Refuse(Codes.IllegalTransition, $"{term} is not open for a reply");

            case Actions.Ack:
                if (state == ExchangeStates.Acknowledged)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} is already acknowledged");
                if (state is ExchangeStates.Delivered or ExchangeStates.Answered or ExchangeStates.Expired or ExchangeStates.Killed)
                    return Decision.Allow(ExchangeStates.Acknowledged);
                return Decision.Refuse(Codes.IllegalTransition, $"{term} cannot be acknowledged from this state");

            case Actions.Kill:
                if (state == ExchangeStates.Acknowledged)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} is terminal (acknowledged); nothing to kill");
                return Decision.Allow(ExchangeStates.Killed);

            case Actions.Expire:
                if (state == ExchangeStates.Acknowledged || item.ExpiresAt is null)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} has no expiry to reach");
                if (now <= item.ExpiresAt)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} has not reached expires_at yet");
                if (state == ExchangeStates.Killed)
                    return Decision.Refuse(Codes.IllegalTransition, $"{term} is already killed");
                return Decision.Allow(ExchangeStates.Expired);

            default:
                return Decision.Refuse(Codes.IllegalTransition, $"unknown action '{action}'");
        }
    }

    /// <summary>The state a freshly posted item gets. Only <c>claim</c>, <c>ack</c> and the
    /// expiry sweep can move it afterwards.</summary>
    public static string InitialState(string kind) => kind switch
    {
        ExchangeKinds.Question => ClaimableFrom,
        ExchangeKinds.Message or ExchangeKinds.File or ExchangeKinds.Status or ExchangeKinds.Answer => ExchangeStates.Delivered,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown exchange kind")
    };

    /// <summary>A reply is the item's hop (root = 0), refused at the configured limit.</summary>
    public static bool IsHopOverLimit(int parentHopCount, int hopLimit, out int nextHop)
    {
        // The root is hop 0 and the limit counts replies: a reply to the root is hop 1.
        nextHop = parentHopCount + 1;
        return nextHop > hopLimit;
    }

    /// <summary>
    /// The state an item may be claimed from. The item is created directly in this state: a
    /// role-addressed question is claimable the moment it is posted, so its first state is the
    /// claimable one (criterion 3 depends on it), and 'claimed' remains an edge of this map.
    /// </summary>
    public const string ClaimableFrom = ExchangeStates.Queued;

    /// <summary>Legal edges of the item lifecycle, as (from, action) → to. The unit tests pin
    /// every edge and every refusal named in the plan's phase-2.2 table.</summary>
    public static IReadOnlyDictionary<(string From, string Action), string> Edges { get; } =
        new Dictionary<(string, string), string>
        {
            [(ExchangeStates.Queued, Actions.Claim)] = ExchangeStates.Claimed,
            [(ExchangeStates.Queued, Actions.Reply)] = ExchangeStates.Answered,
            [(ExchangeStates.Claimed, Actions.Reply)] = ExchangeStates.Answered,
            [(ExchangeStates.Delivered, Actions.Ack)] = ExchangeStates.Acknowledged,
            [(ExchangeStates.Answered, Actions.Ack)] = ExchangeStates.Acknowledged,
            [(ExchangeStates.Expired, Actions.Ack)] = ExchangeStates.Acknowledged,
            [(ExchangeStates.Killed, Actions.Ack)] = ExchangeStates.Acknowledged,
            [(ExchangeStates.Queued, Actions.Kill)] = ExchangeStates.Killed,
            [(ExchangeStates.Delivered, Actions.Kill)] = ExchangeStates.Killed,
            [(ExchangeStates.Claimed, Actions.Kill)] = ExchangeStates.Killed,
            [(ExchangeStates.Answered, Actions.Kill)] = ExchangeStates.Killed,
            [(ExchangeStates.Expired, Actions.Kill)] = ExchangeStates.Killed,
            [(ExchangeStates.Queued, Actions.Expire)] = ExchangeStates.Expired,
            [(ExchangeStates.Claimed, Actions.Expire)] = ExchangeStates.Expired,
            [(ExchangeStates.Delivered, Actions.Expire)] = ExchangeStates.Expired,
            [(ExchangeStates.Answered, Actions.Expire)] = ExchangeStates.Expired,
        };
}
