using AgentHQ.Api.Services;
using AgentHQ.Core.Domain;
using AgentHQ.Core.Digest;
using AgentHQ.Core.Plans;
using AgentHQ.Core.Events;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Api.Endpoints;

/// <summary>All REST endpoints except SSE (/events lives in SseEndpoints).</summary>
public static class ApiEndpoints
{
    public static async Task<IResult> Overview(
        RepositoryStore repos, PlanStore plans, SessionStore sessions, EventStore events)
    {
        var repositories = await repos.ListAsync();
        var byStatus = new Dictionary<string, object>();
        foreach (var status in PlanStatusCodes.AllCodes)
        {
            var rows = await plans.ListAsync(status: status);
            if (rows.Count == 0) continue;
            byStatus[status] = rows.Select(p => new
            {
                p.Id, p.Slug, p.Name, priority = p.Priority,
                blocked = p.IsBlockedBool, blockedReason = p.BlockedReason,
                progress = new { checkedCount = p.Checked, total = p.Total },
                session = p.SessionExternalId
            }).ToList();
        }
        var blocked = (await plans.ListAsync()).Where(p => p.IsBlockedBool)
            .Select(p => new { p.Id, p.Slug, reason = p.BlockedReason, status = p.Status }).ToList();
        var verificationQueue = (await plans.ListAsync(status: "implemented"))
            .Select(p => new { p.Id, p.Slug, implementedBy = p.ImplementedBySessionId }).ToList();
        var recent = (await sessions.ListAsync()).Take(10).Select(s => new
        {
            s.ExternalId, s.Title, s.RepositoryId, updated = s.Updated,
            status = SessionStore.DeriveStatus(s, DateTimeOffset.UtcNow)
        }).ToList();
        return Results.Json(new
        {
            repositories = repositories.Select(r => new { r.Id, r.RootPath, r.IsDefaultBool }),
            plans = byStatus,
            blocked,
            verificationQueue,
            recentSessions = recent,
            lastEventSeq = await events.LastSeqAsync()
        });
    }

    public static WebApplication MapRepositories(this WebApplication app)
    {
        app.MapGet("/repositories", async (RepositoryStore store) =>
            Results.Json((await store.ListAsync()).Select(r => new
            {
                r.Id, r.RootPath, r.Provider, r.ExternalId, r.PlansDir, @default = r.IsDefaultBool, firstSeen = r.FirstSeen
            })));

        app.MapPost("/repositories", async (RegisterRepoRequest req, RepositoryStore store, PlanService plans) =>
        {
            if (string.IsNullOrWhiteSpace(req.RootPath) || !Directory.Exists(Path.GetFullPath(req.RootPath)))
                return Results.BadRequest(new { error = "rootPath must be an existing directory" });
            var repo = await store.RegisterAsync(req.RootPath, "zcode", externalId: null, req.IsDefault == true);
            plans.StartWatcher(repo);
            await plans.IndexLessonsAsync(repo);
            return Results.Created($"/repositories/{repo.Id}", new { repo.Id, repo.RootPath });
        });
        return app;
    }

