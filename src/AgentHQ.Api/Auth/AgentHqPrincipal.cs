using AgentHQ.Core.Auth;

namespace AgentHQ.Api.Auth;

/// <summary>The resolved caller on one request. Loopback requests carry the synthetic
/// loopback admin (the trusted perimeter: the ZCode hook, local curl, the test host);
/// everything else carries a database identity. Kept in <c>HttpContext.Items</c> —
/// never a static.</summary>
public sealed record AgentHqPrincipal(string Username, string Role, string? TokenId, bool FromLoopback)
{
    public static readonly AgentHqPrincipal LoopbackAdmin = new("loopback", AuthRoles.Admin, null, true);

    public bool IsAdmin => Role == AuthRoles.Admin;

    public bool HasRole(params string[] roles) => roles.Contains(Role);
}

/// <summary>
/// The one authorization helper (roles, not scopes): the middleware resolves a principal,
/// this decides whether it may touch the route. Group map per the recorded decision —
/// <c>admin</c> everything; <c>agent</c> reads + plan commands + exchange + MCP + the worker
/// list (the Spokes page reads presence); <c>worker</c> its own spoke slice (register,
/// heartbeat, snapshot, events, commands — per-worker scoping is the 404 own-id rule the
/// endpoints enforce) plus read on <c>/events</c>, the down-signal stream (part 3 rules 0);
/// <c>ci</c> the hook ingest and the CI event ingest.
/// </summary>
public static class AuthPolicy
{
    public static bool IsAllowed(AgentHqPrincipal principal, string method, string path)
    {
        if (principal.FromLoopback) return true;
        switch (principal.Role)
        {
            case AuthRoles.Admin:
                return true;
            case AuthRoles.Agent:
                if (path.StartsWith("/auth/identities", StringComparison.Ordinal) ||
                    path.StartsWith("/auth/tokens", StringComparison.Ordinal)) return false; // admin-only
                if (path.StartsWith("/hooks/", StringComparison.Ordinal)) return false;     // ci-only
                return true;
            case AuthRoles.Ci:
                return path.StartsWith("/hooks/", StringComparison.Ordinal)
                       || path.StartsWith("/ci/", StringComparison.Ordinal);
            case AuthRoles.Worker:
                // The pairing routes are anonymous (rule 10) and never reach here. GET /workers
                // (the fleet list) stays admin/agent: a worker token sees only its own slice.
                return path.StartsWith("/events", StringComparison.Ordinal)
                       || (path.StartsWith("/workers/", StringComparison.Ordinal)
                           && !path.Equals("/workers", StringComparison.Ordinal));
            default:
                return false;
        }
    }
}

public static class AgentHqPrincipalExtensions
{
    public const string ItemsKey = "AgentHqPrincipal";

    public static AgentHqPrincipal? GetPrincipal(this HttpContext ctx) =>
        ctx.Items.TryGetValue(ItemsKey, out var p) ? p as AgentHqPrincipal : null;
}
