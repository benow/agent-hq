using System.Text.RegularExpressions;

namespace AgentHQ.Core.Plans;

public sealed record LessonEntry(
    string EntryId,
    string Title,
    string? Category,
    string? SourcePlanId,
    string Body);

/// <summary>
/// Parses a repository's docs/plans/lessons.md: entries are <c>### L-&lt;n&gt;: &lt;title&gt;</c>
/// headings; the body runs to the next <c>### </c> heading or EOF; category is the first line
/// starting <c>Category:</c>; source plan the first line starting <c>Source:</c>.
/// </summary>
public static partial class LessonsParser
{
    [GeneratedRegex(@"^### (L-\d+): (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex EntryHeading();

    public static IReadOnlyList<LessonEntry> Parse(string markdown)
    {
        var entries = new List<LessonEntry>();
        if (string.IsNullOrEmpty(markdown)) return entries;
        var matches = EntryHeading().Matches(markdown);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : markdown.Length;
            var body = markdown[start..end].Trim();

            string? category = null, source = null;
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (category is null && trimmed.StartsWith("Category:", StringComparison.OrdinalIgnoreCase))
                    category = trimmed["Category:".Length..].Trim();
                if (source is null && trimmed.StartsWith("Source:", StringComparison.OrdinalIgnoreCase))
                    source = trimmed["Source:".Length..].Trim();
            }
            entries.Add(new LessonEntry(matches[i].Groups[1].Value, matches[i].Groups[2].Value.Trim(),
                category, source, body));
        }
        return entries;
    }
}