    public static WebApplication MapSessions(this WebApplication app)
    {
        app.MapGet("/sessions", async (string? repo, string? status, DateTimeOffset? updatedSince,
            string? repoRootPath, RepositoryStore repositories, SessionStore sessions, IAgentProvider provider) =>
        {
            // Read-through list: the provider is authoritative for filtering by freshness.
            var repoRef = repo ?? repoRootPath;
            string? rootPath = null;
            if (repoRef is not null)
            {
                var row = await repositories.GetAsync(repoRef);
                if (row is null) return Results.BadRequest(new { error = $"unknown repository '{repoRef}'" });
                rootPath = row.RootPath;
            }
            var result = await provider.ListSessionsAsync(new SessionQuery(rootPath, updatedSince, status));
            return Results.Json(result.Select(s => new
            {
                s.Id, s.Title, rootPath = s.RootPath, s.TaskType, s.LastOutcome,
                s.TimeCreated, s.TimeUpdated, status = s.Status(DateTimeOffset.UtcNow)
            }));
        });

        app.MapGet("/sessions/{id}", async (string id, IAgentProvider provider, SessionStore sessions) =>
        {
            var detail = await provider.GetSessionAsync(id);
            var mirror = await sessions.GetAsync(id);
            return detail is null && mirror is null
                ? Results.NotFound()
                : Results.Json(new
                {
                    detail?.Info,
                    mirror?.RepositoryId,
                    firstUserMessage = detail?.FirstUserMessageExcerpt ?? mirror?.FirstUserMessageExcerpt
                });
        });

        app.MapGet("/sessions/{id}/digest", async (string id, int? budget, IAgentProvider provider) =>
        {
            var detail = await provider.GetSessionAsync(id);
            if (detail is null) return Results.NotFound();
            var delta = await provider.GetMessagesAsync(id, new SessionMessageQuery());
            return Results.Text(DigestBuilder.Render(detail.Info, delta.Messages, budget ?? DigestBuilder.DefaultBudget),
                "text/markdown");
        });

        app.MapGet("/sessions/{id}/messages", async (string id, long? after, string? include, IAgentProvider provider) =>
        {
            var includeSet = (include ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var delta = await provider.GetMessagesAsync(id, new SessionMessageQuery(
                after,
                IncludeReasoning: includeSet.Contains("reasoning"),
                IncludeTool: includeSet.Contains("tool"),
                IncludeSteps: includeSet.Contains("steps")));
            return Results.Json(new { id, messages = delta.Messages, maxSequence = delta.MaxSequence });
        });
        return app;
    }

    public static WebApplication MapPlans(this WebApplication app)
    {
        app.MapPost("/plans", async (SubmitPlanRequest req, RepositoryStore repos, PlanService plans) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Markdown))
                return Results.BadRequest(new { error = "name and markdown are required" });
            var repo = await repos.GetAsync(req.Repo);
            if (repo is null) return Results.BadRequest(new { error = $"unknown repository '{req.Repo}'" });
            var (plan, warnings) = await plans.SubmitAsync(repo, req.Name, req.Markdown);
            return Results.Created($"/plans/{plan.Id}",
                new { plan.Id, plan.Slug, plan.Status, warnings, file = plan.FilePath });
        });

        app.MapGet("/plans", async (string? repo, string? status, bool? ready, bool? blocked, PlanStore store) =>
        {
            var repoId = repo; // callers pass the repository id
            var rows = await store.ListAsync(repoId, status, ready, blocked);
            return Results.Json(rows.Select(p => new
            {
                p.Id, p.Slug, p.Name, p.Status, priority = p.Priority,
                blocked = p.IsBlockedBool, blockedReason = p.BlockedReason,
                session = p.SessionExternalId, implementedBy = p.ImplementedBySessionId,
                progress = new { checkedCount = p.Checked, total = p.Total },
                p.Version, updated = p.Updated
            }));
        });

        app.MapGet("/plans/{id}", async (string id, PlanStore store, PlanService svc) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            var history = await store.HistoryAsync(id);
            var deps = await store.DependenciesAsync(id);
            return Results.Json(new
            {
                plan.Id, plan.Slug, plan.Name, plan.Status, priority = plan.Priority,
                plan.RepositoryId,
                blocked = plan.IsBlockedBool, blockedReason = plan.BlockedReason,
                session = plan.SessionExternalId, implementedBy = plan.ImplementedBySessionId,
                progress = new { checkedCount = plan.Checked, total = plan.Total },
                plan.Version, updated = plan.Updated, file = plan.FilePath,
                dependencies = deps.Select(d => new { d.Id, d.Slug, d.Status }),
                history = history.Select(h => new
                {
                    h.FromStatus, h.ToStatus, h.ActorType, h.ActorSessionId, h.Command,
                    h.GitHead, h.Note, separationOfDutiesWarning = h.SodWarning, at = h.At
                })
            });
        });

        app.MapGet("/plans/{id}/content", async (string id, PlanStore store, PlanService svc) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            return Results.Text(await svc.ReadContentAsync(plan), "text/markdown");
        });

        app.MapPut("/plans/{id}/content", async (string id, UpdateContentRequest req, PlanStore store, PlanService svc) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            var updated = await svc.WriteContentAsync(plan, req.Markdown ?? "");
            return Results.Ok(new { updated.Id, updated.Version, progress = new { checkedCount = updated.Checked, total = updated.Total } });
        });

        app.MapPatch("/plans/{id}", async (string id, PatchPlanRequest req, PlanStore store, PlanService svc, EventStore events) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            await store.UpdateMetaAsync(id, req.Priority, req.SessionId);
            if (req.SessionId is not null && plan.SessionExternalId is null)
                await events.AppendAsync(EventTypes.PlanSessionLinked, new
                {
                    planId = id, slug = plan.Slug, sessionId = req.SessionId, via = "PATCH"
                });
            return Results.Ok(new { ok = true });
        });

        app.MapPatch("/plans/{id}/status", async (string id, PatchStatusRequest req, PlanStore store, PlanService svc) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();

            // Blocking is the orthogonal flag, not a lifecycle move: {blocked: true|false}.
            if (req.Blocked is not null)
            {
                var blockOutcome = await svc.ApplyManualTransitionAsync(plan, null, req.By ?? "andy",
                    req.SessionId, command: null, note: req.Note, blocked: req.Blocked);
                return blockOutcome.Ok
                    ? Results.Ok(new { ok = true, status = blockOutcome.Plan!.Status, blocked = blockOutcome.Plan.IsBlockedBool })
                    : Results.UnprocessableEntity(new { ok = false, error = blockOutcome.Error });
            }

            PlanStatus to;
            try { to = PlanStatusCodes.FromCode(req.Status ?? ""); }
            catch (ArgumentException) { return Results.BadRequest(new { error = $"unknown status '{req.Status}'" }); }
            var outcome = await svc.ApplyManualTransitionAsync(plan, to, req.By ?? "andy", req.SessionId,
                command: null, note: req.Note);
            return outcome.Ok
                ? Results.Ok(new { ok = true, status = outcome.Plan!.Status, warnings = outcome.Warnings })
                : Results.UnprocessableEntity(new { ok = false, error = outcome.Error });
        });

        // The web UI records human actions as /plan commands with attribution (same path as the
        // hook feed), so history shows one vocabulary regardless of where the action came from.
        app.MapPost("/plans/{id}/commands", async (string id, PlanCommandRequest req, PlanStore store,
            RepositoryStore repos, PlanService svc) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "command is required" });
            var repo = await repos.GetAsync(plan.RepositoryId);
            if (repo is null) return Results.BadRequest(new { error = $"repository {plan.RepositoryId} not found" });

            var prompt = $"/plan {req.Command} {plan.Slug}{(string.IsNullOrWhiteSpace(req.Note) ? "" : " " + req.Note)}";
            var parsed = PlanCommand.TryParse(prompt);
            if (parsed is null) return Results.BadRequest(new { error = $"unparseable command '{req.Command}'" });
            var actor = req.By == "openclaw"
                ? new Infrastructure.Services.TransitionActor("openclaw", "ui")
                : Infrastructure.Services.TransitionActor.Human("ui");
            var outcome = await svc.ApplyCommandAsync(plan, parsed, actor);
            return outcome.Ok
                ? Results.Ok(new { ok = true, status = outcome.Plan!.Status, blocked = outcome.Plan.IsBlockedBool, warnings = outcome.Warnings })
                : Results.UnprocessableEntity(new { ok = false, error = outcome.Error, warnings = outcome.Warnings });
        });

        app.MapPost("/plans/{id}/dependencies", async (string id, DependencyRequest req, PlanStore store) =>
        {
            var plan = await store.GetAsync(id);
            if (plan is null) return Results.NotFound();
            if (await store.GetAsync(req.RequiresPlanId ?? "") is null)
                return Results.BadRequest(new { error = "unknown requiresPlanId" });
            var added = await store.AddDependencyAsync(id, req.RequiresPlanId!);
            return added
                ? Results.Ok(new { ok = true })
                : Results.UnprocessableEntity(new { ok = false, error = "dependency rejected: self-reference or cycle" });
        });

        app.MapGet("/repositories/{id}/lessons", async (string id, PlanStore store) =>
            Results.Json((await store.LessonsAsync(id)).Select(l => new
            {
                l.EntryId, l.Title, l.Category, l.SourcePlanId, l.ContentHash,
                firstSeenAt = DateTimeOffset.FromUnixTimeMilliseconds(l.FirstSeenAt)
            })));
        return app;
    }

    public record RegisterRepoRequest(string? RootPath, bool? IsDefault);
    public record SubmitPlanRequest(string? Repo, string? Name, string? Markdown);
    public record UpdateContentRequest(string? Markdown);
    public record PatchPlanRequest(long? Priority, string? SessionId);
    public record PatchStatusRequest(string? Status, string? SessionId, string? By, string? Note, bool? Blocked);
    public record PlanCommandRequest(string? Command, string? Note, string? By);
    public record DependencyRequest(string? RequiresPlanId);
}
