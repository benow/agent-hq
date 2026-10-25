using System.Text.Json.Serialization;
using AgentHQ.Core.Domain;
using AgentHQ.Core.Events;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Api.Services;

public sealed record HookPromptRequest(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("prompt")] string? Prompt,
    [property: JsonPropertyName("cwd")] string? Cwd);

/// <summary>
/// The hook feed: UserPromptSubmit hooks POST session-attributed prompts here. The feed
/// carries intent and attribution; agent evidence flows via plan files the watchers cover.
/// Unrecognized or unresolvable input records a hook.rejected event and changes no state.
/// </summary>
public sealed class HookFeedService(
    IAgentProvider provider,
    RepositoryStore repositories,
    SessionStore sessions,
    SessionSyncService sync,
    PlanStore plans,
    PlanService planService,
    EventStore events,
    Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _log = logger.ForContext("Subsystem", "hooks");

    public static async Task<IResult> HandleAsync(HookPromptRequest req, HookFeedService svc) =>
        await svc.IngestAsync(req);

    public async Task<IResult> IngestAsync(HookPromptRequest req)
    {
        var raw = req.Prompt;
        if (string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.Cwd))
        {
            await RejectAsync(raw ?? "", "session_id and cwd are required");
            return Results.BadRequest(new { ok = false, error = "session_id and cwd are required" });
        }

        // 1. resolve repository from cwd (longest registered root prefix)
        var repo = await repositories.ResolveByCwdAsync(req.Cwd);
        if (repo is null)
        {
            await RejectAsync(raw!, $"cwd {req.Cwd} is not under a registered repository");
            return Results.UnprocessableEntity(new { ok = false, error = "cwd not under a registered repository" });
        }

        // 2. resolve session: mirror first, immediate provider lookup on race
        var mirrorRow = await sessions.GetAsync(req.SessionId);
        if (mirrorRow is null)
        {
            var detail = await provider.GetSessionAsync(req.SessionId);
            if (detail is null)
            {
                await RejectAsync(raw!, $"session {req.SessionId} unknown to provider");
                return Results.UnprocessableEntity(new { ok = false, error = "unknown session" });
            }
            await sync.MirrorAsync(detail.Info);
        }

        // 3. match /plan command
        var command = PlanCommand.TryParse(raw!);
        if (command is null)
        {
            _log.Debug("non-/plan prompt from {Session} ignored", req.SessionId);
            return Results.Ok(new { ok = true, matched = false });
        }

        // 4-5. resolve the <file> argument and apply the command with full attribution
        var outcome = await planService.ApplyCommandByFileAsync(repo, command, TransitionActor.Session(req.SessionId));
        if (outcome.ResolveError is not null)
        {
            await RejectAsync(raw!, outcome.ResolveError, req.SessionId);
            return Results.UnprocessableEntity(new { ok = false, error = outcome.ResolveError });
        }
        if (!outcome.Outcome!.Ok)
        {
            await RejectAsync(raw!, outcome.Outcome.Error!, req.SessionId);
            return Results.UnprocessableEntity(new
            {
                ok = false,
                error = outcome.Outcome.Error,
                warnings = outcome.Outcome.Warnings
            });
        }
        var plan = outcome.Outcome.Plan!;
        return Results.Ok(new
        {
            ok = true,
            matched = true,
            plan = new { plan.Id, plan.Slug, status = plan.Status },
            warnings = outcome.Outcome.Warnings
        });
    }

    private async Task RejectAsync(string prompt, string reason, string? sessionId = null)
    {
        _log.Information("hook rejected: {Reason} (prompt: {Prompt})", reason, Truncate(prompt));
        await events.AppendAsync(EventTypes.HookRejected, new
        {
            sessionId, reason, prompt = Truncate(prompt, 2000)
        });
    }

    private static string Truncate(string s, int max = 200) =>
        s.Length <= max ? s : s[..max] + "…";
}
