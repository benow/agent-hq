# 3 — the wire contract and `AgentHQ.Client` (shared NuGet)

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15

## Goal

One versioned contract between hub and spokes, shipped as a maintained NuGet package —
**`AgentHQ.Client`** on the private feed (registry.benow.ca, per step 0's reconcile). The
hub implements it; the spoke consumes it; any future spoke (a builds-local worker, a NAS
worker, a third party) gets the same package and nothing else. Server code stays on the
server. This part is the heart of the architecture: registration, bulk snapshot, delta
replication with actor passthrough, and the command queue.

## Context

- Andy's rule: **anything shared between hub and spoke is NuGet.** The package carries the
  contract models, the typed client, and a conformance test kit (below). It must not
  reference server assemblies — contract-first, or it isn't a contract.
- The turn-based invariant survives here: replication makes the hub's merged event stream
  the single cursor space; `get_events(sinceSeq)` never requires a spoke to be online.
- Sessions are read-through today; in the split the spoke's mirror replicates upward and
  the hub holds a projection. Message content still never crosses — metadata for joins and
  events only, per the standing invariant.
- The CI ingest endpoint rides the same API surface (the nastv `notify-agent-hq` step is a
  curl client of it — it does not need the package, but its payload is versioned here so
  the contract has one home).

## The contract

All endpoints under the hub's API, authenticated per part 2 (`worker` role for spoke
routes, `ci` for ingest). JSON policy as the host's: camelCase, nulls omitted. Plain
property names (the MCP rule applies to anything the SDK might serialize).

| Route | Direction | Purpose |
|---|---|---|
| `POST /workers/register` | spoke → hub | `{name, machine, version, capabilities[]}` → `{workerId, serverTime, contractVersion}` — capabilities are typed (rule 7) |
| `POST /workers/{id}/heartbeat` | spoke → hub | presence; → `{lastAcceptedSeq, commandsPending}` |
| `POST /workers/{id}/snapshot` | spoke → hub | full state (below); hub replaces its projections for that worker |
| `POST /workers/{id}/events` | spoke → hub | ordered batch of deltas; → `{accepted, globalSeqFrom, nextExpected}` |
| `GET /workers/{id}/commands?wait=25` | hub → spoke | long-poll claim of queued envelopes (lease) |
| `POST /workers/{id}/commands/{commandId}/result` | spoke → hub | outcome of one command (idempotent by commandId) |
| `POST /ci/events` | CI → hub | the `notify-agent-hq` payload, one model in the package; the build spoke (part 5) serves the same ingest on its own loopback for co-located runners |

Rules that are the contract:

1. **Actor passthrough.** Every event and command envelope carries `actor` (the original
   session id, an identity like `linda-v2` / `andy`, or a CI run like
   `ci:deploy.yml/4412`) plus `transportedBy` (the worker).
   History rows record `actor`; the worker is never the actor. Separation of duties must
   mean the same thing after the hop as before.
2. **Idempotence and order.** Events carry `(workerId, originSeq)` — a unique key on the
   hub; duplicates are acknowledged, not applied. A batch whose first `originSeq` ≠ the
   hub's `nextExpected` is rejected with the expected value; the spoke rewinds and resends
   from its cursor. No buffering of gaps server-side — resend is the simpler protocol.
3. **Hub re-sequencing.** The hub assigns its own global `seq` on accept (the merged
   stream); origin ids and timestamps are preserved as payload fields. All existing cursors
   (`?sinceSeq=`, MCP `get_events`, SSE replay) work against the global space unchanged.
4. **Commands are leased.** Claim returns up to N envelopes with a 60 s visibility lease;
   expiry redelivers. Results are idempotent by `commandId`; a late duplicate result after
   redelivery must not double-apply (the spoke's local idempotence via `PlanService` plus
   the result's outcome hash covers this; test it).
5. **Snapshot shape**: repositories, plans (rows **and markdown** — the hub serves plan
   content to UI/MCP from the projection), plan history, session mirror rows, lessons,
   and the tail of the spoke's event log (configurable depth; default enough to replay a
   day). The hub replaces-that-worker's-projections transactionally.
