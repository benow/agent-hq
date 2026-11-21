using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentHQ.Infrastructure.Services;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace AgentHQ.Tests;

/// <summary>
/// The auth perimeter (distributed part 2): loopback trust, the two grants, revocation, the
/// rate limit, the role policy and the audit trail — one host per class (lessons.md), one
/// source IP per rate-limit-sensitive test so failure counters do not bleed between tests.
/// Non-loopback requests come from HttpClients built on TestServer.CreateHandler with the
/// RemoteIpAddress overridden — that is the only lever the in-memory host has.
/// </summary>
public class AuthTests(AuthFixture app) : IClassFixture<AuthFixture>
{
    private const string LanIp = "192.168.0.50";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private IdentityService Identities => app.Get<IdentityService>();

    /// <summary>An HttpClient whose requests arrive as the given remote IP.</summary>
    private HttpClient AsRemote(AgentHQApp app, string ip, Action<HttpClient>? configure = null)
    {
        var client = new HttpClient(app.Host.GetTestServer().CreateHandler(
            ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip)))
        { BaseAddress = app.Client.BaseAddress };
        configure?.Invoke(client);
        return client;
    }

    private async Task<HttpResponseMessage> LanAsync(HttpMethod method, string url, object? body = null,
        Action<HttpRequestMessage>? configure = null, string ip = LanIp)
    {
        // No `using` on the client or the message: the in-memory handler touches both after
        // SendAsync's headers have returned (disposal mid-flight is the ObjectDisposed race).
        var client = AsRemote(app, ip);
        var msg = new HttpRequestMessage(method, url);
        if (body is not null)
            msg.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        configure?.Invoke(msg);
        var resp = await client.SendAsync(msg);
        // Read eagerly: the in-memory response stream closes when the request scope ends.
        var text = await resp.Content.ReadAsStringAsync();
        resp.Content = new StringContent(text, Encoding.UTF8, resp.Content.Headers.ContentType?.MediaType ?? "application/json");
        return resp;
    }

    private Task<HttpResponseMessage> LanLoginAsync(string username, string password,
        string? grant = null, string ip = LanIp) =>
        LanAsync(HttpMethod.Post, "/auth/login", new { username, password, grant }, ip: ip);

    private async Task<(HttpResponseMessage Resp, string? Token)> LoginOkAsync(
        string username, string password, string? grant = null, string ip = LanIp)
    {
        var resp = await LanLoginAsync(username, password, grant, ip);
        resp.EnsureSuccessStatusCode();
        // Parse from the eagerly-read string: response-content streams from the in-memory
        // handler are not stable for later reads.
        var text = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (doc.RootElement.TryGetProperty("identity", out var id))
        {
            Assert.Equal(username, id.GetProperty("username").GetString());
            Assert.NotEmpty(id.GetProperty("role").GetString() ?? "");
        }
        return (resp, doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null);
    }

    private static Action<HttpRequestMessage> Bearer(string token) =>
        m => m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Identities for the role-policy tests, created once (loopback is trusted).</summary>
    private async Task SeedIdentitiesAsync()
    {
        if ((await Identities.ListIdentitiesAsync()).Count > 0) return;
        await Identities.CreateIdentityAsync("andy", "andy-pass-1", AgentHQ.Core.Auth.AuthRoles.Admin);
        await Identities.CreateIdentityAsync("linda", "linda-pass-1", AgentHQ.Core.Auth.AuthRoles.Agent);
        await Identities.CreateIdentityAsync("ci-bot", "ci-pass-1", AgentHQ.Core.Auth.AuthRoles.Ci);
        await Identities.CreateIdentityAsync("worker-bot", "worker-pass-1", AgentHQ.Core.Auth.AuthRoles.Worker);
    }

    // ---- the perimeter ----

    [Fact]
    public async Task Loopback_Is_Trusted_No_Credentials()
    {
        // Criterion 1's regression clause: the existing no-credential flow (hook, UI, API)
        // keeps working — the hook path is exercised unchanged by the E2E simulation.
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/")).StatusCode);
        var hook = await app.HookAsync("auth-sess", "not a plan command", cwd: "/tmp/auth-nonexistent-cwd");
        // Reaching the hook feed at all is the point; an unknown session is a handler-level
        // 422 (hook.rejected), never a middleware 401/403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, hook.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, hook.StatusCode);
    }

    [Fact]
    public async Task NonLoopback_Anonymous_Is_401_On_Every_Route()
    {
        foreach (var url in new[] { "/overview", "/plans", "/repositories", "/events?sinceSeq=0", "/", "/index.html", "/no-such-thing" })
            Assert.True(HttpStatusCode.Unauthorized == (await LanAsync(HttpMethod.Get, url)).StatusCode,
                $"{url} did not answer 401");
        var hook = await LanAsync(HttpMethod.Post, "/hooks/zcode/prompts",
            new { session_id = "s", prompt = "x", cwd = "/tmp/auth-nonexistent-cwd" });
        Assert.Equal(HttpStatusCode.Unauthorized, hook.StatusCode);
    }

    [Fact]
    public async Task Token_Grant_Login_And_Authenticated_Access()
    {
        await SeedIdentitiesAsync();
        var (resp, token) = await LoginOkAsync("andy", "andy-pass-1", ip: "10.0.0.1");
        Assert.NotNull(token);
        // (login body already parsed in LoginOkAsync; the identity fields were asserted there)

        var withToken = await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!));
        Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);

        // Without the token, the same routes stay behind the wall.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LanAsync(HttpMethod.Get, "/overview")).StatusCode);
    }

    [Fact]
    public async Task Cookie_Grant_Sets_Session_Cookie_And_Authenticates()
    {
        await SeedIdentitiesAsync();
        var (resp, token) = await LoginOkAsync("andy", "andy-pass-1", grant: "cookie", ip: "10.0.0.2");
        Assert.Null(token); // the cookie grant never echoes the secret in the body
        var setCookie = Assert.Single(resp.Headers.GetValues("Set-Cookie").ToList());
        Assert.Contains("agent_hq_token=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        var cookie = setCookie[(setCookie.IndexOf("agent_hq_token=") + "agent_hq_token=".Length)..].Split(';')[0];
        var withCookie = await LanAsync(HttpMethod.Get, "/events?sinceSeq=0", configure: m =>
            m.Headers.Add("Cookie", $"agent_hq_token={cookie}"));
        Assert.Equal(HttpStatusCode.OK, withCookie.StatusCode); // SSE catch-up (JSON mode) rides the cookie

        var anonymous = await LanAsync(HttpMethod.Get, "/events?sinceSeq=0");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Wrong_Password_Is_401_With_Audit_Row()
    {
        await SeedIdentitiesAsync();
        var resp = await LanLoginAsync("andy", "nope", ip: "10.1.1.1");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var audit = await Identities.AuditAsync();
        Assert.Contains(audit, a => a.Kind == "login_failed" && a.Username == "andy"
            && a.Source == "10.1.1.1" && a.Route == "/auth/login");
    }

    [Fact]
    public async Task Unknown_Identity_Login_Is_401_With_Audit_Row()
    {
        var resp = await LanLoginAsync("nobody", "nope", ip: "10.1.1.2");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        var audit = await Identities.AuditAsync();
        Assert.Contains(audit, a => a.Kind == "login_failed" && a.Username == "nobody" && a.Source == "10.1.1.2");
    }

    [Fact]
    public async Task Revoked_Token_Stops_Working_On_The_Next_Call()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("andy", "andy-pass-1", ip: "10.2.2.2");
        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!))).StatusCode);

        var tok = (await Identities.AuthenticateAsync(token!))!.Value.Token;
        Assert.Equal(HttpStatusCode.OK,
            (await app.Client.PostAsync($"/auth/tokens/{tok.Id}/revoke", null)).StatusCode);

        var after = await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!), ip: "10.2.2.2");
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        var audit = await Identities.AuditAsync();
        Assert.Contains(audit, a => a.Kind == "denied" && a.Source == "10.2.2.2"
            && (a.Detail ?? "").Contains("revoked"));
    }

    [Fact]
    public async Task Logout_Revokes_The_Presented_Token()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("linda", "linda-pass-1", ip: "10.3.3.3");
        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Post, "/auth/logout", null, configure: Bearer(token!))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!))).StatusCode);
    }

    [Fact]
    public async Task Login_Rate_Limit_Kicks_In_And_Says_So()
    {
        var ip = "10.4.4.4";
        await SeedIdentitiesAsync();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LanLoginAsync("andy", "wrong", ip: ip)).StatusCode);

        var limited = await LanLoginAsync("andy", "andy-pass-1", ip: ip); // even correct credentials
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        var bodyText = await limited.Content.ReadAsStringAsync();
        Assert.Contains("rate_limited", bodyText);
        var audit = await Identities.AuditAsync();
        Assert.Contains(audit, a => a.Kind == "rate_limited" && a.Source == ip);

        // A different source is unaffected by the same username's failures.
        Assert.Equal(HttpStatusCode.OK, (await LanLoginAsync("andy", "andy-pass-1", ip: "10.4.4.5")).StatusCode);
    }

    // ---- roles, not scopes ----

    [Fact]
    public async Task Agent_Role_Reads_And_Herds_But_Not_Identity_Management()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("linda", "linda-pass-1", ip: "10.5.5.1");

        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Get, "/plans", configure: Bearer(token!))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!))).StatusCode);
        var denied = await LanAsync(HttpMethod.Post, "/auth/identities",
            new { username = "x", password = "y", role = "ci" }, configure: Bearer(token!));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var hookDenied = await LanAsync(HttpMethod.Post, "/hooks/zcode/prompts",
            new { session_id = "s", prompt = "x", cwd = "/tmp/auth-nonexistent-cwd" }, configure: Bearer(token!));
        Assert.Equal(HttpStatusCode.Forbidden, hookDenied.StatusCode);
    }

    [Fact]
    public async Task Ci_Role_Is_Ingest_Only()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("ci-bot", "ci-pass-1", ip: "10.6.6.1");

        Assert.Equal(HttpStatusCode.Forbidden, (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(token!))).StatusCode);
        var hook = await LanAsync(HttpMethod.Post, "/hooks/zcode/prompts",
            new { session_id = "ci-sess", prompt = "not a plan command", cwd = "/tmp/auth-nonexistent-cwd" },
            configure: Bearer(token!));
        // 422 comes from the hook feed itself (no repository for that cwd), not the
        // middleware: the ingest surface accepted the ci credential.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, hook.StatusCode);
    }

    [Fact]
    public async Task Worker_Role_Has_No_Current_Routes()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("worker-bot", "worker-pass-1", ip: "10.7.7.1");
        // The spoke endpoint group is part 3; until it registers, the worker role passes nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await LanAsync(HttpMethod.Get, "/plans", configure: Bearer(token!))).StatusCode);
    }

    [Fact]
    public async Task Admin_Manages_Identities_And_Revokes_Per_Identity()
    {
        await SeedIdentitiesAsync();
        var (_, token) = await LoginOkAsync("andy", "andy-pass-1", ip: "10.8.8.1");

        var created = await LanAsync(HttpMethod.Post, "/auth/identities",
            new { username = "temp", password = "temp-pass", role = "agent" }, configure: Bearer(token!));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await LanAsync(HttpMethod.Post, "/auth/identities",
            new { username = "temp", password = "z", role = "agent" }, configure: Bearer(token!))).StatusCode);

        var (_, tempToken) = await LoginOkAsync("temp", "temp-pass", ip: "10.8.8.2");
        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(tempToken!))).StatusCode);

        var revoked = await LanAsync(HttpMethod.Post, "/auth/identities/temp/revoke-tokens", null, configure: Bearer(token!));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LanAsync(HttpMethod.Get, "/overview", configure: Bearer(tempToken!))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await LanAsync(HttpMethod.Delete, "/auth/identities/temp", null, configure: Bearer(token!))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LanLoginAsync("temp", "temp-pass", ip: "10.8.8.3")).StatusCode);
    }

    // ---- bootstrap (composition behaviour, dedicated host) ----

    [Fact]
    public async Task Admin_Bootstrap_End_To_End()
    {
        // AGENTHQ_ADMIN_USER/PASSWORD passed through options: the identity exists before the
        // port opens (bootstrap runs in BuildAsync, not on first request).
        var boot = new AgentHQApp { AdminUser = "boss", AdminPassword = "boss-pass", StartWatchers = false };
        await boot.InitializeAsync();
        try
        {
            using var lan = AsRemote(boot, "10.9.9.1");
            var login = await lan.PostAsJsonAsync("/auth/login",
                new { username = "boss", password = "boss-pass", grant = "token" });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var docText = await login.Content.ReadAsStringAsync();
            Assert.Contains("\"role\":\"admin\"", docText.Replace(" ", ""));
            var identities = await boot.Get<IdentityService>().ListIdentitiesAsync();
            Assert.Single(identities.Where(i => i.Username == "boss" && i.Role == "admin"));
        }
        finally { await boot.DisposeAsync(); }
    }
}

