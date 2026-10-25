using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentHQ.Tests;

public class PlanLifecycleIntegrationTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private static string Get(JsonElement e, string p) => e.GetProperty(p).ToString();

    [Fact]
    public async Task SubmitWritesFileAndRow()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Lifecycle basics", AgentHQApp.PlanMarkdown());
        Assert.Equal("lifecycle-basics", Get(plan, "slug"));
        Assert.Equal("submitted", Get(plan, "status"));
        var file = Path.Combine(app.PlansDir, "lifecycle-basics.md");
        Assert.True(File.Exists(file));

        var detail = await app.GetPlanAsync(Get(plan, "id"));
        Assert.Equal(2, detail.GetProperty("progress").GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SubmitSuffixesSlugsOnCollisionAndWarnsSoftValidation()
    {
        await app.RegisterRepositoryAsync();
        var a = await app.SubmitPlanAsync("Dup plan", "# no sections\n");
        var b = await app.SubmitPlanAsync("Dup plan", "# no sections\n");
        Assert.Equal("dup-plan", Get(a, "slug"));
        Assert.Equal("dup-plan-2", Get(b, "slug"));
        Assert.Contains(b.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("Verification Criteria"));
    }

    [Fact]
    public async Task HookDrivesFullHappyPathWithAttribution()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Happy path", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var path = "docs/plans/happy-path.md";

        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess1", $"/plan start {path}")).StatusCode);
        Assert.Equal("refining", Get(await app.GetPlanAsync(id), "status"));
        Assert.Equal("sess1", Get(await app.GetPlanAsync(id), "session"));   // linkage

        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess2", $"/plan review {path}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess1", $"/plan implement {path}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess1", $"/plan implemented {path}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess2", $"/plan verify {path}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess2", $"/plan pass {path}")).StatusCode);

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("verified", Get(detail, "status"));
        Assert.Equal("sess1", Get(detail, "implementedBy"));
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(new[] { "submitted→refining", "refining→in_review", "in_review→implementing",
                              "implementing→implemented", "implemented→verifying", "verifying→verified" },
            history.Select(h => $"{h.GetProperty("fromStatus")}→{h.GetProperty("toStatus")}"));
        // Nulls are omitted from JSON: absence of gitHead means "not a git repo" (the temp
        // repository is not one), so no gitHead key is expected at all.
        Assert.All(history, h => Assert.False(h.TryGetProperty("gitHead", out _)));
    }

    [Fact]
    public async Task ShortCutEntryRecordsIntermediateImplementedAttributedToVerifier()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Short cut", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        foreach (var cmd in new[] { "/plan start docs/plans/short-cut.md", "/plan review docs/plans/short-cut.md",
                                    "/plan implement docs/plans/short-cut.md",
                                    "/plan verify docs/plans/short-cut.md" })
            Assert.Equal(HttpStatusCode.OK, (await app.HookAsync("sess1", cmd)).StatusCode);

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("verifying", Get(detail, "status"));
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        var last = history[^2..];
        Assert.Equal("implemented", last[0].GetProperty("toStatus").GetString());
        Assert.Equal("sess1", last[0].GetProperty("actorSessionId").GetString());
        Assert.Contains("short-cut", last[0].GetProperty("command").GetString());
    }

    [Fact]
    public async Task FailLoopThenPass()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Fix loop", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var p = "docs/plans/fix-loop.md";
        foreach (var (s, cmd) in new[]
                 {
                     ("sess1", "/plan start"), ("sess2", "/plan review"), ("sess1", "/plan implement"),
                     ("sess1", "/plan implemented"), ("sess2", "/plan verify"),
                 })
            Assert.Equal(HttpStatusCode.OK, (await app.HookAsync(s, $"{cmd} {p}")).StatusCode);

        await app.HookAsync("sess2", $"/plan fail {p} tests fail in parser");
        Assert.Equal("fixing", Get(await app.GetPlanAsync(id), "status"));
        await app.HookAsync("sess1", $"/plan implemented {p}");
        Assert.Equal("implemented", Get(await app.GetPlanAsync(id), "status"));
        await app.HookAsync("sess2", $"/plan verify {p}");
        await app.HookAsync("sess2", $"/plan pass {p}");
        Assert.Equal("verified", Get(await app.GetPlanAsync(id), "status"));
    }

    [Fact]
    public async Task SeparationOfDutiesWarnsButStillTransitions()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Sod check", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var p = "docs/plans/sod-check.md";
        await app.HookAsync("sess1", $"/plan start {p}");
        await app.HookAsync("sess1", $"/plan review {p}");
        await app.HookAsync("sess1", $"/plan implement {p}");
        await app.HookAsync("sess1", $"/plan implemented {p}");
        await app.HookAsync("sess1", $"/plan verify {p}");
        var resp = await app.HookAsync("sess1", $"/plan pass {p}");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("separation-of-duties"));
        var detail = await app.GetPlanAsync(id);
        Assert.Equal("verified", Get(detail, "status"));   // soft: transitioned anyway
        Assert.True(detail.GetProperty("history").EnumerateArray().Last()
            .GetProperty("separationOfDutiesWarning").GetBoolean());
    }

    [Fact]
    public async Task IllegalCommandIsRejected()
    {
        await app.RegisterRepositoryAsync();
        await app.SubmitPlanAsync("Illegal move", AgentHQApp.PlanMarkdown());
        var seq = await app.LastSeqAsync();
        var resp = await app.HookAsync("sess1", "/plan implement docs/plans/illegal-move.md");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("submitted", Get(await app.GetPlanAsync(
            Get(await app.SubmitPlanAsync("Illegal move", AgentHQApp.PlanMarkdown()), "id")), "status"));
        await app.WaitForEventAsync("hook.rejected", seq);
    }

    [Fact]
    public async Task UnresolvableFileArgIsRejected()
    {
        await app.RegisterRepositoryAsync();
        var seq = await app.LastSeqAsync();
        var resp = await app.HookAsync("sess1", "/plan start docs/plans/nope.md");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var rejected = await AgentHQApp.WaitForAsync(async () =>
            (await app.EventsSinceAsync(seq)).Any(e => e.Type == "hook.rejected"));
        Assert.True(rejected);
    }

    [Fact]
    public async Task UnregisteredPlanFileIsNeverActedOn()
    {
        // A plan-shaped file that exists in docs/plans/ but was never submitted is not a plan.
        await app.RegisterRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(app.PlansDir, "stray.md"), AgentHQApp.PlanMarkdown());
        var seq = await app.LastSeqAsync();
        var resp = await app.HookAsync("sess1", "/plan start docs/plans/stray.md");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        await app.WaitForEventAsync("hook.rejected", seq);
        var plans = await app.Client.GetFromJsonAsync<JsonElement>("/plans");
        Assert.DoesNotContain(plans.EnumerateArray(), p => p.GetProperty("slug").GetString() == "stray");
        Assert.Equal(AgentHQApp.PlanMarkdown(), await File.ReadAllTextAsync(Path.Combine(app.PlansDir, "stray.md")));
    }

    [Fact]
    public async Task CwdOutsideRegisteredRepoIsRejected()
    {
        var resp = await app.HookAsync("sess1", "/plan start x", cwd: "/somewhere/else");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task BlockedIsOrthogonalToStatus()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Block me", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var p = "docs/plans/block-me.md";
        await app.HookAsync("sess1", $"/plan start {p}");
        await app.HookAsync("sess1", $"/plan block {p} waiting on credentials");
        var detail = await app.GetPlanAsync(id);
        Assert.Equal("refining", Get(detail, "status"));   // unchanged
        Assert.True(detail.GetProperty("blocked").GetBoolean());
        Assert.Equal("waiting on credentials", Get(detail, "blockedReason"));

        var blockedList = await app.Client.GetFromJsonAsync<JsonElement>("/plans?blocked=true");
        Assert.Contains(blockedList.EnumerateArray(), x => Get(x, "id") == id);

        await app.HookAsync("sess1", $"/plan unblock {p}");
        Assert.False((await app.GetPlanAsync(id)).GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task DependenciesAreSoftAndReadyQueueHonoursThem()
    {
        await app.RegisterRepositoryAsync();
        var dep = await app.SubmitPlanAsync("Dep plan", AgentHQApp.PlanMarkdown());
        var main = await app.SubmitPlanAsync("Main plan", AgentHQApp.PlanMarkdown());
        var depId = Get(dep, "id");
        var mainId = Get(main, "id");

        Assert.Equal(HttpStatusCode.OK,
            (await app.Client.PostAsJsonAsync($"/plans/{mainId}/dependencies", new { requiresPlanId = depId })).StatusCode);
        // cycle + self-reference rejected
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.Client.PostAsJsonAsync($"/plans/{depId}/dependencies", new { requiresPlanId = mainId })).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.Client.PostAsJsonAsync($"/plans/{mainId}/dependencies", new { requiresPlanId = mainId })).StatusCode);

        await app.HookAsync("sess1", "/plan start docs/plans/main-plan.md");
        await app.HookAsync("sess1", "/plan review docs/plans/main-plan.md");
        var ready = await app.Client.GetFromJsonAsync<JsonElement>("/plans?ready=true");
        Assert.DoesNotContain(ready.EnumerateArray(), x => Get(x, "id") == mainId);   // dep not verified

        var resp = await app.HookAsync("sess1", "/plan implement docs/plans/main-plan.md");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("dep-plan"));
    }

    [Fact]
    public async Task PriorityListingOrder()
    {
        await app.RegisterRepositoryAsync();
        var low = await app.SubmitPlanAsync("Pri low", AgentHQApp.PlanMarkdown());
        var high = await app.SubmitPlanAsync("Pri high", AgentHQApp.PlanMarkdown());
        await app.Client.PatchAsJsonAsync($"/plans/{Get(high, "id")}", new { priority = 10 });
        var ordering = await app.Client.GetFromJsonAsync<JsonElement>("/plans");
        var mine = ordering.EnumerateArray().Where(x => Get(x, "id") is var i && (i == Get(high, "id") || i == Get(low, "id")))
            .Select(x => Get(x, "id")).ToList();
        Assert.Equal(Get(high, "id"), mine[0]);
    }

    [Fact]
    public async Task ManualStatusPatchUsesSameRules()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Manual patch", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await app.Client.PatchAsJsonAsync($"/plans/{id}/status", new { status = "verified" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await app.Client.PatchAsJsonAsync($"/plans/{id}/status", new { status = "deferred", by = "openclaw", note = "later" })).StatusCode);
        Assert.Equal("deferred", Get(await app.GetPlanAsync(id), "status"));
        await app.Client.PatchAsJsonAsync($"/plans/{id}/status", new { status = "refining", by = "andy" });
        var detail = await app.GetPlanAsync(id);
        Assert.Equal("refining", Get(detail, "status"));
        Assert.Equal("andy", detail.GetProperty("history").EnumerateArray().Last().GetProperty("actorType").GetString());
    }
}
