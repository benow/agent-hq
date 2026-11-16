using AgentHQ.Core.Events;

namespace AgentHQ.Infrastructure.Events;

/// <summary>The durable event log, implemented for SQLite (spoke) and PostgreSQL (hub).</summary>
public interface IEventStore
{
    /// <summary>Fired after a directly-appended event commits; subscribers are awaited sequentially.</summary>
    event Func<AgentHqEvent, Task>? Published;

    Task<AgentHqEvent> AppendAsync(string type, object payload, CancellationToken ct = default);

    /// <summary>Appends inside the caller's transaction; announce post-commit via
    /// <see cref="PublishedAfterCommitAsync"/>.</summary>
    Task<AgentHqEvent> AppendAsync(string type, object payload, System.Data.Common.DbTransaction tx,
        CancellationToken ct = default);

    /// <summary>Announces an already-appended event to live subscribers (post-commit).</summary>
    Task<AgentHqEvent> PublishedAfterCommitAsync(AgentHqEvent evt);

    Task<IReadOnlyList<AgentHqEvent>> ReadSinceAsync(long sinceSeq, IReadOnlyList<string>? types = null,
        int limit = 100_000);

    Task<long> LastSeqAsync();

    /// <summary>The oldest event seq at or after <paramref name="atMs"/>, or null.</summary>
    Task<long?> OldestSeqSinceAsync(long atMs);
}