/// <summary>Shared fixture for the perimeter tests — no ZCode watcher (inotify budget).</summary>
public sealed class AuthFixture : AgentHQApp
{
    public AuthFixture() => StartWatchers = false;
}

/// <summary>
/// The external access point (Linda V1): edge-forwarded requests (trusted proxy header with
/// the shared secret) are restricted to /auth/login and /mcp; everything else answers 404 —
/// never 401, so a remote caller cannot confirm which surfaces exist.
/// </summary>
public sealed class AuthExternalFixture : AgentHQApp
{
    public AuthExternalFixture()
    {
        StartWatchers = false;
        EdgeToken = "edge-secret";
        AdminUser = "v1";
        AdminPassword = "v1-pass";
    }
}

public class AuthExternalTests(AuthExternalFixture app) : IClassFixture<AuthExternalFixture>
{
    private const string LanIp = "203.0.113.7";
    private const string EdgeHeaderName = "X-AgentHQ-Edge";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient AsEdge(string? token = null, string? edgeValue = "edge-secret")
    {
        var handler = app.Host.GetTestServer().CreateHandler(
            ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse(LanIp));
        var client = new HttpClient(handler) { BaseAddress = app.Client.BaseAddress };
        if (edgeValue is not null) client.DefaultRequestHeaders.Add(EdgeHeaderName, edgeValue);
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        return client;
    }

