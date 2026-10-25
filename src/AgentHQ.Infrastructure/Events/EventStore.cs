using System.Text.Json;
using Dapper;
using AgentHQ.Core.Events;

namespace AgentHQ.Infrastructure.Events;

/// <summary>
/// The events table is the durable SSE log: append returns the seq; SSE replay reads rows
/// above Last-Event-ID; the poll form (sinceSeq) reads the same rows as JSON.
/// </summary>
public sealed class EventStore
{
    private readonly AgentHqDb _db;
    private readonly Serilog.ILogger _log;

    public EventStore(AgentHqDb db, Serilog.ILogger? logger = null)
    {
        _db = db;
        _log = (logger ?? Serilog.Log.ForContext<EventStore>()).ForContext("Subsystem", "events");
    }

    public event Func<AgentHqEvent, Task>? Published;

    public async Task<AgentHqEvent> AppendAsync(string type, object payload, CancellationToken ct = default)
    {
        await using var conn = _db.Open();
        var at = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(payload, AgentHqEvent.JsonOpts);
        var seq = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at); SELECT last_insert_rowid();",
            new { type, payload = json, at = at.ToUnixTimeMilliseconds() });
        var evt = new AgentHqEvent(seq, type, JsonSerializer.Deserialize<JsonElement>(json), at);
        _log.Information("event.append seq={Seq} type={Type}", seq, type);
        if (Published is not null)
            foreach (var handler in Published.GetInvocationList())
                await ((Func<AgentHqEvent, Task>)handler)(evt);
        return evt;
    }

    /// <summary>
    /// Rows above <paramref name="sinceSeq"/>. The default limit is high on purpose: a catch-up
    /// read (SSE replay, sinceSeq poll) must reach the current position in one call, or a client
    /// that reconnects with a small window would silently resume far behind live.
    /// </summary>
    public async Task<IReadOnlyList<AgentHqEvent>> ReadSinceAsync(long sinceSeq, IReadOnlyList<string>? types = null, int limit = 100_000)
    {
        await using var conn = _db.Open();
        var sql = "SELECT seq, type, payload, at FROM events WHERE seq > @sinceSeq";
        if (types is { Count: > 0 })
            sql += " AND type IN @types";
        sql += " ORDER BY seq LIMIT @limit";
        var rows = await conn.QueryAsync<(long seq, string type, string payload, long at)>(
            sql, new { sinceSeq, types, limit });
        return rows.Select(r => new AgentHqEvent(r.seq, r.type,
            JsonSerializer.Deserialize<JsonElement>(r.payload),
            DateTimeOffset.FromUnixTimeMilliseconds(r.at))).ToList();
    }

    public async Task<long> LastSeqAsync()
    {
        await using var conn = _db.Open();
        var seq = await conn.ExecuteScalarAsync<long?>("SELECT MAX(seq) FROM events");
        return seq ?? 0;
    }
}
