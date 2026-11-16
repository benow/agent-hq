using AgentHQ.Infrastructure.Events;
using Npgsql;
using AgentHQ.Infrastructure.Stores;
using System.Text.Json;
using Dapper;
using AgentHQ.Core.Events;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.PgStores;

/// <summary>
/// The events table on PostgreSQL. Same shapes as the SQLite EventStore; the dialect
/// differences are <c>RETURNING seq</c> for the insert (no <c>last_insert_rowid()</c>) and
/// BIGSERIAL for the sequence. Payloads serialize with <see cref="AgentHqEvent.JsonOpts"/>.
/// </summary>
public sealed class EventStorePg : IEventStore
{
    private readonly AgentHqPgDb _db;
    private readonly Serilog.ILogger _log;

    public EventStorePg(AgentHqPgDb db, Serilog.ILogger? logger = null)
    {
        _db = db;
        _log = (logger ?? Serilog.Log.ForContext<EventStorePg>()).ForContext("Subsystem", "events");
    }

    public event Func<AgentHqEvent, Task>? Published;

    public async Task<AgentHqEvent> AppendAsync(string type, object payload, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(payload, AgentHqEvent.JsonOpts);
        await using var conn = await _db.OpenAsync();
        var seq = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at) RETURNING seq",
            new { type, payload = json, at = at.ToUnixTimeMilliseconds() });
        return await PublishedAfterCommitAsync(seq, type, json, at);
    }

    public async Task<AgentHqEvent> AppendAsync(string type, object payload, System.Data.Common.DbTransaction tx,
        CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(payload, AgentHqEvent.JsonOpts);
        var seq = await tx.Connection!.ExecuteScalarAsync<long>(
            "INSERT INTO events (type, payload, at) VALUES (@type, @payload, @at) RETURNING seq",
            new { type, payload = json, at = at.ToUnixTimeMilliseconds() }, tx);
        return new AgentHqEvent(seq, type, JsonSerializer.Deserialize<JsonElement>(json), at);
    }

    public async Task<AgentHqEvent> PublishedAfterCommitAsync(AgentHqEvent evt)
    {
        _log.Information("event.append seq={Seq} type={Type}", evt.Seq, evt.Type);
        if (Published is not null)
            foreach (var handler in Published.GetInvocationList())
                await ((Func<AgentHqEvent, Task>)handler)(evt);
        return evt;
    }

    private Task<AgentHqEvent> PublishedAfterCommitAsync(long seq, string type, string json, DateTimeOffset at) =>
        PublishedAfterCommitAsync(new AgentHqEvent(seq, type, JsonSerializer.Deserialize<JsonElement>(json), at));

    public async Task<IReadOnlyList<AgentHqEvent>> ReadSinceAsync(long sinceSeq, IReadOnlyList<string>? types = null,
        int limit = 100_000)
    {
        await using var conn = await _db.OpenAsync();
        var sql = "SELECT seq, type, payload, at FROM events WHERE seq > @sinceSeq";
        // Dapper's automatic list expansion (IN @types) emits positional syntax Npgsql rejects,
        // so the type filter expands explicitly here.
        var p = new DynamicParameters(new { sinceSeq, limit });
        if (types is { Count: > 0 })
        {
            var names = new List<string>(types.Count);
            for (var i = 0; i < types.Count; i++)
            {
                var name = "type" + i;
                names.Add("@" + name);
                p.Add(name, types[i]);
            }
            sql += " AND type IN (" + string.Join(", ", names) + ")";
        }
        sql += " ORDER BY seq LIMIT @limit";
        var rows = await conn.QueryAsync<(long seq, string type, string payload, long at)>(sql, p);
        return rows.Select(r => new AgentHqEvent(r.seq, r.type,
            JsonSerializer.Deserialize<JsonElement>(r.payload),
            DateTimeOffset.FromUnixTimeMilliseconds(r.at))).ToList();
    }

    public async Task<long> LastSeqAsync()
    {
        await using var conn = await _db.OpenAsync();
        var seq = await conn.ExecuteScalarAsync<long?>("SELECT MAX(seq) FROM events");
        return seq ?? 0;
    }

    public async Task<long?> OldestSeqSinceAsync(long atMs)
    {
        await using var conn = await _db.OpenAsync();
        return await conn.ExecuteScalarAsync<long?>(
            "SELECT MIN(seq) FROM events WHERE at >= @atMs", new { atMs });
    }
}