    private async Task<string> EdgeLoginAsync()
    {
        using var client = AsEdge();
        var resp = await client.PostAsJsonAsync("/auth/login", new
        { username = "v1", password = "v1-pass", grant = "token" });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task Edge_Forwarded_Requests_Are_Restricted_With_404_Posture()
    {
        // Everything except /auth/login and /mcp: 404 even though the caller would hold valid
        // credentials — the plan's do-not-confirm-surfaces rule.
        foreach (var url in new[] { "/plans", "/overview", "/", "/index.html", "/repositories", "/definitely-not-here" })
        {
            using var client = AsEdge();
            Assert.True(HttpStatusCode.NotFound == (await client.GetAsync(url)).StatusCode,
                $"{url} did not answer 404 through the edge");
        }
    }

    [Fact]
    public async Task Edge_Header_Without_The_Secret_Is_Just_A_Lan_Request()
    {
        using var client = AsEdge(edgeValue: "wrong-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/plans")).StatusCode); // 401, not 404: normal perimeter
    }

    [Fact]
    public async Task Edge_Login_Works_And_Mcp_Requires_The_Token()
    {
        var token = await EdgeLoginAsync();
        Assert.False(string.IsNullOrEmpty(token));

        using (var client = AsEdge())
        {
            var denied = await client.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "linda-v1-probe", version = "1.0" } }
            });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        // MCP is the one allowed surface: initialize answers OK (the shape Linda V1's client
        // drives). The full headless round trip is criterion 4 — see AuthMcpTests.
        using (var client = AsEdge(token))
        {
            var init = await client.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { },
                    clientInfo = new { name = "linda-v1-probe", version = "1.0" } }
            });
            Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        }
    }
}

