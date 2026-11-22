using AgentHQ.Api.Auth;
using AgentHQ.Client;
using AgentHQ.Infrastructure.Services;
using AgentHQ.Infrastructure.Stores;

namespace AgentHQ.Api.Endpoints;

/// <summary>
/// The spoke routes (part 3, distributed-agent-hq): registration, heartbeats, snapshot,
/// event deltas, the leased command claim/result, pairing, and the CI ingest — all thin
/// wrappers over the services the REST/MCP surfaces use (the hub's one-sync rule). Every
/// <c>/workers/{id}/…</c> route returns 404 unless {id} is the caller identity's own worker —
/// a worker token cannot read or write another worker's slice, and the 404 must not reveal
/// whether the id exists. The contract header is enforced by middleware for the whole group.
/// </summary>
public static class WorkerEndpoints
{
    public static WebApplication MapWorkers(this WebApplication app)
    {
        // ---- fleet (dashboard Spokes page; admin/agent — a worker token cannot list others) ----
        app.MapGet("/workers", async (WorkerService workers) =>
            Results.Json(new { workers = await workers.ListPresenceAsync() }));

        // ---- registration + presence ----
        app.MapPost("/workers/register", async (WorkerIdentityRequest req, HttpContext ctx,
            WorkerService workers) =>
        {
            if (string.IsNullOrWhiteSpace(req.name) || string.IsNullOrWhiteSpace(req.version))
                return Results.BadRequest(new { ok = false, error = "name and version are required" });
            var worker = await ResolveWorkerAsync(ctx, workers, req.workerId);
            if (worker is null)
                return NotFoundAmbiguous();
            var outcome = await workers.RegisterAsync(worker.Id, new RegisterRequest(
                req.name.Trim(), req.machine?.Trim() ?? "", req.version.Trim(), req.capabilities ?? []));
            if (!outcome.Ok)
                return Results.Json(new { ok = false,
                    error = $"repository_owned_by_other_worker: {outcome.ConflictWorkerId}",
                    detail = "ownership is exclusive; drop the repository from this spoke's plan-engine capability or release it there first" },
                    statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(new { workerId = outcome.Row.Id, serverTime = DateTimeOffset.UtcNow,
                contractVersion = ContractMiddleware.AgentHqContractVersion });
        });

        app.MapPost("/workers/{id}/heartbeat", async (string id, HeartbeatBody body, HttpContext ctx,
            WorkerService workers) =>
        {
            var worker = await ResolveScopedAsync(ctx, workers, id);
            if (worker is null) return NotFoundAmbiguous();
            var resp = await workers.HeartbeatAsync(worker.Id, body.version ?? "");
            return Results.Ok(resp);
        });

        // ---- replication ----
        app.MapPost("/workers/{id}/snapshot", async (string id, Snapshot snapshot, HttpContext ctx,
            WorkerService workers) =>
        {
            var worker = await ResolveScopedAsync(ctx, workers, id);
            if (worker is null) return NotFoundAmbiguous();
            var outcome = await workers.ApplySnapshotAsync(worker.Id, snapshot);
            return outcome.Ok
                ? Results.Ok(new { accepted = true, nextExpected = outcome.NextExpected })
                : Results.Json(new { ok = false,
                    error = $"repository_owned_by_other_worker: {outcome.ConflictWorkerId}" },
                    statusCode: StatusCodes.Status409Conflict);
        });

        app.MapPost("/workers/{id}/events", async (string id, EventsBatch batch, HttpContext ctx,
            WorkerService workers) =>
        {
            var worker = await ResolveScopedAsync(ctx, workers, id);
            if (worker is null) return NotFoundAmbiguous();
            var outcome = await workers.SendEventsAsync(worker.Id, batch);
            return outcome is null
                ? Results.Json(new { ok = false, error = "batch_ahead_of_cursor", expected = (await workers.GetByIdAsync(worker.Id))!.NextExpectedSeq },
                    statusCode: StatusCodes.Status422UnprocessableEntity)
                : Results.Ok(outcome.Value.Ack);
        });

        // ---- commands (the leased claim is the fallback channel; SSE is the down-signal) ----
        app.MapGet("/workers/{id}/commands", async (string id, int? wait, HttpContext ctx,
            CommandQueueService queue, CancellationToken ct) =>
        {
            var worker = await ResolveScopedAsync(ctx, ctx.RequestServices.GetRequiredService<WorkerService>(), id);
            if (worker is null) return NotFoundAmbiguous();
            var commands = await queue.ClaimAsync(worker.Id, wait ?? 0, ct);
            return Results.Ok(new { commands });
        });

        app.MapPost("/workers/{id}/commands/{commandId}/result", async (string id, string commandId,
            CommandResult result, HttpContext ctx, CommandQueueService queue) =>
        {
            var worker = await ResolveScopedAsync(ctx, ctx.RequestServices.GetRequiredService<WorkerService>(), id);
            if (worker is null) return NotFoundAmbiguous();
            var ack = await queue.PostResultAsync(worker.Id, result);
            return Results.Ok(ack);
        });

        // ---- pairing (rule 10): create + poll anonymous; approve admin-session only ----
        app.MapPost("/workers/pairing", async (HttpContext ctx, PairingService pairing) =>
        {
            var source = ctx.Connection.RemoteIpAddress?.ToString();
            var (outcome, nonce) = await pairing.CreateAsync(source);
            return outcome == PairingService.CreateOutcome.RateLimited
                ? Results.Json(new { ok = false, error = "rate_limited",
                    detail = "10 pairing creates per 10 minutes per source" },
                    statusCode: StatusCodes.Status429TooManyRequests)
                : Results.Ok(nonce);
        });

        app.MapPost("/workers/pairing/{nonce}/approve", async (string nonce, ApproveBody body,
            HttpContext ctx, PairingService pairing) =>
        {
            var principal = ctx.GetPrincipal();
            if (principal is null || !principal.IsAdmin)
                return Results.Json(new { ok = false, error = "admin session required to approve a pairing" },
                    statusCode: StatusCodes.Status401Unauthorized);
            if (string.IsNullOrWhiteSpace(body.name))
                return Results.BadRequest(new { ok = false, error = "name is required" });
            return await pairing.ApproveAsync(nonce, body.name.Trim()) switch
            {
                PairingService.ApproveOutcome.Approved => Results.Ok(new { ok = true }),
                PairingService.ApproveOutcome.NameTaken => Results.Json(new { ok = false,
                    error = "name_taken",
                    detail = $"identity '{body.name.Trim()}' already exists; the nonce is still pending — approve again with another name" },
                    statusCode: StatusCodes.Status422UnprocessableEntity),
                _ => Results.Json(new { ok = false, error = "nonce_not_approvable: unknown, expired, or already used" },
                    statusCode: StatusCodes.Status422UnprocessableEntity)
            };
        });

        app.MapGet("/workers/pairing/{nonce}", async (string nonce, PairingService pairing) =>
            Results.Ok(await pairing.PollAsync(nonce)));

        // ---- CI ingest (part 3; the build spoke mirrors this on its loopback, part 5) ----
        app.MapPost("/ci/events", async (CiEvent ciEvent, CiIngestService ci) =>
        {
            var seq = await ci.IngestAsync(ciEvent);
            return Results.Ok(new { accepted = true, seq });
        });

        return app;
    }

    // ---- scoping helpers ----

    /// <summary>The worker addressed by the caller's own identity. Loopback/admin may address
    /// any worker id (the test host, local curl). Null → 404, ambiguous on purpose: unknown
    /// worker and another worker's id are indistinguishable (the error taxonomy pins it).</summary>
    private static async Task<AgentHQ.Models.WorkerRow?> ResolveScopedAsync(
        HttpContext ctx, WorkerService workers, string id)
    {
        var principal = ctx.GetPrincipal();
        if (principal is { FromLoopback: true })
            return await workers.GetByIdAsync(id);
        var own = await OwnWorkerAsync(principal, workers, ctx.RequestServices.GetRequiredService<IIdentityStore>());
        return own is not null && own.Id == id ? own : null;
    }

    /// <summary>Registration names its own workerId (bound at pairing/identity creation); a
    /// re-registration must carry the caller's own id so a restart cannot adopt another
    /// worker's slice. Loopback names an existing workerId directly.</summary>
    private static async Task<AgentHQ.Models.WorkerRow?> ResolveWorkerAsync(
        HttpContext ctx, WorkerService workers, string? workerId)
    {
        var principal = ctx.GetPrincipal();
        var own = await OwnWorkerAsync(principal, workers, ctx.RequestServices.GetRequiredService<IIdentityStore>());
        if (principal is { FromLoopback: true })
            return workerId is { Length: > 0 } ? await workers.GetByIdAsync(workerId) : own;
        if (own is null || (workerId is { Length: > 0 } && workerId != own.Id)) return null;
        return own;
    }

    private static async Task<AgentHQ.Models.WorkerRow?> OwnWorkerAsync(
        AgentHqPrincipal? principal, WorkerService workers, IIdentityStore identities)
    {
        if (principal?.Username is null) return null;
        var identity = await identities.GetByUsernameAsync(principal.Username);
        return identity is null ? null : await workers.GetByIdentityAsync(identity.Id);
    }

    private static IResult NotFoundAmbiguous() =>
        Results.NotFound(new { ok = false, error = "not_found" });

    public record WorkerIdentityRequest(string? workerId, string? name, string? machine,
        string? version, Capability[]? capabilities);
    public record HeartbeatBody(string? version);
    public record ApproveBody(string? name);
}
