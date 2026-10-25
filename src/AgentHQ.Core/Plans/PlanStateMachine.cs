namespace AgentHQ.Core.Plans;

/// <summary>
/// Result of validating a transition. <see cref="IsAllowed"/> false means reject outright
/// (illegal lifecycle move). Warnings (soft enforcement — unmet dependencies, separation of
/// duties) do not block the transition and are reported back to the caller.
/// </summary>
public sealed record TransitionResult(bool IsAllowed, string? Error, IReadOnlyList<string> Warnings)
{
    public static readonly TransitionResult Ok = new(true, null, Array.Empty<string>());
    public static TransitionResult OkWithWarnings(params string[] warnings) =>
        new(true, null, warnings);
    public static TransitionResult Rejected(string error) => new(false, error, Array.Empty<string>());
}

/// <summary>
/// The plan lifecycle state machine. Pure rules, no storage: submitted → refining →
/// in_review → implementing → implemented → verifying → verified → done, with the rework
/// loop verifying → fixing → implemented, deferred/abandoned from anywhere, and
/// deferred → refining re-activation. Blocked is an orthogonal flag, not a state here.
/// </summary>
public static class PlanStateMachine
{
    private static readonly Dictionary<(PlanStatus From, string Command), PlanStatus> Map = new()
    {
        // lifecycle commands
        [(PlanStatus.Submitted, "start")] = PlanStatus.Refining,
        [(PlanStatus.Refining, "review")] = PlanStatus.InReview,
        [(PlanStatus.InReview, "sendback")] = PlanStatus.Refining,
        [(PlanStatus.InReview, "implement")] = PlanStatus.Implementing,
        [(PlanStatus.Implementing, "implemented")] = PlanStatus.Implemented,
        [(PlanStatus.Fixing, "implemented")] = PlanStatus.Implemented,
        [(PlanStatus.Implemented, "verify")] = PlanStatus.Verifying,
        [(PlanStatus.Implementing, "verify")] = PlanStatus.Verifying,   // short-cut: via implemented
        [(PlanStatus.Fixing, "verify")] = PlanStatus.Verifying,          // short-cut: via implemented
        [(PlanStatus.Verifying, "pass")] = PlanStatus.Verified,
        [(PlanStatus.Verifying, "fail")] = PlanStatus.Fixing,
        [(PlanStatus.Verified, "done")] = PlanStatus.Done,
        [(PlanStatus.Deferred, "reactivate")] = PlanStatus.Refining,
        [(PlanStatus.Submitted, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Refining, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.InReview, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Implementing, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Implemented, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Verifying, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Verified, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Fixing, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Done, "defer")] = PlanStatus.Deferred,
        [(PlanStatus.Submitted, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Refining, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.InReview, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Implementing, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Implemented, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Verifying, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Verified, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Fixing, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Deferred, "abandon")] = PlanStatus.Abandoned,
        [(PlanStatus.Done, "abandon")] = PlanStatus.Abandoned,
    };

    /// <summary>Commands that start or continue development work (session linking applies).</summary>
    public static bool IsWorkCommand(string command) => command is "start" or "implement";

    /// <summary>
    /// Commands issued by a verifier that may enter via the short-cut (first recording
    /// → implemented attributed to the verifier).
    /// </summary>
    public static bool IsShortCutEntry(string command, PlanStatus from) =>
        command == "verify" && from is PlanStatus.Implementing or PlanStatus.Fixing;

    public static bool CanTransition(PlanStatus from, string command) => Map.ContainsKey((from, command));

    public static PlanStatus Target(PlanStatus from, string command) => Map[(from, command)];

    /// <summary>
    /// Validates a raw from→to move (used by both the command path and the manual
    /// PATCH path so both enforce identical rules).
    /// </summary>
    public static TransitionResult Validate(PlanStatus from, PlanStatus to)
    {
        if (from == to)
            return TransitionResult.Rejected($"Plan is already '{from.Code()}'; no transition performed.");
        if (Map.Values.Any(t => t == to) || to is PlanStatus.Deferred or PlanStatus.Abandoned)
        {
            // Confirm some (from, cmd) pair actually allows this move — e.g. verified is only
            // reachable from verifying; refining only from submitted/deferred/in_review.
            var legal = Map.Where(kv => kv.Value == to && kv.Key.From == from).Select(kv => kv.Key.Command).ToList();
            if (legal.Count > 0) return TransitionResult.Ok;
            return TransitionResult.Rejected($"Transition {from.Code()} → {to.Code()} is not allowed.");
        }
        return TransitionResult.Rejected($"Unknown target status '{to.Code()}'.");
    }

    /// <summary>Resolves the single command producing from→to, for history rows on the manual path.</summary>
    public static string? CommandFor(PlanStatus from, PlanStatus to) =>
        Map.FirstOrDefault(kv => kv.Key.From == from && kv.Value == to).Key.Command;
}