/// <summary>
/// Criterion 4: a headless MCP client completing get_overview + get_events with a bearer
/// token (the shape Linda V2 will use), plus the anonymous-401 proof on the same route.
/// Drives streamable-HTTP JSON-RPC directly — the same wire the official clients speak.
/// </summary>
public class AuthMcpTests(AuthFixture app) : IClassFixture<AuthFixture>
{
    private const string LanIp = "192.168.0.60";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private HttpClient AsLan(string? token = null)
    {
        var handler = app.Host.GetTestServer().CreateHandler(
            ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse(LanIp));
        var client = new HttpClient(handler) { BaseAddress = app.Client.BaseAddress };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpResponseMessage Resp, JsonElement Body)> McpAsync(
        HttpClient client, string? session, object body)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        msg.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        if (session is not null) msg.Headers.Add("Mcp-Session-Id", session);
        var resp = await client.SendAsync(msg);
        var text = await resp.Content.ReadAsStringAsync();
        JsonElement doc;
        if (text.TrimStart().StartsWith("event:") || text.Contains("\nevent:") || text.TrimStart().StartsWith("data:"))
        {
            var data = text.Split('\n').First(l => l.TrimStart().StartsWith("data:"));
            doc = JsonDocument.Parse(data.TrimStart()[5..].Trim()).RootElement.Clone();
        }
        else if (string.IsNullOrWhiteSpace(text))
            doc = JsonDocument.Parse("{}").RootElement.Clone();
        else
            doc = JsonDocument.Parse(text).RootElement.Clone();
        return (resp, doc);
    }

    [Fact]
    public async Task Headless_Mcp_Round_Trip_With_Bearer_Token()
    {
        await app.Get<IdentityService>().CreateIdentityAsync("linda2", "linda2-pass", AgentHQ.Core.Auth.AuthRoles.Agent);
        string token;
        using (var loginClient = AsLan())
        {
            var login = await loginClient.PostAsJsonAsync("/auth/login",
                new { username = "linda2", password = "linda2-pass", grant = "token" });
            login.EnsureSuccessStatusCode();
            token = (await login.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("token").GetString()!;
        }

        using var client = AsLan(token);
        var (initResp, init) = await McpAsync(client, null, new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-03-26", capabilities = new { },
                clientInfo = new { name = "linda-v2-probe", version = "1.0" } }
        });
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        Assert.True(init.TryGetProperty("result", out _), init.ToString());
        var session = initResp.Headers.TryGetValues("Mcp-Session-Id", out var sids) ? sids.First() : null;

        await McpAsync(client, session, new { jsonrpc = "2.0", method = "notifications/initialized" });

        // The PM loop, exactly as Linda V2 will drive it: get_overview → get_events(sinceSeq).
        var (overviewResp, overview) = await McpAsync(client, session, new
        {
            jsonrpc = "2.0", id = 2, method = "tools/call",
            @params = new { name = "get_overview", arguments = new { } }
        });
        Assert.Equal(HttpStatusCode.OK, overviewResp.StatusCode);
        Assert.True(overview.TryGetProperty("result", out _), overview.ToString());

        var (eventsResp, events) = await McpAsync(client, session, new
        {
            jsonrpc = "2.0", id = 3, method = "tools/call",
            @params = new { name = "get_events", arguments = new { sinceSeq = 0, types = "" } }
        });
        Assert.Equal(HttpStatusCode.OK, eventsResp.StatusCode);
        Assert.True(events.TryGetProperty("result", out _), events.ToString());

        // Criterion 4's captured transcript, asserted as evidence. Tool results are
        // content[{type:"text", text:"<json>"}] — parse the text payload of each.
        // Empty hub: the shape is what matters — repositories array, lastSeq echo.
        Assert.Contains("repositories", Text(overview));
        Assert.Contains("lastSeq", Text(events));
    }

    private static string Text(JsonElement toolResult)
    {
        var text = toolResult.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        return text ?? "";
    }

    [Fact]
    public async Task Anonymous_Mcp_From_Lan_Is_401()
    {
        using var client = AsLan();
        var resp = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-03-26", capabilities = new { },
                clientInfo = new { name = "probe", version = "1.0" } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}

