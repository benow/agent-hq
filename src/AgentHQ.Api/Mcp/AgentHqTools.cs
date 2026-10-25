using System.ComponentModel;
using AgentHQ.Core.Plans;
using AgentHQ.Infrastructure.Events;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;
using ModelContextProtocol.Server;

namespace AgentHQ.Api.Mcp;

/// <summary>
/// MCP tool façade — thin wrappers over the same service handlers as REST, so both stay in
/// sync by construction. Tool descriptions carry the contract and etiquette (updatedSince,
/// ready=true, herding on stuck states) per the layered guidance model.
/// </summary>
[McpServerToolType]
public static class AgentHqTools
{
    [McpServerTool, Description("One-call operational picture: repositories, plans grouped by status (blocked with reasons), verification queue, recently active sessions, last event seq. Orient with this first; then use get_events with sinceSeq to stay current — never re-list.")]
    public static async Task<object> GetOverview(RepositoryStore repos, PlanStore plans, SessionStore sessions, EventStore events) =>
        new
        {
            repositories = (await repos.ListAsync()).Select(r => new { r.Id, r.RootPath, r.IsDefaultBool }),
            plans = await plans.ListAsync(),
            blocked = (await plans.ListAsync()).Where(p => p.IsBlockedBool),
            verificationQueue = await plans.ListAsync(status: "implemented"),
            lastEventSeq = await events.LastSeqAsync()
        };

    [McpServerTool, Description("List registered repositories (id, root path, default flag).")]
    public static async Task<object> ListRepositories(RepositoryStore repos) =>
        (await repos.ListAsync()).Select(r => new { r.Id, r.RootPath, r.Provider, @default = r.IsDefaultBool });

    [McpServerTool, Description("List development agent sessions. Pass updatedSince to cheaply catch up instead of re-listing.")]
    public static async Task<object> ListSessions(Core.Domain.IAgentProvider provider, string rootPath, string updatedSince, string status) =>
        await provider.ListSessionsAsync(new Core.Domain.SessionQuery(
            string.IsNullOrWhiteSpace(rootPath) ? null : rootPath,
            DateTimeOffset.TryParse(updatedSince, out var since) ? since : null,
            string.IsNullOrWhiteSpace(status) ? null : status));

    [McpServerTool, Description("Deterministic markdown digest of a session (L1): user messages verbatim, tail-weighted assistant text, tool one-liners. Pass budget chars (default 8000).")]
    public static async Task<string> GetSessionDigest(string sessionId, int? budget, Core.Domain.IAgentProvider provider)
    {
        var detail = await provider.GetSessionAsync(sessionId);
        if (detail is null) return $"session {sessionId} not found";
        var delta = await provider.GetMessagesAsync(sessionId, new Core.Domain.SessionMessageQuery());
        return Core.Digest.DigestBuilder.Render(detail.Info, delta.Messages, budget ?? Core.Digest.DigestBuilder.DefaultBudget);
    }

