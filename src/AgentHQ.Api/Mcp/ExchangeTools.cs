using System.ComponentModel;
using System.Text.Json;
using AgentHQ.Core.Exchange;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Exchange;
using AgentHQ.Infrastructure.Stores;
using ModelContextProtocol.Server;

namespace AgentHQ.Api.Mcp;

/// <summary>
/// The Exchange MCP tools — thin wrappers over <see cref="ExchangeService"/>, the same service the
/// REST routes call. The rules from AGENTS.md apply hard here: every parameter is required (empty
/// string is "not supplied" and is trimmed in the body), returned property names are the wire
/// names, embedded payloads are serialized as JSON text, and the description carries the contract
/// and names the error codes.
/// </summary>
[McpServerToolType]
public static class ExchangeTools
{
    private const string Path = "empty ''";

    private static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static object Fail(ExchangeError e) => new { ok = false, error = e.Code, detail = e.Detail };

    private static object Item(ExchangeItem i) => new
    {
        id = i.Id, kind = i.Kind, state = i.State, threadId = i.ThreadId, hopCount = i.HopCount,
        sender = i.Sender.ToDisplayString(), recipient = i.Recipient.ToDisplayString(),
        repositoryId = i.RepositoryId, subject = i.Subject, purpose = i.Purpose, body = i.Body,
        contextRefs = i.ContextRefs, claimedBy = i.ClaimedBySessionId,
        createdAt = i.CreatedAt, answeredAt = i.AnsweredAt, acknowledgedAt = i.AcknowledgedAt,
        expiresAt = i.ExpiresAt, killedAt = i.KilledAt,
        blob = i.BlobPath is null ? null : new
        {
            size = i.BlobSize, contentType = i.BlobContentType, sha256 = i.BlobSha256,
            download = $"/exchange/items/{i.Id}/blob"
        }
    };

    private static bool TryActor(string text, out ExchangeActor actor, out object? error)
    {
        if (ExchangeActor.TryParse(text, out actor, out var message)) { error = null; return true; }
        error = new { ok = false, error = ExchangeStateMachine.Codes.UnknownActor, detail = message };
        return false;
    }

