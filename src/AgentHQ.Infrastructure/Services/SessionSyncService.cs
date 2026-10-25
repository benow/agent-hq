using AgentHQ.Core.Domain;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// Keeps the sessions mirror in step with the provider. Startup: full sync. On watcher
/// change: diff session rows above the last external time_updated high-water mark and emit
/// session.created / session.updated events.
/// </summary>
public sealed class SessionSyncService
{
    private readonly IAgentProvider _provider;
    private readonly SessionStore _sessions;
    private readonly RepositoryStore _repositories;
    private readonly EventStore _events;
    private readonly Serilog.ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SessionSyncService(IAgentProvider provider, SessionStore sessions,
        RepositoryStore repositories, EventStore events, Serilog.ILogger? logger = null)
    {
        _provider = provider;
        _sessions = sessions;
        _repositories = repositories;
        _events = events;
        _log = (logger ?? Serilog.Log.ForContext<SessionSyncService>()).ForContext("Subsystem", "sessions");
    }

    public async Task FullSyncAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var registered = await _repositories.ListAsync();
            var knownRepoRoots = registered.Select(r => r.RootPath).ToHashSet(StringComparer.Ordinal);
            var all = await _provider.ListSessionsAsync(new SessionQuery());
            foreach (var s in all)
            {
                // Auto-discovery: a session rooted at a path we have never seen registers the repository.
                if (knownRepoRoots.Add(s.RootPath))
                    await _repositories.RegisterAsync(s.RootPath, _provider.Id, externalId: null);
                await MirrorAsync(s);
            }
            _log.Information("session full sync: {Count} sessions", all.Count);
        }
        finally { _gate.Release(); }
    }

    public Task SyncOnDbChangeAsync() => DiffAsync();

    public async Task DiffAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // Strict high-water on mirrored time_updated: the boundary row was already seen.
            var highWater = await _sessions.MaxExternalUpdatedAsync();
            var changed = await _provider.ListSessionsAsync(new SessionQuery { UpdatedSince = highWater });
            foreach (var s in changed)
                await MirrorAsync(s);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Mirrors one provider session. ZCode bumps session.time_updated on every internal step, so
    /// most bumps carry nothing an observer can act on: an update that changes no metadata emits
    /// no event, which keeps the event log (and anything built on it) signal-shaped.
    /// </summary>
    public async Task MirrorAsync(SessionInfo s)
    {
        var repo = await _repositories.ResolveByCwdAsync(s.RootPath)
                   ?? await _repositories.RegisterAsync(s.RootPath, _provider.Id, externalId: null);
        var detail = await _provider.GetSessionAsync(s.Id);
        var before = await _sessions.GetAsync(s.Id);
        await _sessions.UpsertAsync(s, repo.Id, detail?.FirstUserMessageExcerpt);

        if (before is not null && !Changed(before, s, repo.Id))
            return;

        var evtType = before is null ? EventTypes.SessionCreated : EventTypes.SessionUpdated;
        await _events.AppendAsync(evtType, new
        {
            sessionId = s.Id,
            repositoryId = repo.Id,
            title = s.Title,
            taskType = s.TaskType,
            lastOutcome = s.LastOutcome,
            timeUpdated = s.TimeUpdated,
            status = s.Status(DateTimeOffset.UtcNow)
        });
    }

    /// <summary>The metadata an event consumer can act on. time_updated alone is not one.</summary>
    private static bool Changed(Stores.SessionRow before, SessionInfo s, string repositoryId) =>
        before.Title != s.Title ||
        before.TaskType != s.TaskType ||
        before.LastOutcome != s.LastOutcome ||
        before.ArchivedBool != s.Archived ||
        before.DeletedBool != s.Deleted ||
        before.RepositoryId != repositoryId;
}