/// <summary>The auth path against the hub's PostgreSQL store (the deployment provider on
/// builds): bootstrap, login, token auth, per-identity revocation and the audit trail.</summary>
public class AuthPgTests(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Auth_Round_Trip_On_Postgres()
    {
        var connStr = await pg.NewDatabaseAsync();
        var app = new AgentHQApp
        {
            DbProvider = "postgres", PgConnStr = connStr,
            AdminUser = "boss", AdminPassword = "boss-pass", StartWatchers = false
        };
        await app.InitializeAsync();
        try
        {
            using (var lan = new HttpClient(app.Host.GetTestServer().CreateHandler(
                ctx => ctx.Connection.RemoteIpAddress = IPAddress.Parse("192.168.9.9")))
            { BaseAddress = app.Client.BaseAddress })
            {
                var login = await lan.PostAsJsonAsync("/auth/login",
                    new { username = "boss", password = "boss-pass", grant = "token" });
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                var token = (await login.Content.ReadFromJsonAsync<JsonElement>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))).GetProperty("token").GetString()!;
                lan.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("/overview")).StatusCode);
                await app.Get<IdentityService>().RevokeAllTokensAsync(
                    (await app.Get<IdentityService>().ListIdentitiesAsync()).Single(i => i.Username == "boss"));
                Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("/overview")).StatusCode);
            }

            var audit = await app.Get<IdentityService>().AuditAsync();
            Assert.Contains(audit, a => a.Kind == "bootstrap_ok" && a.Username == "boss");
            Assert.Contains(audit, a => a.Kind == "login_ok" && a.Username == "boss");
            Assert.Contains(audit, a => a.Kind == "denied" && (a.Detail ?? "").Contains("revoked"));
        }
        finally { await app.DisposeAsync(); }
    }
}
