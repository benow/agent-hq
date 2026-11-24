using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentHQ.Client.Conformance;

/// <summary>One named check result of the kit.</summary>
public sealed record CheckResult(string Name, bool Passed, string Detail);

/// <summary>
/// The Agent HQ v1 spoke-conformance kit (plan 03 "the package"). Any hub implementation
/// runs this against its real composition: it registers a fresh worker identity, then proves
/// the contract's load-bearing rules — header enforcement, own-id scoping, the three-case
/// ordering rule, the claim lease field, result dedupe with the hash formula, snapshot
/// ownership conflict, the heartbeat shape, and the CI ingest shape. The hub's own test
/// suite runs it; a third-party hub can too. (Lease *expiry* redelivery is a timing property
/// the kit cannot wait 60 s for; the hub's own suite proves it by backdating the lease.)
/// </summary>
public static class AgentHqConformance
{
    /// <summary>What the kit needs from the host under test: a factory that provisions a
    /// brand-new worker identity and returns an HttpClient already carrying its bearer token,
    /// plus a delegate that enqueues one command to a named worker (the enqueue API is the
    /// queue service, not a public route) and a factory for a `ci`-role client. Third-party
    /// hubs implement the same three against their own wiring.</summary>
    public sealed record Host(
        Func<Task<(HttpClient Client, string WorkerId)>> NewWorkerAsync,
        Func<string /*workerId*/, string /*kind*/, string /*actor*/, JsonElement, Task> EnqueueCommandAsync,
        Func<Task<HttpClient>> NewCiClientAsync,
        HttpClient AdminClient);

    private static readonly JsonSerializerOptions Json = Dtos.Json;

