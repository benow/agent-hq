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
  composition (one composition root — the mode is options, not a second host). Nothing in
  the forwarder knows ZCode — part 5's build spoke composes the same host with
  `AgentHQ.Client` + build capabilities, and its existence is the proof of the seam.
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
  worker — the rebuild case), register, then send the bulk snapshot. Heartbeat every
  **300 s** (contract rule 8; the hub renders offline after 900 s — three missed beats).
- **The down-link is SSE-first** (contract rule 0): one `GET /events` stream with the
  worker token; `commands.pending` triggers an immediate claim, `release.available`
  triggers the self-update check below. `Last-Event-ID` reconnect replays from the global
  cursor. While the stream is healthy there is **no polling at all**; when it drops, the
  spoke resumes the long-poll claim loop (`wait=25`) and retries SSE with backoff.
- **Commands**: claimed through the leased route (SSE only makes it reactive). Executed
  locally through `PlanService` — the same one path — with the envelope's `actor` as
  attribution. Result posted back; the resulting local events replicate upward like any
  others, so the UI's outcome arrives via the normal stream.
- **Self-update** (contract rule 9): on `release.available` (or a heartbeat
  `serverVersion` mismatch), download the release into a releases directory next to the
  install, health-check it (starts, passes its own `/health`, contract version accepted),
  flip the `current` symlink, restart the unit via systemd, verify health; on any failure
  flip back, restart the previous release, and record an `update.rolled_back` event to the
  hub. The deploy script's release discipline ("a rolled-back deploy is not a deploy")
  applies verbatim to the spoke's own updates.
- **Injection proxy**: when part 6's digest/injection asks the spoke, the spoke answers
  from local state where it can (plan status) and proxies the hub-owned part (exchange
  inbox) with a short timeout and a cached last-good fallback — never blocking, never
  injecting stale-as-fact (the cache carries its age).
- **Degradation matrix** (each row is a test):
  - hub down → `/plan` applies locally; outbox grows; heartbeat retries with backoff;
    nothing errors into the hook path (`|| true` floor kept).
  - hub up, laptop was offline → ordered drain, no loss, no duplication.
  - SSE drops, hub up → claim falls back to long-poll mid-flight; no command lost;
    stream reconnects with replay and polling stops again.
  - bad update → health-check fails → previous release restored, `update.rolled_back`
    visible at the hub; the spoke's version now matches the hub's last-known-good.
  - ZCode down → spoke idles; presence stays honest via heartbeat.
  - hub wiped → registration + snapshot rebuild it (the overview's criterion 8).
- **Startup reconcile**: on boot, compare local cursor vs hub `lastAcceptedSeq`; snapshot
  if the hub is empty, drain otherwise. Never assume.
- **The lazy `/plan` gate** (the harness's only network knowledge is loopback — part 6):
  spoke absent on loopback → the hook/command short-circuits and emits the install
  guidance line, once per session, and posts nothing; spoke present but the hub is
  unreachable → the hook still succeeds (it only saw loopback) and the spoke surfaces
  the outage via its own presence, not by failing prompting. Configured-but-unreachable
  is **visible**, never silent (Andy's rule: system stability errors must show).

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
- [ ] Registration/heartbeat (300 s)/snapshot loop on `AgentHQ.Client`; SSE-first
      down-link with long-poll fallback and `Last-Event-ID` reconnect; presence surfaced
      to the hub UI.
- [ ] Command execution path with actor passthrough; idempotence for redelivered commands.
- [ ] Self-update: releases directory, health-check, `current` symlink flip, systemd
      restart, rollback + `update.rolled_back` event; the degradation-matrix row tested.
- [ ] Injection proxy (skeleton now; part 6 builds the digest on it).
- [ ] Laptop unit rename to `agent-hq-spoke.service` (deploy script laptop mode; legacy
      refusal logic updated per the naming rules).
- [ ] E2E: the simulation gains a spoke actor — in-memory hub (Postgres) + real spoke
      composition + synthetic ZCode db + hook-fed commands; the kill-mid-drain,
      hub-wiped, and offline-drain scenarios from the matrix above. Keep the fixture
      discipline from `tests/AgentHQ.Tests/AGENTS.md` (one host per class, one repo per
      test, time-boxed polls): the spoke actor adds real watchers and the inotify budget
      here is 128 — do not add per-test hosts.
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
5. Presence: killing the spoke flips the hub UI to offline after the 900 s rule
   (three missed 300 s beats); restart flips it back.
6. Mode symmetry: the same binary in hub mode passes today's full suite unchanged
   (rollback credibility, not just prose).
7. The E2E simulation with the spoke actor is green in CI.
8. SSE-first transport: with the stream healthy, a hub-originated command executes on the
   spoke within ~1 s of enqueue (E2E timestamps); killing the stream mid-flight loses no
   command and the fallback long-poll claims it; reconnect replays nothing seen.
9. Self-update: a simulated new release installs via symlink flip and restart; a release
   that fails its health-check is rolled back to the previous release with an
   `update.rolled_back` event visible at the hub; the spoke's service ends healthy in
   both cases.
10. Lazy gate: with no spoke on loopback, a `/plan` command emits the install guidance
    and writes nothing anywhere; with the spoke up and the hub stopped, prompting is
    never blocked and the outage is visible on the spoke's presence, not in the hook.
11. Actor passthrough to history: a plan command forwarded from the hub writes the synced
    history row naming the ORIGINAL session id / identity as `actor`, with the worker
    recorded as `transportedBy` — spot-check query recorded as evidence. *(Moved here
    2026-11-22 from part 3's criterion 6: the hub never applies a command, so this is the
    first part where such a history row exists.)*

## Documentation

- `docs/features/distributed.md`: spoke behaviour, degradation matrix, rollback.
- `docs/operations.md`: the laptop spoke unit, env vars, rollback drill steps.
- `src/AgentHQ.Api/AGENTS.md`: mode selection in the composition root; `src/AgentHQ.Infrastructure/AGENTS.md`: outbox table.

## Status Log

- 2026-11-15 — drafted. The same-binary mode switch (vs a separate spoke project) chosen so
  the E2E exercises the real composition and rollback is a restart — the alternative bought
  a second host to keep in sync and nothing else.

- 2026-11-22 — AMENDED in the design session with Andy. Transport moved to **SSE-first**
  (his call, matching the 03 amendment) with the long-poll demoted to the SSE-down
  fallback; heartbeats 300 s against the 900 s presence rule. Added the **self-update
  loop** — hub-signaled via heartbeat `serverVersion` / `release.available`, spoke pulls,
  health-check + symlink flip + restart, rollback reports `update.rolled_back` — reusing
  the deploy script's release discipline rather than inventing an updater. Added the
  **lazy `/plan` gate** semantics: unconfigured machine → guidance emit, nothing posted;
  configured-but-unreachable hub → prompting never blocked but the outage is *visible*
  (Andy: stability errors must not be silent). No repository-tree configuration exists in
  this design — participation is hub-side registration plus the machine-level spoke,
  settled in the same session.

- 2026-11-22 — Criterion 11 added, moved from part 3's criterion 6 during part 3's
  verification. Part 3 merges events and stores projections but never applies a command,
  so "a history row written via a forwarded command" had no path there; the history-row
  actor check belongs to the part that first executes a forwarded command. No other change
  to this plan.
