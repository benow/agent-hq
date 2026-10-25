using AgentHQ.Core.Domain;
using AgentHQ.Core.Digest;
using AgentHQ.Providers.ZCode;
using Xunit;

namespace AgentHQ.Tests;

public class ZCodeProviderTests : IClassFixture<ZCodeFixture>
{
    private readonly ZCodeFixture _fx;
    public ZCodeProviderTests(ZCodeFixture fx) => _fx = fx;

    [Fact]
    public void SchemaGuardPassesOnTestedVersion()
    {
        var p = new ZCodeAgentProvider(_fx.DbPath);
        Assert.Equal(ProviderHealth.Ok, p.Health);
    }

    [Fact]
    public async Task SchemaAheadDegrades()
    {
        var fx = new ZCodeFixture();
        await fx.InitializeAsync();
        try
        {
            await fx.InsertMigrationAsync("0099_future_schema");
            var p = new ZCodeAgentProvider(fx.DbPath);
            Assert.Equal(ProviderHealth.SchemaAhead, p.Health);
            Assert.Empty(await p.ListSessionsAsync(new SessionQuery()));
            Assert.Null(await p.GetSessionAsync("sess1"));
        }
        finally { await fx.DisposeAsync(); }
    }

    [Fact]
    public void MissingDbDegrades()
    {
        var p = new ZCodeAgentProvider("/tmp/definitely-not-a-db-" + Guid.NewGuid() + ".sqlite");
        Assert.Equal(ProviderHealth.Missing, p.Health);
    }

    [Fact]
    public async Task ListsSessions()
    {
        var p = await _fx.CreateProviderAsync();
        var all = await p.ListSessionsAsync(new SessionQuery());
        Assert.Equal(3, all.Count);
        var a = await p.ListSessionsAsync(new SessionQuery(RepositoryRootPath: _fx.RootA));
        Assert.Equal(2, a.Count);
        var since = await p.ListSessionsAsync(new SessionQuery(UpdatedSince: DateTimeOffset.FromUnixTimeMilliseconds(1_789_000_000_000)));
        Assert.Single(since);   // strict > : sess1 sits exactly on the high-water mark
        Assert.Equal("sess3", since[0].Id);
    }

    [Fact]
    public async Task RepositoriesMapProjectToRecentRoot()
    {
        var p = await _fx.CreateProviderAsync();
        var repos = await p.ListRepositoriesAsync();
        Assert.Equal(2, repos.Count);
        Assert.Equal(_fx.RootA, repos.Single(r => r.ExternalProjectId == "proj-a").RootPath);
    }

    [Fact]
    public async Task GetSessionIncludesExcerpt()
    {
        var p = await _fx.CreateProviderAsync();
        var d = await p.GetSessionAsync("sess1");
        Assert.NotNull(d);
        Assert.Contains("player bug", d!.FirstUserMessageExcerpt);
    }

    [Fact]
    public async Task PartFilteringDropsReasoningToolsStepsByDefault()
    {
        var p = await _fx.CreateProviderAsync();
        var delta = await p.GetMessagesAsync("sess1", new SessionMessageQuery());
        var types = delta.Messages.SelectMany(m => m.Parts).Select(x => x.Type).Distinct().ToList();
        Assert.Equal(new[] { "text" }, types);

        var wide = await p.GetMessagesAsync("sess1", new SessionMessageQuery(IncludeReasoning: true, IncludeTool: true, IncludeSteps: true));
        var wideTypes = wide.Messages.SelectMany(m => m.Parts).Select(x => x.Type).Distinct().OrderBy(x => x).ToList();
        Assert.Contains("reasoning", wideTypes);
        Assert.Contains("tool", wideTypes);
        Assert.Contains("step-start", wideTypes);
    }

