using System.Text.Json;
using AgentHQ.Core.Events;
using AgentHQ.Core.Exchange;
using AgentHQ.Api.Services;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Exchange;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Api.Endpoints;

/// <summary>
/// The Exchange's REST surface. Every route is a thin call into <see cref="ExchangeService"/> —
/// no SQL and no state moves here. Errors share one shape everywhere:
/// <c>{"ok":false,"error":"&lt;code&gt;","detail":"&lt;sentence&gt;"}</c>.
/// </summary>
public static class ExchangeEndpoints
{
    public static WebApplication MapExchange(this WebApplication app)
    {
        app.MapPost("/exchange/items", async (PostItemRequest req, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(req.Sender, out var sender, out var senderError))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, senderError!, 422);
            ExchangeActor? recipient = null;
            var result = await svc.PostAsync(new PostRequest(
                sender, (req.Kind ?? "").Trim().ToLowerInvariant(), recipient, req.Recipient,
                req.Repository, req.Subject, req.Purpose, req.Body, req.ContextRefs,
                req.ExpiresAt, req.IdempotencyKey, req.LatencyClass));
            if (!result.Ok) return Error(result.Error!);
            var item = result.Item!;
            return Results.Json(new
            {
                id = item.Id, state = item.State, deduplicated = result.Deduplicated, threadId = item.ThreadId,
                kind = item.Kind, sender = item.Sender.ToDisplayString(),
                recipient = item.Recipient.ToDisplayString(), createdAt = item.CreatedAt, subject = item.Subject
            }, statusCode: result.Deduplicated ? 200 : 201);
        });

        app.MapGet("/exchange/items/{id}", async (string id, ExchangeService svc) =>
        {
            var item = await svc.GetAsync(id);
            return item is null ? Error(new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), 404)
                : Results.Json(ItemJson(item));
        });

        app.MapGet("/exchange/inbox", async (string? actor, string? repo, string? state, int? limit,
            ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(actor, out var reader, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            var items = await svc.InboxAsync(reader, repo, state, null, limit ?? 200);
            return Results.Json(items.Select(ItemJson).ToList());
        });

        app.MapPost("/exchange/items/{id}/claim", async (string id, ClaimRequest req, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(req.Claimant, out var claimant, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            var result = await svc.ClaimAsync(id, claimant);
            if (result.Ok) return Results.Json(new { state = result.Item!.State, claimedBy = result.Item.ClaimedBySessionId });
            return result.Error!.Code == ExchangeStateMachine.Codes.AlreadyClaimed
                ? Results.Json(new { ok = false, error = result.Error.Code, detail = result.Error.Detail,
                    claimedBy = result.Item?.ClaimedBySessionId }, statusCode: 409)
                : Error(result.Error);
        });

        app.MapPost("/exchange/items/{id}/reply", async (string id, ReplyRequest req, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(req.Sender, out var sender, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            var result = await svc.ReplyAsync(id, sender, req.Subject, req.Body, req.IdempotencyKey);
            if (!result.Ok) return Error(result.Error!);
            var item = result.Item!;
            return Results.Json(new
            {
                id = item.Id, state = item.State, deduplicated = result.Deduplicated, threadId = item.ThreadId,
                hopCount = item.HopCount, kind = item.Kind
            }, statusCode: result.Deduplicated ? 200 : 201);
        });

        app.MapPost("/exchange/items/{id}/ack", async (string id, AckRequest req, ExchangeService svc) =>
        {
            var by = string.IsNullOrWhiteSpace(req.AckSender) ? ExchangeActor.Andy
                : ExchangeActor.TryParse(req.AckSender, out var actor, out _) ? actor : ExchangeActor.Andy;
            var result = await svc.AckAsync(id, by);
            return result.Ok
                ? Results.Json(new { state = result.Item!.State, id = result.Item.Id })
                : Error(result.Error!);
        });

        app.MapPost("/exchange/items/{id}/kill", async (string id, KillRequest req, ExchangeService svc) =>
        {
            var byText = string.IsNullOrWhiteSpace(req.By) ? "andy:andy" : req.By;
            if (!ExchangeActor.TryParse(byText, out var by, out _))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, $"unknown actor '{req.By}'", 403);
            if (!by.IsAndy)
                return Fail(ExchangeStateMachine.Codes.ScopeDenied, "only andy may kill an item", 403);
            var result = await svc.KillAsync(id, by, req.Reason);
            return result.Ok ? Results.Json(new { state = result.Item!.State }) : Error(result.Error!);
        });

        app.MapGet("/exchange/open", async (string? repo, ExchangeService svc) =>
        {
            var open = await svc.OpenAsync(repo);
            return Results.Json(open.Select(o => new
            {
                id = o.Item.Id, threadId = o.Item.ThreadId,
                sender = o.Item.Sender.ToDisplayString(),
                recipient = o.Item.Recipient.ToDisplayString(),
                repository = o.Item.RepositoryId,
                ageSeconds = o.AgeSeconds, hops = o.HopCount, purpose = o.Item.Purpose,
                state = o.Item.State, claimedBy = o.ClaimedBy, subject = o.Item.Subject,
                createdAt = o.Item.CreatedAt
            }));
        });

        // Chunked deposit: raw bytes at ?offset=, with ?total= and the sender's ?sha256= (the
        // verification is end-to-end: the server hashes what it received and compares).
        app.MapPost("/exchange/items/{id}/blob", async (string id, long? offset, long? total, string? sha256,
            string? contentType, HttpContext ctx, ExchangeService svc) =>
        {
            var item = await svc.GetAsync(id);
            if (item is null) return Error(new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), 404);
            if (item.Kind != ExchangeKinds.File)
                return Fail(ExchangeStateMachine.Codes.IllegalTransition, $"item {id} is a {item.Kind}; only file items carry a blob", 422);
            using var buffer = new MemoryStream();
            var copy = new byte[81920];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(copy, ctx.RequestAborted)) > 0)
            {
                buffer.Write(copy, 0, read);
                if (buffer.Length > svc.Options.BlobMaxBytes)
                    return Fail(ExchangeStateMachine.Codes.BlobTooLarge,
                        $"the chunk exceeds the {svc.Options.BlobMaxBytes}-byte blob limit", 413);
            }
            var bytes = buffer.ToArray();
            var declaredTotal = total ?? bytes.Length;
            var chunk = await svc.Blobs.AppendChunkAsync(id, offset ?? 0, declaredTotal, bytes, sha256);
            if (!chunk.Ok)
                return Fail(chunk.Code!, chunk.Detail!, chunk.Code == ExchangeStateMachine.Codes.BlobTooLarge || chunk.Total > 1 << 20 ? 413 : 422);
            string? storedType = null;
            if (chunk.Complete)
            {
                var head = bytes.Take(4096).ToArray();
                var (type, mismatch) = BlobStore.ResolveContentType(contentType, head);
                storedType = type;
                if (mismatch is { } m)
                    svc.BlobsLogWarning(id, m.Declared, m.Sniffed);
                await svc.SaveBlobAsync(id, chunk.Sha256!, declaredTotal, type);
            }
            return Results.Json(new { received = chunk.Received, complete = chunk.Complete, sha256 = chunk.Sha256,
                total = chunk.Total, contentType = storedType });
        });

        app.MapGet("/exchange/items/{id}/blob", async (string id, ExchangeService svc) =>
        {
            var item = await svc.GetAsync(id);
            if (item is null) return Error(new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"no item {id}"), 404);
            if (string.IsNullOrEmpty(item.BlobPath) || item.BlobSize is null)
                return Error(new ExchangeError(ExchangeStateMachine.Codes.NotFound, $"item {id} has no blob"), 404);
            var (bytes, code, detail) = await svc.Blobs.ReadAsync(id, item.BlobSize.Value, item.BlobSha256);
            if (bytes is null) return Error(new ExchangeError(code!, detail!));
            return Results.File(bytes, item.BlobContentType ?? "application/octet-stream",
                fileDownloadName: null);
        });

        // Watch: SSE when given nothing, JSON catch-up with ?sinceSeq= or ?cursor=.
        app.MapGet("/exchange/watch", (Func<HttpContext, string?, long?, long?, ExchangeService, EventStore, SseHub, Task>)
            HandleWatchAsync);


        app.MapPost("/exchange/presence", async (PresenceRequest req, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(req.Actor, out var actor, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            if (!string.IsNullOrWhiteSpace(req.SessionId) && actor.ActorType == ExchangeActorTypes.ZCode &&
                actor.ActorId == ExchangeActorTypes.RoleWildcard)
                actor = ExchangeActor.ZCode(req.SessionId.Trim());
            var latency = string.IsNullOrWhiteSpace(req.LatencyClass) ? null : req.LatencyClass;
            await svc.HeartbeatAsync(actor, latency ?? ExchangeLatency.Human);
            return Results.NoContent();
        });

        app.MapGet("/exchange/presence", async (ExchangeService svc) =>
            Results.Json((await svc.PresenceAsync()).Select(p => new
            {
                actor = p.Actor, latencyClass = p.LatencyClass, lastSeenAt = p.LastSeenAt,
                secondsSinceSeen = p.SecondsSinceSeen, online = p.Online
            })));

        app.MapPost("/exchange/cursor", async (CursorRequest req, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(req.Actor, out var actor, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            var (set, cleared) = await svc.SetCursorAsync(actor, req.LastSeq);
            return Results.Json(new { cursor = set, cleared });
        });

        app.MapGet("/exchange/cursor", async (string? actor, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(actor, out var reader, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            var cursor = await svc.GetCursorAsync(reader);
            return Results.Json(new
            {
                actor = reader.ToDisplayString(), lastSeq = cursor?.LastSeq ?? 0,
                updatedAt = cursor is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeMilliseconds(cursor.UpdatedAt)
            });
        });

        app.MapGet("/exchange/digest", async (string? actor, string? cwd, ExchangeService svc) =>
        {
            if (!ExchangeActor.TryParse(actor, out var reader, out var error))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, error!, 422);
            if (reader.ActorType != ExchangeActorTypes.ZCode || reader.IsZCodeRole)
                return Fail(ExchangeStateMachine.Codes.ScopeDenied, "a digest belongs to one ZCode session (zcode:<session id>)", 422);
            if (string.IsNullOrWhiteSpace(cwd))
                return Fail(ExchangeStateMachine.Codes.UnknownActor, "cwd is required to resolve the session's repository", 422);
            var text = await svc.DigestAsync(reader.ActorId, cwd);
            return Results.Json(new { digest = text, inject = text.Length > 0 });
        });

        return app;
    }

    private static async Task HandleWatchAsync(HttpContext ctx, string? actor, long? sinceSeq, long? cursor,
        ExchangeService svc, EventStore events, SseHub hub)
    {
        var reader = actor;
        long from = sinceSeq ?? 0;
        if (cursor is not null)
        {
            if (reader is null)
            {
                await WriteErrorAsync(ctx, 422, ExchangeStateMachine.Codes.UnknownActor,
                    "cursor= needs actor= so the server cursor can be found");
                return;
            }
            if (!ExchangeActor.TryParse(reader, out var a, out var error))
            {
                await WriteErrorAsync(ctx, 422, ExchangeStateMachine.Codes.UnknownActor, error!);
                return;
            }
            var stored = await svc.GetCursorAsync(a);
            from = stored?.LastSeq ?? 0;
        }
        from = Math.Min(from, await events.LastSeqAsync());

        if (sinceSeq is not null || cursor is not null)
        {
            var resync = await svc.PrepareCatchUpAsync(from);
            var rows = await events.ReadSinceAsync(resync.FromSeq, EventTypes.ExchangeTypes);
            if (reader is not null && ExchangeActor.TryParse(reader, out var a2, out _))
                await svc.SetCursorAsync(a2, rows.Count > 0 ? rows[^1].Seq : resync.FromSeq);
            ctx.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, new
            {
                resync = resync.IsResync, fromSeq = resync.FromSeq, lastSeq = await events.LastSeqAsync(),
                events = rows
            }, AgentHqEvent.JsonOpts);
            return;
        }

        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        var body = ctx.Response.Body;
        async Task Write(string text)
        {
            await body.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), ctx.RequestAborted);
            await body.FlushAsync(ctx.RequestAborted);
        }
        await Write("retry: 5000\n\n");
        var exchangeTypes = EventTypes.ExchangeTypes.ToHashSet(StringComparer.Ordinal);
        var fromHeader = long.TryParse(ctx.Request.Headers["Last-Event-ID"].ToString(), out var h) ? h : 0L;
        var replayedThrough = 0L;
        long subscribedAfter = await events.LastSeqAsync();
        using var live = hub.Subscribe(async evt =>
        {
            if (!exchangeTypes.Contains(evt.Type)) return;
            if (evt.Seq <= subscribedAfter && evt.Seq <= replayedThrough) return;
            await Write(SseHub.Format(evt));
        });
        foreach (var evt in await events.ReadSinceAsync(fromHeader, EventTypes.ExchangeTypes))
        {
            await Write(SseHub.Format(evt));
            if (evt.Seq > replayedThrough) replayedThrough = evt.Seq;
        }
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await heartbeat.WaitForNextTickAsync(ctx.RequestAborted))
                await Write(": heartbeat\n\n");
        }
        catch (OperationCanceledException)
        {
            // The client went away; disposing the subscription ends this request.
        }
    }

    private static IResult Error(ExchangeError e, int? status = null) =>
        Results.Json(new { ok = false, error = e.Code, detail = e.Detail }, statusCode: status ?? StatusFor(e.Code));

    private static IResult Fail(string code, string detail, int? status = null) =>
        Results.Json(new { ok = false, error = code, detail }, statusCode: status ?? StatusFor(code));

    private static int StatusFor(string code) => code switch
    {
        ExchangeStateMachine.Codes.NotFound => 404,
        ExchangeStateMachine.Codes.ScopeDenied => 403,
        ExchangeStateMachine.Codes.AlreadyClaimed => 409,
        ExchangeStateMachine.Codes.HashMismatch => 409,
        ExchangeStateMachine.Codes.BodyTooLarge => 413,
        ExchangeStateMachine.Codes.BlobTooLarge => 413,
        ExchangeStateMachine.Codes.ThreadKilled => 409,
        _ => 422
    };

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string code, string detail)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { ok = false, error = code, detail }, AgentHqEvent.JsonOpts);
    }

    /// <summary>The item as the browser and REST clients read it (bodies included — this is the
    /// deliberate fetch, the opposite of the digest).</summary>
    private static object ItemJson(ExchangeItem i) => new
    {
        id = i.Id, kind = i.Kind, state = i.State, latencyClass = i.LatencyClass,
        threadId = i.ThreadId, hopCount = i.HopCount,
        sender = i.Sender.ToDisplayString(), recipient = i.Recipient.ToDisplayString(),
        repositoryId = i.RepositoryId, subject = i.Subject, purpose = i.Purpose, body = i.Body,
        contextRefs = i.ContextRefs, claimedBy = i.ClaimedBySessionId,
        createdAt = i.CreatedAt, claimedAt = i.ClaimedAt, expiresAt = i.ExpiresAt,
        answeredAt = i.AnsweredAt, acknowledgedAt = i.AcknowledgedAt, killedAt = i.KilledAt,
        blob = i.BlobPath is null ? null : new
        {
            size = i.BlobSize, contentType = i.BlobContentType, sha256 = i.BlobSha256,
            url = $"/exchange/items/{i.Id}/blob"
        }
    };

    public record PostItemRequest(string? Kind, string? Sender, string? Recipient, string? Repository,
        string? Subject, string? Purpose, string? Body, string? ContextRefs, DateTimeOffset? ExpiresAt,
        string? IdempotencyKey, string? LatencyClass);
    public record ClaimRequest(string? Claimant);
    public record ReplyRequest(string? Sender, string? Body, string? Subject, string? IdempotencyKey);
    public record AckRequest(string? AckSender);
    public record KillRequest(string? By, string? Reason);
    public record PresenceRequest(string? Actor, string? LatencyClass, string? SessionId);
    public record CursorRequest(string? Actor, long LastSeq);
}
