using System.Collections.Concurrent;
using AgentHQ.Client;
using AgentHQ.Core.Auth;
using AgentHQ.Infrastructure.Stores;
using AgentHQ.Models;
using Serilog;

namespace AgentHQ.Infrastructure.Services;

public sealed record PairingApproval(string Token, WorkerRow Worker, IdentityRow Identity);

/// <summary>
/// The pairing nonce flow — the credential bootstrap (part 3, distributed rule 10). A spoke
/// (or any tool) creates a nonce unauthenticated but rate-limited (10 creates per 10 min per
/// source; the poll GET is exempt — polling every couple of seconds for a 5-minute TTL is the
/// intended client behaviour); a browser approves it with an admin session; the poll then
/// delivers the fresh `worker`-role bearer token exactly once and the nonce is burned. The raw
/// token lives only in the pairing row until delivery and the spoke's own config file after —
/// never typed by a human, never shown to an LLM, never logged.
/// </summary>
public sealed class PairingService
{
    private const int MaxCreatesPerWindow = 10;
    private static readonly TimeSpan CreateWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Ttl = AgentHqContract.PairingTtl;

    private readonly IWorkerStore _workers;
    private readonly IIdentityStore _identities;
    private readonly WorkerService _workerService;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _creates = new();

    public PairingService(IWorkerStore workers, IIdentityStore identities, WorkerService workerService,
        ILogger? logger = null)
    {
        _workers = workers;
        _identities = identities;
        _workerService = workerService;
        _logger = logger ?? Log.ForContext<PairingService>();
    }

    public enum CreateOutcome { Created, RateLimited }

    public async Task<(CreateOutcome Outcome, PairingCreated? Nonce)> CreateAsync(string? source)
    {
        var now = DateTimeOffset.UtcNow;
        if (source is not null)
        {
            var q = _creates.GetOrAdd(source, _ => new Queue<DateTimeOffset>());
            lock (q)
            {
                while (q.Count > 0 && now - q.Peek() > CreateWindow) q.Dequeue();
                if (q.Count >= MaxCreatesPerWindow)
                {
                    _logger.Warning("pairing: create refused from {Source} — 10 per 10 min limit reached", source);
                    return (CreateOutcome.RateLimited, null);
                }
                q.Enqueue(now);
            }
        }

        var row = new WorkerPairingRow
        {
            Nonce = TokenGenerator.New()[..24],
            CreatedAt = now.ToUnixTimeMilliseconds(),
            ExpiresAt = now.Add(Ttl).ToUnixTimeMilliseconds(),
            State = "pending"
        };
        await _workers.InsertPairingAsync(row);
        _logger.Information("pairing: nonce {Nonce} created (TTL 5 min) from {Source}", row.Nonce, source ?? "loopback");
        return (CreateOutcome.Created, new PairingCreated(row.Nonce,
            $"/workers/pairing/{row.Nonce}/approve", DateTimeOffset.FromUnixTimeMilliseconds(row.ExpiresAt)));
    }

    /// <summary>Browser approval with an admin session: creates the worker identity, its bound
    /// worker record, and a fresh worker token attached to the nonce. Single-approve — a nonce
    /// already approved (or expired) refuses.</summary>
    public async Task<bool> ApproveAsync(string nonce, string spokeName)
    {
        var row = await _workers.GetPairingAsync(nonce);
        if (row is null || row.State != "pending" || row.IsExpired) return false;

        var identity = await _identities.CreateAsync(spokeName, AuthRoles.Worker,
            PasswordHasher.Hash(TokenGenerator.New())); // random unusable password — tokens are the credential
        var worker = await _workerService.EnsureForIdentityAsync(identity.Id);
        var raw = TokenGenerator.New();
        var token = await _identities.CreateTokenAsync(identity.Id, TokenGenerator.Hash(raw), label: "pairing");

        row.State = "approved";
        row.IdentityId = identity.Id;
        row.TokenRaw = raw; // held only until the one delivery
        row.TokenId = token.Id;
        await _workers.UpdatePairingAsync(row);
        _logger.Information("pairing: nonce {Nonce} approved — worker {WorkerId} '{Name}' created, token {TokenId} attached",
            nonce, worker.Id, spokeName, token.Id[..8]);
        return true;
    }

    /// <summary>The poll: pending until approved; the token is delivered exactly once and the
    /// nonce burns to `consumed`; `expired` past TTL; `unknown` for a bad nonce.</summary>
    public async Task<PairingPoll> PollAsync(string nonce)
    {
        var row = await _workers.GetPairingAsync(nonce);
        if (row is null) return new PairingPoll("unknown", null);
        if (row.State == "pending") return new PairingPoll("pending", null);
        if (row.State == "expired") return new PairingPoll("expired", null);
        if (row.State == "consumed") return new PairingPoll("consumed", null);
        // approved → deliver once, then burn. No token, no delivery: treat as consumed-empty
        // (defensive — approval always attaches a token).
        var token = row.TokenRaw;
        row.State = "consumed";
        row.TokenRaw = null;
        await _workers.UpdatePairingAsync(row);
        _logger.Information("pairing: nonce {Nonce} token delivered, nonce consumed", nonce);
        return new PairingPoll("approved", token);
    }
}