    [McpServerTool, Description(
        "Post to the Exchange: a question (kind=question — purpose is required: what its answer unblocks), a message, a file (then upload the bytes with the HTTP route POST /exchange/items/{id}/blob, offset/total/sha256) or a status. " +
        "sender and recipient are 'type:id' — andy:andy, openclaw:linda_v2, linda_v1:linda_v1, zcode:<session id>; recipient 'zcode:*' means any live session in 'repository' and needs it. A message with no recipient is routed by the last @andy/@openclaw/@linda_v1 mention, or refused. " +
        "Pass idempotencyKey (any string) to make a retry safe: the same sender + key returns the original id with deduplicated=true. contextRefs is a JSON object such as {\"repo\":\"<repository id>\",\"plan\":\"<plan id>\"}. " +
        $"Errors: unknown_actor, purpose_required, body_too_large, bad_idempotency_key, rate_limited, illegal_transition, not_found. Unused text arguments take {Path}.")]
    public static async Task<object> ExchangePost(string kind, string sender, string recipient, string repository,
        string subject, string purpose, string body, string contextRefs, string idempotencyKey,
        string latencyClass, long expiresInSeconds, ExchangeService svc)
    {
        if (!TryActor(sender, out var from, out var actorError)) return actorError!;
        var result = await svc.PostAsync(new PostRequest(
            from, N(kind)?.ToLowerInvariant() ?? "", null, N(recipient), N(repository), N(subject), N(purpose),
            N(body), N(contextRefs),
            expiresInSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds) : null,
            N(idempotencyKey), N(latencyClass)));
        return result.Ok
            ? new { ok = true, id = result.Item!.Id, state = result.Item.State, threadId = result.Item.ThreadId,
                deduplicated = result.Deduplicated }
            : Fail(result.Error!);
    }

    [McpServerTool, Description(
        "Fetch one Exchange item in full, body included — the deliberate read the digest deliberately never does. " +
        "actor is who is reading ('type:id'). Errors: not_found, unknown_actor.")]
    public static async Task<object> ExchangeGet(string itemId, string actor, ExchangeService svc)
    {
        if (!TryActor(actor, out _, out var actorError)) return actorError!;
        var item = await svc.GetAsync(itemId);
        if (item is null) return new { ok = false, error = ExchangeStateMachine.Codes.NotFound, detail = $"no item {itemId}" };
        var thread = await svc.ThreadAsync(item.ThreadId);
        return new { ok = true, item = Item(item), thread = thread.Select(Item).ToList() };
    }

    [McpServerTool, Description(
        "A consumer's Exchange inbox: items addressed to actor ('type:id'), plus role-addressed (zcode:*) items in the session's repository when 'repository' names it (registered id or a cwd under it). " +
        $"state filters to one of queued, delivered, claimed, answered, acknowledged, expired, killed ({Path} = all). limit <= 0 means 200. " +
        "Errors: unknown_actor.")]
    public static async Task<object> ExchangeListInbox(string actor, string repository, string state, long limit,
        ExchangeService svc)
    {
        if (!TryActor(actor, out var reader, out var actorError)) return actorError!;
        var items = await svc.InboxAsync(reader, N(repository), N(state), null, limit <= 0 ? 200 : (int)limit);
        return new { ok = true, count = items.Count, items = items.Select(Item).ToList() };
    }

    [McpServerTool, Description(
        "Claim a role-addressed question (recipient zcode:*) in your repository so that you — and only you — may answer it. claimant is 'zcode:<session id>'. " +
        "A second claimant gets error already_claimed with claimedBy (that session also keeps working it; you do not). " +
        $"Errors: already_claimed, illegal_transition, not_found, unknown_actor. Pass {Path} for repository.")]
    public static async Task<object> ExchangeClaim(string itemId, string claimant, string repository, ExchangeService svc)
    {
        if (!TryActor(claimant, out var who, out var actorError)) return actorError!;
        _ = repository; // accepted for symmetry with exchange_post's routing; a claim needs only the item
        var result = await svc.ClaimAsync(itemId, who);
        return result.Ok
            ? new { ok = true, id = result.Item!.Id, state = result.Item.State, claimedBy = result.Item.ClaimedBySessionId }
            : new { ok = false, error = result.Error!.Code, detail = result.Error.Detail, claimedBy = result.Item?.ClaimedBySessionId };
    }

    [McpServerTool, Description(
        "Answer a question (or reply again in its thread). sender is 'type:id'. Creates an answer on the question's thread addressed back to its sender and moves the question to answered. " +
        "Only the claimant may answer a claimed question; nobody may answer after the thread is killed; the hop limit (3 replies to the root) refuses further depth. " +
        "idempotencyKey makes a retry safe. Errors: hop_limit (names the thread), thread_killed, illegal_transition, rate_limited, body_too_large, not_found, unknown_actor.")]
    public static async Task<object> ExchangeReply(string itemId, string sender, string body, string subject,
        string idempotencyKey, ExchangeService svc) =>
        await ReplyTo(itemId, sender, body, subject, idempotencyKey, svc);

    [McpServerTool, Description(
        "Acknowledge an item (or a whole answered thread): state becomes acknowledged and the item drops out of every digest. " +
        "ackSender is 'type:id' who acknowledges. Errors: illegal_transition, not_found.")]
    public static async Task<object> ExchangeAck(string itemId, string ackSender, ExchangeService svc)
    {
        if (!TryActor(ackSender, out var who, out var actorError)) return actorError!;
        var result = await svc.AckAsync(itemId, who);
        return result.Ok
            ? new { ok = true, id = result.Item!.Id, state = result.Item.State }
            : Fail(result.Error!);
    }

    [McpServerTool, Description(
        "Andy's kill switch for a live thread: every member that is not acknowledged becomes killed; later replies are refused thread_killed and digests render it killed. " +
        "Only andy may call it (by='andy:andy'). Pass the root item id (any thread member works). Errors: scope_denied, illegal_transition, not_found.")]
    public static async Task<object> ExchangeKill(string itemId, string by, string reason, ExchangeService svc)
    {
        if (!TryActor(by, out var who, out var actorError)) return actorError!;
        if (!who.IsAndy) return new { ok = false, error = ExchangeStateMachine.Codes.ScopeDenied, detail = "only andy may kill an item" };
        var result = await svc.KillAsync(itemId, who, N(reason));
        return result.Ok
            ? new { ok = true, threadId = result.Item!.ThreadId, state = result.Item.State }
            : Fail(result.Error!);
    }

    [McpServerTool, Description(
        "Every open question (queued or claimed) with sender, claimant, age, hop count, purpose and its real state — Andy's list; the operator view rather than a session's inbox. " +
        $"repository limits it to one repository ({Path} = all).")]
    public static async Task<object> ExchangeListOpen(string repository, ExchangeService svc)
    {
        var open = await svc.OpenAsync(N(repository));
        return new
        {
            ok = true,
            count = open.Count,
            questions = open.Select(o => new
            {
                id = o.Item.Id, threadId = o.Item.ThreadId, sender = o.Item.Sender.ToDisplayString(),
                recipient = o.Item.Recipient.ToDisplayString(), repositoryId = o.Item.RepositoryId,
                ageSeconds = o.AgeSeconds, hopCount = o.HopCount, purpose = o.Item.Purpose,
                state = o.Item.State, claimedBy = o.ClaimedBy, subject = o.Item.Subject
            }).ToList()
        };
    }

    [McpServerTool, Description(
        "Catch-up poll for push consumers: agent-hq Exchange events (exchange.created/claimed/answered/acknowledged/expired/killed/resync/presence) after sinceSeq. " +
        "With actor, the server cursor advances to the last seq returned; a cursor older than the replay window answers resync=true and the catch-up from the floor, never a silent gap. " +
        "For a live stream use GET /exchange/watch (SSE). Errors: unknown_actor.")]
    public static async Task<object> ExchangeWatch(long sinceSeq, string actor, EventStore events,
        ExchangeService svc)
    {
        ExchangeActor? reader = null;
        if (!string.IsNullOrWhiteSpace(actor))
        {
            if (!TryActor(actor, out var a, out var actorError)) return actorError!;
            reader = a;
        }
        var (resync, fromSeq) = await svc.PrepareCatchUpAsync(Math.Min(sinceSeq, await events.LastSeqAsync()));
        var rows = await events.ReadSinceAsync(fromSeq, AgentHQ.Core.Events.EventTypes.ExchangeTypes);
        if (reader is not null && rows.Count > 0) await svc.SetCursorAsync(reader.Value, rows[^1].Seq);
        return new
        {
            ok = true, resync = resync, fromSeq = fromSeq, lastSeq = await events.LastSeqAsync(),
            events = rows.Select(e => new { seq = e.Seq, type = e.Type, at = e.At,
                payload = JsonSerializer.Serialize(e.Payload, AgentHqEventOptions) }).ToList()
        };
    }

    [McpServerTool, Description(
        "Report a heartbeat for a push/pull consumer (actor 'type:id'): minutes- or human-paced consumers call this each turn. presence is a heartbeat, not a status: " +
        "an actor whose last heartbeat is older than 600 s renders offline (see exchange_list_open / GET /exchange/presence). latencyClass: realtime, turn, minutes, human.")]
    public static async Task<object> ExchangePresence(string actor, string latencyClass, ExchangeService svc)
    {
        if (!TryActor(actor, out var who, out var actorError)) return actorError!;
        await svc.HeartbeatAsync(who, N(latencyClass) ?? ExchangeLatency.Human);
        return new { ok = true, actor = who.ToDisplayString(), latencyClass = N(latencyClass) ?? ExchangeLatency.Human };
    }

    [McpServerTool, Description(
        "Advance (lastSeq > 0) or clear (lastSeq = 0, meaning replay from the start) a consumer's stored Exchange cursor for actor. Returns the cursor now stored. " +
        $"Errors: unknown_actor. Pass {Path} for nothing.")]
    public static async Task<object> ExchangeCursor(string actor, long lastSeq, ExchangeService svc)
    {
        if (!TryActor(actor, out var who, out var actorError)) return actorError!;
        var (set, cleared) = await svc.SetCursorAsync(who, lastSeq);
        return new { ok = true, actor = who.ToDisplayString(), lastSeq = set, cleared = cleared };
    }

    [McpServerTool, Description(
        "Upload a file to a 'file' item over MCP (bytes are chunked on the HTTP route, so this tool only tells you how): " +
        "POST /exchange/items/{id}/blob?offset=<n>&total=<bytes>&sha256=<hex>&contentType=<type> with the chunk as the raw body, repeating offset += chunk length. " +
        "The server verifies the whole-file SHA-256 and rejects a mismatch (hash_mismatch, partial deleted). Returns the URL and hash to hand to the recipient; blob bytes never travel in MCP results or the digest.")]
    public static Task<object> ExchangeUpload(string itemId, string sha256, long totalBytes, string contentType) =>
        Task.FromResult<object>(new
        {
            ok = true, itemId = itemId, sha256 = sha256, totalBytes = totalBytes, contentType = contentType,
            deposit = $"/exchange/items/{itemId}/blob?offset=0&total={totalBytes}&sha256={sha256}&contentType={Uri.EscapeDataString(contentType)}",
            method = "POST with the raw chunk bytes; repeat with offset advanced by each chunk's length; complete=true only at total"
        });

    [McpServerTool, Description(
        "Download a blob: returns its Download URL (GET /exchange/items/{id}/blob), ContentType and Sha256 to verify your copy. " +
        "Bytes are served by the HTTP route, never inside this result. Errors: not_found (no blob) — fetch the item with exchange_get.")]
    public static async Task<object> ExchangeDownload(string itemId, ExchangeService svc)
    {
        var item = await svc.GetAsync(itemId);
        if (item is null) return new { ok = false, error = ExchangeStateMachine.Codes.NotFound, detail = $"no item {itemId}" };
        if (item.BlobPath is null)
            return new { ok = false, error = ExchangeStateMachine.Codes.NotFound, detail = $"item {itemId} has no blob" };
        return new
        {
            ok = true, itemId = item.Id, contentType = item.BlobContentType, sha256 = item.BlobSha256,
            size = item.BlobSize, download = $"/exchange/items/{item.Id}/blob"
        };
    }

    internal static async Task<object> ReplyTo(string itemId, string sender, string body, string subject,
        string idempotencyKey, ExchangeService svc)
    {
        if (!TryActor(sender, out var from, out var actorError)) return actorError!;
        var result = await svc.ReplyAsync(itemId, from, N(subject), N(body), N(idempotencyKey));
        return result.Ok
            ? new { ok = true, id = result.Item!.Id, state = result.Item.State, threadId = result.Item.ThreadId,
                hopCount = result.Item.HopCount, deduplicated = result.Deduplicated }
            : Fail(result.Error!);
    }

    private static readonly JsonSerializerOptions AgentHqEventOptions = AgentHQ.Core.Events.AgentHqEvent.JsonOpts;
}
