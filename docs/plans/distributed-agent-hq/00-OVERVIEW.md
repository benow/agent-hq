# distributed-agent-hq — Agent HQ becomes hub-and-spoke: the hub on builds, spokes beside the agents

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
  (**nuget.benow.ca** — see step 0's reconcile).
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
                ┌──────────────────────────── builds (192.168.0.178) ─────────────────────┐
   NAS / LAN    │ ┌─────────────────────┐  ┌────────────┐  ┌──────────────────────────┐  │
   browsers ────┼►│ agent-hq hub        │◄►│ postgres   │  │ agent-hq spoke (build)   │  │
   Linda V2 ─MCP┼►│ API · UI · SSE · MCP│  │ sidecar    │◄─│ CI ingest (loopback) ·   │  │
                │ │ auth · exchange ·   │  └────────────┘  │ health · integration runs│  │
                │ │ command queue ·     │   events ▲       └────────────▲─────────────┘  │
                │ │ projections         │─────────┘        GitHub runners │ loopback 5318 │
                │ └─────────▲───────────┘                 (notify-agent-hq)               │
                └───────────│─────────────────────────────────────────────────────────────┘
                      events │ ▲ commands    ▲ MCP over the external edge (TLS, /mcp only)
                             │ │              │                   ┌──────────────────┐
                ┌────────────│─│──────┐       └───────────────────│ Linda V1 (cloud) │
                │ laptop     │ │ spoke│                           └──────────────────┘
                │ ┌──────────▼─┴────┐ │
                │ │ agent-hq spoke  │ │  SQLite working copy (today's DB)
   ZCode ◄─hook─│ │ (dev) engine ·  │ │  rollback = unset AGENTHQ_HUB_URL
   loopback ───►│ │ watchers ·outbox│ │
                │ └─────────────────┘ │
                └─────────────────────┘
```

The spoke is the same application in a different mode: with `AGENTHQ_HUB_URL` set it runs as
a spoke (engine + forwarder, no public surfaces); without it, it is exactly today's hub.
That symmetry is the rollback plan.

## Capabilities (the abstraction both spokes share)

A spoke is a **capability set, not a machine role**. Registration (part 3) declares typed
capabilities; the hub renders presence and routes commands by them:

| Capability | Provides | First instance |
|---|---|---|
| `plan-engine` | repositories owned, `/plan` execution, plan-file watching | laptop spoke (part 4) |
| `zcode-harness` | ZCode hook ingest, prompt-time injection, schema/plugin version | laptop spoke (part 6) |
| `build-ci` | loopback CI event ingest + forward (queues when the hub is down) | builds spoke (part 5) |
| `build-health` | runner/container/disk/freshness checks, integration-run observation + backfill | builds spoke (part 5) |

One general client, two instances at launch: **dev** (laptop) and **build** (builds). A
machine may hold several capabilities in one spoke; capabilities are additive. **Topology is
configuration, not code** — the same binary runs the hub (no `AGENTHQ_HUB_URL`), the dev
spoke, or the build spoke, and any spoke points at any hub. Hub-on-laptop remains a valid
topology; it is the rollback.

## Parts and gates

| Part | File | Gate | Depends on |
|---|---|---|---|
| 1 — Hub runtime: container + Postgres | [01-hub-runtime.md](01-hub-runtime.md) | hub serves identically from a container on builds (loopback) | — |
| 2 — Auth: identities and access points | [02-auth.md](02-auth.md) | headless MCP round trip; anonymous LAN request refused | 1 (parallel build; lands before any non-loopback bind) |
| 3 — Wire contract + `AgentHQ.Client` | [03-contract.md](03-contract.md) | contract conformance round-trip incl. no-loss/no-dup | 1 |
| 4 — The spoke (ZCode first) | [04-spoke.md](04-spoke.md) | replication + offline drain + local-first commands green in E2E | 3 |
| 5 — Build spoke (build integration) | [05-build-spoke.md](05-build-spoke.md) | CI events flow via the spoke; health + runs visible at the hub | 4 (spoke host, not its ZCode module) |
| 6 — Harness adapter (ZCode-side delivery) | [06-harness-adapter.md](06-harness-adapter.md) | plugin install; hook→spoke; injection round trip | 4 + merge prereq |
| 7 — Cutover | [07-cutover.md](07-cutover.md) | program criteria below, incl. both Linda probes | all |

**Gate rule:** a part is done when its own Verification Criteria pass, verified in a
*different session* than the one that implemented it (the repository's separation-of-duties
rule; the lifecycle itself is in
[docs/features/plan-lifecycle.md](../../features/plan-lifecycle.md)). Downstream work does
not start before the upstream gate. Gate 3 additionally requires the step-0 feed-hostname
reconcile to be resolved — part 3 publishes to the feed by name.

## How to work on this plan (for the implementing session)

- Read `docs/plans/lessons.md` first, and the feature `AGENTS.md` for every project you
  touch. Read the part file you are implementing end to end before starting.
- Tests run via `scripts/test.sh` (build once, filtered `--no-build` runs; never pipe test
  output). A CLR-abort with no test output is the known harness flake — re-run; CI retries
  once.
- Status moves only through `/plan` commands via `PlanService`; never hand-edit a plan
  status. Evidence goes in the part file's Status Log.
- Every schema change is a new DbUp migration (SQLite) or `Migrations.Pg` script (hub,
  part 1); never edit an applied one.
- Ports: the hub owns **5317**; the laptop spoke keeps **5317** on loopback (the hook and
  ZCode never re-target); the builds spoke takes **5318** (part 5).
- Neither Linda can reach the laptop today — that is expected, not a blocker; the V1
  reachability probe (part 2) is the feasibility gate for the external point.

## Steps

- [x] Merge `zcode/plan-workflow-support` (8636ad8 — the `/plan:*` colon pack nastv is
      already cut over to) and deploy. Everything here builds on that command path.
      (Already an ancestor of main; deployed release da17ca6 contains it.)
- [x] Verify Docker on the laptop (hub tests need Postgres; see part 1). The builds runners
      already have it (nastv builds-stack overview, 2026-11-15).
      (Docker 29.1.3 on Ubuntu 26.04, daemon reachable — 2026-11-15.)
- [x] Reconcile the private feed hostname: Andy says **registry.benow.ca**; nastv's
      `AGENTS.infrastructure.md` last recorded `nuget.benow.ca` (BaGet, `nastv-nuget`).
      Confirm which is live, update the nastv record — that doc also still lacks the builds
      fleet row (part 6 fixes both together if not sooner).
      (RESOLVED 2026-11-15 by probe: `nuget.benow.ca` serves the live NuGet v3 feed
      (`/v3/index.json` → BaGetter); `registry.benow.ca` is the **Docker** container
      registry (401 challenge on `/v2/`). The nastv record was already correct — no update
      needed. The feed for `AgentHQ.Client` is **nuget.benow.ca**; part 3 corrected.)
- [x] Multi-file plan accommodation (delivered 2026-11-15, with the workflow import):
      `PlanDirWatcher` watches `docs/plans/**/*.md` recursively; `POST /plans` with
      `filePath` adopts a drafted file without rewriting; a plan directory's aggregator row
      (alphabetically-first registered file — the `00-OVERVIEW.md` convention) aggregates
      progress and hash across its part files, and a part registered as its own plan leaves
      the umbrella's count; the `/plan:start` template documents the shape (pack v2).

## Program-level verification criteria

1. Hub answers on `192.168.0.178:5317` from the NAS (Linda V2) and the laptop with
   credentials; unauthenticated non-loopback requests are refused.
2. A `/plan` command issued on the laptop appears as an event on the hub within seconds,
   with the ORIGINAL session id as actor (spot-check the history row).
3. Laptop offline → hub stays fully usable (reads, CI ingestion, web UI, both Lindas);
   laptop back → queued events drain in order, no loss, no duplication.
4. A deploy run on builds produces its Agent HQ record within one minute (ties to
   builds-stack criterion 2, same evidence) — via the build spoke's loopback ingest once
   part 5 is live.
5. The web UI renders the plans table from a NAS-hosted browser at `192.168.0.178:5317`.
6. Stopping the hub does not affect ZCode on the laptop (`/plan` applies locally; the
   `|| true` hook guard was already the floor — now nothing queues behind a dead hub).
7. Linda V2 answers a PM question from `get_overview` + `get_events` alone (her captured
   transcript is the evidence). Linda V1 does the same through the external MCP point.
8. **Rebuildability**: wipe the hub's Postgres volume, restore identities from the last
   `pg_dump`, restart → the spoke's next registration rebuilds the spoke-replicated
   projections (plans, history, sessions, CI records) → the reads above are identical.
   The lens property, proven. **Scope note:** exchange blobs and identities are
   hub-owned and NOT spoke-rebuildable — criterion 8 is only claimable for the
   spoke-replicated reads, with identities restored from the nightly dump (part 1).
9. **Rollback drill**: unset `AGENTHQ_HUB_URL` on the laptop, restart → the laptop serves
   as the hub again from its intact SQLite, nothing lost.
10. **Build estate through the build spoke**: runner/stack health and integration runs are
    visible at the hub; with the hub stopped, CI events queue in the spoke and drain on
    return — `continue-on-error` is no longer the only safety.

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
phase 3 (hook injection — becomes part 6 here, retargeted at the spoke) and phase 5 (Linda's
route — becomes part 2 here, simplified to name/password + external point). Record the
supersession in that plan's Status Log when this umbrella is approved.

Absorbs from nastv's builds-stack plan 4 its **mechanism** steps: the notify step's target
becomes the build spoke's loopback ingest (part 5), and the hub-side ingestion/backfill work
becomes the spoke reading its own machine's record files. The signal/noise classifier, the
stability/attention views and Linda's tool surface remain nastv's. Record the supersession in
their plan's Status Log when part 5 is approved.

## Status Log

- 2026-11-15 — drafted by Linda (single file): central hub on builds, thin connector on the
  laptop, auth as precondition. Architecture settled in-session with Andy.
- 2026-11-15 — restructured into this umbrella in-session with Andy: container + Postgres
  sidecar, NuGet client on registry.benow.ca, smart spokes, name/password auth, V2-primary +
  V1-external audience, multi-file gates. The single-file draft (untracked at
  `docs/plans/distributed-hub.md` in the main checkout) is superseded by this directory —
  delete it when this lands.
- 2026-11-15 — REVIEWED (separate session, per the /plan:review gate). Findings, all
  addressed in the same pass:
  1. **Criterion 8 contradiction (fixed):** "wipe Postgres → reads identical" overclaimed —
     exchange blobs and identities are hub-owned and not spoke-rebuildable (part 1 says so).
     Criterion 8 now scopes to spoke-replicated projections with identities restored from
     the nightly `pg_dump`.
  2. **Wrong cross-reference (fixed in 01):** part 1 criterion 6 cited the "pre-cutover
     smoke in part 6"; the cutover part is 07.
  3. **Hidden gate (made explicit):** part 3 hardcodes registry.benow.ca but the hostname
     reconcile is a step-0 checkbox — now a stated precondition of part 3's gate.
  4. **Lesson not carried (fixed in 03):** the 2026-10-24 MCP lesson says the SDK forces
     camelCase *regardless of authored casing* — "plain property names" understated it;
     part 3 now says author and assert camelCase.
  5. **Junior onboarding gap (fixed):** the overview had no pointer to `lessons.md`,
     `scripts/test.sh` discipline, the CLR-abort retry rule, or the plan lifecycle — added
     a "How to work on this plan" section, including the port map (laptop spoke keeps 5317,
     builds spoke 5318, hub 5317 on builds) which was previously unspecified for the laptop
     hook target.
  6. **Inotify budget in part 4's E2E (noted in 04):** the spoke actor adds real watchers
     to the test host; part 4 now names the one-host-per-class fixture discipline.
  Verdict after fixes: **approved** — ready for `/plan implement` on part 1 (step-0 items
  first). The feed-hostname reconcile is an external fact-check, tracked as step 0, not a
  plan defect.
- 2026-11-15 — STEP 0 COMPLETE (implementing session). (1) Merge: 8636ad8 already an
  ancestor of main; deployed release da17ca6 contains the colon pack — no action. (2)
  Docker on the laptop verified: 29.1.3 on Ubuntu 26.04, daemon reachable. (3) Feed
  hostname reconciled by probe: `nuget.benow.ca` is the live NuGet v3 feed (BaGetter,
  `/v3/index.json` returns the resource index); `registry.benow.ca` is the Docker
  container registry (401 auth challenge on `/v2/`, Apache proxy — nastv
  `AGENTS.deploy.md:24`). Andy's recollection conflated the two; nastv's
  `AGENTS.infrastructure.md:136` record was already correct, so no update was needed
  there. Part 3's feed references corrected to nuget.benow.ca in this pass.
- 2026-11-16 — PART 1 IMPLEMENTED (branch zcode/distributed-hub-part1, commit f80e4c2;
  evidence in [01-hub-runtime.md](01-hub-runtime.md) Status Log). Container hub + Postgres
  sidecar live on builds at `builds:~/apps/agent-hq`, loopback-bound, healthy; nightly
  pg_dump timer installed (first dump verified). Full suite green; Postgres-bound tests
  6/6 via Testcontainers. Awaiting verification by a different session per the gate rule —
  parts 2+ do not start before that.
- 2026-11-16 — CI LANDS ON BUILDS (laptop retirement begins). Runner container online
  (agent-hq-builds), RUNNER_ENABLED=true; the deploy job drives `container up` — the hub's
  deploy target is the Docker stack on builds, not the laptop's systemd unit. First green run:
  https://github.com/benow/agent-hq/actions/runs/43797543767 (closes part 1 criterion 2's CI
  clause; evidence also in 01's Status Log). Part 1 now verified in a different session per
  the gate rule; parts 2–7 unblock in dependency order.
