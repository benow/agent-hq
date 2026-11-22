namespace AgentHQ.Api.Auth;

/// <summary>
/// The v1 wire contract's header gate (part 3, distributed-agent-hq): every request to a
/// <c>/workers/</c> or <c>/ci/</c> route carries <c>X-AgentHQ-Contract</c> with a known major.
/// Missing or unknown major → 400 naming both versions — what, why, what to do. Additive
/// changes are minor; breaking changes are a v2 route prefix with the hub accepting one major
/// back. Registered after the auth middleware, so bad credentials answer 401 first.
/// </summary>
public sealed class ContractMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        // Loopback is the trusted perimeter (the local test host, local curl): no header
        // demanded, same rule as auth. Everything remote carries the header.
        var remote = ctx.Connection.RemoteIpAddress;
        var trusted = remote is null || System.Net.IPAddress.IsLoopback(remote);
        if (trusted ||
            (!path.StartsWith("/workers/", StringComparison.Ordinal)
             && !path.StartsWith("/ci/", StringComparison.Ordinal)))
        {
            await next(ctx);
            return;
        }

        var received = ctx.Request.Headers.TryGetValue(ContractHeaderName, out var v)
            ? v.ToString().Trim()
            : null;
        var receivedMajor = received?.Split('.')[0];

        if (string.IsNullOrEmpty(receivedMajor))
        {
            await WriteRejectionAsync(ctx, received,
                "contract header missing: this is a v1 contract route. Send X-AgentHQ-Contract: 1 on every /workers/ and /ci/ request (AgentHQ.Client does this automatically).");
            return;
        }
        if (receivedMajor != AgentHqContractVersion)
        {
            await WriteRejectionAsync(ctx, received,
                $"contract major mismatch: this hub speaks v{AgentHqContractVersion}, the request names v{receivedMajor}. Align the client package version with the hub (breaking changes are a v2 route prefix; the hub accepts one major back).");
            return;
        }
        await next(ctx);
    }

    public const string ContractHeaderName = "X-AgentHQ-Contract";
    public const string AgentHqContractVersion = "1";

    private static async Task WriteRejectionAsync(HttpContext ctx, string? received, string detail)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            ok = false,
            error = $"contract_header_mismatch: expected v{AgentHqContractVersion}, got {(string.IsNullOrEmpty(received) ? "none" : $"v{received}")}",
            detail
        });
    }
}

public static class ContractMiddlewareExtensions
{
    public static IApplicationBuilder UseContractHeader(this IApplicationBuilder app) =>
        app.UseMiddleware<ContractMiddleware>();
}
