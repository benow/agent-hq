using System.Diagnostics;
using AgentHQ.Core.Events;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Infrastructure.Watching;

namespace AgentHQ.Infrastructure.Services;

public sealed record TransitionOutcome(
    bool Ok,
    string? Error,
    IReadOnlyList<string> Warnings,
    PlanRow? Plan);

/// <summary>Who is acting: actor_type (session / openclaw / andy / ui) plus the session id.</summary>
public sealed record TransitionActor(string Type, string? SessionId)
{
    public static TransitionActor Session(string sessionId) => new("session", sessionId);
    public static TransitionActor Human(string sessionId = "ui") => new("andy", sessionId);
}

/// <summary>Result of resolving a &lt;file&gt; argument and applying a command.</summary>
public sealed record CommandOutcome(TransitionOutcome? Outcome, string? ResolveError);

/// <summary>
/// Central plan orchestration: submit/read/update with self-write suppression, lifecycle
/// transitions (command and manual paths share the same rules), blocked flag, dependencies,
/// priority, git-HEAD snapshots, and event emission.
/// </summary>
public sealed class PlanService
{
    private readonly PlanStore _plans;
    private readonly RepositoryStore _repositories;
    private readonly EventStore _events;
    private readonly Serilog.ILogger _log;
    private readonly Dictionary<string, PlanDirWatcher> _watchers = new();

    public PlanService(PlanStore plans, RepositoryStore repositories, EventStore events,
        Serilog.ILogger? logger = null)
    {
        _plans = plans;
        _repositories = repositories;
        _events = events;
        _log = (logger ?? Serilog.Log.ForContext<PlanService>()).ForContext("Subsystem", "plans");
    }

    public IEnumerable<PlanDirWatcher> Watchers => _watchers.Values;

    public void StartWatcher(RepositoryRow repo)
    {
        if (_watchers.ContainsKey(repo.Id)) return;
        var watcher = new PlanDirWatcher(repo, _plans, async plan =>
        {
            if (Path.GetFileName(plan.FilePath) == "lessons.md")
            {
                await IndexLessonsAsync(repo);
                return;
            }
            await _events.AppendAsync(EventTypes.PlanUpdated, new
            {
                planId = plan.Id, slug = plan.Slug, repositoryId = plan.RepositoryId,
                version = plan.Version, progress = new { checkedCount = plan.Checked, total = plan.Total },
                reason = "external-edit"
            });
        });
        watcher.Start();
        _watchers[repo.Id] = watcher;
    }

    public PlanDirWatcher? WatcherFor(string repositoryId) =>
        _watchers.TryGetValue(repositoryId, out var w) ? w : null;

    // ---- submit / read / update ----

