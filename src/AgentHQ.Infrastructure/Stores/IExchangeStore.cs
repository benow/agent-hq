using System.Data.Common;
using AgentHQ.Models;

namespace AgentHQ.Infrastructure.Stores;

/// <summary>Exchange rows (items, presence, cursors, rate ledger), implemented for SQLite and
/// PostgreSQL. State moves only through UPDATEs guarded by the state they move from.</summary>
public interface IExchangeStore
{
    Task<ExchangeItemRow> InsertAsync(ExchangeItemRow row, DbTransaction? tx = null);
    Task<ExchangeItemRow?> GetAsync(string id, DbTransaction? tx = null);
    Task<ExchangeItemRow?> FindByIdempotencyAsync(ExchangeActorKey sender, string key, DbTransaction tx);
    Task<IReadOnlyList<ExchangeItemRow>> ThreadAsync(string threadId);
    Task<IReadOnlyList<ExchangeItemRow>> InboxAsync(ExchangeActorKey actor, IReadOnlyList<string>? repositoryIds,
        IReadOnlyList<string>? states, string? kinds, int limit);
    Task<IReadOnlyList<ExchangeItemRow>> DigestItemsAsync(string sessionId, string repositoryId, int limit = 200);
    Task<IReadOnlyList<ExchangeItemRow>> OpenQuestionsAsync(string? repositoryId);
    Task<IReadOnlyList<ExchangeItemRow>> DueForExpiryAsync(long now, int limit = 500);
    Task<bool> TransitionAsync(string id, string fromState, string toState, string? claimedBy, long at,
        bool claim = false, bool answered = false, bool acknowledged = false, bool killed = false,
        DbTransaction? tx = null);
    Task SetBlobAsync(string id, string path, long size, string contentType, string sha256);
    Task UpsertPresenceAsync(ExchangeActorKey actor, string latencyClass, long now);
    Task<IReadOnlyList<ExchangePresenceRow>> PresenceAsync();
    Task<ExchangeCursorRow?> GetCursorAsync(ExchangeActorKey actor);
    Task SetCursorAsync(ExchangeActorKey actor, long seq, long now);
    Task ClearCursorAsync(ExchangeActorKey actor);
    Task<int> CountRecentPostsAsync(ExchangeActorKey sender, long since, DbTransaction tx);
    Task RecordPostAsync(ExchangeActorKey sender, long at, long pruneBefore, DbTransaction tx);
    Task<long?> OldestRecentPostAsync(ExchangeActorKey sender, long since, DbTransaction tx);
}
