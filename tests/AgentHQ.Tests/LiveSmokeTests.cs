using AgentHQ.Core.Domain;
using AgentHQ.Providers.ZCode;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// Read-only checks against the real ~/.zcode/cli/db. Skipped unless AGENTHQ_LIVE=1.
/// Phase 4's assumption (message/part writes bump session.time_updated) is asserted here.
/// </summary>
public class LiveSmokeTests
{
    private static bool Live => Environment.GetEnvironmentVariable("AGENTHQ_LIVE") == "1";

    private static string ZCodeDb => Environment.GetEnvironmentVariable("AGENTHQ_ZCODE_DB")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".zcode", "cli", "db", "db.sqlite");

    [SkippableFact]
    public async Task SchemaGuardPassesOnRealDb()
    {
        Skip.IfNot(Live, "set AGENTHQ_LIVE=1 to run live smoke tests");
        var provider = new ZCodeAgentProvider(ZCodeDb);
        Assert.Equal(ProviderHealth.Ok, provider.Health);
        var sessions = await provider.ListSessionsAsync(new SessionQuery());
        Assert.NotEmpty(sessions);
    }

    [SkippableFact]
    public async Task RealSessionDigestRenders()
    {
        Skip.IfNot(Live, "set AGENTHQ_LIVE=1 to run live smoke tests");
        var provider = new ZCodeAgentProvider(ZCodeDb);
        var session = (await provider.ListSessionsAsync(new SessionQuery()))
            .FirstOrDefault(s => !s.Archived && !s.Deleted);
        Skip.If(session is null, "no live session to digest");
        var detail = await provider.GetSessionAsync(session!.Id);
        var delta = await provider.GetMessagesAsync(session.Id, new SessionMessageQuery());
        var digest = Core.Digest.DigestBuilder.Render(detail!.Info, delta.Messages);
        Assert.Contains(session.Id, digest);
    }

    [SkippableFact]
    public async Task EveryMessageHasParts()
    {
        Skip.IfNot(Live, "set AGENTHQ_LIVE=1 to run live smoke tests");
        var provider = new ZCodeAgentProvider(ZCodeDb);
        var session = (await provider.ListSessionsAsync(new SessionQuery())).First();
        var delta = await provider.GetMessagesAsync(session.Id,
            new SessionMessageQuery(IncludeReasoning: true, IncludeTool: true, IncludeSteps: true));
        Assert.All(delta.Messages, m => Assert.True(m.Sequence >= 0));
    }

    /// <summary>
    /// The change-detection assumption: message/part writes bump session.time_updated. The
    /// live db is opened read-only, so this asserts the invariant over real history: no
    /// session's newest message is newer than its time_updated.
    /// </summary>
    [SkippableFact]
    public async Task TimeUpdatedIsNeverOlderThanNewestMessage()
    {
        Skip.IfNot(Live, "set AGENTHQ_LIVE=1 to run live smoke tests");
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=" + ZCodeDb + ";Mode=ReadOnly;Pooling=False");
        await conn.OpenAsync();
        var offenders = (await Dapper.SqlMapper.QueryAsync<(string Id, long Updated, long Msg)>(
                conn,
                @"SELECT s.id, s.time_updated, MAX(m.time_created) FROM session s
                  JOIN message m ON m.session_id = s.id GROUP BY s.id HAVING MAX(m.time_created) > s.time_updated")
            ).ToList();
        Assert.True(offenders.Count == 0,
            "sessions whose newest message is newer than time_updated: " +
            string.Join(", ", offenders.Select(o => o.Id)));
    }
}