    [Fact]
    public async Task CursorDelta()
    {
        var p = await _fx.CreateProviderAsync();
        var full = await p.GetMessagesAsync("sess1", new SessionMessageQuery());
        Assert.Equal(3, full.Messages.Count);
        var mid = await p.GetMessagesAsync("sess1", new SessionMessageQuery(AfterSequence: 4));
        Assert.Single(mid.Messages);   // only the last assistant message's parts have sequence > 4
        Assert.Equal(7, mid.MaxSequence);
    }

    [Fact]
    public async Task DigestRendersUserAssistantTools()
    {
        var p = await _fx.CreateProviderAsync();
        var detail = await p.GetSessionAsync("sess1");
        var delta = await p.GetMessagesAsync("sess1", new SessionMessageQuery(IncludeTool: true));
        var md = DigestBuilder.Render(detail!.Info, delta.Messages);
        Assert.Contains("player bug", md);                       // user verbatim
        Assert.Contains("null ref in player.ts", md);             // assistant text
        Assert.Contains("Read: /tmp/agenthq-test-repo-a/src/app.ts", md);
        Assert.Contains("Bash: git commit -m 'fix player'", md);
        Assert.DoesNotContain("thinking...", md);                 // reasoning dropped
    }
}

public class DigestBuilderBudgetTests
{
    private static SessionInfo Info() => new("s1", "/repo", "t", "interactive", null,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, false, false);

    [Fact]
    public void OverBudgetDropsOldestAssistantTextKeepsUsersAndTail()
    {
        var messages = new List<SessionMessage>();
        long seq = 0;
        for (var i = 0; i < 10; i++)
        {
            messages.Add(new SessionMessage($"m{i}-a", "assistant", DateTimeOffset.UtcNow, seq++,
                new[] { new SessionPart("text", $"assistant-msg-{i} " + new string('x', 50), null, null, seq) }));
        }
        messages.Add(new SessionMessage("m-user", "user", DateTimeOffset.UtcNow, seq,
            new[] { new SessionPart("text", "important user instruction", null, null, seq) }));

        var md = DigestBuilder.Render(Info(), messages, budget: 900);
        Assert.Contains("important user instruction", md);
        Assert.Contains("earlier items elided", md);
        Assert.Contains("assistant-msg-9", md);       // tail kept verbatim
        Assert.DoesNotContain("assistant-msg-0 ", md); // oldest dropped
        Assert.True(md.Length < 1600, $"digest too long: {md.Length}");
    }

    [Fact]
    public void HugeUserMessagesStillRespectBudget()
    {
        // Regression: a real live digest rendered 56 KB because the budget only trimmed
        // assistant text. Tool activity and oversized user messages must yield too.
        var messages = new List<SessionMessage>();
        long seq = 0;
        var huge = new string('u', 20_000);
        messages.Add(new SessionMessage("m0", "user", DateTimeOffset.UtcNow, seq++,
            new[] { new SessionPart("text", huge, null, null, 0) }));
        for (var i = 0; i < 200; i++)
            messages.Add(new SessionMessage($"t{i}", "assistant", DateTimeOffset.UtcNow, seq++,
                new[] { new SessionPart("tool", null, "Read", $"/path/file-{i}.ts", seq) }));
        messages.Add(new SessionMessage("m-user2", "user", DateTimeOffset.UtcNow, seq,
            new[] { new SessionPart("text", "latest instruction", null, null, seq) }));

        var md = DigestBuilder.Render(Info(), messages, budget: 2000);
        Assert.True(md.Length <= 2200, $"digest ignored the budget: {md.Length} chars");
        Assert.Contains("latest instruction", md);
        Assert.Contains("elided", md);
    }

    [Fact]
    public void UnderBudgetRendersEverything()
    {
        var messages = new List<SessionMessage>
        {
            new("m0", "user", DateTimeOffset.UtcNow, 0, new[] { new SessionPart("text", "hi", null, null, 0) })
        };
        var md = DigestBuilder.Render(Info(), messages);
        Assert.Contains("hi", md);
        Assert.DoesNotContain("elided", md);
    }
}
