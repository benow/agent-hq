using System.Text.RegularExpressions;

namespace AgentHQ.Core.Plans;

public sealed record PlanProgress(int Checked, int Total);

/// <summary>
/// Parsing rules for plan markdown, exactly as the plan doc specifies:
/// sections are <c>## &lt;Name&gt;</c> headings ending at the next <c>## </c> heading or EOF;
/// checkbox counting applies to the <c>## Steps</c> section only, lines matching
/// <c>^[ \t]*[-*] \[[ xX]\]</c> (indented sub-steps count).
/// </summary>
public static partial class PlanDocument
{
    [GeneratedRegex(@"^## (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"^[ \t]*[-*] \[[ xX]\]", RegexOptions.Multiline)]
    private static partial Regex CheckboxLine();

    public static (string Name, string Body)[] ParseSections(string markdown)
    {
        var matches = SectionHeading().Matches(markdown ?? "");
        var sections = new List<(string, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            sections.Add((matches[i].Groups[1].Value.Trim(), markdown[start..end].Trim()));
        }
        return sections.ToArray();
    }

    public static string? Section(string markdown, string name)
    {
        var match = ParseSections(markdown).FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        return match.Name is null ? null : match.Body;
    }

    public static PlanProgress Progress(string markdown)
    {
        var steps = Section(markdown ?? "", "Steps");
        if (steps is null) return new PlanProgress(0, 0);
        var lines = CheckboxLine().Matches(steps);
        var total = lines.Count;
        if (total == 0) return new PlanProgress(0, 0);
        // checked = checkbox lines containing [x] or [X]
        var checkedCount = Regex.Matches(steps, @"^[ \t]*[-*] \[[xX]\]", RegexOptions.Multiline).Count;
        return new PlanProgress(checkedCount, total);
    }

    /// <summary>Soft validation: names the convention sections missing from the document.</summary>
    public static IReadOnlyList<string> MissingSections(string markdown)
    {
        var missing = new List<string>();
        foreach (var required in new[] { "Steps", "Verification Criteria", "Documentation" })
            if (Section(markdown, required) is null)
                missing.Add(required);
        return missing;
    }
}
