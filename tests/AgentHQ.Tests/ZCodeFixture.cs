using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// Builds a real-shaped ZCode db from the checked-in schema snapshot, seeded with
/// sessions/messages/parts (shapes verified against the live db 2026-10-24:
/// message role lives in message.data JSON; part rows are typed JSON blobs;
/// times are unix ms).
/// </summary>
public sealed class ZCodeFixture : IAsyncLifetime
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(),
        "agenthq-test-zcode-" + Guid.NewGuid().ToString("N") + ".sqlite");

    public string RootA { get; set; } = "/tmp/agenthq-test-repo-a";
    public string RootB { get; set; } = "/tmp/agenthq-test-repo-b";

    private SqliteConnection OpenWrite()
    {
        var conn = new SqliteConnection("Data Source=" + DbPath);
        conn.Open();
        return conn;
    }

    public async Task InitializeAsync()
    {
        var ddl = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "zcode_schema.sql"));
        // sqlite_sequence is internal to SQLite; it appears in a .schema dump but cannot be created.
        ddl = System.Text.RegularExpressions.Regex.Replace(ddl,
            @"CREATE TABLE sqlite_sequence\(name, ?seq\);", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        await using (var conn = OpenWrite())
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync();
        }
        await InsertMigrationAsync("0024_session_database_identity");
        await InsertSessionAsync("sess1", "proj-a", RootA, "Fix player bug", updated: 1_789_000_000_000);
        await InsertSessionAsync("sess2", "proj-a", RootA, "Nightly session", updated: 1_788_000_000_000, taskType: "scheduled");
        await InsertSessionAsync("sess3", "proj-b", RootB, "Other repo work", updated: 1_789_500_000_000);
        await InsertConversationAsync("sess1");
    }

    public async Task InsertMigrationAsync(string id)
    {
        await using var conn = OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO schema_migration (id, checksum, app_version, time_applied) " +
                          $"VALUES ('{id}', 'x', 'test', 0)";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertSessionAsync(string id, string projectId, string directory, string title,
        long updated, string taskType = "interactive", bool archived = false, bool deleted = false,
        string? outcome = null)
    {
        await using var conn = OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session (id, project_id, directory, slug, title, version, task_type,
                time_created, time_updated, time_archived, time_deleted, last_outcome)
            VALUES (@id, @proj, @dir, @slug, @title, 'v', @taskType,
                @created, @updated, @archived, @deleted, @outcome)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@proj", projectId);
        cmd.Parameters.AddWithValue("@dir", directory);
        cmd.Parameters.AddWithValue("@slug", id);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@taskType", taskType);
        cmd.Parameters.AddWithValue("@created", updated - 100_000);
        cmd.Parameters.AddWithValue("@updated", updated);
        cmd.Parameters.AddWithValue("@archived", archived ? updated : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@deleted", deleted ? updated : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetSessionTitleAsync(string id, string title)
    {
        await using var conn = OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session SET title = @t WHERE id = @id";
        cmd.Parameters.AddWithValue("@t", title);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task TouchSessionAsync(string id, long updated)
    {
        await using var conn = OpenWrite();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session SET time_updated = @u WHERE id = @id";
        cmd.Parameters.AddWithValue("@u", updated);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InsertConversationAsync(string sessionId)
    {
        await InsertMessageAsync(sessionId, 0, "user", new[]
        {
            ("text", JsonSerializer.Serialize(new { type = "text", text = "Please fix the player bug in the app." }), (string?)null)
        }, 0);
        await InsertMessageAsync(sessionId, 1, "assistant", new[]
        {
            ("reasoning", "{\"type\":\"reasoning\",\"text\":\"thinking...\"}", (string?)null),
            ("tool", JsonSerializer.Serialize(new
            {
                type = "tool",
                tool = "Read",
                state = new { status = "completed", input = new { file_path = "/tmp/agenthq-test-repo-a/src/app.ts" } }
            }), null),
            ("text", JsonSerializer.Serialize(new { type = "text", text = "I found the bug; it is a null ref in player.ts. Fixing now." }), null)
        }, 3);
        await InsertMessageAsync(sessionId, 2, "assistant", new[]
        {
            ("tool", JsonSerializer.Serialize(new
            {
                type = "tool",
                tool = "Bash",
                state = new { status = "completed", input = new { command = "git commit -m 'fix player'" } }
            }), null),
            ("step-start", "{\"type\":\"step-start\"}", (string?)null)
        }, 6);
    }

    public async Task InsertMessageAsync(string sessionId, long msgSeq, string role,
        (string Type, string Data, string? _)[] parts, long partSeq)
    {
        var msgId = $"msg_{sessionId}_{msgSeq}";
        await using var conn = OpenWrite();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO message (id, session_id, time_created, time_updated, data, sequence)
                VALUES (@id, @sid, @t, @t, @data, @seq)
                """;
            cmd.Parameters.AddWithValue("@id", msgId);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@t", 1_789_000_100_000L + msgSeq);
            cmd.Parameters.AddWithValue("@data", JsonSerializer.Serialize(new { role }));
            cmd.Parameters.AddWithValue("@seq", msgSeq);
            await cmd.ExecuteNonQueryAsync();
        }
        foreach (var (type, data, _) in parts)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO part (id, message_id, session_id, time_created, time_updated, data, sequence)
                VALUES (@id, @mid, @sid, @t, @t, @data, @seq)
                """;
            cmd.Parameters.AddWithValue("@id", $"part_{sessionId}_{partSeq}");
            cmd.Parameters.AddWithValue("@mid", msgId);
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@t", 1_789_000_100_000L + partSeq);
            cmd.Parameters.AddWithValue("@data", data);
            cmd.Parameters.AddWithValue("@seq", partSeq);
            await cmd.ExecuteNonQueryAsync();
            partSeq++;
        }
        msgSeq++;
    }

    public Task<Providers.ZCode.ZCodeAgentProvider> CreateProviderAsync() =>
        Task.FromResult(new Providers.ZCode.ZCodeAgentProvider(DbPath));

    public Task DisposeAsync()
    {
        try { File.Delete(DbPath); } catch { /* temp cleanup best-effort */ }
        return Task.CompletedTask;
    }
}
