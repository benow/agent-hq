using System.Text.RegularExpressions;

namespace AgentHQ.Core.Plans;

public static partial class PlanSlug
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex Valid();

    /// <summary>
    /// Derives a filesystem-safe slug from a plan name: lowercase, spaces → '-', strip the
    /// rest, collapse runs, trim. <paramref name="isTaken"/> yields the -2, -3… suffix chain
    /// on collision within a repository.
    /// </summary>
    public static string Derive(string name, Func<string, bool> isTaken)
    {
        var baseSlug = Normalize(name);
        if (baseSlug.Length == 0) baseSlug = "plan";
        var candidate = baseSlug;
        var n = 1;
        while (isTaken(candidate))
        {
            n++;
            candidate = $"{baseSlug}-{n}";
        }
        return candidate;
    }

    public static string Normalize(string name)
    {
        var lowered = (name ?? "").ToLowerInvariant().Replace(' ', '-');
        var stripped = new string(lowered.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : ' ').ToArray());
        var collapsed = Regex.Replace(stripped, @"-+", "-").Trim('-');
        var result = Regex.Replace(collapsed, @"\s+", "");
        return Valid().IsMatch(result) ? result : result.Length == 0 ? "" : result;
    }

    /// <summary>Repository id: slug of the root path (absolute, lowercased, / → -).</summary>
    public static string ForRootPath(string rootPath)
    {
        var normalized = Path.GetFullPath(rootPath).TrimEnd('/').ToLowerInvariant();
        return normalized.Replace('/', '-');
    }
}
