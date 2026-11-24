using System.Net.Http.Json;
using System.Text.Json;
using AgentHQ.Infrastructure;
using AgentHQ.Infrastructure.Stores;
using Testcontainers.PostgreSql;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// One PostgreSQL container for the class (Docker required); each test boots its own
/// in-memory host against a fresh database in that container, so tests stay isolated without
/// extra hosts. Without Docker the whole class skips with a named reason — the SQLite suite
/// always runs (scripts/test.sh keeps working on machines without Docker).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        if (!File.Exists("/var/run/docker.sock"))
            throw new SkipException("Docker is not available; PostgreSQL-bound hub tests are skipped (named skip, not silent)");
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) return; // skipped — never started
        await _container.DisposeAsync();
    }

    /// <summary>A fresh empty database per test (created from the container's template connection).</summary>
    public async Task<string> NewDatabaseAsync()
    {
        var name = "hub_" + Guid.NewGuid().ToString("N");
        await using var conn = new Npgsql.NpgsqlConnection(
            new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" }.ConnectionString);
        await conn.OpenAsync();
        await new Npgsql.NpgsqlCommand($"CREATE DATABASE {name}", conn).ExecuteNonQueryAsync();
        return new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

}

public sealed class PostgresHubTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static async Task<AgentHQApp> StartAppAsync(string connStr)
    {
        var app = new AgentHQApp { DbProvider = "postgres", PgConnStr = connStr };
        await app.InitializeAsync();
        return app;
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body)
    {
        var resp = await client.PostAsJsonAsync(url, body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SkippableFact]
    public async Task Migrations_Health_And_Overview_Answer_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        var health = await app.Client.GetFromJsonAsync<JsonElement>("/health");
        Assert.Equal("ok", health.GetProperty("status").GetString());
        var overview = await app.Client.GetFromJsonAsync<JsonElement>("/overview");
        Assert.True(overview.TryGetProperty("repositories", out _));
    }

    [SkippableFact]
    public async Task Plan_Lifecycle_Round_Trip_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        var repoId = await app.RegisterRepositoryAsync();
        var plan = await app.SubmitPlanAsync("pg-lifecycle", AgentHQApp.PlanMarkdown());
        var id = plan.GetProperty("id").GetString()!;

        // Hook-driven command path through the real composition (same shape as the E2E:
        // start → review → implement; a /plan:implement on a fresh submission is refused).
        Assert.Equal(200, (int)(await app.HookAsync("sess1", "/plan start docs/plans/pg-lifecycle.md")).StatusCode);
        Assert.Equal(200, (int)(await app.HookAsync("sess2", "/plan review docs/plans/pg-lifecycle.md")).StatusCode);
        var hook = await app.HookAsync("sess1", "/plan:implement docs/plans/pg-lifecycle.md");
        hook.EnsureSuccessStatusCode();
        await app.WaitForEventAsync("plan.status_changed", 0);
        var after = await app.GetPlanAsync(id);
        Assert.Equal("implementing", after.GetProperty("status").GetString());

        // History rows and events persisted
        var store = app.Get<IPlanStore>();
        var history = await store.HistoryAsync(id);
        Assert.Contains(history, h => h.ToStatus == "implementing" && h.ActorSessionId == "sess1");
        var events = await app.EventsSinceAsync(0);
        Assert.Contains(events, e => e.Type == "plan.created");
        Assert.Contains(events, e => e.Type == "plan.status_changed");
    }

    [SkippableFact]
    public async Task Dependencies_Blocks_And_Cas_Update_Behave_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        var repoId = await app.RegisterRepositoryAsync();
        var store = app.Get<IPlanStore>();

        var a = await app.SubmitPlanAsync("pg-dep-a", AgentHQApp.PlanMarkdown());
        var b = await app.SubmitPlanAsync("pg-dep-b", AgentHQApp.PlanMarkdown());
        var aId = a.GetProperty("id").GetString()!;
        var bId = b.GetProperty("id").GetString()!;

        Assert.True(await store.AddDependencyAsync(bId, aId));
        Assert.False(await store.AddDependencyAsync(bId, aId));       // idempotent
        Assert.False(await store.AddDependencyAsync(aId, bId));       // cycle refused
        Assert.Single(await store.DependenciesAsync(bId));

        await store.SetBlockedAsync(aId, true, "waiting");
        await store.AddBlockAsync(aId, "waiting", "andy");
        var blocked = await store.GetAsync(aId);
        Assert.True(blocked!.IsBlockedBool);
        Assert.Equal("waiting", blocked.BlockedReason);
        await store.ResolveOpenBlockAsync(aId, "done");
        await store.SetBlockedAsync(aId, false, null);
        Assert.False((await store.GetAsync(aId))!.IsBlockedBool);

        // Content CAS: first external edit bumps, the same hash again does not.
        var first = await store.UpdateContentAsync(aId, "hash-1", new Core.Plans.PlanProgress(1, 2), externalEdit: true);
        Assert.NotNull(first);
        Assert.Equal(2, first!.Version);
        Assert.Null(await store.UpdateContentAsync(aId, "hash-1", new Core.Plans.PlanProgress(1, 2), externalEdit: true));
    }

    [SkippableFact]
    public async Task Event_Store_Seqs_Are_Ordered_And_Unique_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        var events = app.Get<AgentHQ.Infrastructure.Events.IEventStore>();
        var before = await events.LastSeqAsync();   // startup sync may already have appended
        var s1 = (await events.AppendAsync("t.one", new { n = 1 })).Seq;
        var s2 = (await events.AppendAsync("t.two", new { n = 2 })).Seq;
        Assert.True(s2 > s1);
        Assert.Equal(2, (await events.ReadSinceAsync(before)).Count);
        Assert.Equal(s2, await events.LastSeqAsync());
        var filtered = await events.ReadSinceAsync(0, new[] { "t.one" });
        Assert.Single(filtered);
    }

    [SkippableFact]
    public async Task Sessions_Upsert_And_List_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        await app.RegisterRepositoryAsync();
        var sessions = app.Get<ISessionStore>();
        var now = DateTimeOffset.UtcNow;
        await sessions.UpsertAsync(new Core.Domain.SessionInfo(
            "sess-pg-1", app.RepoRoot, "T", "dev", null, now, now, false, false), "agent-hq", "excerpt");
        await sessions.UpsertAsync(new Core.Domain.SessionInfo(
            "sess-pg-1", app.RepoRoot, "T2", "dev", null, now, now.AddMinutes(5), false, false), "agent-hq", "excerpt");
        var rows = (await sessions.ListAsync()).Where(r => r.ExternalId == "sess-pg-1").ToList();
        Assert.Single(rows);   // the fixture full-sync adds its own sessions; ours is upserted once, updated
        Assert.Equal("T2", rows[0].Title);
    }

    [SkippableFact]
    public async Task Exchange_Round_Trip_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        var (repoId, _) = await app.NewRepoAsync();
        var svc = app.Get<AgentHQ.Infrastructure.Exchange.ExchangeService>();

        var asked = await svc.PostAsync(new AgentHQ.Infrastructure.Exchange.PostRequest(
            new Core.Exchange.ExchangeActor("zcode", "sess1"),
            "question",
            new Core.Exchange.ExchangeActor("linda_v1", "linda"),
            null, repoId, "pg question?", "need answer", "body",
            null, null, null, "turn"));
        Assert.NotNull(asked.Item);

        var answered = await svc.ReplyAsync(asked.Item!.Id,
            new Core.Exchange.ExchangeActor("linda_v1", "linda"), "re: pg question", "pg answer", null);
        Assert.True(answered.Ok, string.Join(";", answered.Error?.Code, answered.Error?.Detail));

        var inbox = await svc.InboxAsync(new Core.Exchange.ExchangeActor("zcode", "sess1"), repository: null, states: null, kinds: null, limit: 100);
        // The answer is addressed back to the asking session; that is what its inbox must show.
        Assert.Contains(inbox, i => i.Id == answered.Item!.Id && i.State == Core.Exchange.ExchangeStates.Delivered);
    }

    [SkippableFact]
    public async Task Command_Lease_Withholds_Then_Redelivers_And_Stays_Pending_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = await StartAppAsync(connStr);
        await app.RegisterRepositoryAsync();
        var identity = await app.Get<AgentHQ.Infrastructure.Services.IdentityService>()
            .CreateIdentityAsync("pg-spoke", "pw-pg-spoke", "worker");
        var worker = await app.Get<AgentHQ.Infrastructure.Services.WorkerService>()
            .EnsureForIdentityAsync(identity.Id);
        var repo = "pg-owned-" + Guid.NewGuid().ToString("N")[..8];
        await app.Get<AgentHQ.Infrastructure.Services.WorkerService>().RegisterAsync(worker.Id,
            new AgentHQ.Client.RegisterRequest("pg-spoke", "pg-machine", "1.0.0",
                [new AgentHQ.Client.Capability("plan-engine", repositories: [repo])]));

        var queue = app.Get<AgentHQ.Infrastructure.Services.CommandQueueService>();
        var commandId = await queue.EnqueueAsync("health.check", repo, "sess_crashed",
            JsonSerializer.SerializeToElement(new { check = "runners" }));

        // The store's predicate is the contract (the route layer is the same code on SQLite):
        // live lease → withheld and not queued; expired lease → pending again and redelivered;
        // result → never pending.
        var store = app.Get<IWorkerStore>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var claimed = await store.ClaimAsync(worker.Id, now, now + 60_000, 10);
        Assert.Equal(commandId, Assert.Single(claimed).Id);
        Assert.Empty(await store.ClaimAsync(worker.Id, now + 1_000, now + 61_000, 10));
        Assert.Equal(0, await store.PendingCountAsync(worker.Id, now + 1_000));
        Assert.Equal(1, await store.PendingCountAsync(worker.Id, now + 61_000));
        Assert.Equal(commandId, Assert.Single(await store.ClaimAsync(worker.Id, now + 61_000, now + 121_000, 10)).Id);
        Assert.True(await store.TrySaveResultAsync(commandId, true, null, "h", now + 122_000));
        Assert.Equal(0, await store.PendingCountAsync(worker.Id, now + 122_000));
    }
}