    public async Task<(PlanRow Plan, IReadOnlyList<string> Warnings)> SubmitAsync(
        RepositoryRow repo, string name, string markdown)
    {
        var slug = PlanSlug.Derive(name, s => _plans.SlugTakenAsync(repo.Id, s).GetAwaiter().GetResult());
        var dir = Path.Combine(repo.RootPath, repo.PlansDir);
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{slug}.md");
        var watcher = WatcherFor(repo.Id);
        watcher?.NoteSelfWrite();
        await File.WriteAllTextAsync(filePath, markdown);

        var progress = PlanDocument.Progress(markdown);
        var plan = new PlanRow
        {
            Id = Guid.NewGuid().ToString(),
            Slug = slug,
            Name = name,
            RepositoryId = repo.Id,
            FilePath = filePath,
            Status = PlanStatus.Submitted.Code(),
            ContentHash = PlanDirWatcher.ContentHash(markdown),
            Checked = progress.Checked,
            Total = progress.Total,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        await _plans.InsertAsync(plan);
        var warnings = PlanDocument.MissingSections(markdown)
            .Select(s => $"plan is missing the '{s}' section (soft validation)")
            .ToList();
        await _events.AppendAsync(EventTypes.PlanCreated, new
        {
            planId = plan.Id, slug, name, repositoryId = repo.Id, status = plan.Status,
            progress = new { checkedCount = progress.Checked, total = progress.Total }
        });
        _log.Information("plan submitted {Slug} in {Repo}", slug, repo.Id);
        return (plan, warnings);
    }

    public Task<string> ReadContentAsync(PlanRow plan) => File.ReadAllTextAsync(plan.FilePath);

    public async Task<PlanRow> WriteContentAsync(PlanRow plan, string markdown)
    {
        var watcher = WatcherFor(plan.RepositoryId);
        watcher?.NoteSelfWrite();
        await File.WriteAllTextAsync(plan.FilePath, markdown);
        var progress = PlanDocument.Progress(markdown);
        // Self-write: hash + progress update in the same step, no version bump, no event.
        return await _plans.UpdateContentAsync(plan.Id, PlanDirWatcher.ContentHash(markdown), progress, externalEdit: false)
               ?? (await _plans.GetAsync(plan.Id))!;
    }

    // ---- transitions ----

    /// <summary>
    /// Resolves the &lt;file&gt; argument against a repository (slug, then repo-relative path, then
    /// absolute path) and applies the command with attribution. This is the one path the hook
    /// feed and the web UI share: both resolve, attribute, and transition identically.
    /// </summary>
    public async Task<CommandOutcome> ApplyCommandByFileAsync(RepositoryRow repo, PlanCommand command,
        TransitionActor actor)
    {
        // Work commands claim the plan for the calling session, so resolve against that
        // session's repository (the one it is working in) before falling back to the plan's.
        var candidates = await _plans.ResolveFileArgAsync(repo, command.Argument);
        if (candidates.Count == 1)
            return new CommandOutcome(await ApplyCommandAsync(candidates[0], command, actor), null);
        if (candidates.Count > 1)
            return new CommandOutcome(null, $"'{command.Argument}' resolves to {candidates.Count} plans");
        if (PlanStateMachine.IsWorkCommand(command.Subcommand) && !string.IsNullOrEmpty(actor.SessionId))
        {
            var sessionRow = await _sessionsOf(actor.SessionId);
            var sessionRepo = sessionRow?.RepositoryId is { } rid ? await _repositories.GetAsync(rid) : null;
            if (sessionRepo is not null && sessionRepo.Id != repo.Id)
            {
                var inSessionRepo = await _plans.ResolveFileArgAsync(sessionRepo, command.Argument);
                if (inSessionRepo.Count == 1)
                    return new CommandOutcome(await ApplyCommandAsync(inSessionRepo[0], command, actor), null);
                if (inSessionRepo.Count > 1)
                    return new CommandOutcome(null, $"'{command.Argument}' resolves to {inSessionRepo.Count} plans in {sessionRepo.Id}");
            }
        }
        return new CommandOutcome(null, $"'{command.Argument}' resolves to no plan in {repo.Id}");
    }

    private Func<string, Task<Stores.SessionRow?>> _sessionsOf = _ => Task.FromResult<Stores.SessionRow?>(null);

    /// <summary>Wires session lookup for attribution-aware file resolution (set by the host).</summary>
    public void UseSessions(Func<string, Task<Stores.SessionRow?>> lookup) => _sessionsOf = lookup;

    /// <summary>Applies a parsed /plan command to a resolved plan with session attribution.</summary>
    public async Task<TransitionOutcome> ApplyCommandAsync(PlanRow plan, PlanCommand command,
        TransitionActor actor)
    {
        var actorSessionId = actor.SessionId;
        var from = PlanStatusCodes.FromCode(plan.Status);
        var sub = command.Subcommand;

        if (sub is "block" or "unblock")
            return await ApplyBlockAsync(plan, sub == "block", command.ReasonTail, actor);

        if (!PlanStateMachine.CanTransition(from, sub))
            return Fail(plan, $"'{sub}' is not a valid command for a plan in '{plan.Status}'.");

        var warnings = new List<string>();
        var repo = (await _repositories.GetAsync(plan.RepositoryId))!;
        string? gitHead = null;
        PlanStatus target = PlanStateMachine.Target(from, sub);

        // Short-cut entry: verify on implementing/fixing first records → implemented,
        // attributed to the verifying session.
        if (PlanStateMachine.IsShortCutEntry(sub, from))
        {
            await RecordTransitionAsync(plan, from, PlanStatus.Implemented, actor.Type, actorSessionId,
                $"verify (short-cut from {from.Code()})", gitHead ??= GitHead(repo), null, sodWarning: false);
            plan = (await _plans.GetAsync(plan.Id))!;
            from = PlanStatus.Implemented;
            target = PlanStatus.Verifying;
            warnings.Add($"short-cut entry: recorded → implemented attributed to verifying session {actorSessionId}");
        }

        // Work commands link the session of record.
        if (PlanStateMachine.IsWorkCommand(sub) && plan.SessionExternalId is null)
        {
            await _plans.UpdateMetaAsync(plan.Id, sessionExternalId: actorSessionId);
            await _events.AppendAsync(EventTypes.PlanSessionLinked, new
            {
                planId = plan.Id, slug = plan.Slug, sessionId = actorSessionId
            });
        }

        if (sub == "implement")
        {
            var unmet = (await _plans.DependenciesAsync(plan.Id))
                .Where(d => d.Status is not ("verified" or "done"))
                .Select(d => d.Slug).ToList();
            if (unmet.Count > 0)
                warnings.Add("unmet dependencies (soft): " + string.Join(", ", unmet));
        }

        if (sub == "implemented")
        {
            await _plans.SetImplementedByAsync(plan.Id, actorSessionId);
        }

        bool sodWarning = false;
        if (sub == "pass")
        {
            // Separation of duties: the session that claimed implemented may not verify.
            var implementedBy = plan.ImplementedBySessionId;
            if (implementedBy is not null && implementedBy == actorSessionId)
            {
                sodWarning = true;
                warnings.Add($"separation-of-duties: plan was implemented by the same session ({actorSessionId}) claiming verification");
            }
        }

        if (target is PlanStatus.Implementing or PlanStatus.Implemented or PlanStatus.Verified)
            gitHead = GitHead(repo);

        await RecordTransitionAsync(plan, from, target, actor.Type, actorSessionId,
            $"/plan {sub}", gitHead, command.ReasonTail, sodWarning);
        plan = (await _plans.GetAsync(plan.Id))!;
        return new TransitionOutcome(true, null, warnings, plan);
    }

    /// <summary>
    /// Manual / fallback path (PATCH /plans/{id}/status). &lt;paramref name="blocked"/&gt; toggles the
    /// orthogonal blocked flag instead of moving the lifecycle; otherwise the same state
    /// machine rules and history rows as commands apply.
    /// </summary>
    public async Task<TransitionOutcome> ApplyManualTransitionAsync(PlanRow plan, PlanStatus? to,
        string actorType, string? actorSessionId, string? command, string? note, bool? blocked = null)
    {
        if (blocked is not null)
            return await ApplyBlockAsync(plan, blocked.Value, note, new TransitionActor(actorType, actorSessionId));
        if (to is null)
            return Fail(plan, "either status or blocked is required");
        var from = PlanStatusCodes.FromCode(plan.Status);
        var target = to.Value;
        var result = PlanStateMachine.Validate(from, target);
        if (!result.IsAllowed)
            return Fail(plan, result.Error!);

        var repo = (await _repositories.GetAsync(plan.RepositoryId))!;
        string? gitHead = null;
        if (target is PlanStatus.Implementing or PlanStatus.Implemented or PlanStatus.Verified)
            gitHead = GitHead(repo);

        bool sodWarning = false;
        var warnings = result.Warnings.ToList();
        if (target == PlanStatus.Verified && actorSessionId is not null &&
            actorSessionId == plan.ImplementedBySessionId)
        {
            sodWarning = true;
            warnings.Add("separation-of-duties: verifier is the implementing session");
        }

        var resolvedCommand = command is not null
            ? command
            : PlanStateMachine.CommandFor(from, target) is { } cmd ? $"PATCH:/{cmd}" : "PATCH";
        await RecordTransitionAsync(plan, from, target, actorType, actorSessionId,
            resolvedCommand, gitHead, note, sodWarning);
        plan = (await _plans.GetAsync(plan.Id))!;
        return new TransitionOutcome(true, null, warnings, plan);
    }

    private async Task<TransitionOutcome> ApplyBlockAsync(PlanRow plan, bool block, string? reason,
        TransitionActor actor)
    {
        if (block && string.IsNullOrWhiteSpace(reason))
            return Fail(plan, "'block' requires a reason.");
        if (block == plan.IsBlockedBool)
            return Fail(plan, block ? "Plan is already blocked." : "Plan is not blocked.");
        await _plans.SetBlockedAsync(plan.Id, block, block ? reason : null);
        var actorLabel = actor.SessionId ?? actor.Type;
        if (block)
            await _plans.AddBlockAsync(plan.Id, reason!, actorLabel);
        else
            await _plans.ResolveOpenBlockAsync(plan.Id, reason);
        var type = block ? EventTypes.PlanBlocked : EventTypes.PlanUnblocked;
        var updated = (await _plans.GetAsync(plan.Id))!;
        await _events.AppendAsync(type, new
        {
            planId = plan.Id, slug = plan.Slug, repositoryId = plan.RepositoryId,
            status = updated.Status, actor = actorLabel, actorType = actor.Type, reason
        });
        return new TransitionOutcome(true, null, Array.Empty<string>(), updated);
    }

    private static TransitionOutcome Fail(PlanRow plan, string error) =>
        new(false, error, Array.Empty<string>(), plan);

    internal async Task RecordTransitionAsync(PlanRow plan, PlanStatus from, PlanStatus to,
        string actorType, string? actorSessionId, string? command, string? gitHead,
        string? note, bool sodWarning)
    {
        await _plans.SetStatusAsync(plan.Id, to.Code());
        await _plans.AddHistoryAsync(new HistoryRow
        {
            PlanId = plan.Id,
            FromStatus = from.Code(),
            ToStatus = to.Code(),
            ActorType = actorType,
            ActorSessionId = actorSessionId,
            Command = command,
            GitHead = gitHead,
            Note = note,
            SeparationOfDutiesWarning = sodWarning ? 1 : 0
        });
        await _events.AppendAsync(EventTypes.PlanStatusChanged, new
        {
            planId = plan.Id, slug = plan.Slug, name = plan.Name,
            from = from.Code(), to = to.Code(),
            actor = actorSessionId ?? actorType, actorType, command,
            gitHead, note,
            separationOfDutiesWarning = sodWarning
        });
        _log.Information("plan transition {Slug} {From} → {To} by {Actor} ({Command})",
            plan.Slug, from.Code(), to.Code(), actorSessionId ?? actorType, command);
    }

    public static string? GitHead(RepositoryRow repo)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = repo.RootPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi)!;
            var head = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return p.ExitCode == 0 && head.Length == 40 ? head : null;
        }
        catch { return null; }
    }

    public async Task IndexLessonsAsync(RepositoryRow repo)
    {
        var path = Path.Combine(repo.RootPath, repo.PlansDir, "lessons.md");
        if (!File.Exists(path)) return;
        var content = await File.ReadAllTextAsync(path);
        var entries = LessonsParser.Parse(content);
        await _plans.UpsertLessonsAsync(repo.Id, entries, PlanDirWatcher.ContentHash(content));
        _log.Information("lessons indexed: {Count} entries in {Repo}", entries.Count, repo.Id);
    }
}
