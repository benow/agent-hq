using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The OpenClaw side, simulated through the official MCP C# client over our in-memory
/// server transport: discovery, the guide, the PM loop reads, and a submit through MCP.
/// </summary>
public class McpClientTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    private async Task<McpClient> ConnectAsync()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(app.Client.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, app.Client);
        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ListsAllTools()
    {
        await using var client = await ConnectAsync();
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet();
        foreach (var expected in new[]
                 {
                     "get_overview", "list_repositories", "list_sessions", "get_session_digest",
                     "get_session_messages", "submit_plan", "list_plans", "read_plan",
                     "update_plan_status", "update_plan", "list_lessons", "get_events", "get_guide"
                 })
            Assert.Contains(expected, names);
        Assert.All(tools, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    [Fact]
    public async Task GuideAndOverview()
    {
        await using var client = await ConnectAsync();
        var guide = await client.CallToolAsync("get_guide", new Dictionary<string, object?>());
        Assert.Contains("separation", Text(guide));

        var overview = await client.CallToolAsync("get_overview", new Dictionary<string, object?>());
        using var doc = JsonDocument.Parse(Text(overview));
        Assert.True(doc.RootElement.TryGetProperty("lastEventSeq", out _));
    }

    [Fact]
    public async Task PmLoopSubmitThenCatchUpBySinceSeq()
    {
        await app.RegisterRepositoryAsync();
        await using var client = await ConnectAsync();

        var overview = await client.CallToolAsync("get_overview", new Dictionary<string, object?>());
        using var odoc = JsonDocument.Parse(Text(overview));
        var sinceSeq = odoc.RootElement.GetProperty("lastEventSeq").GetInt64();

        var submitted = await client.CallToolAsync("submit_plan", new Dictionary<string, object?>
        {
            ["repo"] = app.RepoRoot,
            ["name"] = "Mcp submitted plan",
            ["markdown"] = AgentHQApp.PlanMarkdown()
        });
        using var sdoc = JsonDocument.Parse(Text(submitted));
        var planId = sdoc.RootElement.GetProperty("id").GetString()!;

        var events = await client.CallToolAsync("get_events", new Dictionary<string, object?>
        {
            ["sinceSeq"] = sinceSeq, ["types"] = ""
        });
        using var edoc = JsonDocument.Parse(Text(events));
        var list = edoc.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(list, e => e.GetProperty("type").GetString() == "plan.created");
        Assert.True(edoc.RootElement.GetProperty("lastSeq").GetInt64() > sinceSeq);

        var status = await client.CallToolAsync("update_plan_status", new Dictionary<string, object?>
        {
            ["planId"] = planId, ["status"] = "refining", ["by"] = "openclaw",
            ["sessionId"] = "", ["note"] = ""
        });
        using var tdoc = JsonDocument.Parse(Text(status));
        Assert.True(tdoc.RootElement.GetProperty("ok").GetBoolean());

        var read = await client.CallToolAsync("read_plan", new Dictionary<string, object?> { ["planId"] = planId });
        using var rdoc = JsonDocument.Parse(Text(read));
        Assert.Contains("Goal", rdoc.RootElement.GetProperty("content").GetString());
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
