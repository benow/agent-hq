# AgentHQ.Client

The Agent HQ hub-and-spoke wire contract (v1) as a NuGet package: the contract DTOs and the
typed spoke client. The hub implements the routes; any spoke — ZCode, builds, a third
party — consumes this package and nothing else. It references no other assembly:
contract-first, or it is not a contract.

## Quick start

```csharp
var client = new AgentHqClient(httpClient, bearerToken: pairedWorkerToken);

// register (idempotent — restarts keep the same workerId)
var reg = await client.RegisterAsync(new RegisterRequest(
    name: "laptop-spoke", machine: "laptop", version: "1.0.0",
    capabilities: [new Capability("plan-engine", repositories: ["agent-hq"])]));

// presence (every 300 s; serverVersion signals a self-update, part 4 pulls it)
var hb = await client.HeartbeatAsync(reg.workerId, "1.0.0");

// replication: ordered delta batches from the outbox cursor
var (ack, expected, status) = await client.SendEventsAsync(reg.workerId,
    new EventsBatch(fromOriginSeq: 41, events: [ /* EventDelta… */ ]));
// ack != null      → applied (a fully-duplicate batch trims to zero rows)
// expected != null → batch ahead of the hub cursor: rewind to `expected` and resend

// commands: react to `commands.pending` on GET /events (SSE), or long-poll when down
var commands = await client.ClaimCommandsAsync(reg.workerId, waitSeconds: 25);
foreach (var c in commands)
    await client.PostResultAsync(reg.workerId, new CommandResult(
        c.commandId, ok: true, null, AgentHqClient.ResultHash(true, null), DateTimeOffset.UtcNow));
```

## The contract, in one breath

- Every request carries `X-AgentHQ-Contract: 1` (added automatically).
- Events carry `(workerId, originSeq)`; the hub re-sequences into its global stream, keeps
  `actor` passthrough and records `transportedBy`.
- Commands are leased: claim ≤ 10 envelopes with a 60 s visibility lease; results are
  idempotent by `commandId` (duplicates ack with `duplicate:true`, divergent hashes warn).
- Heartbeats every 300 s; a worker renders offline after 900 s.
- `serverVersion` on the heartbeat is the self-update signal — hub-signaled, spoke-pulled.
- Pairing nonces bootstrap credentials: create → browser approves with an admin session →
  the poll delivers the worker token exactly once.

Pinned shapes live in `Dtos`; fixed values (lease, batch, intervals) in `AgentHqContract`.
Versioning: package semver = contract version; additive changes are minor, breaking changes
are a v2 route prefix with the hub accepting one major back.

Conformance: run `AgentHQ.Client.Conformance` against your hub to prove the contract.