    [McpServerTool, Description("Filtered message delta for a session (L2): after=<sequence>, include=reasoning,tool to widen part types.")]
    public static async Task<object> GetSessionMessages(string sessionId, long? after, string? include, Core.Domain.IAgentProvider provider)
    {
        var set = (include ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await provider.GetMessagesAsync(sessionId, new Core.Domain.SessionMessageQuery(after,
            set.Contains("reasoning"), set.Contains("tool"), set.Contains("steps")));
    }

    [McpServerTool, Description("Submit a plan. Convention sections: Goal, Context, Steps (markdown checkboxes = progress), Dependencies, Verification Criteria, Documentation, Status Log. Draft against list_lessons so past oversight classes are designed out. Returns slug + soft-validation warnings.")]
    public static async Task<object> SubmitPlan(string repo, string name, string markdown,
        RepositoryStore repos, PlanService plans)
    {
        var repoRow = await repos.GetAsync(repo);
        if (repoRow is null) return new { error = $"unknown repository '{repo}'" };
        var (plan, warnings) = await plans.SubmitAsync(repoRow, name, markdown);
        // Plain names: MCP tool results are serialized by the SDK, not the host's JSON options.
        return new { Id = plan.Id, Slug = plan.Slug, Status = plan.Status, Warnings = warnings };
    }

    [McpServerTool, Description("List plans. Filters: repo, status; ready=true = in_review, not blocked, dependencies all verified/done (herd on this); blocked=true = needs-attention queue.")]
    public static async Task<object> ListPlans(string? repo, string? status, bool? ready, bool? blocked, PlanStore store) =>
        await store.ListAsync(repo, status, ready, blocked);

    [McpServerTool, Description("Read a plan: metadata, transition history (actors, commands, git heads), dependencies.")]
    public static async Task<object> ReadPlan(string planId, PlanStore store, PlanService svc)
    {
        var plan = await store.GetAsync(planId);
        if (plan is null) return new { error = "not found" };
        return new
        {
            plan.Id, plan.Slug, plan.Name, plan.Status, priority = plan.Priority,
            blocked = plan.IsBlockedBool, blockedReason = plan.BlockedReason,
            session = plan.SessionExternalId, implementedBy = plan.ImplementedBySessionId,
            progress = new { checkedCount = plan.Checked, total = plan.Total },
            content = await svc.ReadContentAsync(plan),
            history = await store.HistoryAsync(planId),
            dependencies = await store.DependenciesAsync(planId)
        };
    }

    [McpServerTool, Description("Manual/fallback status transition; same lifecycle rules as /plan commands. Resolves the calling session by newest-active-in-repository when sessionId is omitted. Recorded with actor 'openclaw'.")]
    public static async Task<object> UpdatePlanStatus(string planId, string status, string sessionId,
        string by, string note, PlanStore store, PlanService svc, SessionStore sessions)
    {
        var actorSession = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        by = string.IsNullOrWhiteSpace(by) ? "openclaw" : by;
        note = string.IsNullOrWhiteSpace(note) ? null! : note;
        var plan = await store.GetAsync(planId);
        if (plan is null) return new { error = "not found" };
        PlanStatus to;
        try { to = PlanStatusCodes.FromCode(status); }
        catch (ArgumentException) { return new { error = $"unknown status '{status}'" }; }
        if (actorSession is null)
        {
            var active = (await sessions.ListAsync(repoId: plan.RepositoryId, status: "active"))
                .OrderByDescending(s => s.Updated).FirstOrDefault();
            actorSession = active?.ExternalId;
        }
        var outcome = await svc.ApplyManualTransitionAsync(plan, to, by, actorSession, null, note);
        return outcome.Ok
            ? new { ok = true, status = outcome.Plan!.Status, warnings = outcome.Warnings }
            : new { ok = false, error = outcome.Error };
    }

    [McpServerTool, Description("Update plan metadata: priority (integer, higher = sooner) and/or manually link the implementing session of record (fallback for missed hooks).")]
    public static async Task<object> UpdatePlan(string planId, long priority, string sessionId,
        PlanStore store)
    {
        var plan = await store.GetAsync(planId);
        if (plan is null) return new { error = "not found" };
        await store.UpdateMetaAsync(planId,
            priority == long.MinValue ? null : priority,
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId);
        return new { ok = true };
    }

    [McpServerTool, Description("List parsed lesson entries (docs/plans/lessons.md) for a repository — consult when drafting Verification Criteria.")]
    public static async Task<object> ListLessons(string repositoryId, PlanStore store) =>
        await store.LessonsAsync(repositoryId);

    [McpServerTool, Description("Catch-up poll: events since seq N, optional types filter. The rhythm: get_overview first, then this each turn. Returns lastSeq for the next call.")]
    public static async Task<object> GetEvents(long sinceSeq, string types, EventStore events)
    {
        var filter = types?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        List<Core.Events.AgentHqEvent> rows;
        try { rows = (await events.ReadSinceAsync(sinceSeq, filter)).ToList(); }
        catch (Exception ex) { return new { Error = "READ: " + ex }; }
        return new
        {
            events = rows.Select(e => new
            {
                e.Seq,
                e.Type,
                // Embedded as JSON text: the SDK's serializer would render a JsonElement as
                // {"ValueKind": ...}.
                Payload = System.Text.Json.JsonSerializer.Serialize(e.Payload, Core.Events.AgentHqEvent.JsonOpts),
                e.At
            }),
            LastSeq = await events.LastSeqAsync()
        };
    }

    [McpServerTool, Description("The full Agent HQ playbook: plan convention, lifecycle semantics and who moves what, digest usage, catch-up rhythm, separation of duties, lesson consultation.")]
    public static string GetGuide() => GuideText;

    public const string GuideText = """
        # Agent HQ guide

        ## Mission
        You are the project manager. Development agents (ZCode sessions) do the work; you
        scope, submit, herd, and verify. Plans are not only development: research, operations
        and writing run through the same lifecycle. You never touch code, and you never verify
        a plan you implemented yourself (you never implement — separation of duties holds by
        construction).

        ## Orientation rhythm
        1. get_overview once at conversation start.
        2. get_events(sinceSeq) each turn to stay current — never re-list.
        3. All list tools take updatedSince for cheap catch-up.

        ## Plan lifecycle
        submitted → refining → in_review → implementing → implemented → verifying → verified → done,
        with rework verifying → fixing → implemented. deferred/abandoned from any state.
        Moves are recorded by /plan commands from sessions (actor = session id); you act via
        update_plan_status (actor 'openclaw'). Blocked is an orthogonal flag: blocked=true is the
        needs-attention queue — surface blocked plans with their reasons; that is your primary
        herding trigger.

        ## Plan convention
        Sections: Goal, Context, Steps (checkboxes = progress), Dependencies, Verification
        Criteria (concrete and testable), Documentation (written during implementation), Status
        Log (append-only phase claims and evidence). Draft Verification Criteria against
        list_lessons so past classes of oversight get caught at authoring time.

        ## Verification
        GET /plans?status=implemented is the verification queue. You may judge criteria checkable
        from recorded evidence and session digests (get_session_digest). Verdicts: verified on
        pass, fixing on fail. The implementing session's claims live in the plan's Status Log —
        read_plan carries them; audit, don't trust.

        ## Sessions
        L0 list → L1 digest per session → L2 messages only when needed. Digests are deterministic;
        condense into your own notes (workspace MEMORY.md) — re-derive, don't remember.
        """;
}
