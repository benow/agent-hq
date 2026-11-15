using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// Multi-file plans: a drafted directory (docs/plans/&lt;name&gt;/00-OVERVIEW.md + part files)
/// is adopted without rewriting, /plan:start binds to the overview, part-file edits bump the
/// umbrella's version and aggregate progress, and a part registered as its own plan tracks
/// itself and leaves the umbrella's count.
/// </summary>
public class MultiFilePlanTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private static string Get(JsonElement e, string p) => e.GetProperty(p).ToString();
    private static int Int(JsonElement e, string p) => e.GetProperty(p).GetInt32();

    private static string Doc(string steps) => $"""
        # Plan

        ## Goal
        test

        ## Steps
        {steps}

        ## Verification Criteria
        v1

        ## Documentation
        none
        """;

    /// <summary>Writes an umbrella plan directory and returns the overview's repo-relative path.</summary>
    private static async Task<string> WriteDirPlanAsync(string root, string name)
    {
        var dir = Path.Combine(root, "docs", "plans", name);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "00-OVERVIEW.md"),
            Doc("- [ ] overview step\n- [x] overview done"));
        await File.WriteAllTextAsync(Path.Combine(dir, "01-part.md"),
            Doc("- [ ] part one\n- [ ] part two"));
        await File.WriteAllTextAsync(Path.Combine(dir, "02-part.md"),
            Doc("- [x] part three"));
        return $"docs/plans/{name}/00-OVERVIEW.md";
    }

    private async Task<(string Id, JsonElement Row)> AdoptAsync(string repoId, string name, string filePath)
    {
        var resp = await app.Client.PostAsJsonAsync("/plans", new { repo = repoId, name, filePath });
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"adopt {filePath}: {(int)resp.StatusCode} {body}");
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (Get(json, "id"), await app.GetPlanAsync(Get(json, "id")));
    }

    [Fact]
    public async Task AdoptedDirectoryPlanStartsAndAggregatesProgress()
    {
        var (repoId, root) = await app.NewRepoAsync();
        var overview = await WriteDirPlanAsync(root, "umb");

        var (id, row) = await AdoptAsync(repoId, "umb", overview);
        Assert.Equal("submitted", Get(row, "status"));
        // 2 overview boxes (1 checked) + 2 in 01-part (0) + 1 in 02-part (1 checked).
        Assert.Equal(2, Int(row.GetProperty("progress"), "checkedCount"));
        Assert.Equal(5, Int(row.GetProperty("progress"), "total"));
        Assert.EndsWith("00-OVERVIEW.md", Get(row, "file").Replace("\\", "/"));

        var hook = await app.Client.PostAsJsonAsync("/hooks/zcode/prompts",
            new { session_id = "sess1", prompt = $"/plan:start {overview}", cwd = root });
        Assert.True(hook.IsSuccessStatusCode, await hook.Content.ReadAsStringAsync());
        Assert.Equal("refining", Get(await app.GetPlanAsync(id), "status"));
    }

    [Fact]
    public async Task PartFileEditBumpsUmbrellaVersionAndProgress()
    {
        var (repoId, root) = await app.NewRepoAsync();
        var overview = await WriteDirPlanAsync(root, "umb");
        var (id, row) = await AdoptAsync(repoId, "umb", overview);
        var version = Int(row, "version");
        var since = await app.LastSeqAsync();

        var part = Path.Combine(root, "docs", "plans", "umb", "01-part.md");
        await File.WriteAllTextAsync(part, Doc("- [x] part one\n- [ ] part two"));
        await app.WaitForEventAsync("plan.updated", since);

        var after = await app.GetPlanAsync(id);
        Assert.Equal(version + 1, Int(after, "version"));
        Assert.Equal(3, Int(after.GetProperty("progress"), "checkedCount"));
    }

    [Fact]
    public async Task RegisteredPartTracksItselfAndLeavesTheUmbrella()
    {
        var (repoId, root) = await app.NewRepoAsync();
        var overview = await WriteDirPlanAsync(root, "umb");
        var (umbrellaId, row) = await AdoptAsync(repoId, "umb", overview);

        // Registering the part as its own plan removes it from the umbrella's count
        // (2 + 1 remain) — no event wait needed: registration refreshes siblings inline.
        var (partId, _) = await AdoptAsync(repoId, "umb part one", "docs/plans/umb/01-part.md");
        var umbrella = await app.GetPlanAsync(umbrellaId);
        Assert.Equal(2, Int(umbrella.GetProperty("progress"), "checkedCount"));
        Assert.Equal(3, Int(umbrella.GetProperty("progress"), "total"));
        var umbrellaVersion = Int(umbrella, "version");

        // An edit to the registered part bumps the part row only; the umbrella excludes it.
        var since = await app.LastSeqAsync();
        var part = Path.Combine(root, "docs", "plans", "umb", "01-part.md");
        await File.WriteAllTextAsync(part, Doc("- [x] part one\n- [x] part two"));
        await app.WaitForEventAsync("plan.updated", since);
        Assert.Equal(2, Int((await app.GetPlanAsync(partId)).GetProperty("progress"), "checkedCount"));
        await Task.Delay(1200); // bounded negative window: one debounce+settle cycle
        Assert.Equal(umbrellaVersion, Int(await app.GetPlanAsync(umbrellaId), "version"));
    }

    [Fact]
    public async Task AdoptIsIdempotentByPath()
    {
        var (repoId, root) = await app.NewRepoAsync();
        var overview = await WriteDirPlanAsync(root, "umb");
        var (id, _) = await AdoptAsync(repoId, "umb", overview);

        var second = await app.Client.PostAsJsonAsync("/plans",
            new { repo = repoId, name = "umb again", filePath = overview });
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("existing").GetBoolean());
        Assert.Equal(id, Get(body, "id"));
    }

    [Fact]
    public async Task AdoptRejectsMissingFilesAndPathsOutsideThePlansDir()
    {
        var (repoId, root) = await app.NewRepoAsync();

        var missing = await app.Client.PostAsJsonAsync("/plans",
            new { repo = repoId, name = "ghost", filePath = "docs/plans/ghost/00-OVERVIEW.md" });
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("no file at", await missing.Content.ReadAsStringAsync());

        var outside = await app.Client.PostAsJsonAsync("/plans",
            new { repo = repoId, name = "evil", filePath = "../outside-plan.md" });
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, outside.StatusCode);
        Assert.Contains("does not resolve inside", await outside.Content.ReadAsStringAsync());

        await File.WriteAllTextAsync(Path.Combine(root, "notes.txt"), "not markdown");
        var notMd = await app.Client.PostAsJsonAsync("/plans",
            new { repo = repoId, name = "notes", filePath = "notes.txt" });
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, notMd.StatusCode);
        Assert.Contains("not a markdown file", await notMd.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PutContentOnDirectoryPlanIsASelfWrite()
    {
        var (repoId, root) = await app.NewRepoAsync();
        var overview = await WriteDirPlanAsync(root, "umb");
        var (id, row) = await AdoptAsync(repoId, "umb", overview);
        var version = Int(row, "version");

        var put = await app.Client.PutAsJsonAsync($"/plans/{id}/content",
            new { markdown = Doc("- [x] overview step\n- [x] overview done") });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());

        // Self-write: no version bump, no event — and progress reflects the directory aggregate.
        var since = await app.LastSeqAsync();
        await Task.Delay(1200); // bounded negative window: one debounce+settle cycle
        var events = await app.EventsSinceAsync(since);
        Assert.DoesNotContain(events, e => e.Type == "plan.updated");
        var after = await app.GetPlanAsync(id);
        Assert.Equal(version, Int(after, "version"));
        Assert.Equal(3, Int(after.GetProperty("progress"), "checkedCount"));
    }
}
