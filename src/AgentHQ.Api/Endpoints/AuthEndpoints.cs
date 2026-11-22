using AgentHQ.Api.Auth;
using AgentHQ.Core.Auth;
using AgentHQ.Infrastructure.Services;

namespace AgentHQ.Api.Endpoints;

/// <summary>
/// Authentication endpoints (part 2, distributed-agent-hq). Login is deliberately reachable
/// without credentials — the middleware lets POST /auth/login through, and its abuse is
/// handled by the per-source rate limit plus the audit trail. Identity management is
/// admin-only; the policy in AuthPolicy already refuses other roles before these run.
/// </summary>
public static class AuthEndpoints
{
    public static WebApplication MapAuth(this WebApplication app)
    {
        // One mechanism, two token shapes: grant=token returns the bearer token in the body
        // (API/MCP clients, spokes, CI); grant=cookie sets it as the session cookie for the
        // browser (SameSite=Lax, HttpOnly; EventSource reconnect keeps working — no
        // query-string tokens, they leak into logs). No expiry by default: a LAN with a
        // handful of named principals — recorded as an accepted risk.
        app.MapPost("/auth/login", async (LoginRequest req, HttpContext ctx, IdentityService identities) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { ok = false, error = "username and password are required" });
            var grant = string.IsNullOrWhiteSpace(req.Grant) ? "token" : req.Grant.Trim().ToLowerInvariant();
            if (grant is not ("token" or "cookie"))
                return Results.BadRequest(new { ok = false, error = "grant must be 'token' or 'cookie'" });

            var source = ctx.Connection.RemoteIpAddress?.ToString();
            var outcome = await identities.LoginAsync(req.Username.Trim(), req.Password, source, "/auth/login");
            if (outcome is null)
                return Results.UnprocessableEntity(new { ok = false, error = "bad_credentials" });
            var (status, token, identity) = outcome.Value;
            if (status == IdentityService.LoginOutcome.RateLimited)
                return Results.Json(new { ok = false, error = "rate_limited", detail = "too many failed logins from this source; wait and retry" },
                    statusCode: StatusCodes.Status429TooManyRequests);
            if (status == IdentityService.LoginOutcome.BadCredentials)
                return Results.Json(new { ok = false, error = "bad_credentials" },
                    statusCode: StatusCodes.Status401Unauthorized);

            if (grant == "cookie")
            {
                ctx.Response.Cookies.Append(AuthMiddleware.CookieName, token, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Path = "/"
                });
                return Results.Ok(new { ok = true, identity = new { identity.Username, identity.Role } });
            }
            return Results.Ok(new { ok = true, token, identity = new { identity.Username, identity.Role } });
        });

        // Revoke the presented credential (cookie or bearer). Loopback has no token — refused
        // with the rule-refusal shape.
        app.MapPost("/auth/logout", async (HttpContext ctx, IdentityService identities) =>
        {
            var principal = ctx.GetPrincipal();
            if (principal?.TokenId is null)
                return Results.UnprocessableEntity(new { ok = false, error = "no_token: logout revokes the presented bearer/cookie credential" });
            await identities.RevokeTokenAsync(principal.TokenId, principal.Username);
            ctx.Response.Cookies.Delete(AuthMiddleware.CookieName);
            return Results.Ok(new { ok = true });
        });

        // ---- identity management (admin-only; enforced here and by the policy) ----

        app.MapGet("/auth/identities", async (IdentityService identities) =>
        {
            var rows = await identities.ListIdentitiesAsync();
            return Results.Json(rows.Select(i => new { i.Id, i.Username, i.Role, created = i.Created }));
        });

        app.MapPost("/auth/identities", async (CreateIdentityRequest req, IdentityService identities,
            WorkerService workers) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Results.BadRequest(new { ok = false, error = "username and password are required" });
            var role = (req.Role ?? "").Trim().ToLowerInvariant();
            if (!AuthRoles.IsValid(role))
                return Results.UnprocessableEntity(new { ok = false, error = $"role must be one of: {string.Join(", ", AuthRoles.All)}" });
            if (await identities.ListIdentitiesAsync() is var list && list.Any(i =>
                    i.Username.Equals(req.Username.Trim(), StringComparison.Ordinal)))
                return Results.UnprocessableEntity(new { ok = false, error = $"username '{req.Username.Trim()}' is taken" });
            var row = await identities.CreateIdentityAsync(req.Username.Trim(), req.Password, role);
            // Identity binding (part 3): a worker identity owns exactly one worker record from
            // the moment it exists — pairing uses the same path.
            if (role == AuthRoles.Worker)
                await workers.EnsureForIdentityAsync(row.Id);
            return Results.Created($"/auth/identities/{row.Username}", new { row.Id, row.Username, row.Role });
        });

        app.MapDelete("/auth/identities/{username}", async (string username, IdentityService identities) =>
        {
            var deleted = await identities.DeleteIdentityAsync(username);
            return deleted
                ? Results.Ok(new { ok = true, tokensRevokedAll = true })
                : Results.NotFound(new { ok = false, error = "not_found", detail = $"no identity '{username}'" });
        });

        // Per-identity revocation: kill every live token of one identity (e.g. a lost laptop).
        app.MapPost("/auth/identities/{username}/revoke-tokens", async (string username, IdentityService identities) =>
        {
            var identity = await identities.ListIdentitiesAsync();
            var row = identity.FirstOrDefault(i => i.Username == username);
            if (row is null)
                return Results.NotFound(new { ok = false, error = "not_found", detail = $"no identity '{username}'" });
            var n = await identities.RevokeAllTokensAsync(row);
            return Results.Ok(new { ok = true, revoked = n });
        });

        // Individual revocation: kill one token by id (logout handles self-revocation).
        app.MapPost("/auth/tokens/{id}/revoke", async (string id, IdentityService identities) =>
            await identities.RevokeTokenAsync(id)
                ? Results.Ok(new { ok = true })
                : Results.NotFound(new { ok = false, error = "not_found", detail = $"no token '{id}'" }));

        return app;
    }

    // Admin-only enforcement lives in AuthPolicy (the one authorization helper): non-admin
    // roles are refused at the middleware for /auth/identities* and /auth/tokens* already,
    // so these handlers need no second check.

    public record LoginRequest(string? Username, string? Password, string? Grant);
    public record CreateIdentityRequest(string? Username, string? Password, string? Role);
}
