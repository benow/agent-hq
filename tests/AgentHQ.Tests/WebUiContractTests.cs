using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// Contract between the web UI (wwwroot) and the API: the assets are served, and every field
/// the UI reads is present with the expected casing. UI breakage is caught here, server-side.
/// </summary>
public class WebUiContractTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    [Fact]
    public async Task ServesAppShellAndAssets()
    {
        var root = await app.Client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
        Assert.Contains("app.js", await root.Content.ReadAsStringAsync());

        foreach (var (path, type) in new[]
                 {
                     ("/app.js", "text/javascript"),
                     ("/ui.css", "text/css"),
                     ("/index.html", "text/html")
                 })
        {
            var res = await app.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.StartsWith(type, res.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task AssetsMakeNoExternalRequests()
    {
        foreach (var path in new[] { "/", "/app.js", "/ui.css" })
        {
            var text = await app.Client.GetStringAsync(path);
            Assert.DoesNotContain("http://", text.Replace("http://127.0.0.1", ""));
            Assert.DoesNotContain("https://", text);
        }
    }

    [Fact]
    public async Task UiEndpointsReturnTheFieldsTheUiReads()
    {
        var repoId = await app.RegisterRepositoryAsync();
        var created = await app.SubmitPlanAsync("Ui contract", AgentHQApp.PlanMarkdown());
        var id = created.GetProperty("id").GetString()!;

        // POST /plans → { id, slug, status, warnings }
        Assert.True(created.TryGetProperty("slug", out _));
        Assert.True(created.TryGetProperty("warnings", out _));

        // GET /plans (table rows)
        var plans = await app.Client.GetFromJsonAsync<JsonElement>("/plans");
        var row = plans.EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);
        foreach (var field in new[] { "name", "slug", "status", "priority", "blocked", "progress", "updated" })
            Assert.True(row.TryGetProperty(field, out _), $"plans[].{field}");
        Assert.True(row.GetProperty("progress").TryGetProperty("checkedCount", out _));
        Assert.True(row.GetProperty("progress").TryGetProperty("total", out _));

        // GET /plans/{id} (drawer)
        var detail = await app.GetPlanAsync(id);
        foreach (var field in new[] { "history", "dependencies", "version", "repositoryId" })
            Assert.True(detail.TryGetProperty(field, out _), $"plan.{field}");

        // GET /repositories, /sessions, /health, /events?sinceSeq
        var repos = await app.Client.GetFromJsonAsync<JsonElement>("/repositories");
        Assert.True(repos.EnumerateArray().Any(r => r.GetProperty("id").GetString() == repoId));
        var sessions = await app.Client.GetFromJsonAsync<JsonElement>("/sessions");
        var session = sessions.EnumerateArray().First();
        foreach (var field in new[] { "id", "title", "status", "timeUpdated" })
            Assert.True(session.TryGetProperty(field, out _), $"sessions[].{field}");
        var health = await app.Client.GetFromJsonAsync<JsonElement>("/health");
        Assert.True(health.TryGetProperty("providerSchema", out _));
        var events = await app.Client.GetFromJsonAsync<JsonElement>("/events?sinceSeq=0");
        Assert.True(events.TryGetProperty("lastSeq", out _));
        var evt = events.GetProperty("events").EnumerateArray().Last();
        foreach (var field in new[] { "seq", "type", "payload", "at" })
            Assert.True(evt.TryGetProperty(field, out _), $"events[].{field}");
    }

    [Fact]
    public async Task UiCommandRouteRecordsPlanCommandsWithAttribution()
    {
        // The UI's transport for every action: POST /plans/{id}/commands {command, note, by}.
        await app.RegisterRepositoryAsync();
        var created = await app.SubmitPlanAsync("Ui command", AgentHQApp.PlanMarkdown());
        var id = created.GetProperty("id").GetString()!;

        var start = await app.Client.PostAsJsonAsync($"/plans/{id}/commands",
            new { command = "start", note = "", by = "andy" });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var block = await app.Client.PostAsJsonAsync($"/plans/{id}/commands",
            new { command = "block", note = "waiting on credentials", by = "andy" });
        Assert.Equal(HttpStatusCode.OK, block.StatusCode);
        Assert.True((await block.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("blocked").GetBoolean());

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("refining", detail.GetProperty("status").GetString());   // block never moves status
        var rows = detail.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal("/plan start", rows.Last().GetProperty("command").GetString());
        Assert.Equal("andy", rows.Last().GetProperty("actorType").GetString());

        // Commands follow the same rules as the hook feed: an illegal move is refused.
        var illegal = await app.Client.PostAsJsonAsync($"/plans/{id}/commands",
            new { command = "pass", note = "", by = "andy" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, illegal.StatusCode);

        var unblock = await app.Client.PatchAsJsonAsync($"/plans/{id}/status",
            new { blocked = false, note = "credentials provided", by = "andy" });
        Assert.Equal(HttpStatusCode.OK, unblock.StatusCode);
        Assert.False((await app.GetPlanAsync(id)).GetProperty("blocked").GetBoolean());
    }

    [Fact]
    public async Task ActionPathUsedByTheUiRecordsHistoryAndEvent()
    {
        // The UI drives transitions with PATCH /plans/{id}/status (actor "andy").
        await app.RegisterRepositoryAsync();
        var created = await app.SubmitPlanAsync("Ui action", AgentHQApp.PlanMarkdown());
        var id = created.GetProperty("id").GetString()!;
        var seq = await app.LastSeqAsync();

        var resp = await app.Client.PatchAsJsonAsync($"/plans/{id}/status",
            new { status = "deferred", by = "andy", note = "from the ui" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var detail = await app.GetPlanAsync(id);
        Assert.Equal("deferred", detail.GetProperty("status").GetString());
        var last = detail.GetProperty("history").EnumerateArray().Last();
        Assert.Equal("andy", last.GetProperty("actorType").GetString());
        Assert.Equal("from the ui", last.GetProperty("note").GetString());
        await app.WaitForEventAsync("plan.status_changed", seq);
    }

    [Fact]
    public async Task UnknownEventTypeIsSerializedGenericallyForTheActivityPanel()
    {
        // V2 will add event types; the UI renders unknown ones from type + payload.
        var evt = await app.Get<Infrastructure.Events.EventStore>()
            .AppendAsync("conversation.message", new { from = "openclaw", text = "hello" });
        var poll = await app.Client.GetFromJsonAsync<JsonElement>($"/events?sinceSeq={evt.Seq - 1}");
        var row = poll.GetProperty("events").EnumerateArray().Last();
        Assert.Equal("conversation.message", row.GetProperty("type").GetString());
        Assert.Equal("hello", row.GetProperty("payload").GetProperty("text").GetString());
    }
}
