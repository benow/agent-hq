using System.Net.Http.Json;
using System.Text.Json;
using AgentHQ.Infrastructure;
using AgentHQ.Infrastructure.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The plan-workflow contract: colon and space spellings move the hub identically, a hook
/// prompt typed inside a ZCode worktree resolves back to its repository, and the done/abandon
/// archive move re-points the registration instead of stranding it.
/// </summary>
public class PlanWorkflowTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private static string Get(JsonElement e, string p) => e.GetProperty(p).ToString();

    private static async Task<HttpResponseMessage> HookOkAsync(HttpClient client, string sessionId, string prompt, string cwd)
    {
        var resp = await client.PostAsJsonAsync("/hooks/zcode/prompts", new { session_id = sessionId, prompt, cwd });
        var body = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"hook {prompt}: {(int)resp.StatusCode} {body}");
        return resp;
    }

    [Fact]
    public async Task ColonAndSpaceSpellingsProduceIdenticalTransitions()
    {
        var repoId = await app.RegisterRepositoryAsync();
        var colon = await app.SubmitPlanAsync("colon spelling", AgentHQApp.PlanMarkdown());
        var space = await app.SubmitPlanAsync("space spelling", AgentHQApp.PlanMarkdown());
        var colonFile = "docs/plans/colon-spelling.md";
        var spaceFile = "docs/plans/space-spelling.md";

        await HookOkAsync(app.Client, "sess1", $"/plan:start {colonFile}", app.RepoRoot);
        await HookOkAsync(app.Client, "sess1", $"/plan start {spaceFile}", app.RepoRoot);
        Assert.Equal(Get(await app.GetPlanAsync(Get(colon, "id")), "status"),
                     Get(await app.GetPlanAsync(Get(space, "id")), "status"));

        await HookOkAsync(app.Client, "sess2", $"/plan:review {colonFile}", app.RepoRoot);
        Assert.Equal("in_review", Get(await app.GetPlanAsync(Get(colon, "id")), "status"));
        await HookOkAsync(app.Client, "sess1", $"/plan:implement {colonFile}", app.RepoRoot);
        Assert.Equal("implementing", Get(await app.GetPlanAsync(Get(colon, "id")), "status"));
        Assert.Equal("sess1", Get(await app.GetPlanAsync(Get(colon, "id")), "session"));
    }

    [Fact]
    public async Task WorktreeCwdResolvesToRegisteredRepository()
    {
        var repoId = await app.RegisterRepositoryAsync();
        var worktreesRoot = Path.Combine(Path.GetTempPath(), "agenthq-wt-" + Guid.NewGuid().ToString("N"));
        var leaf = Path.GetFileName(app.RepoRoot.TrimEnd('/'));
        var worktree = Path.Combine(worktreesRoot, leaf, "some-plan");
        Directory.CreateDirectory(worktree);
        try
        {
            var store = new RepositoryStore(app.Get<AgentHqDb>(), worktreesRoot);
            var byWorktree = await store.ResolveByCwdAsync(worktree);
            Assert.NotNull(byWorktree);
            Assert.Equal(repoId, byWorktree!.Id);

            // An unregistered project under the worktrees root still does not resolve.
            Assert.Null(await store.ResolveByCwdAsync(Path.Combine(worktreesRoot, "no-such-repo", "w")));

            // Without a configured worktrees root, the mapping is off entirely.
            var plain = app.Get<RepositoryStore>();
            Assert.Null(await plain.ResolveByCwdAsync(worktree));
        }
        finally
        {
            try { Directory.Delete(worktreesRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HookFromInsideWorktreeMovesThePlan()
    {
        var worktreesRoot = Path.Combine(Path.GetTempPath(), "agenthq-wth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktreesRoot);
        WebApplication? extra = null;
        try
        {
            extra = await app.StartAsync(worktreesRoot);
            var client = extra.GetTestClient();
            await client.PostAsJsonAsync("/repositories", new { rootPath = app.RepoRoot });
            var plan = await app.SubmitPlanAsync("worktree flow", AgentHQApp.PlanMarkdown());
            var id = Get(plan, "id");

            var leaf = Path.GetFileName(app.RepoRoot.TrimEnd('/'));
            var cwd = Path.Combine(worktreesRoot, leaf, "worktree-flow");
            Directory.CreateDirectory(cwd);
            await HookOkAsync(client, "sess1", "/plan:start docs/plans/worktree-flow.md", cwd);
            Assert.Equal("refining", Get(await app.GetPlanAsync(id), "status"));
            await HookOkAsync(client, "sess2", "/plan:review docs/plans/worktree-flow.md", cwd);
            Assert.Equal("in_review", Get(await app.GetPlanAsync(id), "status"));
        }
        finally
        {
            if (extra is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await extra.StopAsync(stop.Token); } catch (OperationCanceledException) { }
                try { await extra.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { }
            }
            try { Directory.Delete(worktreesRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ArchiveMoveRePointsInsteadOfStranding()
    {
        var repoId = await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("archive move", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var plansDir = app.PlansDir;
        var doneDir = Path.Combine(plansDir, "done");
        Directory.CreateDirectory(doneDir);
        var before = await app.Get<PlanStore>().GetAsync(id);
        Assert.NotNull(before);

        // The done/abandon archive move: old path leaves the watched directory.
        File.Move(before!.FilePath, Path.Combine(doneDir, "archive-move.md"));

        var repointed = await AgentHQApp.WaitForAsync(async () =>
        {
            var row = await app.Get<PlanStore>().GetAsync(id);
            return row is not null && row.FilePath == Path.Combine(doneDir, "archive-move.md");
        });
        Assert.True(repointed, "plan was not re-pointed to its archived location");

        // The plan stays fully addressable at its archived location.
        var repo = (await app.Get<RepositoryStore>().ListAsync()).Single(r => r.Id == repoId);
        var byDonePath = await app.Get<PlanStore>().ResolveFileArgAsync(repo, "done/archive-move.md");
        Assert.Single(byDonePath);
        var bySlug = await app.Get<PlanStore>().ResolveFileArgAsync(repo, "archive-move");
        Assert.Single(bySlug);
    }
}
