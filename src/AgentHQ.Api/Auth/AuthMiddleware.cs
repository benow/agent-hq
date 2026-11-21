using AgentHQ.Api.Auth;
using AgentHQ.Infrastructure.Services;
using Microsoft.AspNetCore.Http.Json;

namespace AgentHQ.Api.Auth;

/// <summary>
/// The perimeter (part 2, distributed-agent-hq). Requests arriving on loopback are trusted —
/// the ZCode hook keeps its no-token contract, local curl and the in-memory test host just
/// work. Everything else requires a valid bearer token or session cookie on every route,
/// REST, SSE, MCP and static alike. Requests through the trusted edge proxy (the shared
/// secret header) are restricted to <c>/auth/login</c> and <c>/mcp</c>; every other path
/// answers 404, never 401 — a remote caller must not be able to confirm which surfaces exist.
/// </summary>
public sealed class AuthMiddleware
{
    public const string CookieName = "agent_hq_token";
    public const string EdgeHeader = "X-AgentHQ-Edge";

    private readonly RequestDelegate _next;
    private readonly IdentityService _identities;
    private readonly string? _edgeToken;

    public AuthMiddleware(RequestDelegate next, IdentityService identities, string? edgeToken)
    {
        _next = next;
        _identities = identities;
        _edgeToken = string.IsNullOrWhiteSpace(edgeToken) ? null : edgeToken;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        var source = remote?.ToString();
        var path = ctx.Request.Path.Value ?? "/";
        var method = ctx.Request.Method;

        // Loopback is the trusted perimeter: no credentials, full access (hook, local curl,
        // test host). A null remote is the in-process test server — same trust.
        if (remote is null || System.Net.IPAddress.IsLoopback(remote))
        {
            ctx.Items[AgentHqPrincipalExtensions.ItemsKey] = AgentHqPrincipal.LoopbackAdmin;
            await _next(ctx);
            return;
        }

        var isLoginRequest = path == "/auth/login" && method == "POST";
        var viaEdge = _edgeToken is not null && ctx.Request.Headers.TryGetValue(EdgeHeader, out var v)
                      && string.Equals(v.ToString(), _edgeToken, StringComparison.Ordinal);

        if (viaEdge)
        {
            // External posture: only the login route and MCP exist. 404 everything else,
            // authenticated or not — 401 would confirm the surface exists.
            if (!isLoginRequest && !path.StartsWith("/mcp", StringComparison.Ordinal))
            {
                await _identities.DenyAsync("external edge: restricted path", path, source);
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (isLoginRequest)
            {
                ctx.Items[AgentHqPrincipalExtensions.ItemsKey] = null; // anonymous login
                await _next(ctx);
                return;
            }
            var edgePrincipal = await ResolveTokenAsync(ctx, path, source);
            if (edgePrincipal is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await WriteErrorAsync(ctx, "unauthorized");
                return;
            }
            ctx.Items[AgentHqPrincipalExtensions.ItemsKey] = edgePrincipal;
            await _next(ctx);
            return;
        }

        if (isLoginRequest || path == "/login")
        {
            ctx.Items[AgentHqPrincipalExtensions.ItemsKey] = null; // anonymous login (rate-limited in the service)
            await _next(ctx);
            return;
        }

        var principal = await ResolveTokenAsync(ctx, path, source);
        if (principal is null)
        {
            // A browser navigating gets the login page, not a JSON 401: API clients keep the
            // machine shape. Navigation is GET/HEAD with text/html accepted (an SSE
            // EventSource sends text/event-stream and still gets the JSON 401).
            var wantsHtml = (method == "GET" || method == "HEAD")
                            && ctx.Request.Headers.Accept.ToString().Contains("text/html");
            if (wantsHtml)
            {
                ctx.Response.Redirect("/login");
                return;
            }
            await _identities.DenyAsync("no valid token", path, source);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await WriteErrorAsync(ctx, "unauthorized");
            return;
        }
        if (!AuthPolicy.IsAllowed(principal, method, path))
        {
            await _identities.DenyAsync($"role '{principal.Role}' not permitted on route", path, source, principal.Username);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await WriteErrorAsync(ctx, "forbidden");
            return;
        }
        ctx.Items[AgentHqPrincipalExtensions.ItemsKey] = principal;
        await _next(ctx);
    }

    /// <summary>Bearer header first, then the browser cookie. Returns null when neither
    /// carries a token that resolves to an unrevoked identity.</summary>
    private async Task<AgentHqPrincipal?> ResolveTokenAsync(HttpContext ctx, string path, string? source)
    {
        string? raw = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            raw = auth["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(raw))
            raw = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(raw)) return null;

        var resolved = await _identities.AuthenticateAsync(raw);
        if (resolved is null)
        {
            await _identities.DenyAsync("unknown or revoked token", path, source);
            return null;
        }
        return new AgentHqPrincipal(resolved.Value.Identity.Username, resolved.Value.Identity.Role,
            resolved.Value.Token.Id, FromLoopback: false);
    }

    private static async Task WriteErrorAsync(HttpContext ctx, string error)
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error });
    }
}

public static class AuthMiddlewareExtensions
{
    /// <summary>Registered in the composition root with the edge token from options/env.
    /// A factory, not UseMiddleware<T>(args): the reflection binder chokes on the string
    /// argument, and this keeps the construction explicit.</summary>
    public static IApplicationBuilder UseAuth(this IApplicationBuilder app, string? edgeToken)
    {
        var identities = app.ApplicationServices.GetRequiredService<IdentityService>();
        return app.Use(next => new AuthMiddleware(next, identities, edgeToken).InvokeAsync);
    }
}
