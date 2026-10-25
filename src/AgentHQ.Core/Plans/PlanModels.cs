namespace AgentHQ.Core.Plans;

public enum PlanStatus
{
    Submitted,
    Refining,
    InReview,
    Implementing,
    Implemented,
    Verifying,
    Verified,
    Done,
    Deferred,
    Abandoned,
    Fixing
}

public static class PlanStatusCodes
{
    public static string Code(this PlanStatus s) => s switch
    {
        PlanStatus.Submitted => "submitted",
        PlanStatus.Refining => "refining",
        PlanStatus.InReview => "in_review",
        PlanStatus.Implementing => "implementing",
        PlanStatus.Implemented => "implemented",
        PlanStatus.Verifying => "verifying",
        PlanStatus.Verified => "verified",
        PlanStatus.Done => "done",
        PlanStatus.Deferred => "deferred",
        PlanStatus.Abandoned => "abandoned",
        PlanStatus.Fixing => "fixing",
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null)
    };

    public static PlanStatus FromCode(string code) => code switch
    {
        "submitted" => PlanStatus.Submitted,
        "refining" => PlanStatus.Refining,
        "in_review" => PlanStatus.InReview,
        "implementing" => PlanStatus.Implementing,
        "implemented" => PlanStatus.Implemented,
        "verifying" => PlanStatus.Verifying,
        "verified" => PlanStatus.Verified,
        "done" => PlanStatus.Done,
        "deferred" => PlanStatus.Deferred,
        "abandoned" => PlanStatus.Abandoned,
        "fixing" => PlanStatus.Fixing,
        _ => throw new ArgumentException($"Unknown plan status '{code}'.", nameof(code))
    };

    public static IReadOnlyList<string> AllCodes { get; } = new[]
    {
        "submitted", "refining", "in_review", "implementing", "implemented",
        "verifying", "verified", "done", "deferred", "abandoned", "fixing"
    };
}
