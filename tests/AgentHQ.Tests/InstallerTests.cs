using System.Text.Json;
using AgentHQ.Commands.ZCode;
using Xunit;

namespace AgentHQ.Tests;

public class InstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "agenthq-home-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "agenthq-repo-" + Guid.NewGuid().ToString("N"));

    public InstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(_home, ".zcode"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    [Fact]
    public void InstallsCommandsHookAndAgentsSnippet()
    {
        var exit = Installer.Run(_home, _repo, "5317");
        Assert.Equal(0, exit);

        var commands = Directory.GetFiles(Path.Combine(_home, ".zcode", "commands", "plan"), "*.md");
        Assert.Equal(14, commands.Length);
        foreach (var file in commands)
            Assert.StartsWith("<!-- agent-hq v1 -->", File.ReadAllText(file));

        var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, ".zcode", "settings.json")));
        var cmd = settings.RootElement.GetProperty("hooks").GetProperty("UserPromptSubmit")[0]
            .GetProperty("command").GetString()!;
        Assert.Contains("http://127.0.0.1:5317/hooks/zcode/prompts", cmd);
        Assert.Contains("${CLAUDE_SESSION_ID}", cmd);
        Assert.EndsWith("|| true", cmd);

        Assert.Contains("<!-- agent-hq -->", File.ReadAllText(Path.Combine(_repo, "AGENTS.md")));
    }

    [Fact]
    public void SecondRunChangesNothing()
    {
        Installer.Run(_home, _repo, "5317");
        var snapshot = Snapshot();
        var agentSnapshot = File.ReadAllText(Path.Combine(_repo, "AGENTS.md"));

        Installer.Run(_home, _repo, "5317");
        Assert.Equal(snapshot, Snapshot());
        Assert.Equal(agentSnapshot, File.ReadAllText(Path.Combine(_repo, "AGENTS.md")));
    }

    [Fact]
    public void MergePreservesUnrelatedSettingsKeys()
    {
        File.WriteAllText(Path.Combine(_home, ".zcode", "settings.json"),
            """{ "theme": "dark", "hooks": { "Stop": [ { "command": "echo bye" } ] }, "nested": { "a": 1 } }""");
        Installer.Run(_home, _repo, "5317");
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, ".zcode", "settings.json")));
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("nested").GetProperty("a").GetInt32());
        Assert.Equal("echo bye", doc.RootElement.GetProperty("hooks").GetProperty("Stop")[0]
            .GetProperty("command").GetString());
        Assert.Single(doc.RootElement.GetProperty("hooks").GetProperty("UserPromptSubmit").EnumerateArray());
    }

    [Fact]
    public void UserEditedCommandIsLeftAlone()
    {
        var dest = Path.Combine(_home, ".zcode", "commands", "plan");
        Directory.CreateDirectory(dest);
        var edited = "# my own start command\n\ncustom\n";
        File.WriteAllText(Path.Combine(dest, "start.md"), edited);

        var exit = Installer.Run(_home, _repo, "5317");
        Assert.Equal(2, exit);   // warnings
        Assert.Equal(edited, File.ReadAllText(Path.Combine(dest, "start.md")));
        // ...but the other commands are still installed
        Assert.StartsWith("<!-- agent-hq v1 -->", File.ReadAllText(Path.Combine(dest, "implement.md")));
    }

    [Fact]
    public void LegacyInstallIsUpgradedNotWarnedAbout()
    {
        // What a pre-rename install looks like: same body, old marker on line one.
        var dest = Path.Combine(_home, ".zcode", "commands", "plan");
        Directory.CreateDirectory(dest);
        var packSrc = Path.Combine(AppContext.BaseDirectory, "Commands");
        var ours = File.ReadAllText(Path.Combine(packSrc, "implement.md"));
        var body = ours[ours.IndexOf('\n')..];
        File.WriteAllText(Path.Combine(dest, "implement.md"), "<!-- dev-bridge v1 -->" + body);

        var exit = Installer.Run(_home, _repo, "5317");
        Assert.Equal(0, exit);
        Assert.StartsWith("<!-- agent-hq v1 -->", File.ReadAllText(Path.Combine(dest, "implement.md")));
    }

    [Fact]
    public void LegacyInstallWithEditedBodyIsLeftAlone()
    {
        var dest = Path.Combine(_home, ".zcode", "commands", "plan");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "start.md"), "<!-- dev-bridge v1 -->\n---\nmy tweak\n");

        var exit = Installer.Run(_home, _repo, "5317");
        Assert.Equal(2, exit);
        Assert.Contains("my tweak", File.ReadAllText(Path.Combine(dest, "start.md")));
    }

    private string Snapshot() => string.Join("\n", Directory.GetFiles(_home, "*", SearchOption.AllDirectories)
        .OrderBy(f => f)
        .Select(f => f + ":" + File.ReadAllText(f)));
}
