using System.Text.Json;
using Dapper;
using AgentHQ.Core.Domain;
using Microsoft.Data.Sqlite;

namespace AgentHQ.Providers.ZCode;

/// <summary>
/// Reads ZCode's internal session index (~/.zcode/cli/db/db.sqlite) read-only. The coupling
/// to ZCode's private schema is quarantined here behind a schema guard: at construction we
/// read the schema_migration marker; ahead/missing → degraded mode (empty results, health
/// flag), never throwing.
/// </summary>
public sealed class ZCodeAgentProvider : IAgentProvider
{
    /// <summary>Latest ZCode schema migration the provider has been tested against
    /// (marker prefix parsed from schema_migration.id, e.g. "0024_…" → 24).</summary>
    public const int TestedZCodeSchemaVersion = 24;

    private readonly string _connectionString;
    private readonly Serilog.ILogger _log;

    public string Id => "zcode";
    public ProviderHealth Health { get; private set; }
    public string? HealthDetail { get; private set; }

    /// <summary>
    /// Reads the db at <paramref name="dbPath"/>. There is deliberately no fallback to the
    /// user's real ~/.zcode db: a misconfigured or test host must never mirror live sessions.
    /// The host resolves the configured path (AGENTHQ_ZCODE_DB) and passes it explicitly.
    /// </summary>
    public ZCodeAgentProvider(string? dbPath, Serilog.ILogger? logger = null)
    {
        _log = (logger ?? Serilog.Log.ForContext<ZCodeAgentProvider>()).ForContext("Subsystem", "provider");
        var path = dbPath ?? throw new ArgumentNullException(nameof(dbPath),
            "ZCode db path must be explicit (AGENTHQ_ZCODE_DB or program configuration).");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        CheckSchema();
    }

