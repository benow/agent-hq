using System.Text.RegularExpressions;

namespace AgentHQ.Commands.ZCode;

/// <summary>
/// Installs the colon workflow command pack (WorkflowCommands/*.md, invoked as
/// /plan:&lt;verb&gt;) into a repository's project-scope `.zcode/commands/plan/`. The pack is
/// mastered in this repository — target repositories (including nastv) sync FROM here; this is
/// the one sync path. The overwrite rules mirror the installer's: a destination carrying our
/// version marker is upgraded, a file without the marker is treated as user-authored and left
/// alone unless --force (the documented one-time cutover for repositories whose pack predates
/// the marker).
/// </summary>
public static class WorkflowSync
{
    public const string CommandVersion = "1";
    private static readonly Regex Marker = new(@"^<!-- agent-hq v(\d+) -->", RegexOptions.Compiled);

    public static int Run(string repo, bool force, string? sourceDir = null)
    {
        var warnings = new List<string>();
        var src = sourceDir ?? Path.Combine(AppContext.BaseDirectory, "WorkflowCommands");
        if (!Directory.Exists(src))
        {
            Console.Error.WriteLine($"workflow command pack not found at {src}");
            return 1;
        }
        var destDir = Path.Combine(repo, ".zcode", "commands", "plan");
        Directory.CreateDirectory(destDir);

        var installed = 0;
        var preserved = 0;
        foreach (var file in Directory.GetFiles(src, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var dest = Path.Combine(destDir, Path.GetFileName(file));
            var ours = File.ReadAllText(file);
            if (File.Exists(dest))
            {
                var current = File.ReadAllText(dest);
                var version = MarkerVersion(current);
                if (version is null && !force)
                {
                    warnings.Add($"left user-authored command alone (no marker): {Path.GetFileName(dest)}");
                    preserved++;
                    continue;
                }
                if (version > MarkerVersion(ours) && !force)
                {
                    warnings.Add($"left newer command alone: {Path.GetFileName(dest)}");
                    preserved++;
                    continue;
                }
                if (current == ours)
                {
                    installed++;   // already up to date; idempotent re-run
                    continue;
                }
            }
            File.WriteAllText(dest, ours);
            installed++;
        }

        foreach (var w in warnings) Console.WriteLine($"warning: {w}");
        Console.WriteLine($"workflow commands synced to {destDir}: {installed} written, {preserved} preserved");
        // 2 = a deliberate, visible partial install (same convention as the installer).
        return preserved > 0 ? 2 : 0;
    }

    private static int? MarkerVersion(string text)
    {
        var m = Marker.Match(text.TrimStart());
        if (!m.Success) return null;
        return int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }
}