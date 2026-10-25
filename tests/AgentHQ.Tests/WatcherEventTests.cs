using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHQ.Core.Domain;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Services;
using Xunit;

namespace AgentHQ.Tests;

public class WatcherAndEventIntegrationTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private static string Get(JsonElement e, string p) => e.GetProperty(p).ToString();

    [Fact]
    public async Task ExternalPlanEditBumpsVersionAndEmitsPlanUpdated()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Watched plan", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var file = Path.Combine(app.PlansDir, "watched-plan.md");

        var seq = await app.LastSeqAsync();
        var edited = AgentHQApp.PlanMarkdown().Replace("- [ ] step one", "- [x] step one");
        await File.WriteAllTextAsync(file, edited);

        await app.WaitForEventAsync("plan.updated", seq);
        var detail = await app.GetPlanAsync(id);
        Assert.True(int.Parse(Get(detail, "version")) >= 2);
        Assert.Equal(1, detail.GetProperty("progress").GetProperty("checkedCount").GetInt32());

        var events = (await app.EventsSinceAsync(seq)).Where(e => e.Type == "plan.updated").ToList();
        Assert.Equal(id, events[0].Payload.GetProperty("planId").GetString());
        Assert.Equal(2, events[0].Payload.GetProperty("version").GetInt32());   // first bump only
    }

    [Fact]
    public async Task SelfWriteViaPutDoesNotEmitPlanUpdated()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Self write", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var seq = await app.LastSeqAsync();

        var resp = await app.Client.PutAsJsonAsync($"/plans/{id}/content",
            new { markdown = AgentHQApp.PlanMarkdown("changed goal").Replace("- [ ] step two", "- [x] step two") });
        resp.EnsureSuccessStatusCode();

        // Give the watcher longer than its debounce to (incorrectly) fire.
        await Task.Delay(1500);
        var events = await app.EventsSinceAsync(seq);
        Assert.DoesNotContain(events, e => e.Type == "plan.updated");
        var detail = await app.GetPlanAsync(id);
        Assert.Equal(1, detail.GetProperty("progress").GetProperty("checkedCount").GetInt32());
    }

    [Fact]
    public async Task LessonsIndexedFromWatchedFile()
    {
        var repoId = await app.RegisterRepositoryAsync();
        await File.WriteAllTextAsync(Path.Combine(app.PlansDir, "lessons.md"), """
            # Lessons

            ### L-1: Always run the tests locally
            Category: tests
            Source: some-plan
            Run `dotnet test`.
            """);
        var svc = app.Get<PlanService>();
        var repo = await app.Get<AgentHQ.Infrastructure.Stores.RepositoryStore>().GetAsync(repoId);
        await svc.IndexLessonsAsync(repo!);
        var lessons = await app.Client.GetFromJsonAsync<JsonElement>($"/repositories/{repoId}/lessons");
        Assert.Single(lessons.EnumerateArray());
        Assert.Equal("L-1", lessons[0].GetProperty("entryId").GetString());
        Assert.Equal("tests", lessons[0].GetProperty("category").GetString());
    }

    [Fact]
    public async Task SubstantiveSessionChangeReachesEventLog()
    {
        var seq = await app.LastSeqAsync();
        var sync = app.Get<SessionSyncService>();
        var info = (await app.Get<IAgentProvider>().GetSessionAsync("sess1"))!;
        await sync.MirrorAsync(info.Info with { Title = "Renamed by the user" });

        await app.WaitForEventAsync("session.updated", seq);
        var evt = (await app.EventsSinceAsync(seq)).Last(e => e.Type == "session.updated");
        Assert.Equal("sess1", evt.Payload.GetProperty("sessionId").GetString());
        Assert.Equal("Renamed by the user", evt.Payload.GetProperty("title").GetString());
    }

    [Fact]
    public async Task BareTimeUpdatedBumpEmitsNoEvent()
    {
        // ZCode bumps time_updated on every internal step; if that alone produced an event the
        // log would be dominated by no-op rows (observed live: 116 rows, 1 distinct payload).
        var seq = await app.LastSeqAsync();
        var info = (await app.Get<IAgentProvider>().GetSessionAsync("sess1"))!;
        // Same session, one second later, nothing else different — the live watcher may run its
        // own pass concurrently, so assert on this specific mirror call, not on absence overall.
        await app.Get<SessionSyncService>().MirrorAsync(
            info.Info with { TimeUpdated = info.Info.TimeUpdated.AddSeconds(1) });
        await Task.Delay(200);
        Assert.DoesNotContain(await app.EventsSinceAsync(seq),
            e => e.Type == "session.updated" && e.Payload.GetProperty("timeUpdated").GetString()
                == info.Info.TimeUpdated.AddSeconds(1).ToString("O"));
    }

    [Fact]
    public async Task SseReplaysFromLastEventId()
    {
        await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("Replay plan", AgentHQApp.PlanMarkdown());
        var id = Get(plan, "id");
        var seqBefore = await app.LastSeqAsync();

        // Two transitions recorded before the client (re)connects.
        await app.HookAsync("sess1", "/plan start docs/plans/replay-plan.md");
        await app.HookAsync("sess1", "/plan review docs/plans/replay-plan.md");

        using var req = new HttpRequestMessage(HttpMethod.Get, "/events?types=plan.status_changed");
        req.Headers.Add("Last-Event-ID", seqBefore.ToString());
        var text = "";
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var resp = await app.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        using (var stream = await resp.Content.ReadAsStreamAsync(cts.Token))
        using (var reader = new StreamReader(stream))
        {
            Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
            try
            {
                while (text.Split("event: plan.status_changed").Length - 1 < 2)
                {
                    var line = await reader.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    text += line + "\n";
                }
            }
            catch (OperationCanceledException) { }
        }
        Assert.Equal(2, text.Split("event: plan.status_changed").Length - 1);
        Assert.Contains(id, text);
    }

    [Fact]
    public async Task LargeHistoryReplaysToTheCurrentPosition()
    {
        // Regression: the replay window was capped at 500 rows, so a client replaying from an
        // old cursor resumed hundreds of events behind live and never caught up.
        var store = app.Get<AgentHQ.Infrastructure.Events.EventStore>();
        var start = await store.LastSeqAsync();
        for (var i = 0; i < 600; i++)
            await store.AppendAsync("test.replay", new { i });
        var latest = await store.LastSeqAsync();
        var rows = await store.ReadSinceAsync(start);
        Assert.Equal(latest, rows[^1].Seq);
        Assert.Contains(rows, r => r.Type == "test.replay");
    }

    [Fact]
    public async Task EventsPollFormReturnsSameRowsAsJson()
    {
        await app.RegisterRepositoryAsync();
        var seq = await app.LastSeqAsync();
        await app.SubmitPlanAsync("Poll plan", AgentHQApp.PlanMarkdown());
        var body = await app.Client.GetFromJsonAsync<JsonElement>($"/events?sinceSeq={seq}");
        Assert.Contains(body.GetProperty("events").EnumerateArray(), e => e.GetProperty("type").GetString() == "plan.created");
        Assert.True(body.GetProperty("lastSeq").GetInt64() > seq);
    }
}
