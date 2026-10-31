namespace AgentHQ.Core.Exchange;

/// <summary>
/// Exchange configuration, resolved once at startup from the environment (one composition root:
/// services never read environment variables themselves). Fields and defaults come from the plan
/// (phase 1b); <c>SubjectMaxChars</c> and <c>HopLimit</c> have no env var on purpose.
/// </summary>
public sealed record ExchangeOptions
{
    public string BlobDir { get; init; } = "";
    public int BodyMaxBytes { get; init; } = 32768;
    public int SubjectMaxChars { get; init; } = 120;
    public long BlobMaxBytes { get; init; } = 52_428_800;
    public int RatePerMinute { get; init; } = 30;
    public int HopLimit { get; init; } = 3;
    public long EventReplayWindowSeconds { get; init; } = 604_800;
    public long PresenceStaleSeconds { get; init; } = 600;
    public int DigestBudgetChars { get; init; } = 2000;

    public const string BlobDirEnv = "AGENTHQ_EXCHANGE_BLOB_DIR";
    public const string BodyMaxEnv = "AGENTHQ_EXCHANGE_BODY_MAX";
    public const string BlobMaxEnv = "AGENTHQ_EXCHANGE_BLOB_MAX";
    public const string RateEnv = "AGENTHQ_EXCHANGE_RATE";
    public const string DigestBudgetEnv = "AGENTHQ_EXCHANGE_DIGEST_BUDGET";

    /// <summary>
    /// Resolves options from an environment lookup. An unparseable or out-of-range value is a
    /// startup failure naming the variable and its accepted range — never a silent default.
    /// </summary>
    public static ExchangeOptions FromEnvironment(Func<string, string?> get, string dataDir)
    {
        var blobDir = get(BlobDirEnv);
        return new ExchangeOptions
        {
            BlobDir = string.IsNullOrWhiteSpace(blobDir) ? Path.Combine(dataDir, "blobs") : blobDir,
            BodyMaxBytes = Int(get, BodyMaxEnv, 32768, 1, 4 * 1024 * 1024),
            BlobMaxBytes = Long(get, BlobMaxEnv, 52_428_800, 1, 4L * 1024 * 1024 * 1024),
            RatePerMinute = Int(get, RateEnv, 30, 1, 100_000),
            DigestBudgetChars = Int(get, DigestBudgetEnv, 2000, 200, 1_000_000)
        };
    }

    private static int Int(Func<string, string?> get, string name, int fallback, int min, int max) =>
        (int)Long(get, name, fallback, min, max);

    private static long Long(Func<string, string?> get, string name, long fallback, long min, long max)
    {
        var raw = get(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!long.TryParse(raw.Trim(), out var value) || value < min || value > max)
            throw new InvalidOperationException(
                $"{name}='{raw}' is not usable: it must be an integer from {min} to {max} " +
                "(bytes or a count, depending on the variable). Fix the service environment and restart.");
        return value;
    }
}
