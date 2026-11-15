# distributed-hub — Agent HQ becomes hub-and-spoke: the hub on builds, spokes beside the agents

Status: drafted · Umbrella plan · Started: 2026-11-15
Owner: Andy (decisions) · Drafted from: Linda's single-file draft (`docs/plans/distributed-hub.md`,
2026-11-15) plus the architecture session that superseded it

## Decision

Agent HQ stops being a monolith on a transient laptop and becomes a hub-and-spoke system:

- **The hub** — API, web UI, SSE, MCP tools, CI ingestion, exchange, the state database —
  runs on **builds (192.168.0.178)** in a **container, with a PostgreSQL sidecar**. Always-on,
  LAN-bound, authenticated. It is the lens: the one place state is visible from every machine
  that needs it.
- **Spokes** — small, sub-contained applications that live beside an agent implementation
  (the first: ZCode on the laptop) — keep everything physically coupled to the agents: the
  hook ingest on loopback, the ZCode provider, the file watchers, the plan engine, and a
  local SQLite working copy. Each spoke talks to the hub over a versioned HTTP API through a
  **shared NuGet client** (`AgentHQ.Client`) published to the private feed
  (**registry.benow.ca**).
- **Audience**: **Linda V2 (OpenClaw, internal network) is the primary user** — project
  management, verification, attention routing. **Linda V1 (Meta Muse, external)** gets the
  same MCP visibility through an explicit external access point. Andy uses the web UI and
  the API. Today neither Linda can reach the laptop's loopback-only bind; that is the
  problem this plan cleans up.

The layering is unchanged from the single-file draft and matches nastv's builds-stack guard:
**repository files remain the durable record; the hub is the lens; each spoke's SQLite is a
working copy.** A hub rebuilt from nothing re-syncs from its spokes and loses nothing that
matters.

## Why spokes are smart, not thin (the load-bearing decision)

The plan engine is file-coupled in both directions: Agent HQ writes plan files (`submit`
creates them, `PUT /content` rewrites them — `PlanService.cs:101,234`) and watches the ZCode
agent edit them (Status Log appends demanded by the command templates). Those files live in
working trees on the laptop. A thin forwarder would need the hub to parse and write files it
does not have — bidirectional file sync, the classic distributed tar pit.

So the spoke keeps the engine where the files are, and replicates **outcomes** upward:

- `/plan` commands apply locally and instantly — ZCode never waits on builds, the network,
  or the hub being up. The draft's criterion 6 (hub stopped ≠ ZCode affected) becomes true
  by construction, not by queue-drain.
- The spoke forwards state, not files-as-protocol: registration, a bulk snapshot, and
  origin-tagged event deltas. The hub stores projections and re-sequences them into its own
  cursor space; `get_overview` / `get_events(sinceSeq)` keep their exact turn-based shape.
- Actions that originate at the hub (a web-UI click, an MCP call by Linda) route back down
  as **queued commands**; the owning spoke claims one, executes it locally, reports the
  outcome. The worker is transport, never the actor — the original session id (or MCP
  identity) is passed through, so separation of duties keeps meaning what it means today.

## Architecture

```
                        ┌────────────────────────── builds (192.168.0.178) ─────────────────────┐
   NAS / LAN browsers   │  ┌─────────────────────┐        ┌──────────────┐                      │
   ─────────────────────┼─►│ agent-hq hub        │ ◄────► │ postgres     │  (docker compose)   │
   Linda V2 (OpenClaw)  │  │ API · UI · SSE · MCP│        │ sidecar      │                      │
   ─────── MCP ────────►│  │ auth · exchange ·   │        └──────────────┘                      │
                        │  │ CI ingest · command │                                              │
   GitHub runners ──────┼─►│ queue · projections │                                              │
   (notify-agent-hq)    │  └─────────▲───────────┘                                              │
                        └────────────│──────────────────────────────────────────────────────────┘
                              events │ ▲ commands        ▲ MCP over the external edge
                                     │ │                  │ (TLS, /mcp only)
                        ┌────────────│─│──────────┐       │      ┌──────────────────┐
                        │  laptop    │ │ spoke    │       └──────│ Linda V1 (cloud) │
                        │  ┌─────────▼─┴───────┐  │              └──────────────────┘
                        │  │ agent-hq spoke    │  │
   ZCode ◄──hook── loop │  │ engine · watchers │  │   SQLite working copy (today's DB)
   (commands, inject)──►│  │ provider · outbox │  │   rollback = unset AGENTHQ_HUB_URL
                        │  └───────────────────┘  │
                        └─────────────────────────┘
```

The spoke is the same application in a different mode: with `AGENTHQ_HUB_URL` set it runs as
a spoke (engine + forwarder, no public surfaces); without it, it is exactly today's hub.
That symmetry is the rollback plan.

## Parts and gates

| Part | File | Gate | Depends on |
|---|---|---|---|
| 1 — Hub runtime: container + Postgres | [01-hub-runtime.md](01-hub-runtime.md) | hub serves identically from a container on builds (loopback) | — |
| 2 — Auth: identities and access points | [02-auth.md](02-auth.md) | headless MCP round trip; anonymous LAN request refused | 1 (parallel build; lands before any non-loopback bind) |
| 3 — Wire contract + `AgentHQ.Client` | [03-contract.md](03-contract.md) | contract conformance round-trip incl. no-loss/no-dup | 1 |
| 4 — The spoke (ZCode first) | [04-spoke.md](04-spoke.md) | replication + offline drain + local-first commands green in E2E | 3 |
| 5 — Harness adapter (ZCode-side delivery) | [05-harness-adapter.md](05-harness-adapter.md) | plugin install; hook→spoke; injection round trip | 4 + merge prereq |
| 6 — Cutover | [06-cutover.md](06-cutover.md) | program criteria below, incl. both Linda probes | all |