    private void CheckSchema()
    {
        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var ids = conn.Query<string>("SELECT id FROM schema_migration").ToList();
            if (ids.Count == 0)
            {
                Degraded(ProviderHealth.Missing, "schema_migration table empty");
                return;
            }
            var version = ids.Select(ParseVersion).Max();
            if (version > TestedZCodeSchemaVersion)
            {
                Degraded(ProviderHealth.SchemaAhead, $"schema version {version} > tested {TestedZCodeSchemaVersion}");
                return;
            }
            Health = ProviderHealth.Ok;
            HealthDetail = $"schema version {version}";
        }
        catch (Exception ex)
        {
            Degraded(ProviderHealth.Missing, ex.Message);
        }
    }

    private void Degraded(ProviderHealth health, string detail)
    {
        Health = health;
        HealthDetail = detail;
        _log.Warning("ZCode provider degraded: {Health} — {Detail}", health, detail);
    }

    internal static int ParseVersion(string migrationId) =>
        int.TryParse(new string(migrationId.TakeWhile(char.IsDigit).ToArray()), out var v) ? v : 0;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static DateTimeOffset ToDto(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    public async Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync()
    {
        if (Health != ProviderHealth.Ok) return Array.Empty<RepositoryInfo>();
        await using var conn = Open();
        // A project_id maps to the root path most recently seen among its sessions.
        var rows = await conn.QueryAsync<(string projectId, string directory, long updated)>(
            @"SELECT project_id, directory, MAX(time_updated) FROM session
              WHERE time_deleted IS NULL GROUP BY project_id, directory");
        return rows
            .GroupBy(r => r.projectId)
            .Select(g => new RepositoryInfo(g.Key,
                g.OrderByDescending(x => x.updated).First().directory,
                ToDto(g.Max(x => x.updated))))
            .ToList();
    }

    private static SessionInfo MapSession(dynamic s) => new(
        (string)s.id, (string)s.directory, (string)s.title, (string)s.task_type,
        (string?)s.last_outcome, ToDto((long)s.time_created), ToDto((long)s.time_updated),
        s.time_archived is not null, s.time_deleted is not null);

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(SessionQuery q)
    {
        if (Health != ProviderHealth.Ok) return Array.Empty<SessionInfo>();
        await using var conn = Open();
        var sql = "SELECT id, project_id, directory, title, task_type, last_outcome, time_created, time_updated, time_archived, time_deleted FROM session WHERE 1=1";
        var parameters = new DynamicParameters();
        if (q.UpdatedSince is not null)
        {
            sql += " AND time_updated > @since";   // strict high-water: the boundary row was already seen
            parameters.Add("since", q.UpdatedSince.Value.ToUnixTimeMilliseconds());
        }
        sql += " ORDER BY time_updated DESC LIMIT @limit";
        parameters.Add("limit", q.Limit);
        var rows = await conn.QueryAsync(sql, parameters);
        var sessions = rows.Select(MapSession).ToList();
        if (q.RepositoryRootPath is not null)
        {
            var root = q.RepositoryRootPath.TrimEnd('/');
            sessions = sessions.Where(s => s.RootPath == root ||
                                           s.RootPath.TrimEnd('/').StartsWith(root + "/")).ToList();
        }
        if (q.Status is not null)
        {
            var now = DateTimeOffset.UtcNow;
            sessions = sessions.Where(s => s.Status(now) == q.Status).ToList();
        }
        return sessions;
    }

    public async Task<SessionDetail?> GetSessionAsync(string externalId)
    {
        if (Health != ProviderHealth.Ok) return null;
        await using var conn = Open();
        var s = await conn.QuerySingleOrDefaultAsync(
            "SELECT id, project_id, directory, title, task_type, last_outcome, time_created, time_updated, time_archived, time_deleted FROM session WHERE id = @id",
            new { id = externalId });
        if (s is null) return null;
        var firstUser = await conn.QueryFirstOrDefaultAsync<string>(
            @"SELECT substr(json_extract(p.data, '$.text'), 1, 200)
              FROM message m JOIN part p ON p.message_id = m.id
              WHERE m.session_id = @id AND json_extract(m.data, '$.role') = 'user'
                AND p.data LIKE '{""type"":""text""%'
              ORDER BY m.sequence LIMIT 1", new { id = externalId });
        return new SessionDetail(MapSession(s), firstUser);
    }

    public async Task<SessionDelta> GetMessagesAsync(string externalId, SessionMessageQuery q)
    {
        if (Health != ProviderHealth.Ok) return new SessionDelta(externalId, Array.Empty<SessionMessage>(), 0);
        await using var conn = Open();

        var messages = (await conn.QueryAsync<(string id, string data, long sequence)>(
            "SELECT id, data, sequence FROM message WHERE session_id = @id ORDER BY sequence",
            new { id = externalId })).ToList();

        var parts = (await conn.QueryAsync<(string messageId, string data, long sequence)>(
            "SELECT message_id, data, sequence FROM part WHERE session_id = @id ORDER BY sequence",
            new { id = externalId })).ToLookup(p => p.messageId);

        var result = new List<SessionMessage>();
        long maxSeq = 0;
        foreach (var m in messages)
        {
            var role = JsonDocument.Parse(m.data).RootElement.TryGetProperty("role", out var roleEl)
                ? roleEl.GetString() : null;
            var includedParts = new List<SessionPart>();
            foreach (var p in parts[m.id].OrderBy(p => p.sequence))
            {
                using var doc = JsonDocument.Parse(p.data);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString()!;
                var sequence = p.sequence;
                if (sequence > maxSeq) maxSeq = sequence;
                if (!IncludePart(type, q)) continue;
                includedParts.Add(MapPart(root, type, sequence));
            }
            if (includedParts.Count > 0 || q.AfterSequence is null)
                result.Add(new SessionMessage(m.id, role ?? "unknown", DateTimeOffset.UtcNow, m.sequence, includedParts));
        }

        if (q.AfterSequence is not null)
            result = result.Where(m => m.Sequence > q.AfterSequence
                || m.Parts.Any(p => p.Sequence > q.AfterSequence)).ToList();

        return new SessionDelta(externalId, result, maxSeq);
    }

    private static bool IncludePart(string type, SessionMessageQuery q) => type switch
    {
        "text" => true,
        "reasoning" => q.IncludeReasoning,
        "tool" => q.IncludeTool,
        "step-start" or "step-finish" => q.IncludeSteps,
        _ => false   // timeline, compaction, file: never
    };

    private static SessionPart MapPart(JsonElement root, string type, long sequence)
    {
        string? text = null, toolName = null, summary = null;
        if (type == "text" && root.TryGetProperty("text", out var textEl))
            text = textEl.GetString();
        if (type == "tool")
        {
            if (root.TryGetProperty("tool", out var toolEl)) toolName = toolEl.GetString();
            summary = ToolSummary(root);
        }
        return new SessionPart(type, text, toolName, summary, sequence);
    }

    /// <summary>One-line summary of a tool call: input for Edit/Write (file path), command
    /// for Bash; output is never summarized here (raw output is dropped).</summary>
    private static string? ToolSummary(JsonElement root)
    {
        if (!root.TryGetProperty("state", out var state)) return null;
        if (!state.TryGetProperty("input", out var input)) return null;
        if (input.ValueKind != JsonValueKind.Object) return null;
        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
            return fp.GetString();
        if (input.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.String)
            return cmd.GetString();
        return null;
    }

    public ValueTask SubscribeAsync(Func<ProviderEvent, Task> onEvent, CancellationToken ct) =>
        ValueTask.CompletedTask;   // change detection lives in the watcher, not the provider
}
