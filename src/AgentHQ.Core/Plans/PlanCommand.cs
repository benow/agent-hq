using System.Text.RegularExpressions;

namespace AgentHQ.Core.Plans;

/// <summary>
/// A parsed /plan command from a hook-fed prompt: /plan &lt;subcommand&gt; &lt;argument&gt; or
/// /plan:&lt;subcommand&gt; &lt;argument&gt;. The two spellings are one-to-one — the colon form
/// is the workflow command (from the plan/ command subdirectory), the space form the state
/// command — and both move the hub identically. The argument (the &lt;file&gt;) may be a slug,
/// a repository-relative path, or an absolute path.
/// </summary>
public sealed record PlanCommand(string Subcommand, string Argument, string? ReasonTail)
{
    private static readonly Regex Pattern = new(@"^/plan(?::|\s+)(\w+)\s+(.+)$", RegexOptions.Compiled);

    public static PlanCommand? TryParse(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var match = Pattern.Match(prompt.Trim());
        if (!match.Success) return null;
        var rest = match.Groups[2].Value.Trim();

        // Verdict/rework commands carry a free-text reason: "/plan fail <file> <reason>".
        // The file argument itself never contains whitespace, so the first token is the file
        // and everything after — when the command takes a reason — is the reason tail.
        var firstSpace = rest.IndexOf(' ');
        if (firstSpace < 0)
            return new PlanCommand(match.Groups[1].Value.ToLowerInvariant(), rest, null);

        var file = rest[..firstSpace];
        var tail = rest[(firstSpace + 1)..].Trim();
        return new PlanCommand(match.Groups[1].Value.ToLowerInvariant(), file,
            TakesReason(match.Groups[1].Value.ToLowerInvariant()) && tail.Length > 0 ? tail : null);
    }

    /// <summary>Commands whose template includes a trailing &lt;reason&gt; argument.</summary>
    public static bool TakesReason(string subcommand) =>
        subcommand is "sendback" or "fail" or "block" or "defer" or "abandon";
}