**Gate rule:** a part is done when its own Verification Criteria pass, verified in a
*different session* than the one that implemented it (the repository's separation-of-duties
rule). Downstream work does not start before the upstream gate.

## Prerequisites (step 0)

- [ ] Merge `zcode/plan-workflow-support` (8636ad8 — the `/plan:*` colon pack nastv is
      already cut over to) and deploy. Everything here builds on that command path.
- [ ] Verify Docker on the laptop (hub tests need Postgres; see part 1). The builds runners
      already have it (nastv builds-stack overview, 2026-11-15).
- [ ] Reconcile the private feed hostname: Andy says **registry.benow.ca**; nastv's
      `AGENTS.infrastructure.md` last recorded `nuget.benow.ca` (BaGet, `nastv-nuget`).
      Confirm which is live, update the nastv record — that doc also still lacks the builds
      fleet row (part 6 fixes both together if not sooner).
- [ ] Enabling fix: `PlanDirWatcher` is non-recursive, so directory plans (this umbrella,
      and nastv's `builds-stack/`) are invisible to external-edit tracking. Make it watch
      `docs/plans/**/*.md` (skip double-counting the `done/` archive move the pending branch
      already handles) and add a test.

## Program-level verification criteria

1. Hub answers on `192.168.0.178:5317` from the NAS (Linda V2) and the laptop with
   credentials; unauthenticated non-loopback requests are refused.
2. A `/plan` command issued on the laptop appears as an event on the hub within seconds,
   with the ORIGINAL session id as actor (spot-check the history row).
3. Laptop offline → hub stays fully usable (reads, CI ingestion, web UI, both Lindas);
   laptop back → queued events drain in order, no loss, no duplication.
4. A deploy run on builds produces its Agent HQ record within one minute (ties to
   builds-stack criterion 2, same evidence).
5. The web UI renders the plans table from a NAS-hosted browser at `192.168.0.178:5317`.
6. Stopping the hub does not affect ZCode on the laptop (`/plan` applies locally; the
   `|| true` hook guard was already the floor — now nothing queues behind a dead hub).
7. Linda V2 answers a PM question from `get_overview` + `get_events` alone (her captured
   transcript is the evidence). Linda V1 does the same through the external MCP point.
8. **Rebuildability**: wipe the hub's Postgres volume → the spoke's next registration
   rebuilds the projections → the reads above are identical. The lens property, proven.
9. **Rollback drill**: unset `AGENTHQ_HUB_URL` on the laptop, restart → the laptop serves
   as the hub again from its intact SQLite, nothing lost.

## Invariants carried forward (the ones this plan must not break)

- The hub manages work; it never does it. Spokes execute; the hub coordinates and renders.
- Evidence lives in plan files and history rows; the hub's projections are derived state.
- One state machine: commands from hook, UI, MCP or the routing queue all apply through
  `PlanService` — on the spoke, where the files are.
- Turn-based-first: `get_overview` + `get_events(sinceSeq)` against the hub stay the whole
  picture; no tool requires remembering state between calls.
- No AI summarisation in the hub; digests stay deterministic.
- One deploy path: `scripts/agent-hq-deploy.sh` gains a container mode; no second way to
  ship. Naming rules apply unchanged (`AgentHQ.*` projects, `agent-hq-*` runtime).
- Widening exposure beyond the LAN (the V1 external point) is a recorded security decision,
  never a config default.

## Supersedes / deltas from the single-file draft

- systemd unit on builds → **container + PostgreSQL sidecar** (Andy, 2026-11-15 session).
- thin connector forwarding raw events → **smart spoke + versioned contract + NuGet client**
  (same session; the file-coupling rationale above).
- #110 two-credential brief → **simple name/password identities** (Andy: "name password is
  fine"); bearer for API/MCP clients, cookie for the browser, roles not scopes.
- "migrate the existing SQLite data from the laptop" → **first-registration bulk snapshot**;
  no data-copy step. The laptop's SQLite becomes the spoke's working copy in place.
- Added: Linda V1 external access point (MCP-only), the harness adapter part, multi-file
  gating.
- Kept from the draft: actor passthrough, offline queue/drain, LAN-only posture, plan-file
  freshness via the spoke (git stays the durable record, never the live path), auth before
  any bind.

Also supersedes, inside [conversation-enhancements-etc.md](../conversation-enhancements-etc.md):
phase 3 (hook injection — becomes part 5 here, retargeted at the spoke) and phase 5 (Linda's
route — becomes part 2 here, simplified to name/password + external point). Record the
supersession in that plan's Status Log when this umbrella is approved.

## Status Log

- 2026-11-15 — drafted by Linda (single file): central hub on builds, thin connector on the
  laptop, auth as precondition. Architecture settled in-session with Andy.
- 2026-11-15 — restructured into this umbrella in-session with Andy: container + Postgres
  sidecar, NuGet client on registry.benow.ca, smart spokes, name/password auth, V2-primary +
  V1-external audience, multi-file gates. The single-file draft (untracked at
  `docs/plans/distributed-hub.md` in the main checkout) is superseded by this directory —
  delete it when this lands.
