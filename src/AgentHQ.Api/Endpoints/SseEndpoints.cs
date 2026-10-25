using System.Text;
using AgentHQ.Api.Services;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Events;

namespace AgentHQ.Api.Endpoints;

public static class SseEndpoints
{
    /// <summary>
    /// One SSE stream: heartbeat every 15 s, retry hint, optional ?types= filter, replay from
    /// Last-Event-ID (or ?lastEventId=) before streaming live. The same rows are available
    /// as JSON via ?sinceSeq= for MCP-only consumers.
    /// </summary>
    public static WebApplication MapEvents(this WebApplication app)
    {
        app.MapGet("/events", (Func<HttpContext, EventStore, SseHub, string?, long?, long?, Task>)
            HandleEventsAsync);
        return app;
    }

    private static async Task HandleEventsAsync(HttpContext ctx, EventStore events, SseHub hub,
        string? types, long? sinceSeq, long? lastEventId)
    {
        var filter = types?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        // Poll form for MCP-only consumers: the same rows as JSON.
        if (sinceSeq is not null)
        {
            var rows = await events.ReadSinceAsync(sinceSeq.Value, filter?.ToList());
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await System.Text.Json.JsonSerializer.SerializeAsync(ctx.Response.Body,
                new { events = rows, lastSeq = await events.LastSeqAsync() }, AgentHqEvent.JsonOpts);
            return;
        }

        var fromSeq = lastEventId ?? ParseLastEventIdHeader(
            ctx.Request.Headers.TryGetValue("Last-Event-ID", out var header) ? header.ToString() : null);
        if (ctx.Request.Query.TryGetValue("lastEventId", out var q) && long.TryParse(q, out var qv))
            fromSeq = qv;

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        var body = ctx.Response.Body;
        async Task WriteAsync(string text)
        {
            await body.WriteAsync(Encoding.UTF8.GetBytes(text), ctx.RequestAborted);
            await body.FlushAsync(ctx.RequestAborted);
        }

        await WriteAsync("retry: 5000\n\n");

        // Subscribe before replaying: events appended between the replay query and the
        // subscription would otherwise be lost. Duplicates from the overlap are harmless.
        var replayedThrough = 0L;
        long subscribedAfter = await events.LastSeqAsync();
        using var live = hub.Subscribe(async evt =>
        {
            if (evt.Seq <= subscribedAfter && evt.Seq <= replayedThrough) return;
            if (filter is null || filter.Contains(evt.Type))
                await WriteAsync(SseHub.Format(evt));
        });

        foreach (var evt in await events.ReadSinceAsync(fromSeq, filter?.ToList()))
        {
            if (filter is null || filter.Contains(evt.Type))
                await WriteAsync(SseHub.Format(evt));
            if (evt.Seq > replayedThrough) replayedThrough = evt.Seq;
        }

        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await heartbeat.WaitForNextTickAsync(ctx.RequestAborted))
                await WriteAsync(": heartbeat\n\n");
        }
        catch (OperationCanceledException) { }
    }

    private static long ParseLastEventIdHeader(string? header) =>
        long.TryParse(header, out var v) ? v : 0;
}
