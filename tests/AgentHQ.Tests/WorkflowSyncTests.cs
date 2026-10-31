using AgentHQ.Commands.ZCode;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The sync-workflow verb: installs the /plan:* workflow pack into a repository's
/// project-scope `.zcode/commands/plan/`, marker-guarded and idempotent, like the installer.
/// </summary>
public class WorkflowSyncTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "agenthq-wfrepo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private string DestDir => Path.Combine(_repo, ".zcode", "commands", "plan");

    [Fact]
    public void InstallsPackIntoProjectScope()
    {
        var exit = WorkflowSync.Run(_repo, force: false);
        Assert.Equal(0, exit);

        var files = Directory.GetFiles(DestDir, "*.md");
        Assert.Equal(6, files.Length);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.StartsWith("<!-- agent-hq v1 -->", text);
            Assert.Contains("name: plan:", text);
        }
        // abort was renamed: the pack must not carry it under the old name.
        Assert.DoesNotContain(Path.Combine(DestDir, "abort.md"), files);
    }

    [Fact]
    public void SecondRunChangesNothing()
    {
        WorkflowSync.Run(_repo, force: false);
        var snapshot = Snapshot();
        Assert.Equal(0, WorkflowSync.Run(_repo, force: false));
        Assert.Equal(snapshot, Snapshot());
    }

    [Fact]
    public void PreservesUserAuthoredFileWithoutMarker()
    {
        Directory.CreateDirectory(DestDir);
        File.WriteAllText(Path.Combine(DestDir, "start.md"), "mine");
        File.WriteAllText(Path.Combine(DestDir, "notes.md"), "unrelated file");

        var exit = WorkflowSync.Run(_repo, force: false);

        Assert.Equal(2, exit);  // deliberate, visible partial install
        Assert.Equal("mine", File.ReadAllText(Path.Combine(DestDir, "start.md")));
        Assert.Equal("unrelated file", File.ReadAllText(Path.Combine(DestDir, "notes.md")));
        // the rest of the pack still installs
        Assert.True(File.Exists(Path.Combine(DestDir, "review.md")));
    }

    [Fact]
    public void ForceOverwritesUnmarkedFile()
    {
        Directory.CreateDirectory(DestDir);
        File.WriteAllText(Path.Combine(DestDir, "start.md"), "old abort-era template");

        var exit = WorkflowSync.Run(_repo, force: true);

        Assert.Equal(0, exit);
        Assert.StartsWith("<!-- agent-hq v1 -->", File.ReadAllText(Path.Combine(DestDir, "start.md")));
    }

    private string[] Snapshot() => Directory.GetFiles(DestDir, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => $"{p}:{File.ReadAllText(p).GetHashCode()}")
        .ToArray();
}
