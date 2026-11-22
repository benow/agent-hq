using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgentHQ.Client;

/// <summary>
/// The typed spoke client of the Agent HQ v1 contract. One instance per spoke; the bearer
/// token is the paired worker token. Retries are exponential backoff with jitter, bounded —
/// only for idempotent calls and transport-level failures; per-stream sends (the delta
/// batches) are retried by the caller's cursor logic, never here (the ack-lost case is the
/// protocol's own recovery, not a blind resend).
/// </summary>
public sealed class AgentHqClient
{
    private readonly HttpClient _http;
    private readonly int _maxRetries;
    private readonly Random _jitter = new();

    public AgentHqClient(HttpClient httpClient, string bearerToken, int maxRetries = 3)
    {
        _http = httpClient;
        _maxRetries = maxRetries;
        if (!_http.DefaultRequestHeaders.Contains(AgentHqContract.HeaderName))
            _http.DefaultRequestHeaders.TryAddWithoutValidation(AgentHqContract.HeaderName, AgentHqContract.Version);
        if (!string.IsNullOrEmpty(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
    }

    /// <summary>The unauthenticated client used for pairing (no bearer yet).</summary>
    public AgentHqClient(HttpClient httpClient) : this(httpClient, "") { }

    // ---- registration + presence ----

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        await SendAsync(async () =>
        {
            var resp = await _http.PostAsJsonAsync("/workers/register", request, Dtos.Json, ct);
            return (resp, await resp.Content.ReadFromJsonAsync<RegisterResponse>(Dtos.Json, ct));
        });

    public async Task<HeartbeatResponse> HeartbeatAsync(string workerId, string spokeVersion,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/workers/{workerId}/heartbeat",
            new HeartbeatRequest(spokeVersion), Dtos.Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<HeartbeatResponse>(Dtos.Json, ct))!;
    }

    // ---- replication (up-signal) ----

    /// <summary>Sends one delta batch. Returns the ack, or the expected cursor on a 422
    /// (batch ahead — rewind and resend from it), or null on an unrecognised failure.</summary>
    public async Task<(EventsAck? Ack, long? Expected, HttpStatusCode Status)> SendEventsAsync(
        string workerId, EventsBatch batch, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/workers/{workerId}/events", batch, Dtos.Json, ct);
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            var rejected = await resp.Content.ReadFromJsonAsync<EventsRejected>(Dtos.Json, ct);
            return (null, rejected?.expected, resp.StatusCode);
        }
        if (!resp.IsSuccessStatusCode) return (null, null, resp.StatusCode);
        var ack = await resp.Content.ReadFromJsonAsync<EventsAck>(Dtos.Json, ct);
        return (ack, null, resp.StatusCode);
    }

    public async Task SendSnapshotAsync(string workerId, Snapshot snapshot, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/workers/{workerId}/snapshot", snapshot, Dtos.Json, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ---- commands (down-signal claim + result) ----

    /// <summary>Long-poll claim: waits up to <paramref name="waitSeconds"/> for queued envelopes
    /// (the SSE-down fallback). Returns an empty list when nothing arrived inside the window.</summary>
    public async Task<CommandEnvelope[]> ClaimCommandsAsync(string workerId, int waitSeconds = 25,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(waitSeconds + 10));
        var resp = await _http.GetAsync($"/workers/{workerId}/commands?wait={waitSeconds}", cts.Token);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Dtos.Json, cts.Token);
        return doc.TryGetProperty("commands", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(e => e.Deserialize<CommandEnvelope>(Dtos.Json)!).ToArray()
            : Array.Empty<CommandEnvelope>();
    }

    public async Task<CommandResultAck> PostResultAsync(string workerId, CommandResult result,
        CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync($"/workers/{workerId}/commands/{result.commandId}/result",
            result, Dtos.Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CommandResultAck>(Dtos.Json, ct))!;
    }

    /// <summary>Canonical hash of a command result (the contract's resultHash): sha256 of the
    /// canonical JSON of {ok, error} — the same formula hub and spoke must apply.</summary>
    public static string ResultHash(bool ok, string? error)
    {
        var canonical = JsonSerializer.Serialize(new { ok, error }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    // ---- pairing (credential bootstrap, rule 10) ----

    public async Task<PairingCreated> PairingCreateAsync(CancellationToken ct = default) =>
        await SendAsync(async () =>
        {
            var resp = await _http.PostAsync("/workers/pairing", content: null, ct);
            return (resp, await resp.Content.ReadFromJsonAsync<PairingCreated>(Dtos.Json, ct));
        });

    public async Task<PairingPoll> PairingPollAsync(string nonce, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/workers/pairing/{nonce}", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PairingPoll>(Dtos.Json, ct))!;
    }

    // ---- CI ingest ----

    public async Task<CiAck> PostCiEventAsync(CiEvent ciEvent, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("/ci/events", ciEvent, Dtos.Json, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<CiAck>(Dtos.Json, ct))!;
    }

    // ---- internals ----

    /// <summary>POST with bounded exponential backoff + jitter, for calls whose resend is
    /// safe (register, pairing create — the hub-side rules are idempotent or non-queued).
    /// Envelope/result sends are NOT retried here.</summary>
    private async Task<T> SendAsync<T>(Func<Task<(HttpResponseMessage Resp, T? Body)>> call)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= _maxRetries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1) + _jitter.Next(50)));
            try
            {
                var (resp, body) = await call();
                if ((int)resp.StatusCode >= 400 && (int)resp.StatusCode < 500 && resp.StatusCode != HttpStatusCode.TooManyRequests)
                    throw new AgentHqClientException($"request refused ({(int)resp.StatusCode}): {resp.ReasonPhrase}");
                if (resp.IsSuccessStatusCode && body is not null) return body;
                last = new AgentHqClientException($"unexpected response ({(int)resp.StatusCode})");
            }
            catch (AgentHqClientException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or InvalidOperationException or JsonException)
            {
                last = ex;
            }
        }
        throw new AgentHqClientException("request failed after retries", last);
    }
}

public sealed class AgentHqClientException(string message, Exception? inner = null)
    : Exception(message, inner);
