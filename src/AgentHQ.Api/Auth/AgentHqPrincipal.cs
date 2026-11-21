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
/// <c>admin</c> everything; <c>agent</c> reads + plan commands + exchange + MCP;
/// <c>worker</c> the spoke endpoints only (part 3 registers and enforces them — every
/// current route is refused, the stub lives here so part 3 has one place to extend);
/// <c>ci</c> the hook ingest only.
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
                return path.StartsWith("/hooks/", StringComparison.Ordinal);
            case AuthRoles.Worker:
                return false; // spoke endpoint groups arrive with part 3; nothing is granted yet
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
