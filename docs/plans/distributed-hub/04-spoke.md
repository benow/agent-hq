# 4 — the spoke: the agent-side application (ZCode implementation first)

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15

## Goal

A small, sub-contained application that lives beside an agent implementation and keeps
everything physically coupled to it. The first spoke is ZCode on the laptop. It is the
**same application in a different mode**: with `AGENTHQ_HUB_URL` set, it runs as a spoke —
engine, watchers, provider and hook ingest stay live on loopback, public surfaces (UI, MCP,
REST reads) turn off, and a forwarder replicates outcomes to the hub and executes routed
commands. Without the variable it is exactly today's hub — which is the rollback plan.

## Context

- Why the engine stays here (the load-bearing rationale, from the overview): plan files are
  written by both sides — `PlanService` on submit/content, the ZCode agent on Status Log
  appends — and they live in laptop working trees. The spoke applies `/plan` commands
  locally and instantly; ZCode never waits on the network.
- The spoke keeps the local SQLite working copy (today's database, in place — no migration
  at cutover, which is why the overview dropped the draft's data-copy step), the ZCode
  provider (`AGENTHQ_ZCODE_DB` explicit path — the never-fall-back rule carries over), both
  watchers, lessons indexing, and the hook endpoints on loopback with the unchanged
  contract.
- Projects: the spoke host reuses `AgentHQ.Infrastructure`, `AgentHQ.Providers.ZCode`,
  `AgentHQ.Commands.ZCode`. New: the spoke-mode wiring and forwarder live with the host
  composition (one composition root — the mode is options, not a second host). A future
  non-ZCode spoke would compose `AgentHQ.Client` + its own provider; nothing in the
  forwarder knows ZCode.
- Runtime identity: the laptop unit becomes `agent-hq-spoke.service` (deploy script's
  laptop mode); env `AGENTHQ_SPOKE_NAME` (default the hostname), `AGENTHQ_HUB_URL`,
  `AGENTHQ_WORKTREES_ROOT` (from the merged branch).

## The forwarder

- **Outbox**: a table in the local SQLite. Everything the spoke wants the hub to know is
  an outbox row first — appended in the same logical step as the local change it mirrors
  (event append hooks the outbox enqueue; a crash between the two is closed by the startup
  reconcile below). Drain in `originSeq` order, batched, against the contract's
  idempotence key; on reject-with-`nextExpected`, rewind to that cursor.
- **Registration + snapshot**: on start (and when the hub says it has no state for this
  worker — the rebuild case), register, then send the bulk snapshot. Heartbeat every 30 s;
  the hub's presence rule (offline after 600 s) renders spokes in the UI's presence strip.
- **Commands**: a long-poll claim loop (the contract's lease). Executed locally through
  `PlanService` — the same one path — with the envelope's `actor` as attribution. Result
  posted back; the resulting local events replicate upward like any others, so the UI's
  outcome arrives via the normal stream.
- **Injection proxy**: when part 5's digest/injection asks the spoke, the spoke answers
  from local state where it can (plan status) and proxies the hub-owned part (exchange
  inbox) with a short timeout and a cached last-good fallback — never blocking, never
  injecting stale-as-fact (the cache carries its age).
- **Degradation matrix** (each row is a test):
  - hub down → `/plan` applies locally; outbox grows; heartbeat retries with backoff;
    nothing errors into the hook path (`|| true` floor kept).
  - hub up, laptop was offline → ordered drain, no loss, no duplication.
  - ZCode down → spoke idles; presence stays honest via heartbeat.
  - hub wiped → registration + snapshot rebuild it (the overview's criterion 8).
- **Startup reconcile**: on boot, compare local cursor vs hub `lastAcceptedSeq`; snapshot
  if the hub is empty, drain otherwise. Never assume.

## What spoke mode turns off

Public surfaces move to the hub — one truth. In spoke mode: no web UI, no MCP, no REST
reads beyond `/health` and the hook endpoints. The SQLite file remains inspectable
directly. Rollback (hub mode) restores everything, unchanged.

## Steps

- [ ] Mode selection in the composition root (`AGENTHQ_HUB_URL` present → spoke services
      registered, public endpoints withheld); one composition root preserved — the E2E
      host exercises both modes.
- [ ] Outbox table (SQLite migration), event-append hook, startup reconcile, drain loop
      with backoff, cursor persistence.
- [ ] Registration/heartbeat/snapshot/command loop on `AgentHQ.Client`; presence surfaced
      to the hub UI.
- [ ] Command execution path with actor passthrough; idempotence for redelivered commands.
- [ ] Injection proxy (skeleton now; part 5 builds the digest on it).
- [ ] Laptop unit rename to `agent-hq-spoke.service` (deploy script laptop mode; legacy
      refusal logic updated per the naming rules).
- [ ] E2E: the simulation gains a spoke actor — in-memory hub (Postgres) + real spoke
      composition + synthetic ZCode db + hook-fed commands; the kill-mid-drain,
      hub-wiped, and offline-drain scenarios from the matrix above.
- [ ] Docs: `docs/features/distributed.md` (spoke side), `docs/operations.md` (laptop
      unit, rollback drill), Infrastructure + Tests feature files.

## Verification Criteria

1. Local-first: with the hub stopped, a `/plan start` through the hook applies locally,
   the file changes, history records the ORIGINAL session id; on hub return the event
   drains exactly once (row counts on both sides as evidence).
2. Offline drain: N events queued while offline drain in order; hub row count and content
   match; a duplicate batch is a no-op.
3. Command routing: a hub-originated command (UI action / MCP call) executes on the spoke
   with the initiator as actor; the outcome event flows back within seconds; a redelivered
   command after a simulated crash does not double-apply.
4. Snapshot rebuild: a fresh hub + existing spoke → registrations + snapshot reproduce the
   plan table, history and sessions identical to the spoke's local reads.
5. Presence: killing the spoke flips the hub UI to offline after the 600 s rule; restart
   flips it back.
6. Mode symmetry: the same binary in hub mode passes today's full suite unchanged
   (rollback credibility, not just prose).
7. The E2E simulation with the spoke actor is green in CI.

## Documentation

- `docs/features/distributed.md`: spoke behaviour, degradation matrix, rollback.
- `docs/operations.md`: the laptop spoke unit, env vars, rollback drill steps.
- `src/AgentHQ.Api/AGENTS.md`: mode selection in the composition root; `src/AgentHQ.Infrastructure/AGENTS.md`: outbox table.

## Status Log

- 2026-11-15 — drafted. The same-binary mode switch (vs a separate spoke project) chosen so
  the E2E exercises the real composition and rollback is a restart — the alternative bought
  a second host to keep in sync and nothing else.
