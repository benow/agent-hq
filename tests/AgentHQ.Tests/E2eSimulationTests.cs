using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The whole loop with no live agents: the harness plays every actor — ZCode sessions via the
/// hook feed, the human/OpenClaw via PATCH, and the plan file itself via the watcher.
/// </summary>
public class E2eSimulationTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private static string Get(JsonElement e, string p) => e.GetProperty(p).ToString();

    /// <summary>
    /// Opens an SSE stream. The returned handle owns its cancellation source; tests must
    /// dispose it (using) so a timed-out read never leaves a live stream on a disposed host.
    /// </summary>
    private sealed class SseStream : IDisposable
    {
        public required HttpResponseMessage Sse { get; init; }
        public required StreamReader Reader { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public void Dispose() { Cts.Cancel(); Reader.Dispose(); Sse.Dispose(); Cts.Dispose(); }
    }

    private async Task<SseStream> OpenSseAsync(long lastEventId, int seconds = 20)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var req = new HttpRequestMessage(HttpMethod.Get, "/events");
        req.Headers.Add("Last-Event-ID", lastEventId.ToString());
        var resp = await app.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return new SseStream { Sse = resp, Cts = cts, Reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token)) };
    }

    [Fact]
    public async Task FullLoopPassPath()
    {
        using var sse = await OpenSseAsync(await app.LastSeqAsync(), 30);
        var reader = sse.Reader;
        async Task<string> WaitSseAsync(string type, int timeoutMs = 10000)
        {
            var buf = "";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(sse.Cts.Token);
            cts.CancelAfter(timeoutMs);
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line is null) throw new InvalidOperationException("SSE closed");
                    buf += line + "\n";
                    // An SSE frame ends with a blank line; only then is the payload complete.
                    if (buf.Contains("event: " + type) && buf.EndsWith("\n\n")) return buf;
                }
            }
            catch (OperationCanceledException) { throw new TimeoutException($"no SSE {type}; got:\n{buf}"); }
        }

        // 1. a substantive session change (ZCode wrote a new title) → session.updated on SSE.
        //    A bare time_updated bump deliberately does not emit: see SessionSyncService.
        var sessionInfo = (await app.Get<Core.Domain.IAgentProvider>().GetSessionAsync("sess1"))!;
        await app.ZCode.SetSessionTitleAsync("sess1", "E2E: retitled by ZCode");
        await app.Get<Infrastructure.Services.SessionSyncService>().MirrorAsync(
            sessionInfo.Info with { Title = "E2E: retitled by ZCode" });
        var sseText = await WaitSseAsync("session.updated");
        Assert.Contains("E2E: retitled by ZCode", sseText);

        // 2. OpenClaw submits through MCP-equivalent REST; start via hook
        var repoId = await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("E2E plan", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var file = "docs/plans/e2e-plan.md";

        var start = await app.HookAsync("sess1", $"/plan start {file}");
        Assert.Equal(200, (int)start.StatusCode);
        Assert.Contains("plan.status_changed", await WaitSseAsync("plan.status_changed"));
        Assert.Equal("sess1", Get(await app.GetPlanAsync(id), "session"));

        await app.HookAsync("sess2", $"/plan review {file}");
        var impl = await app.HookAsync("sess1", $"/plan implement {file}");
        Assert.Equal(200, (int)impl.StatusCode);

        // 3. the implementing session ticks steps in the plan file itself → version bump
        var path = Path.Combine(app.PlansDir, "e2e-plan.md");
        var content = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, content.Replace("- [ ] step one", "- [x] step one"));
        Assert.Contains("plan.updated", await WaitSseAsync("plan.updated"));
        var afterEdit = await app.GetPlanAsync(id);
        Assert.Equal(1, afterEdit.GetProperty("progress").GetProperty("checkedCount").GetInt32());

        // 4. implemented → verify → pass, plus a Lessons consult (status log evidence unchanged by us)
        Assert.Equal(200, (int)(await app.HookAsync("sess1", $"/plan implemented {file}")).StatusCode);
        Assert.Equal("sess1", Get(await app.GetPlanAsync(id), "implementedBy"));
        Assert.Equal(200, (int)(await app.HookAsync("sess3", $"/plan verify {file}")).StatusCode);
        Assert.Equal(200, (int)(await app.HookAsync("sess3", $"/plan pass {file}")).StatusCode);
        // the human closes it out
        var done = await app.Client.PatchAsJsonAsync($"/plans/{id}/status", new { status = "done", by = "andy" });
        Assert.Equal(200, (int)done.StatusCode);

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("done", Get(detail, "status"));
        var history = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal("submitted", history[0].GetProperty("fromStatus").GetString());
        Assert.Equal("done", history[^1].GetProperty("toStatus").GetString());
        Assert.All(history.Where(h => h.GetProperty("actorType").GetString() == "session"),
            h => Assert.False(string.IsNullOrEmpty(h.GetProperty("actorSessionId").GetString())));
        Assert.Equal("andy", history[^1].GetProperty("actorType").GetString());

        // 6. reconnect with Last-Event-ID → gap replay
        var lastSeq = await app.LastSeqAsync();
        await app.SubmitPlanAsync("After reconnect", AgentHQApp.PlanMarkdown());
        using var resumed = await OpenSseAsync(lastSeq, 10);
        var replay = "";
        try
        {
            while (!replay.Contains("plan.created"))
            {
                var line = await resumed.Reader.ReadLineAsync(resumed.Cts.Token);
                if (line is null) break;
                replay += line + "\n";
            }
        }
        catch (OperationCanceledException) { }
        Assert.Contains("plan.created", replay);
    }

    [Fact]
    public async Task FailPathReworkLoop()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("E2E fail", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var file = "docs/plans/e2e-fail.md";
        foreach (var (s, cmd) in new[]
                 {
                     ("sess1", "/plan start"), ("sess2", "/plan review"), ("sess1", "/plan implement"),
                     ("sess1", "/plan implemented"), ("sess3", "/plan verify"),
                 })
            Assert.Equal(200, (int)(await app.HookAsync(s, $"{cmd} {file}")).StatusCode);

        var fail = await app.HookAsync("sess3", $"/plan fail {file} verification criterion 2 fails: parser drops tabs");
        var body = await fail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(200, (int)fail.StatusCode);
        Assert.Equal("fixing", Get(await app.GetPlanAsync(id), "status"));

        // the fixing session also used the block escape-hatch mid-fix
        await app.HookAsync("sess1", $"/plan block {file} need fixture from upstream");
        await app.HookAsync("sess1", $"/plan unblock {file} fixture copied locally");
        Assert.Equal("fixing", Get(await app.GetPlanAsync(id), "status"));

        Assert.Equal(200, (int)(await app.HookAsync("sess1", $"/plan implemented {file}")).StatusCode);
        Assert.Equal(200, (int)(await app.HookAsync("sess3", $"/plan verify {file}")).StatusCode);
        Assert.Equal(200, (int)(await app.HookAsync("sess3", $"/plan pass {file}")).StatusCode);

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("verified", Get(detail, "status"));
        var transitions = detail.GetProperty("history").EnumerateArray()
            .Select(h => $"{h.GetProperty("fromStatus")}={h.GetProperty("toStatus")}").ToList();
        Assert.Contains("verifying=fixing", transitions);
        Assert.Contains("fixing=implemented", transitions);
        // fail reason is carried mechanically, narrative lives in the file
        var failRow = detail.GetProperty("history").EnumerateArray()
            .First(h => h.GetProperty("toStatus").GetString() == "fixing");
        Assert.Contains("criterion 2", failRow.GetProperty("note").GetString());
    }

    [Fact]
    public async Task OverviewShowsHerdingQueues()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Herd me", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var file = "docs/plans/herd-me.md";
        await app.HookAsync("sess1", $"/plan start {file}");
        await app.HookAsync("sess1", $"/plan block {file} waiting for credentials");
        await app.HookAsync("sess1", $"/plan unblock {file}");

        var overview = await app.Client.GetFromJsonAsync<JsonElement>("/overview");
        Assert.Contains(overview.GetProperty("plans").GetProperty("refining").EnumerateArray(),
            p => Get(p, "id") == id);
        Assert.True(overview.GetProperty("lastEventSeq").GetInt64() > 0);
    }
}