    public static async Task<IReadOnlyList<CheckResult>> RunAsync(Host host, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();
        void Record(string name, bool pass, string detail) => results.Add(new(name, pass, detail));

        var (client, workerId) = await host.NewWorkerAsync();

        // 1. Contract header: a request with a foreign major → 400 naming both versions
        //    (the header is swapped on the given client and restored afterwards).
        {
            var original = client.DefaultRequestHeaders.GetValues(AgentHqContract.HeaderName).FirstOrDefault();
            client.DefaultRequestHeaders.Remove(AgentHqContract.HeaderName);
            client.DefaultRequestHeaders.Add(AgentHqContract.HeaderName, "2");
            var resp = await client.PostAsJsonAsync($"/workers/{workerId}/heartbeat",
                new { version = "0.0.0" }, Json, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            Record("contract-header.mismatch-400",
                resp.StatusCode == HttpStatusCode.BadRequest
                && text.Contains("v1") && text.Contains("v2"),
                $"status {(int)resp.StatusCode} body: {text[..Math.Min(text.Length, 200)]}");
            client.DefaultRequestHeaders.Remove(AgentHqContract.HeaderName);
            if (original is not null)
                client.DefaultRequestHeaders.Add(AgentHqContract.HeaderName, original);
        }

        // 2. Heartbeat shape: serverVersion + contractVersion + lastAcceptedSeq + commandsPending.
        {
            var resp = await client.PostAsJsonAsync($"/workers/{workerId}/heartbeat",
                new { version = "1.0.0" }, Json, ct);
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var ok = resp.IsSuccessStatusCode
                     && doc.TryGetProperty("lastAcceptedSeq", out var las) && las.ValueKind == JsonValueKind.Number
                     && doc.TryGetProperty("commandsPending", out var cp) && cp.ValueKind == JsonValueKind.Number
                     && doc.TryGetProperty("serverVersion", out var sv) && sv.ValueKind == JsonValueKind.String
                     && doc.TryGetProperty("contractVersion", out var cv) && cv.ValueKind == JsonValueKind.String;
            Record("heartbeat.shape", ok, $"status {(int)resp.StatusCode} body {doc.ToString()[..Math.Min(doc.ToString().Length, 200)]}");
        }

        // 3. Own-id scoping: another worker's id → 404 indistinguishable from unknown.
        {
            var (clientB, workerB) = await host.NewWorkerAsync();
            var resp = await clientB.PostAsJsonAsync($"/workers/{workerId}/heartbeat",
                new { version = "1.0.0" }, Json, ct);
            Record("worker-scope.own-id-404",
                resp.StatusCode == HttpStatusCode.NotFound,
                $"status {(int)resp.StatusCode} (worker B addressing worker A's id)");
            resp.Dispose();
        }

        // 4. Three-case ordering: equal applies, duplicate trims to zero, ahead rejects 422.
        long nextExpected;
        {
            var ack1 = await SendBatchAsync(client, workerId, from: 0, origins: [1, 2], ct);
            var dupAck = await SendBatchAsync(client, workerId, from: 0, origins: [1, 2], ct);
            var ahead = await client.PostAsJsonAsync($"/workers/{workerId}/events",
                new { fromOriginSeq = 99, events = Array.Empty<object>() }, Json, ct);
            var aheadBody = await ahead.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            nextExpected = 3;
            var ok = ack1 is { accepted: true } && ack1.nextExpected == nextExpected
                     && dupAck is { accepted: true } && dupAck.nextExpected == nextExpected
                     && ahead.StatusCode == HttpStatusCode.UnprocessableEntity
                     && aheadBody.TryGetProperty("expected", out var exp) && exp.GetInt64() == nextExpected;
            Record("ordering.three-case", ok,
                $"ack1 {ack1?.ToString() ?? "null"}; dup {dupAck?.ToString() ?? "null"}; ahead {(int)ahead.StatusCode}");
        }

        // 5. Result dedupe: first result records, second acks duplicate; hash formula matches.
        {
            await host.EnqueueCommandAsync(workerId, AgentHqContract.CommandKindHealthCheck,
                "conformance", JsonSerializer.SerializeToElement(new { check = "ping" }, Json));
            CommandEnvelope envelope = null!;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                var envelopes = await new AgentHqClient(client).ClaimCommandsAsync(workerId, 0, ct);
                envelope = envelopes.FirstOrDefault(e => e.kind == AgentHqContract.CommandKindHealthCheck)!;
                if (envelope is not null || DateTime.UtcNow > deadline) break;
                await Task.Delay(100, ct);
            } while (true);

            if (envelope is null)
            {
                Record("command-result.dedupe", false, "no health.check command was claimable within 5 s");
            }
            else
            {
            Record("command-lease.field",
                envelope.leaseUntil is { } lease && lease > DateTimeOffset.UtcNow,
                $"leaseUntil {envelope.leaseUntil?.ToString("O") ?? "null"}");
            var okHash = AgentHqClient.ResultHash(ok: true, null);
            var ack1 = await PostResultAsync(client, workerId, envelope.commandId, ok: true, null, okHash, ct);
            var ack2 = await PostResultAsync(client, workerId, envelope.commandId, ok: true, null, okHash, ct);
            var ack3 = await PostResultAsync(client, workerId, envelope.commandId, ok: false, "diverged",
                AgentHqClient.ResultHash(false, "diverged"), ct);
            Record("command-result.dedupe",
                ack1 is { ok: true, duplicate: false } && ack2 is { ok: true, duplicate: true }
                && ack3 is { ok: true, duplicate: true },
                $"first {ack1?.ToString() ?? "null"} second {ack2?.ToString() ?? "null"} divergent {ack3?.ToString() ?? "null"}");
            }
        }

        // 6. Snapshot ownership: a snapshot claiming a repo owned by another worker → 409.
        {
            var (clientB, workerB) = await host.NewWorkerAsync();
            var snapshot = new Snapshot(
                repos: [new SnapshotRepo("conformance-owned-repo", "/tmp")],
                plans: [], history: [], sessions: [], lessons: [], events: []);
            var own = await client.PostAsJsonAsync($"/workers/{workerId}/snapshot", snapshot, Json, ct);
            var conflict = await clientB.PostAsJsonAsync($"/workers/{workerB}/snapshot", snapshot, Json, ct);
            Record("snapshot.ownership-409",
                own.IsSuccessStatusCode && conflict.StatusCode == HttpStatusCode.Conflict,
                $"own {(int)own.StatusCode} conflict {(int)conflict.StatusCode}");
            own.Dispose(); conflict.Dispose();
        }

        // 7. CI ingest shape.
        {
            var ci = await host.NewCiClientAsync();
            var resp = await ci.PostAsJsonAsync("/ci/events",
                new { workflow = "deploy.yml", run = 4412, repo = "benow/agent-hq",
                    conclusion = "success", url = "https://example.invalid/run/4412",
                    timestamp = DateTimeOffset.UtcNow }, Json, ct);
            var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var ok = resp.IsSuccessStatusCode && doc.TryGetProperty("accepted", out var a) && a.ValueKind == JsonValueKind.True
                     && doc.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number;
            Record("ci-ingest.shape", ok, $"status {(int)resp.StatusCode} body {doc.ToString()[..Math.Min(doc.ToString().Length, 120)]}");
            resp.Dispose();
        }

        return results;
    }

    private static async Task<EventsAck?> SendBatchAsync(HttpClient client, string workerId,
        long from, long[] origins, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync($"/workers/{workerId}/events",
            new { fromOriginSeq = from, events = origins.Select(o => new
            {
                originSeq = o, type = "plan.updated",
                occurredAt = DateTimeOffset.UtcNow, actor = "conformance",
                transportedBy = "conformance-worker", payload = (JsonElement)JsonSerializer.SerializeToElement(new { n = o }, Json)
            }) }, Json, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<EventsAck>(Json, ct);
    }

    private static async Task<CommandResultAck?> PostResultAsync(HttpClient client, string workerId,
        string commandId, bool ok, string? error, string hash, CancellationToken ct)
    {
        var resp = await client.PostAsJsonAsync($"/workers/{workerId}/commands/{commandId}/result",
            new { commandId, ok, error, resultHash = hash, finishedAt = DateTimeOffset.UtcNow }, Json, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CommandResultAck>(Json, ct);
    }

    /// <summary>The kit needs the raw token it was given to test header enforcement without
    /// the contract header — the hub's NewWorkerAsync factory stashes it in the client's
    /// default headers; this reads it back.</summary>
    private static async Task<string?> TokenOfAsync(HttpClient client) =>
        client.DefaultRequestHeaders.Authorization?.Parameter;
}
