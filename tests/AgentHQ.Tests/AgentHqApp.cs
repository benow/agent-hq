using System.Net.Http.Json;
using System.Text.Json;
using AgentHQ.Api;
using AgentHQ.Infrastructure;
using AgentHQ.Infrastructure.Events;
using ILogger = Serilog.ILogger;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// Full in-memory host on temp directories: synthetic zcode db, a real repository working
/// tree with docs/plans/, real FileSystemWatchers. Tests trigger watchers by writing files
/// and poll time-boxed instead of sleeping blindly.
/// </summary>
public sealed class AgentHQApp : IAsyncLifetime
{
    // Repositories are keyed by the synthetic zcode db's session.directory, so each test class
    // (own fixture instance) must own its own directory tree: xUnit runs classes in parallel.
    public string RootDir { get; } = Path.Combine(Path.GetTempPath(), "agenthq-it-" + Guid.NewGuid().ToString("N"));
    public string ZCodeRoot { get; } = "/tmp/agenthq-it/zcode-" + Guid.NewGuid().ToString("N");
    public string OtherZCodeRoot { get; } = "/tmp/agenthq-it/zcode-b-" + Guid.NewGuid().ToString("N");
    public string RepoRoot => ZCodeRoot;
    public string PlansDir => Path.Combine(RepoRoot, "docs", "plans");
    public ZCodeFixture ZCode { get; } = new();
    public WebApplication Host { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string AgentHqDbPath => Path.Combine(RootDir, "bridge.sqlite");

    public static readonly ILogger Log = new LoggerConfiguration().MinimumLevel.Warning()
        .WriteTo.Sink(new XunitLogSink()).CreateLogger();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(PlansDir);
        Directory.CreateDirectory(RootDir);
        // Repository root must be the synthetic db's session.directory for cwd resolution.
        Directory.CreateDirectory(ZCodeRoot);
        Directory.CreateDirectory(OtherZCodeRoot);
        ZCode.RootA = ZCodeRoot;
        ZCode.RootB = OtherZCodeRoot;
        await ZCode.InitializeAsync();

        Host = await StartAsync();
        Client = Host.GetTestClient();
    }

    public async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = await AgentHqHost.BuildAsync(builder, new AgentHqOptions(AgentHqDbPath, ZCode.DbPath,
            StartWatchers: true, DataDir: Path.Combine(RootDir, "data")), Log);
        await app.StartAsync();
        return app;
    }

    public IServiceProvider Services => Host.Services;
    public T Get<T>() where T : notnull => Host.Services.GetRequiredService<T>();

    // ---- helpers ----

    public async Task<string> RegisterRepositoryAsync()
    {
        var resp = await Client.PostAsJsonAsync("/repositories", new { rootPath = RepoRoot });
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetString()!;
    }

    public async Task<JsonElement> SubmitPlanAsync(string name, string markdown)
    {
        var resp = await Client.PostAsJsonAsync("/plans", new { repo = RepoRoot, name, markdown });
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    public Task<HttpResponseMessage> HookAsync(string sessionId, string prompt, string? cwd = null) =>
        Client.PostAsJsonAsync("/hooks/zcode/prompts",
            new { session_id = sessionId, prompt, cwd = cwd ?? RepoRoot });

    public async Task<JsonElement> GetPlanAsync(string id) =>
        await Client.GetFromJsonAsync<JsonElement>($"/plans/{id}");

    public async Task<long> LastSeqAsync() => await Get<EventStore>().LastSeqAsync();

    public async Task<IReadOnlyList<AgentHqEvent>> EventsSinceAsync(long seq) =>
        await Get<EventStore>().ReadSinceAsync(seq);

    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    public async Task WaitForEventAsync(string type, long sinceSeq, int timeoutMs = 8000)
    {
        var ok = await WaitForAsync(async () =>
            (await EventsSinceAsync(sinceSeq)).Any(e => e.Type == type), timeoutMs);
        Assert.True(ok, $"timed out waiting for {type} after seq {sinceSeq}");
    }

    public static string PlanMarkdown(string goal = "Do the thing") => $"""
        # Plan

        ## Goal
        {goal}

        ## Context
        test

        ## Steps
        - [ ] step one
        - [ ] step two

        ## Verification Criteria
        - tests pass

        ## Documentation
        README

        ## Status Log
        """;

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        // Let long-lived server work (MCP's streamable-HTTP sessions, SSE heartbeats) observe the
        // cancellation and unwind before the host is torn down; disposing mid-flight has caused an
        // intermittent runtime abort in the test host. Bounded so a stuck test cannot hang teardown.
        await Task.Delay(150);
        if (Host is not null)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await Host.StopAsync(stop.Token); } catch (OperationCanceledException) { }
            try { await Host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }
        }
        await ZCode.DisposeAsync();
        try { Directory.Delete(RootDir, recursive: true); } catch { }
        try { Directory.Delete(ZCodeRoot, recursive: true); } catch { }
        try { Directory.Delete(OtherZCodeRoot, recursive: true); } catch { }
    }
}

public sealed class XunitLogSink : Serilog.Core.ILogEventSink
{
    public void Emit(Serilog.Events.LogEvent logEvent) => System.IO.File.AppendAllText("/tmp/serilog-probe.log", logEvent.RenderMessage() + "\n");
}

/// <summary>Mocks a provider that returns nothing — used for isolated store tests.</summary>
public static class EmptyProvider
{
    public static Mock<Core.Domain.IAgentProvider> Create()
    {
        var mock = new Mock<Core.Domain.IAgentProvider>();
        mock.SetupGet(p => p.Id).Returns("zcode");
        mock.SetupGet(p => p.Health).Returns(Core.Domain.ProviderHealth.Ok);
        mock.Setup(p => p.ListSessionsAsync(It.IsAny<Core.Domain.SessionQuery>()))
            .ReturnsAsync(Array.Empty<Core.Domain.SessionInfo>());
        mock.Setup(p => p.ListRepositoriesAsync()).ReturnsAsync(Array.Empty<Core.Domain.RepositoryInfo>());
        mock.Setup(p => p.GetSessionAsync(It.IsAny<string>()))
            .ReturnsAsync((Core.Domain.SessionDetail?)null);
        return mock;
    }
}
