using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AgentHQ.Tests;

public class RepositoryIntegrationTests(AgentHQApp app) : IClassFixture<AgentHQApp>
{
    [Fact]
    public async Task DiscoveryRegistersReposFromProviderSessions()
    {
        // The startup full sync discovered repositories from the synthetic zcode db.
        var repos = await app.Client.GetFromJsonAsync<JsonElement>("/repositories");
        var roots = repos.EnumerateArray().Select(r => r.GetProperty("rootPath").GetString()).ToList();
        Assert.Contains(app.ZCode.RootA, roots);
        Assert.Contains(app.ZCode.RootB, roots);
    }

    [Fact]
    public async Task RegisterCreateAndList()
    {
        var id = await app.RegisterRepositoryAsync();
        Assert.StartsWith("-", id);   // path slug
        var repos = await app.Client.GetFromJsonAsync<JsonElement>("/repositories");
        Assert.Contains(repos.EnumerateArray(), r => r.GetProperty("id").GetString() == id);
        Assert.True(Directory.Exists(app.PlansDir));
    }

    [Fact]
    public async Task RegisterRejectsMissingDirectory()
    {
        var resp = await app.Client.PostAsJsonAsync("/repositories", new { rootPath = "/nope/does-not-exist" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task SessionsMirroredAtStartup()
    {
        var sessions = await app.Client.GetFromJsonAsync<JsonElement>("/sessions");
        var ids = sessions.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("sess1", ids);
        Assert.Contains("sess3", ids);
    }

    [Fact]
    public async Task SessionsFilterByRepo()
    {
        var sessions = await app.Client.GetFromJsonAsync<JsonElement>($"/sessions?repoRootPath={Uri.EscapeDataString(app.ZCode.RootA)}");
        Assert.All(sessions.EnumerateArray(), s => Assert.Equal(app.ZCode.RootA, s.GetProperty("rootPath").GetString()));
    }

    [Fact]
    public async Task SessionDetailAndMessagesAndDigest()
    {
        var detail = await app.Client.GetFromJsonAsync<JsonElement>("/sessions/sess1");
        Assert.Equal("Fix player bug", detail.GetProperty("info").GetProperty("title").GetString());

        var messages = await app.Client.GetFromJsonAsync<JsonElement>("/sessions/sess1/messages");
        Assert.Equal(3, messages.GetProperty("messages").GetArrayLength());

        var digest = await app.Client.GetStringAsync("/sessions/sess1/digest");
        Assert.Contains("player bug", digest);
        Assert.Contains("null ref", digest);

        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/sessions/nope")).StatusCode);
    }
}
