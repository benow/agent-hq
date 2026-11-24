using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHQ.Client;
using AgentHQ.Client.Conformance;
using AgentHQ.Infrastructure;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;
using Dapper;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The spoke wire contract (distributed part 3), played end to end against the real
/// composition: registration and identity binding, the three-case ordering rule, snapshot
/// ownership, the leased command queue, pairing, CI ingest, the contract header gate, role
/// scoping, and the SSE down-signal. The fake spoke is an HttpClient loop — it adds no
/// watchers (lessons.md). Non-loopback requests go through remote-address-overridden
/// handlers (the AuthTests pattern); response bodies are read eagerly.
/// </summary>
public class WorkerTests(WorkerFixture app) : IClassFixture<WorkerFixture>
{
    private static readonly JsonSerializerOptions Json = Dtos.Json;

    private static Capability[] CapsFor(string repo) =>
        [new Capability("plan-engine", repositories: [repo])];

    // ---- helpers ----

    private HttpClient AsRemote(string ip) => new(
        app.Host.GetTestServer().CreateHandler(ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip)))
    { BaseAddress = app.Client.BaseAddress };

    /// <summary>A fresh paired worker: identity + bound worker record + bearer token, wired
    /// through the same services the pairing approval uses.</summary>
    private async Task<(HttpClient Client, string Token, string WorkerId, string Username, string Repo)> NewWorkerAsync(
        string ip = "192.168.0.61")
    {
        var identities = app.Get<IdentityService>();
        var workers = app.Get<WorkerService>();
        var username = $"spoke-{Guid.NewGuid().ToString("N")[..8]}";
        // Repository ownership is exclusive (rule 5): every worker in this shared-fixture
        // class claims its own unique repo name, so tests never fight over one claim.
        var repo = $"owned-{username[6..]}";
        var identity = await identities.CreateIdentityAsync(username, "pw-" + username, "worker");
        var worker = await workers.EnsureForIdentityAsync(identity.Id);
        var raw = "tok-" + Guid.NewGuid().ToString("N");
        await app.Get<IIdentityStore>().CreateTokenAsync(identity.Id, Core.Auth.TokenGenerator.Hash(raw), label: "test");
        var client = AsRemote(ip);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
        client.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        await workers.RegisterAsync(worker.Id, new RegisterRequest(
            username, "test-machine", "1.0.0", CapsFor(repo)));
        return (client, raw, worker.Id, username, repo);
    }

    private static EventDelta Delta(long originSeq, string actor = "sess_abc") => new(
        originSeq, "plan.status_changed", DateTimeOffset.UtcNow,
        actor, "test-spoke",
        JsonSerializer.SerializeToElement(new { slug = "demo", from = "implementing", to = "implemented" }, Json));

    private static EventsBatch Batch(long from, params long[] origins) => new(from,
        origins.Select(o => Delta(o)).ToArray());

    private async Task<(EventsAck? Ack, long? Expected, HttpStatusCode Status)> SendAsync(
        HttpClient client, string workerId, long from, params long[] origins)
    {
        var resp = await client.PostAsJsonAsync($"/workers/{workerId}/events", Batch(from, origins), Json);
        var text = await resp.Content.ReadAsStringAsync();
        if ((int)resp.StatusCode == 422)
            return (null, JsonDocument.Parse(text).RootElement.GetProperty("expected").GetInt64(), resp.StatusCode);
        resp.EnsureSuccessStatusCode();
        return (System.Text.Json.JsonSerializer.Deserialize<EventsAck>(text, Json), null, resp.StatusCode);
    }

    private async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body)
    {
        var resp = await client.PostAsJsonAsync(url, body, Json);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(Json))!;
    }

    // ---- registration + identity binding ----

    [Fact]
    public async Task RegisterIsIdempotentPerIdentity()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var r1 = await PostJsonAsync(client, "/workers/register",
            new { workerId, name = "spoke-a", machine = "laptop", version = "1.0.0", capabilities = CapsFor(repo) });
        Assert.Equal(workerId, r1.GetProperty("workerId").GetString());
        Assert.Equal("1", r1.GetProperty("contractVersion").GetString());

        // A restart re-registers: same workerId, refreshed declaration.
        var caps2 = new Capability[] { new("plan-engine", repositories: [repo + "-2"]), new("build-ci", loopbackPort: 5318) };
        var r2 = await PostJsonAsync(client, "/workers/register",
            new { workerId, name = "spoke-a", machine = "laptop", version = "1.1.0", capabilities = caps2 });
        Assert.Equal(workerId, r2.GetProperty("workerId").GetString());
        var fleet = await app.Client.GetFromJsonAsync<JsonElement>("/workers");
        var row = fleet.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == workerId);
        Assert.Equal("1.1.0", row.GetProperty("version").GetString());
        var kinds = row.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetProperty("kind").GetString()).ToList();
        Assert.Contains("plan-engine", kinds);
        Assert.Contains("build-ci", kinds);
        Assert.Equal([repo + "-2"], row.GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetString()).ToList());

        // Another identity cannot adopt the first worker's id: it 404s ambiguously.
        var (clientB, _, _, _, repoB) = await NewWorkerAsync();
        var hijack = await clientB.PostAsJsonAsync("/workers/register",
            new { workerId, name = "rogue", machine = "x", version = "1", capabilities = CapsFor(repoB) }, Json);
        Assert.Equal(HttpStatusCode.NotFound, hijack.StatusCode);
    }

    [Fact]
    public async Task HeartbeatCarriesPresenceAndVersion()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var hb = await PostJsonAsync(client, $"/workers/{workerId}/heartbeat", new { version = "2.3.4" });
        foreach (var f in new[] { "lastAcceptedSeq", "commandsPending", "serverVersion", "contractVersion" })
            Assert.True(hb.TryGetProperty(f, out _), f);
        Assert.Equal("1", hb.GetProperty("contractVersion").GetString());
        Assert.False(string.IsNullOrEmpty(hb.GetProperty("serverVersion").GetString()));
    }

    // ---- ordering (rule 2) ----

    [Fact]
    public async Task EventBatchesFollowTheThreeCaseRule()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var seqBefore = await app.LastSeqAsync();

        // equal: applied
        var apply = await SendAsync(client, workerId, 0, 1, 2);
        Assert.True(apply.Ack is { accepted: true });
        Assert.Equal(3, apply.Ack!.nextExpected);
        Assert.True(apply.Ack.globalSeqFrom > 0);

        // below (the ack-lost resend of the same batch): trimmed, acknowledged, no new rows
        var trimmed = await SendAsync(client, workerId, 0, 1, 2);
        Assert.True(trimmed.Ack is { accepted: true });
        Assert.Equal(3, trimmed.Ack!.nextExpected);

        // partial overlap: prefix trimmed, tail applied
        var overlap = await SendAsync(client, workerId, 2, 2, 3, 4);
        Assert.True(overlap.Ack is { accepted: true });
        Assert.Equal(5, overlap.Ack!.nextExpected);

        // ahead: 422 naming the expected value
        var (ack, expected, status) = await SendAsync(client, workerId, 9, 10);
        Assert.Null(ack);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(5, expected);

        // the spoke rewinds to the expected cursor and resends: no loss, no duplication
        var recovered = await SendAsync(client, workerId, expected!.Value, expected!.Value);
        Assert.True(recovered.Ack is { accepted: true });
        Assert.Equal(expected.Value + 1, recovered.Ack!.nextExpected);

        // row-count proof: exactly 5 spoke events landed (2 applied + 2 tail + the resent one;
        // the duplicates trimmed)
        var merged = await app.Get<IEventStore>().ReadSinceAsync(seqBefore);
        var spokeRows = merged.Where(e => e.Type == "plan.status_changed").ToList();
        Assert.Equal(5, spokeRows.Count);

        // actor passthrough (rule 1): payloads keep the original actor and name the worker
        foreach (var e in spokeRows)
        {
            Assert.Equal("sess_abc", e.Payload.GetProperty("actor").GetString());
            Assert.Equal("test-spoke", e.Payload.GetProperty("transportedBy").GetString());
            Assert.True(e.Payload.TryGetProperty("originSeq", out _));
        }
    }

    [Fact]
    public async Task KillMidDrainResendLosesAndDuplicatesNothing()
    {
        var (client, _, workerId, username, repo) = await NewWorkerAsync();
        var seqBefore = await app.LastSeqAsync();

        // The spoke drains 1–3; the hub commits, the response dies mid-drain (we drop it).
        var first = await SendAsync(client, workerId, 0, 1, 2, 3);
        Assert.Equal(4, first.Ack!.nextExpected);

        // The spoke restarts and re-registers: same workerId, refreshed declaration (the plan's
        // rule that a restart must not mint a new worker and orphan its projections/queue).
        var reReg = await client.PostAsJsonAsync("/workers/register",
            new { workerId, name = username, machine = "test-machine", version = "1.1.0", capabilities = CapsFor(repo) }, Json);
        reReg.EnsureSuccessStatusCode();
        Assert.Equal(workerId, (await reReg.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("workerId").GetString());

        // The ack-lost resend (from its cursor, before the death) trims: no loss, no duplication.
        var resend = await SendAsync(client, workerId, 0, 1, 2, 3);
        Assert.True(resend.Ack is { accepted: true });
        Assert.Equal(4, resend.Ack!.nextExpected);

        // The drain continues from the spoke's own tail (4–5 are new; the rest trims).
        var tail = await SendAsync(client, workerId, 4, 4, 5);
        Assert.Equal(6, tail.Ack!.nextExpected);

        // Exactly five spoke events, each originSeq exactly once across the whole crash dance.
        var rows = (await app.Get<IEventStore>().ReadSinceAsync(seqBefore))
            .Where(e => e.Type == "plan.status_changed").ToList();
        Assert.Equal(5, rows.Count);
        var origins = rows.Select(e => e.Payload.GetProperty("originSeq").GetInt64()).ToList();
        Assert.Equal([1L, 2L, 3L, 4L, 5L], origins.OrderBy(o => o).ToList());
        Assert.Equal(5, origins.Distinct().Count());
    }

    [Fact]
    public async Task ConcurrentBatchesForOneWorkerApplyOnce()
    {
        var (client, _, workerId, _, _) = await NewWorkerAsync();
        var seqBefore = await app.LastSeqAsync();

        // Two requests for the same worker racing on the same cursor read (the Postgres READ
        // COMMITTED hazard). The (worker_id, origin_seq) key is the guard, not the cursor.
        var results = await Task.WhenAll(
            SendAsync(client, workerId, 0, 1, 2, 3),
            SendAsync(client, workerId, 0, 1, 2, 3));
        Assert.All(results, r => Assert.True(r.Ack is { accepted: true }));

        var rows = (await app.Get<IEventStore>().ReadSinceAsync(seqBefore))
            .Where(e => e.Type == "plan.status_changed").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal([1L, 2L, 3L], rows.Select(e => e.Payload.GetProperty("originSeq").GetInt64())
            .OrderBy(o => o).ToList());

        // The cursor is the truth either way: a probe ahead of it names 4.
        var (_, expected, status) = await SendAsync(client, workerId, 99, 99);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(4, expected);
    }

    [Fact]
    public async Task MergedStreamInterleavesTwoWorkersAndHubOriginEvents()    {
        var (clientA, _, workerA, _, repoA) = await NewWorkerAsync();
        var (clientB, _, workerB, _, repoB) = await NewWorkerAsync();
        var seqBefore = await app.LastSeqAsync();
        await SendAsync(clientA, workerA, 0, 1);
        await app.Get<IEventStore>().AppendAsync("plan.updated", new { slug = "hub-origin" });
        await SendAsync(clientB, workerB, 0, 1);

        var rows = await app.Get<IEventStore>().ReadSinceAsync(seqBefore);
        Assert.Equal(3, rows.Count);                    // spoke A, hub, spoke B — interleaved
        Assert.Equal("plan.updated", rows[1].Type);
        Assert.Equal(workerA, rows[0].Payload.GetProperty("workerId").GetString());
        Assert.Equal(workerB, rows[2].Payload.GetProperty("workerId").GetString());

        // ?sinceSeq replay sees the same merged history
        var poll = await app.Client.GetFromJsonAsync<JsonElement>($"/events?sinceSeq={seqBefore}");
        Assert.Equal(3, poll.GetProperty("events").GetArrayLength());
    }

    // ---- snapshot (rule 5) ----

    [Fact]
    public async Task SnapshotReplacesProjectionsAndSetsTheCursor()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var planRow = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = "plan-1", ["slug"] = "demo", ["name"] = "Demo", ["repositoryId"] = "repo-1",
            ["filePath"] = "docs/plans/demo.md", ["status"] = "implementing", ["priority"] = 2L,
            ["isBlocked"] = 0L, ["contentHash"] = "abc", ["checked"] = 1L,
            ["total"] = 3L, ["version"] = 2L, ["createdAt"] = 1000L, ["updatedAt"] = 2000L
        }, Json);
        var snapshot = new Snapshot(
            repos: [new SnapshotRepo(repo, "/repos/agent-hq")],
            plans: [new SnapshotPlan(planRow, "# Demo\n\n## Status Log\n- worked\n")],
            history: [JsonSerializer.SerializeToElement(new
            {
                seq = 1, planId = "plan-1", fromStatus = "submitted", toStatus = "implementing",
                actorType = "sess_orig", command = "/plan start", at = 1500
            }, Json)],
            sessions: [JsonSerializer.SerializeToElement(new
            {
                provider = "zcode", externalId = "session_x", title = "work", taskType = "dev",
                externalTimeCreated = 900, externalTimeUpdated = 901, archived = 0, deleted = 0, ingestedAt = 902
            }, Json)],
            lessons: [JsonSerializer.SerializeToElement(new
            {
                entryId = "L-1", title = "test first", contentHash = "h", firstSeenAt = 800
            }, Json)],
            events: [Delta(1, actor: "sess_orig")]);
        var seqBefore = await app.LastSeqAsync();
        var resp = await client.PostAsJsonAsync($"/workers/{workerId}/snapshot", snapshot, Json);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(2, body.GetProperty("nextExpected").GetInt64());

        // The tail merged into the global stream exactly once; cursor set — a resend trims.
        var merged = await app.Get<IEventStore>().ReadSinceAsync(seqBefore);
        Assert.Single(merged.Where(e => e.Type == "plan.status_changed"));
        var (ack, _, _) = await SendAsync(client, workerId, 1, 1);
        Assert.True(ack is { accepted: true, nextExpected: 2 });

        // Projections stored (rows AND markdown — rule 3's "something to read" for part 4).
        var count = await app.Get<IWorkerStore>().CountProjectionRowsAsync(workerId);
        Assert.Equal(5, count); // 1 repo + 1 plan + 1 history + 1 session + 1 lesson
        var fleet = await app.Client.GetFromJsonAsync<JsonElement>("/workers");
        var row = fleet.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == workerId);
        Assert.Equal([repo], row.GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetString()).ToList());
    }

    [Fact]
    public async Task SnapshotRepoOwnedByAnotherWorkerConflicts()
    {
        var (clientA, _, workerA, _, repoA) = await NewWorkerAsync();
        var (clientB, _, workerB, _, repoB) = await NewWorkerAsync();
        var snapshotOf = (string ownRepo) => new Snapshot(
            repos: [new SnapshotRepo(ownRepo, "/repos/x")],
            plans: [], history: [], sessions: [], lessons: [], events: []);
        (await clientA.PostAsJsonAsync($"/workers/{workerA}/snapshot", snapshotOf(repoA), Json))
            .EnsureSuccessStatusCode();
        var conflict = await clientB.PostAsJsonAsync($"/workers/{workerB}/snapshot", snapshotOf(repoA), Json);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var text = await conflict.Content.ReadAsStringAsync();
        Assert.Contains(workerA, text); // names the other worker (same-worker re-snapshot is fine)
    }

    // ---- command queue (rule 4) ----

    [Fact]
    public async Task CommandQueueClaimsLeasesAndDedupesResults()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var queue = app.Get<CommandQueueService>();
        var seq = await app.LastSeqAsync();

        // routing by repository ownership (rule 7) + the down-signal event
        var commandId = await queue.EnqueueAsync("plan.command", repo, "sess_pm",
            JsonSerializer.SerializeToElement(new { repo = "agent-hq", command = "start", args = new[] { "x" } }, Json));
        await app.WaitForEventAsync("commands.pending", seq);

        var claim = (await client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0", Json))!;
        var commands = claim.GetProperty("commands");
        Assert.Equal(1, commands.GetArrayLength());
        var env = commands.EnumerateArray().First();
        Assert.Equal(commandId, env.GetProperty("commandId").GetString());
        Assert.Equal("plan.command", env.GetProperty("kind").GetString());
        Assert.Equal("sess_pm", env.GetProperty("actor").GetString());
        Assert.True(env.TryGetProperty("leaseUntil", out _));

        // a leased envelope is not returned again before expiry (same worker or not)
        var claim2 = await app.Client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0");
        Assert.Equal(0, claim2.GetProperty("commands").GetArrayLength());

        // results: first records, duplicate acknowledged and ignored, divergent warns
        var hash = AgentHqClient.ResultHash(true, null);
        var r1 = await PostJsonAsync(client, $"/workers/{workerId}/commands/{commandId}/result",
            new { commandId, ok = true, error = (string?)null, resultHash = hash, finishedAt = DateTimeOffset.UtcNow });
        Assert.False(r1.GetProperty("duplicate").GetBoolean());
        var seqBeforeWarn = await app.LastSeqAsync();
        var r2 = await PostJsonAsync(client, $"/workers/{workerId}/commands/{commandId}/result",
            new { commandId, ok = true, error = (string?)null, resultHash = hash, finishedAt = DateTimeOffset.UtcNow });
        Assert.True(r2.GetProperty("duplicate").GetBoolean());
        var r3 = await PostJsonAsync(client, $"/workers/{workerId}/commands/{commandId}/result",
            new { commandId, ok = false, error = "diverged", resultHash = AgentHqClient.ResultHash(false, "diverged"),
                finishedAt = DateTimeOffset.UtcNow });
        Assert.True(r3.GetProperty("duplicate").GetBoolean());
        var diverged = (await app.Get<IEventStore>().ReadSinceAsync(seqBeforeWarn))
            .SingleOrDefault(e => e.Type == "command.result_diverged");
        Assert.NotEqual(default, diverged);
        Assert.Equal(commandId, diverged.Payload.GetProperty("commandId").GetString());

        // worker B cannot claim A's commands, and A's id from B is the ambiguous 404
        var (clientB, _, workerB, _, repoB) = await NewWorkerAsync();
        var cross = await clientB.GetAsync($"/workers/{workerId}/commands?wait=0");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
    }

    [Fact]
    public async Task ExpiredLeaseRedeliversTheCommand()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var queue = app.Get<CommandQueueService>();
        var commandId = await queue.EnqueueAsync("health.check", repo, "sess_crashed",
            JsonSerializer.SerializeToElement(new { check = "runners" }, Json));

        var claimedAt = DateTimeOffset.UtcNow;
        var first = await client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0", Json);
        Assert.Equal(commandId, first.GetProperty("commands")[0].GetProperty("commandId").GetString());

        // The lease the contract pins: 60 s, in the future.
        var leaseUntil = first.GetProperty("commands")[0].GetProperty("leaseUntil").GetDateTimeOffset();
        var leaseSeconds = (leaseUntil - claimedAt).TotalSeconds;
        Assert.InRange(leaseSeconds, 55, 65);

        // While the lease is live the envelope is leased, not queued: rule 4's heartbeat rule
        // counts "queued (un-leased)" only, so it is neither pending nor redelivered.
        var live = await PostJsonAsync(client, $"/workers/{workerId}/heartbeat", new { version = "1" });
        Assert.Equal(0, live.GetProperty("commandsPending").GetInt32());
        var leaked = await client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0", Json);
        Assert.Equal(0, leaked.GetProperty("commands").GetArrayLength());

        // The spoke died holding it. Past the deadline the hub must redeliver it (rule 4,
        // "expiry redelivers") and report it as pending again. Backdating by a second
        // simulates crossing the real 60 s boundary; no test waits a minute.
        var db = new AgentHqDb(app.AgentHqDbPath);
        await using (var conn = db.Open())
            await conn.ExecuteAsync(
                "UPDATE worker_commands SET lease_until = @past WHERE id = @id",
                new { past = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(), id = commandId });
        var crashed = await PostJsonAsync(client, $"/workers/{workerId}/heartbeat", new { version = "1" });
        Assert.Equal(1, crashed.GetProperty("commandsPending").GetInt32());

        var redelivered = await client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0", Json);
        var commands = redelivered.GetProperty("commands");
        Assert.Equal(1, commands.GetArrayLength());
        Assert.Equal(commandId, commands[0].GetProperty("commandId").GetString());

        // A finished command is never pending, whatever its lease says.
        await PostJsonAsync(client, $"/workers/{workerId}/commands/{commandId}/result",
            new { commandId, ok = true, error = (string?)null, resultHash = AgentHqClient.ResultHash(true, null),
                finishedAt = DateTimeOffset.UtcNow });
        var done = await PostJsonAsync(client, $"/workers/{workerId}/heartbeat", new { version = "1" });
        Assert.Equal(0, done.GetProperty("commandsPending").GetInt32());
    }

    [Fact]
    public async Task EnqueueWithoutAnOwningWorkerRefuses()
    {
        var queue = app.Get<CommandQueueService>();
        // health.check has no repo-owner fallback and this fixture holds no build-health
        // capability — nothing can take it, so enqueue refuses instead of dropping it.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.EnqueueAsync("health.check", $"unowned-{Guid.NewGuid().ToString("N")[..8]}", "sess_x",
                JsonSerializer.SerializeToElement(new { })));
    }

    // ---- contract header (rule 6) ----

    [Fact]
    public async Task ContractHeaderGateNamesBothVersions()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        foreach (var (header, expect) in new[] { ((string?)"2", "v2"), ((string?)null, "none") })
        {
            using var bare = AsRemote("192.168.0.61");
            bare.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
            if (header is not null) bare.DefaultRequestHeaders.Add("X-AgentHQ-Contract", header);
            var resp = await bare.PostAsJsonAsync($"/workers/{workerId}/heartbeat", new { version = "1" }, Json);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("v1", text);
            Assert.Contains(expect, text);
        }
    }

    // ---- roles (criterion 10) ----

    [Fact]
    public async Task WorkerAndCiRolesAreScoped()
    {
        var (workerClient, _, _, _, _) = await NewWorkerAsync();
        // worker reads /events (the down-signal stream's poll form)
        var events = await workerClient.GetAsync("/events?sinceSeq=0");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        // worker cannot list the fleet
        var fleet = await workerClient.GetAsync("/workers");
        Assert.Equal(HttpStatusCode.Forbidden, fleet.StatusCode);

        // ci reaches /ci/events and nothing else
        var identities = app.Get<IdentityService>();
        var ciIdentity = await identities.CreateIdentityAsync("ci-" + Guid.NewGuid().ToString("N")[..6], "pw", "ci");
        var ciRaw = "tok-" + Guid.NewGuid().ToString("N");
        await app.Get<IIdentityStore>().CreateTokenAsync(ciIdentity.Id, Core.Auth.TokenGenerator.Hash(ciRaw), label: "t");
        var ci = AsRemote("192.168.0.61");
        ci.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ciRaw);
        ci.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        var ok = await ci.PostAsJsonAsync("/ci/events",
            new { workflow = "deploy.yml", run = 7, repo = "benow/agent-hq", conclusion = "success",
                url = "https://example.invalid/7", timestamp = DateTimeOffset.UtcNow }, Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var ack = await ok.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(ack.GetProperty("accepted").GetBoolean());
        Assert.True(ack.GetProperty("seq").GetInt64() > 0);
        var refused = await ci.GetAsync("/plans");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    // ---- SSE down-signal (criterion 8) ----

    [Fact]
    public async Task SseSignalDeliversCommandsPendingWithinASecond()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var queue = app.Get<CommandQueueService>();
        // The spoke's subscription: ?types=commands.pending,release.available per rule 0.
        var stream = await client.GetAsync("/events?types=commands.pending,release.available",
            HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        var body = stream.Content.ReadAsStream();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var commandId = await queue.EnqueueAsync("health.check", repo, "sess_pm",
            JsonSerializer.SerializeToElement(new { check = "runners" }, Json));

        using var reader = new StreamReader(body);
        var sawSignal = false;
        while (watch.ElapsedMilliseconds < 8000)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            if (line?.StartsWith("event: commands.pending") == true)
            {
                sawSignal = watch.ElapsedMilliseconds < 1500;
                break;
            }
        }
        Assert.True(sawSignal, $"commands.pending did not arrive within ~1s (took {watch.ElapsedMilliseconds}ms)");

        // The signal is not the lease: the claim route delivers the envelope.
        var claim = await app.Client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0");
        Assert.Equal(1, claim.GetProperty("commands").GetArrayLength());
    }

    [Fact]
    public async Task SseDownFallsBackToLongPollClaimWithNoCommandLost()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var queue = app.Get<CommandQueueService>();

        // The stream was up and then dropped (opened, disposed — nothing is subscribed).
        var dropped = await client.GetAsync("/events?types=commands.pending,release.available",
            HttpCompletionOption.ResponseHeadersRead);
        dropped.Dispose();

        // With no stream, the spoke's long-poll claim is the correctness floor: a command
        // enqueued while the poll waits is delivered by the poll itself, not by a signal.
        var claimTask = client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=25", Json);
        await Task.Delay(300);
        var commandId = await queue.EnqueueAsync("health.check", repo, "sess_fallback",
            JsonSerializer.SerializeToElement(new { check = "runners" }, Json));
        var claim = await claimTask.WaitAsync(TimeSpan.FromSeconds(10));

        var commands = claim.GetProperty("commands");
        Assert.Equal(1, commands.GetArrayLength());
        Assert.Equal(commandId, commands[0].GetProperty("commandId").GetString());

        // Leased by the poll: nothing is redelivered to a second claim before expiry.
        var again = await client.GetFromJsonAsync<JsonElement>($"/workers/{workerId}/commands?wait=0", Json);
        Assert.Equal(0, again.GetProperty("commands").GetArrayLength());
    }

    [Fact]
    public async Task SseReconnectWithLastEventIdReplaysOnlyWhatWasMissed()
    {
        var (client, _, workerId, _, repo) = await NewWorkerAsync();
        var queue = app.Get<CommandQueueService>();
        var json = Dtos.Json;

        // First connection: the spoke sees one signal, then the stream dies.
        var first = await client.GetAsync("/events?types=commands.pending,release.available",
            HttpCompletionOption.ResponseHeadersRead);
        using (var reader = new StreamReader(first.Content.ReadAsStream()))
        {
            var signalTask = Task.Run(async () =>
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) return (long?)null;
                    if (line.StartsWith("id: ") && long.TryParse(line[4..], out var id)) return id;
                }
            });
            await queue.EnqueueAsync("health.check", repo, "sess_a",
                JsonSerializer.SerializeToElement(new { check = "runners" }, json));
            var seen = await signalTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(seen);
            first.Dispose();

            // While the stream is down, two more commands are queued (two more signals).
            await queue.EnqueueAsync("health.check", repo, "sess_b",
                JsonSerializer.SerializeToElement(new { check = "runners" }, json));
            await queue.EnqueueAsync("health.check", repo, "sess_c",
                JsonSerializer.SerializeToElement(new { check = "runners" }, json));

            // Reconnect naming the last id seen: exactly the two missed signals replay, no
            // repeat of the one already handled (criterion 8's "replays nothing already seen").
            using var reconnect = new HttpRequestMessage(HttpMethod.Get,
                "/events?types=commands.pending,release.available");
            reconnect.Headers.Add("Last-Event-ID", seen!.Value.ToString());
            var second = await client.SendAsync(reconnect, HttpCompletionOption.ResponseHeadersRead);
            using var reader2 = new StreamReader(second.Content.ReadAsStream());
            var replayed = new List<long>();
            while (replayed.Count < 2)
            {
                var line = reader2.ReadLine();
                if (line is null) break;
                if (line.StartsWith("id: ") && long.TryParse(line[4..], out var id)) replayed.Add(id);
            }
            Assert.Equal(2, replayed.Count);
            Assert.DoesNotContain(seen!.Value, replayed);
            Assert.All(replayed, id => Assert.True(id > seen!.Value));
        }
    }

    // ---- collision taxonomy (fixing pass: operator-caused collisions are clean 4xx) ----

    [Fact]
    public async Task RegistrationRepoOwnedByAnotherWorkerConflicts()
    {
        var (clientA, _, workerA, _, repoA) = await NewWorkerAsync();
        var (clientB, _, workerB, _, repoB) = await NewWorkerAsync();

        // B claims A's repository → 409 naming A, never a UNIQUE-violation 500.
        var conflict = await clientB.PostAsJsonAsync("/workers/register",
            new { workerId = workerB, name = "spoke-b", machine = "x", version = "1", capabilities = CapsFor(repoA) }, Json);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains(workerA, await conflict.Content.ReadAsStringAsync());

        // B's own claim survives the refusal, and re-registering with it is idempotent.
        var own = await clientB.PostAsJsonAsync("/workers/register",
            new { workerId = workerB, name = "spoke-b", machine = "x", version = "1.1", capabilities = CapsFor(repoB) }, Json);
        own.EnsureSuccessStatusCode();
        var fleet = await app.Client.GetFromJsonAsync<JsonElement>("/workers");
        var rowB = fleet.GetProperty("workers").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == workerB);
        Assert.Equal([repoB], rowB.GetProperty("repositories").EnumerateArray()
            .Select(r => r.GetString()).ToList());
        Assert.Equal("1.1", rowB.GetProperty("version").GetString());
    }

    [Fact]
    public async Task PairingApproveWithTakenNameIsACleanConflict()
    {
        var anon = AsRemote("192.168.0.90");
        anon.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        var created = await anon.PostAsync("/workers/pairing", content: null);
        created.EnsureSuccessStatusCode();
        var nonce = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("nonce").GetString()!;

        // The name is already an identity (the second run of a demo, a retry, a typo'd dup).
        await app.Get<IdentityService>().CreateIdentityAsync("taken-spoke", "pw", "worker");
        var taken = await app.Client.PostAsJsonAsync($"/workers/pairing/{nonce}/approve",
            new { name = "taken-spoke" }, Json); // loopback is an admin session
        Assert.Equal(HttpStatusCode.UnprocessableEntity, taken.StatusCode);
        var text = await taken.Content.ReadAsStringAsync();
        Assert.Contains("name_taken", text);
        // The message says what to do, and the nonce stays usable.
        Assert.Contains("still pending", text);

        var retry = await app.Client.PostAsJsonAsync($"/workers/pairing/{nonce}/approve",
            new { name = "fresh-spoke-" + Guid.NewGuid().ToString("N")[..6] }, Json);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var delivered = await anon.GetFromJsonAsync<JsonElement>($"/workers/pairing/{nonce}");
        Assert.Equal("approved", delivered.GetProperty("state").GetString());
        Assert.False(string.IsNullOrEmpty(delivered.GetProperty("token").GetString()));
    }

    // ---- pairing (rule 10, criterion 9) ----

    [Fact]
    public async Task PairingFlowDeliversTokenExactlyOnce()
    {
        var anon = AsRemote("192.168.0.77"); // its own IP so rate-limit tests do not interfere
        anon.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        var created = await anon.PostAsync("/workers/pairing", content: null);
        created.EnsureSuccessStatusCode();
        var nonce = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("nonce").GetString()!;

        // approve without an admin session → 401
        var unauth = await anon.PostAsJsonAsync($"/workers/pairing/{nonce}/approve",
            new { name = "spoke-x" }, Json);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // poll is pending before approval
        var poll1 = await anon.GetFromJsonAsync<JsonElement>($"/workers/pairing/{nonce}");
        Assert.Equal("pending", poll1.GetProperty("state").GetString());

        // browser approval with an admin session (cookie grant)
        var identities = app.Get<IdentityService>();
        await identities.CreateIdentityAsync("pair-admin", "pair-admin-pw", "admin");
        var login = await identities.LoginAsync("pair-admin", "pair-admin-pw", "192.168.0.5", "/auth/login");
        Assert.NotNull(login);
        var browser = AsRemote("192.168.0.5");
        browser.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        browser.DefaultRequestHeaders.Add("Cookie", $"agent_hq_token={login!.Value.Token}");
        var approve = await browser.PostAsJsonAsync($"/workers/pairing/{nonce}/approve",
            new { name = "laptop-spoke" }, Json);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // the poll delivers a working worker token exactly once
        var delivered = await anon.GetFromJsonAsync<JsonElement>($"/workers/pairing/{nonce}");
        Assert.Equal("approved", delivered.GetProperty("state").GetString());
        var token = delivered.GetProperty("token").GetString()!;
        var spoke = AsRemote("192.168.0.77");
        spoke.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        spoke.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        var reg = await spoke.PostAsJsonAsync("/workers/register",
            new { name = "laptop-spoke", machine = "laptop", version = "1",
                capabilities = CapsFor($"paired-{nonce[..8]}") }, Json);
        Assert.Equal(HttpStatusCode.OK, reg.StatusCode);
        var wid = (await reg.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("workerId").GetString();
        var hb = await spoke.PostAsJsonAsync($"/workers/{wid}/heartbeat", new { version = "1" }, Json);
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);

        // second poll: consumed, no token
        var again = await anon.GetFromJsonAsync<JsonElement>($"/workers/pairing/{nonce}");
        Assert.Equal("consumed", again.GetProperty("state").GetString());
        Assert.False(again.TryGetProperty("token", out var leaked) && leaked.ValueKind == JsonValueKind.String);

        // bad nonce → unknown; expired → expired
        var unknown = await anon.GetFromJsonAsync<JsonElement>("/workers/pairing/no-such-nonce");
        Assert.Equal("unknown", unknown.GetProperty("state").GetString());
        var store = app.Get<IWorkerStore>();
        var expiredNonce = "expired-nonce-1";
        await store.InsertPairingAsync(new AgentHQ.Models.WorkerPairingRow
        {
            Nonce = expiredNonce, CreatedAt = 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(), State = "pending"
        });
        var expired = await anon.GetFromJsonAsync<JsonElement>($"/workers/pairing/{expiredNonce}");
        Assert.Equal("expired", expired.GetProperty("state").GetString());
    }

    [Fact]
    public async Task PairingCreateRateLimitIsPerSource()
    {
        const string ip = "192.168.0.88";
        var client = AsRemote(ip);
        client.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 11; i++)
            last = (await client.PostAsync("/workers/pairing", content: null)).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        var text = await (await client.PostAsync("/workers/pairing", content: null)).Content.ReadAsStringAsync();
        Assert.Contains("rate_limited", text);
        // another source is unaffected
        var other = AsRemote("192.168.0.89");
        other.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
        var resp2 = await other.PostAsync("/workers/pairing", content: null);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    // ---- conformance kit (criterion 2) ----

    [Fact]
    public async Task ConformanceKitPassesAgainstTheRealHub()
    {
        var results = await AgentHqConformance.RunAsync(new AgentHqConformance.Host(
            NewWorkerAsync: async () =>
            {
                var (client, _, workerId, _, repo) = await NewWorkerAsync();
                return (client, workerId);
            },
            EnqueueCommandAsync: async (workerId, kind, actor, payload) =>
            {
                // Route by this worker's own repo claim, deterministic in a shared fixture.
                var repos = await app.Get<IWorkerStore>().ListReposAsync(workerId);
                var repo = repos.FirstOrDefault()?.RepoName ?? $"kit-{workerId[..8]}";
                await app.Get<CommandQueueService>().EnqueueAsync(kind, repo, actor, payload);
            },
            NewCiClientAsync: async () =>
            {
                var identities = app.Get<IdentityService>();
                var identity = await identities.CreateIdentityAsync("ci-conf-" + Guid.NewGuid().ToString("N")[..6], "pw", "ci");
                var raw = "tok-" + Guid.NewGuid().ToString("N");
                await app.Get<IIdentityStore>().CreateTokenAsync(identity.Id, Core.Auth.TokenGenerator.Hash(raw), label: "t");
                var client = AsRemote("192.168.0.61");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);
                client.DefaultRequestHeaders.Add("X-AgentHQ-Contract", "1");
                return client;
            },
            AdminClient: app.Client));

        var failures = results.Where(r => !r.Passed).ToList();
        Assert.True(failures.Count == 0,
            "conformance failures: " + string.Join(" | ", failures.Select(f => $"{f.Name}: {f.Detail}")));
    }
}

/// <summary>No watchers, no repositories: the spoke surface never touches the plan tree, and
/// the full-suite parallel run sits at the 128-instance inotify budget (lessons.md).</summary>
public sealed class WorkerFixture : AgentHQApp
{
    public WorkerFixture() => StartWatchers = false;
}
