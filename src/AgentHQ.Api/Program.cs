using System.Reflection;
using System.Text.Json.Serialization;
using AgentHQ.Api.Endpoints;
using AgentHQ.Api.Mcp;
using AgentHQ.Api.Services;
using AgentHQ.Core.Domain;
using AgentHQ.Core.Exchange;
using AgentHQ.Infrastructure;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Exchange;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Infrastructure.Watching;
using AgentHQ.Providers.ZCode;
using Serilog;

namespace AgentHQ.Api;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "install-zcode")
            return AgentHQ.Commands.ZCode.Runner.Run(args.Skip(1).ToArray(), ResolveZCodeDbPath());
        if (args.Length > 0 && args[0] == "--version")
        {
            Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                              ?? typeof(Program).Assembly.GetName().Version?.ToString());
            return 0;
        }

        Directory.CreateDirectory(Path.Combine(DataDir, "logs"));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(DataDir, "logs", "agent-hq-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true)
            .CreateBootstrapLogger();

        try
        {
            // Content root is the application directory, not the working directory: the web UI
            // (wwwroot/) ships next to the assembly, and `dotnet run` from the repo root or a
            // systemd unit started from $HOME must serve the same assets.
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });
            // Graceful SIGTERM shutdown under systemd (a no-op outside it).
            builder.Host.UseSystemd();

            builder.Logging.ClearProviders();
            // Per-request ASP.NET logs are noise for a bridge that talks to itself all day.
            var frameworkLevel = Environment.GetEnvironmentVariable("AGENTHQ_LOG_ASPNET") ?? "Warning";
            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Services(services)
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", ParseLevel(frameworkLevel))
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(DataDir, "logs", "agent-hq-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 10,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true));

            var port = int.Parse(Environment.GetEnvironmentVariable("AGENTHQ_PORT") ?? "5317");
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

            var app = await AgentHqHost.BuildAsync(builder, new AgentHqOptions(
                AgentHqDbPath: Environment.GetEnvironmentVariable("AGENTHQ_DB")
                              ?? Path.Combine(DataDir, "agent-hq.sqlite"),
                ZCodeDbPath: ResolveZCodeDbPath(),
                StartWatchers: true),
                Log.Logger);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "agent-hq terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static Serilog.Events.LogEventLevel ParseLevel(string text) =>
        Enum.TryParse<Serilog.Events.LogEventLevel>(text, ignoreCase: true, out var level)
            ? level : Serilog.Events.LogEventLevel.Warning;

    public static string DataDir => Environment.GetEnvironmentVariable("AGENTHQ_DATA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "agent-hq");

    public static string ResolveZCodeDbPath() => Environment.GetEnvironmentVariable("AGENTHQ_ZCODE_DB")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zcode", "cli", "db", "db.sqlite");
}

/// <summary>
/// Host options. <paramref name="DataDir"/> is the root for provider logs and any data the
/// process derives (reset per in-memory test host so hosts never share files).
/// </summary>
public sealed record AgentHqOptions(string AgentHqDbPath, string ZCodeDbPath, bool StartWatchers,
    string? DataDir = null);

/// <summary>
/// Host composition shared by the real host and the in-memory TestServer: services, endpoint
/// mapping, event wiring, startup sync and watchers.
/// </summary>
public static class AgentHqHost
{
    public static async Task<WebApplication> BuildAsync(WebApplicationBuilder builder, AgentHqOptions options,
        Serilog.ILogger logger)
    {
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        builder.Services.AddSingleton<AgentHqDb>(_ =>
            new AgentHqDb(options.AgentHqDbPath, logger.ForContext("Subsystem", "db")));
        builder.Services.AddSingleton(sp => new EventStore(sp.GetRequiredService<AgentHqDb>(),
            logger.ForContext("Subsystem", "events")));
        builder.Services.AddSingleton(sp => new RepositoryStore(sp.GetRequiredService<AgentHqDb>()));
        builder.Services.AddSingleton(sp => new SessionStore(sp.GetRequiredService<AgentHqDb>()));
        builder.Services.AddSingleton(sp => new PlanStore(sp.GetRequiredService<AgentHqDb>()));
        builder.Services.AddSingleton(sp => new ExchangeStore(sp.GetRequiredService<AgentHqDb>()));
        // Exchange configuration is resolved once here, from the environment and the data dir —
        // services never read environment variables themselves (one composition root).
        var exchangeOptions = ExchangeOptions.FromEnvironment(Environment.GetEnvironmentVariable,
            options.DataDir ?? Program.DataDir);
        builder.Services.AddSingleton(exchangeOptions);
        builder.Services.AddSingleton(sp => new BlobStore(exchangeOptions.BlobDir, exchangeOptions.BlobMaxBytes));
        builder.Services.AddSingleton(sp => new ExchangeService(
            sp.GetRequiredService<AgentHqDb>(),
            sp.GetRequiredService<ExchangeStore>(),
            sp.GetRequiredService<RepositoryStore>(),
            sp.GetRequiredService<EventStore>(),
            sp.GetRequiredService<BlobStore>(),
            exchangeOptions,
            logger: logger.ForContext("Subsystem", "exchange")));
        builder.Services.AddSingleton(sp => new PlanService(
            sp.GetRequiredService<PlanStore>(),
            sp.GetRequiredService<RepositoryStore>(),
            sp.GetRequiredService<EventStore>(),
            logger.ForContext("Subsystem", "plans")));
        builder.Services.AddSingleton<IAgentProvider>(_ =>
            new ZCodeAgentProvider(options.ZCodeDbPath, logger));
        builder.Services.AddSingleton(sp => new SessionSyncService(
            sp.GetRequiredService<IAgentProvider>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<RepositoryStore>(),
            sp.GetRequiredService<EventStore>(),
            logger));
        builder.Services.AddSingleton(sp => new HookFeedService(
            sp.GetRequiredService<IAgentProvider>(),
            sp.GetRequiredService<RepositoryStore>(),
            sp.GetRequiredService<SessionStore>(),
            sp.GetRequiredService<SessionSyncService>(),
            sp.GetRequiredService<PlanStore>(),
            sp.GetRequiredService<PlanService>(),
            sp.GetRequiredService<EventStore>(),
            logger));
        builder.Services.AddSingleton<SseHub>();
        builder.Services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "agent-hq", Version = "1.0.0" };
                o.ServerInstructions =
                    "You manage the work; you never replace it — plans can be development, research, operations or writing. Orient with get_overview, stay current with get_events(sinceSeq), herd plans through their lifecycle, and verify from recorded evidence — never your own implementation. The tools are self-describing; get_guide carries the full playbook.";
            })
            .WithHttpTransport()
            .WithToolsFromAssembly();

        var app = builder.Build();

        var provider = app.Services.GetRequiredService<IAgentProvider>();
        var sync = app.Services.GetRequiredService<SessionSyncService>();
        var plans = app.Services.GetRequiredService<PlanService>();
        var sessionStore = app.Services.GetRequiredService<SessionStore>();
        plans.UseSessions(sessionStore.GetAsync);
        var repos = app.Services.GetRequiredService<RepositoryStore>();
        var hub = app.Services.GetRequiredService<SseHub>();
        var events = app.Services.GetRequiredService<EventStore>();
        events.Published += hub.PublishAsync;

        // migrations + startup full sync + watchers
        app.Services.GetRequiredService<AgentHqDb>().Migrate(Path.Combine(options.DataDir ?? Program.DataDir, "logs", "migrations.log"));
        await sync.FullSyncAsync();
        foreach (var repo in await repos.ListAsync())
        {
            plans.StartWatcher(repo);
            await plans.IndexLessonsAsync(repo);
        }
        if (options.StartWatchers)
        {
            var zcodeWatcher = new ZCodeDbWatcher(options.ZCodeDbPath, sync.SyncOnDbChangeAsync, logger: logger);
            zcodeWatcher.Start();
            app.Lifetime.ApplicationStopping.Register(zcodeWatcher.Dispose);
        }
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var w in plans.Watchers) w.Dispose();
        });

        // The expiry sweep: an unanswered question past expires_at leaves every digest, emits
        // exchange.expired and stays readable by id. Reads sweep too; this timer keeps a quiet
        // system honest without waiting for a read.
        var exchange = app.Services.GetRequiredService<ExchangeService>();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                try
                {
                    await exchange.SweepExpiredAsync(app.Lifetime.ApplicationStopping);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "exchange expiry sweep failed; the next tick retries in 60s");
                }
            }
        });

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            provider = provider.Id,
            providerSchema = provider.Health switch
            {
                ProviderHealth.Ok => "ok",
                ProviderHealth.SchemaAhead => "schema-ahead",
                _ => "missing"
            },
            providerDetail = provider.HealthDetail
        }));
        app.MapGet("/overview", ApiEndpoints.Overview);
        app.MapRepositories();
        app.MapSessions();
        app.MapPlans();
        app.MapExchange();
        app.MapEvents();
        app.MapPost("/hooks/zcode/prompts", HookFeedService.HandleAsync);
        app.MapMcp("/mcp");

        // Web UI: plain static files, no build step (wwwroot/). Falls through to the API routes.
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }
}
