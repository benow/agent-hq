using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentHQ.Commands.ZCode;

/// <summary>
/// One-command, idempotent ZCode-side installation: copies the /plan command pack to
/// ~/.zcode/commands/plan/ (user scope; overwrites only files whose Agent HQ version
/// marker differs, leaving user-edited files alone — including files installed under the
/// previous name, which carry an older marker), merges the UserPromptSubmit hook into
/// ~/.zcode/settings.json (key-based, idempotent), and appends the Agent HQ section to
/// the repository's AGENTS.md if the marker is absent.
/// </summary>
public static class Installer
{
    public const string CommandVersion = "1";

    public static int Run(string home, string repo, string port)
    {
        var warnings = new List<string>();

        // 1. command pack
        var packSrc = Path.Combine(AppContext.BaseDirectory, "Commands");
        var packDest = Path.Combine(home, ".zcode", "commands", "plan");
        var copied = 0;
        if (Directory.Exists(packSrc))
        {
            Directory.CreateDirectory(packDest);
            foreach (var file in Directory.GetFiles(packSrc, "*.md"))
            {
                var dest = Path.Combine(packDest, Path.GetFileName(file));
                var ours = File.ReadAllText(file);
                if (File.Exists(dest))
                {
                    var installed = File.ReadAllText(dest);
                    if (FirstLineMarker(installed) is null && !IsLegacyMarker(installed))
                    {
                        warnings.Add($"left user-edited command alone: {Path.GetFileName(file)}");
                        continue;
                    }
                    // Ours from before the rename, but edited since: treat it like any other
                    // edited command rather than overwriting someone's change.
                    if (IsLegacyMarker(installed) && LegacyBodyEdited(installed, ours))
                    {
                        warnings.Add($"left user-edited command alone: {Path.GetFileName(file)}");
                        continue;
                    }
                }
                File.WriteAllText(dest, ours);
                copied++;
            }
        }
        else warnings.Add($"command pack not found at {packSrc}");

        // 2. hook merge into settings.json
        var settingsPath = Path.Combine(home, ".zcode", "settings.json");
        JsonObject settings;
        if (File.Exists(settingsPath))
            settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            settings = new JsonObject();
        }

        var hooks = settings["hooks"] as JsonObject ?? new JsonObject();
        settings["hooks"] = hooks;
        var submit = hooks["UserPromptSubmit"] as JsonArray ?? new JsonArray();
        hooks["UserPromptSubmit"] = submit;

        var hookCommand =
            $$"""curl -sf -X POST http://127.0.0.1:{{port}}/hooks/zcode/prompts -H 'Content-Type: application/json' -d '{"session_id":"${CLAUDE_SESSION_ID}","prompt":"$PROMPT","cwd":"$CWD"}' || true""";
        var alreadyPresent = submit.OfType<JsonObject>()
            .Any(h => h["command"]?.ToString() == hookCommand);
        if (!alreadyPresent)
        {
            submit.Add(new JsonObject
            {
                ["matcher"] = "",
                ["command"] = hookCommand
            });
            File.WriteAllText(settingsPath, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // 3. AGENTS.md snippet
        var agentsPath = Path.Combine(repo, "AGENTS.md");
        var snippet = """

            <!-- agent-hq -->
            ## Development is managed via Agent HQ

            Plans live in `docs/plans/` and move through a managed lifecycle (`/plan` commands).
            Before working: read `docs/plans/lessons.md` — the repository's accumulated lessons.
            The Agent HQ MCP tools are self-describing; orient with `get_overview`.
            """;
        if (File.Exists(agentsPath))
        {
            if (!File.ReadAllText(agentsPath).Contains("<!-- agent-hq -->"))
                File.AppendAllText(agentsPath, snippet);
        }
        else
        {
            File.WriteAllText(agentsPath, "# AGENTS" + snippet);
        }

        Console.WriteLine($"Agent HQ ZCode integration installed (commands copied: {copied}, hook port {port}).");
        foreach (var w in warnings) Console.WriteLine($"warning: {w}");
        return warnings.Count == 0 ? 0 : 2;
    }

    /// <summary>True for a file installed before the rename: same marker, previous name.</summary>
    internal static bool IsLegacyMarker(string content) =>
        content.Split('\n', 2)[0].Trim().StartsWith("<!-- dev-bridge v");

    /// <summary>Body equality ignoring the marker line — the test for "nobody edited this".</summary>
    internal static bool LegacyBodyEdited(string installed, string ours) =>
        !string.Equals(AfterFirstLine(installed), AfterFirstLine(ours), StringComparison.Ordinal);

    private static string AfterFirstLine(string content) =>
        content.Contains('\n') ? content[(content.IndexOf('\n') + 1)..].ReplaceLineEndings("\n").TrimEnd() : "";

    internal static string? FirstLineMarker(string content)
    {
        var first = content.Split('\n', 2)[0].Trim();
        return first.StartsWith("<!-- agent-hq v") ? first : null;
    }
}
