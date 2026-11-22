using System.Text.Json;
using AgentHQ.Client;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;
using Serilog;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// The hub-side command queue (part 3, distributed-agent-hq rules 4 and 7). Hub surfaces
/// enqueue here (the web UI / MCP mode switch is part 4 — until then the E2E actor and tests
/// call this service directly); spokes claim leased envelopes and report results. Routing is
/// by repository ownership first (a plan.command goes to the plan-engine holder that owns the
/// repo), then by kind (a health.check to a build-health holder). Unknown capability kinds are
/// stored and surfaced but never routed.
/// </summary>
public sealed class CommandQueueService
{
    private const int LeaseSeconds = 60;
    private const int ClaimBatch = 10;

    private readonly IWorkerStore _workers;
    private readonly IEventStore _events;
    private readonly ILogger _logger;

    public CommandQueueService(IWorkerStore workers, IEventStore events, ILogger? logger = null)
    {
        _workers = workers;
        _events = events;
        _logger = logger ?? Log.ForContext<CommandQueueService>();
    }

    /// <summary>Enqueues one command to the worker that owns <paramref name="repo"/>, or —
    /// when no repo is given — to a holder of the capability kind the command implies
    /// (plan.command → plan-engine, health.check → build-health). Returns the command id.</summary>
    public async Task<string> EnqueueAsync(string kind, string? repo, string actor, JsonElement payload)
    {
        string? workerId = repo is { Length: > 0 } ? await _workers.RouteByRepoAsync(repo) : null;
        if (workerId is null)
        {
            var kindCapability = kind switch
            {
                AgentHqContract.CommandKindPlan => "plan-engine",
                AgentHqContract.CommandKindHealthCheck => "build-health",
                _ => null
            };
            if (kindCapability is not null)
            {
                foreach (var w in await _workers.ListAsync())
                {
                    if (!WorkerService.HasCapability(w, kindCapability)) continue;
                    workerId = w.Id;
                    break;
                }
            }
        }
        if (workerId is null)
            throw new InvalidOperationException(
                $"no worker can take a '{kind}' command" +
                (repo is { Length: > 0 } ? $" for repository '{repo}'" : "") +
                " — no worker owns the repository and none declares a matching capability");

        var row = new WorkerCommandRow
        {
            Id = Guid.NewGuid().ToString(),
            WorkerId = workerId,
            Kind = kind,
            Repo = string.IsNullOrEmpty(repo) ? null : repo,
            Actor = actor,
            Payload = JsonSerializer.Serialize(payload, Dtos.Json),
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _workers.InsertCommandAsync(row);
        // The down-signal (rule 0): the payload names the target workerId; the spoke claims
        // through the leased route — the signal only says "come now".
        await _events.AppendAsync(AgentHqContract.EventTypeCommandsPending, new
        {
            workerId, commandId = row.Id, kind, repo = row.Repo, actor
        });
        _logger.Information("queue: command {CommandId} ({Kind}) enqueued for {WorkerId} actor={Actor}",
            row.Id, kind, workerId, actor);
        return row.Id;
    }

    /// <summary>Claim with a long-poll: the fallback channel (rule 0). Up to 10 envelopes,
    /// 60 s visibility lease; a leased envelope is not returned again before expiry.</summary>
    public async Task<IReadOnlyList<CommandEnvelope>> ClaimAsync(string workerId, int waitSeconds,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, 60));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var leaseUntil = DateTimeOffset.UtcNow.AddSeconds(LeaseSeconds).ToUnixTimeMilliseconds();
            var claimed = await _workers.ClaimAsync(workerId, now, leaseUntil, ClaimBatch);
            if (claimed.Count > 0)
                return claimed.Select(ToEnvelope).ToList();
            if (DateTimeOffset.UtcNow >= deadline) return Array.Empty<CommandEnvelope>();
            await Task.Delay(200, ct);
        }
    }

    /// <summary>Records a result, idempotent by commandId (rule 4): the first result wins and
    /// its hash is stored; a later result for the same command is acknowledged and ignored —
    /// a divergent hash raises the warning event instead of double-applying.</summary>
    public async Task<CommandResultAck> PostResultAsync(string workerId, CommandResult result)
    {
        var command = await _workers.GetCommandAsync(result.commandId);
        if (command is null || command.WorkerId != workerId)
            throw new InvalidOperationException($"unknown command {result.commandId} for worker {workerId}");
        var recorded = await _workers.TrySaveResultAsync(result.commandId, result.ok, result.error,
            result.resultHash, result.finishedAt.ToUnixTimeMilliseconds());
        if (recorded)
        {
            _logger.Information("queue: command {CommandId} finished ok={Ok} by {WorkerId}",
                result.commandId, result.ok, workerId);
            return new CommandResultAck(ok: true, duplicate: false);
        }

        var existing = await _workers.GetCommandAsync(result.commandId);
        if (existing?.ResultHash is { } hash && hash != result.resultHash)
        {
            _logger.Warning(
                "queue: divergent duplicate result for {CommandId} from {WorkerId} — stored hash {StoredHash}, this {ThisHash}; ignored, not applied twice",
                result.commandId, workerId, hash, result.resultHash);
            await _events.AppendAsync("command.result_diverged", new
            {
                commandId = result.commandId, workerId, storedHash = hash, receivedHash = result.resultHash
            });
        }
        else
        {
            _logger.Debug("queue: duplicate result for {CommandId} acknowledged (redelivery, same hash)",
                result.commandId);
        }
        return new CommandResultAck(ok: true, duplicate: true);
    }

    private static CommandEnvelope ToEnvelope(WorkerCommandRow row) => new(
        row.Id, row.Kind, row.Repo, row.Actor,
        JsonSerializer.Deserialize<JsonElement>(row.Payload, Dtos.Json),
        row.Issued,
        row.LeaseUntil is { } v ? DateTimeOffset.FromUnixTimeMilliseconds(v) : null);
}
