using AgentHQ.Client;
using AgentHQ.Infrastructure.Events;
using Serilog;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// The CI ingest endpoint's service (part 3): turns a `notify-agent-hq` payload into a
/// hub-origin event with actor <c>ci:&lt;workflow&gt;/&lt;run&gt;</c>. The payload shape is
/// versioned here so the contract has one home; the build spoke (part 5) serves the same
/// ingest on its own loopback for co-located runners. Event-only for now — the health/run
/// views that read these events are part 5's surfaces.
/// </summary>
public sealed class CiIngestService
{
    public const string EventTypeCiRun = "ci.run";

    private readonly IEventStore _events;
    private readonly ILogger _logger;

    public CiIngestService(IEventStore events, ILogger? logger = null)
    {
        _events = events;
        _logger = logger ?? Log.ForContext<CiIngestService>();
    }

    public async Task<long> IngestAsync(CiEvent ciEvent)
    {
        var evt = await _events.AppendAsync(EventTypeCiRun, new
        {
            workflow = ciEvent.workflow,
            run = ciEvent.run,
            repo = ciEvent.repo,
            conclusion = ciEvent.conclusion,
            url = ciEvent.url,
            timestamp = ciEvent.timestamp,
            actor = $"ci:{ciEvent.workflow}/{ciEvent.run}"
        });
        _logger.Information("ci: {Workflow} #{Run} {Conclusion} on {Repo} → seq {Seq}",
            ciEvent.workflow, ciEvent.run, ciEvent.conclusion, ciEvent.repo, evt.Seq);
        return evt.Seq;
    }
}