6. **Versioning.** Package semver = contract version; requests carry
   `X-AgentHQ-Contract: 1`. Additive changes are minor; breaking changes are a v2 route
   prefix, and the hub accepts one major back. A mismatched header gets a 400 naming both
   versions (errors say what, why and what to do).
7. **Capabilities are typed, not strings.** Each registration entry is `{kind, …fields}`;
   initial kinds: `plan-engine` (repositories owned), `zcode-harness` (schema/plugin
   version), `build-ci` (loopback ingest), `build-health` (check list). The hub renders
   presence by capability and routes commands by repository ownership (a plan command goes
   to the `plan-engine` holder) or by kind (a `health.check` to a `build-health` holder).
   One spoke may hold several kinds; capabilities are additive.

## The package

- **Contents**: `AgentHQ.Client` (models + typed `HttpClient` — register, heartbeat,
  snapshot, sendEvents, claimCommands, postResult, health; retries with exponential
  backoff + jitter; per-stream ordered sends) and `AgentHQ.Client.Conformance` (the test
  kit: a fixture any hub implementation runs to prove the contract — the hub's own test
  suite runs it, and a future third-party hub could too).
- **Publishing**: pack + push to registry.benow.ca from CI on tag (`v*`); branch builds get
  prerelease suffixes. `nuget.config` in-repo points at the feed; credentials via the
  existing CI secret convention. Dependency rule: the package references nothing from this
  solution except the shared models assembly — and even that is repackaged, not projected,
  so consumers see one DLL.
- The **hub implements** the routes in `AgentHQ.Api` behind part 2's roles; the logic lives
  in services (the MCP thin-wrapper rule generalizes: REST, MCP and the spoke routes all
  sit on the same services).

## Steps

- [ ] Contract models + routes table above, versioned header, error taxonomy.
- [ ] Hub side: workers/queue/projections tables (Postgres, `Migrations.Pg`), the ingest
      services, re-sequencing into the existing event stream, snapshot application.
- [ ] CI ingest endpoint + payload shapes (shared versioning home; nastv implements the
      sender side under its builds-stack plan 4).
- [ ] `AgentHQ.Client` + conformance kit; pack/push pipeline; `nuget.config`.
- [ ] Conformance + E2E: the in-memory hub runs the kit; an E2E actor plays a fake spoke
      (register → snapshot → events → command claim → result), including kill-mid-drain.
- [ ] Docs: `docs/features/distributed.md` (new page: contract, flows, degradation), the
      package's README, link from `docs/README.md`.

## Verification Criteria

1. A throwaway console project restores `AgentHQ.Client` from registry.benow.ca and
   completes register → heartbeat → snapshot → events → command round-trip against a hub
   (evidence: its transcript in the Status Log).
2. The conformance kit passes against the real hub composition in CI.
3. Duplicate event batch → acknowledged, applied once (row counts prove it). Gap batch →
   rejected with `nextExpected`; the spoke's resend recovers; no loss, no duplication
   across a kill-mid-drain E2E run.
4. Merged stream: events from two workers interleave with hub-origin events under one
   global `seq`; `?sinceSeq=` replay and MCP `get_events` return a consistent merged history.
5. Command lease: an un-claimed (crashed) command redelivers after expiry; a duplicate
   result does not double-apply (history has one row).
6. Actor passthrough: a history row written via a forwarded command names the original
   session id, `transportedBy` names the worker — spot-check query recorded as evidence.
7. Contract mismatch: a v2-headed request against a v1 hub → 400 naming both versions.

## Documentation

- `docs/features/distributed.md`: the contract, flows, idempotence/order rules, degradation.
- `AGENTS.md` (hub): the "REST and MCP stay in sync by construction" rule gains "and the
  spoke routes sit on the same services".
- Package README on the feed page.

## Status Log

- 2026-11-15 — drafted. The reject-and-resend ordering rule (no server-side gap buffering)
  chosen over the alternative for simplicity — resend from cursor is bounded by the outbox
  and needs no hub-side reorder logic.
