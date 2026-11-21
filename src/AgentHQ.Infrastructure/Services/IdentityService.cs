using System.Collections.Concurrent;
using AgentHQ.Core.Auth;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;
using Serilog;

namespace AgentHQ.Infrastructure.Services;

/// <summary>
/// Login, token issuance/revocation, bootstrap and the auth audit trail. The per-source rate
/// limit is in-process by design: the hub is a single process, and a restart clearing the
/// backoff window is acceptable for a LAN perimeter (the audit rows persist either way).
/// Audit kinds: login_ok, login_failed, denied, rate_limited, token_revoked, bootstrap_skipped.
/// </summary>
public sealed class IdentityService
{
    private const int MaxFailuresPerWindow = 5;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Backoff = TimeSpan.FromMinutes(5);

    private readonly IIdentityStore _store;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public IdentityService(IIdentityStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger ?? Log.ForContext<IdentityService>();
    }

    /// <summary>First-start bootstrap from AGENTHQ_ADMIN_USER/AGENTHQ_ADMIN_PASSWORD: creates
    /// the admin identity when the username is not yet known; warns and ignores on later
    /// starts (idempotent — env credentials never overwrite an existing identity).</summary>
    public async Task<bool> BootstrapAdminAsync(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            if (username is not null || password is not null)
                _logger.Warning("auth bootstrap: AGENTHQ_ADMIN_USER and AGENTHQ_ADMIN_PASSWORD must both be set; neither applied");
            return false;
        }
        var existing = await _store.GetByUsernameAsync(username);
        if (existing is not null)
        {
            _logger.Warning(
                "auth bootstrap: identity '{Username}' already exists; env credentials ignored " +
                "(bootstrap is first-start only — to change the password, delete and recreate the identity)",
                username);
            await _store.AppendAuditAsync("bootstrap_skipped", username, null, null, "identity already exists");
            return false;
        }
        await _store.CreateAsync(username, AuthRoles.Admin, PasswordHasher.Hash(password));
        _logger.Information("auth bootstrap: admin identity '{Username}' created", username);
        await _store.AppendAuditAsync("bootstrap_ok", username, null, null, "admin identity created");
        return true;
    }

    public enum LoginOutcome { Ok, BadCredentials, RateLimited }

    /// <summary>Verifies credentials and issues a fresh bearer token. Rate limit first: a
    /// source that has burned its failure window is refused before credentials are checked.</summary>
    public async Task<(LoginOutcome Outcome, string Token, IdentityRow Identity)?> LoginAsync(
        string username, string password, string? source, string? route)
    {
        if (IsRateLimited(source))
        {
            await _store.AppendAuditAsync("rate_limited", username, route, source, "login refused: failure window");
            _logger.Warning("auth: login for '{Username}' from {Source} refused — rate limit in force", username, source ?? "unknown");
            return (LoginOutcome.RateLimited, "", null!);
        }
        var identity = await _store.GetByUsernameAsync(username);
        if (identity is null || !PasswordHasher.Verify(password, identity.PasswordHash))
        {
            RecordFailure(source);
            await _store.AppendAuditAsync("login_failed", username, route, source, "bad credentials");
            _logger.Information("auth: failed login for '{Username}' from {Source}", username, source ?? "unknown");
            return (LoginOutcome.BadCredentials, "", null!);
        }
        ClearFailures(source);
        var raw = TokenGenerator.New();
        var token = await _store.CreateTokenAsync(identity.Id, TokenGenerator.Hash(raw), label: "login");
        await _store.AppendAuditAsync("login_ok", username, route, source, $"token {token.Id[..8]}");
        _logger.Information("auth: identity '{Username}' ({Role}) logged in from {Source}, token {TokenId}",
            identity.Username, identity.Role, source ?? "unknown", token.Id[..8]);
        return (LoginOutcome.Ok, raw, identity);
    }

    public Task<(IdentityRow Identity, AuthTokenRow Token)?> AuthenticateAsync(string rawToken) =>
        _store.AuthenticateAsync(TokenGenerator.Hash(rawToken));

    public async Task<bool> RevokeTokenAsync(string tokenId, string? username = null)
    {
        var ok = await _store.RevokeTokenAsync(tokenId);
        if (ok)
        {
            await _store.AppendAuditAsync("token_revoked", username, null, null, $"token {tokenId[..8]}");
            _logger.Information("auth: token {TokenId} revoked", tokenId[..8]);
        }
        return ok;
    }

    public async Task<int> RevokeAllTokensAsync(IdentityRow identity)
    {
        var n = await _store.RevokeAllTokensAsync(identity.Id);
        await _store.AppendAuditAsync("token_revoked", identity.Username, null, null, $"all tokens ({n})");
        if (n > 0) _logger.Information("auth: revoked {Count} token(s) for '{Username}'", n, identity.Username);
        return n;
    }

    /// <summary>Writes a `denied` audit row for an anonymous or role-refused request on a
    /// non-loopback route (route + source recorded, per the perimeter rule).</summary>
    public Task DenyAsync(string reason, string route, string? source, string? username = null)
    {
        _logger.Warning("auth: denied {Reason} — {MethodHint} {Route} from {Source}",
            reason, route, source ?? "unknown");
        return _store.AppendAuditAsync("denied", username, route, source, reason);
    }

    public Task<IReadOnlyList<IdentityRow>> ListIdentitiesAsync() => _store.ListAsync();
    public Task<IReadOnlyList<AuthAuditRow>> AuditAsync(int take = 100) => _store.AuditAsync(take);

    public Task<IdentityRow> CreateIdentityAsync(string username, string password, string role) =>
        _store.CreateAsync(username, role, PasswordHasher.Hash(password));

    public Task<bool> DeleteIdentityAsync(string username) => _store.DeleteAsync(username);

    // ---- per-source rate limit ----

    private bool IsRateLimited(string? source)
    {
        if (source is null) return false;
        if (!_failures.TryGetValue(source, out var q)) return false;
        lock (q)
        {
            // The rule: five failures inside the window arms the backoff; the backoff lasts
            // five minutes from the newest failure, then the source gets a fresh window.
            var now = DateTimeOffset.UtcNow;
            while (q.Count > 0 && now - q.Peek() > FailureWindow) q.Dequeue();
            return q.Count >= MaxFailuresPerWindow && now - NewestLocked(q) < Backoff;
        }
    }

    private void RecordFailure(string? source)
    {
        if (source is null) return;
        var q = _failures.GetOrAdd(source, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            q.Enqueue(DateTimeOffset.UtcNow);
            while (q.Count > MaxFailuresPerWindow * 4) q.Dequeue();
        }
    }

    private void ClearFailures(string? source)
    {
        if (source is not null) _failures.TryRemove(source, out _);
    }

    private static DateTimeOffset NewestLocked(Queue<DateTimeOffset> q) =>
        q.ToArray()[^1];
}
