# 5 — the build spoke: build integration capability on builds

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15
Coordinates with: nastv builds-stack plan 4 — its mechanism steps are absorbed here
(see the supersession note in the overview); its classifier, views and Linda tooling are not.

## Goal

The build server stops being a dumb push client and becomes the second spoke: a resident
agent-hq application on builds declaring the **build integration capability** — CI event
ingest, build-estate health, and integration-run observation. Same spoke binary, same
contract, same `AgentHQ.Client`; a different capability set. Together with part 4 this is
the capability model proven: **one general client, two instances — dev on the laptop,
build on builds** — with the topology (which machine runs the hub) as configuration.

## Context

- nastv builds-stack plan 4 designed CI ingestion as a workflow step phoning the hub over
  the LAN with a shared secret, plus a hub-side "backfill-on-start" file scan. With a spoke
  resident on builds, both halves move into the spoke, next to their inputs:
  - The GitHub runners are on the same box → the `notify-agent-hq` step posts to the
    spoke's **loopback** ingest — no GH secret, no LAN dependency, and hub-down no longer
    means lost CI events (the outbox queues them; the annotation stays as the visible
    floor).
  - The nightly digest and exercise history (`docs/app/tasks/overnight-status.md`,
    `history.jsonl`) are files on builds — and the layering rule says the files are the
    record. A spoke reading its own machine's files is exactly the pattern: the hub stays
    file-blind everywhere, which is the point of the split.
- The hub keeps `POST /ci/events` (part 3) for clients with no co-located spoke; both
  transports share one payload model from the package.
- Depends on part 4's spoke host and forwarder — but not on anything ZCode-specific; that
  independence is this part's proof obligation (criterion 6).

## Decisions

- **Unit**: `agent-hq-spoke.service` on builds (`AGENTHQ_SPOKE_NAME=builds`,
  `AGENTHQ_HUB_URL` → the hub, `AGENTHQ_PORT=5318` for its loopback listener — the hub
  owns 5317). Deployed by the same deploy script's spoke mode; only the profile differs
  from the laptop's.
- **Capabilities declared**: `build-ci` (loopback ingest + forward) and `build-health`
  (checks + run observation). No `plan-engine` — no plan files live on builds.
- **CI ingest**: `POST /ci/events` on loopback, same payload model as the hub route;
  actor = the CI run (`ci:<workflow>/<run-id>`), transportedBy = the spoke. Enqueued to
  the outbox, forwarded in order, idempotent at the hub (contract rule 2).
- **Health checks — bounded and deterministic** (no monitoring framework, no
  summarisation): a declarative list in spoke config, each check producing
  `ok | warn | fail` plus a one-line detail:
  - units: the two GitHub Actions runner services are active;
  - containers: the compose stack's containers are up/healthy (docker socket, read-only);
  - disk: free space above threshold on the data volume;
  - freshness: digest file and `history.jsonl` modified within their expected windows;
  - last-run: outcome and age of the newest integration run.
  Cheap checks ride the heartbeat (30 s); freshness/last-run re-check per minute or on
  change. Only **changes** emit events (`build.health_changed`); the heartbeat carries the
  current snapshot; the hub renders a build-estate panel and a capability chip per spoke.
- **Integration runs**: tail `history.jsonl` (append-watched, with the debounce/hash
  discipline the plan watcher established), one run record per new line, plus
  **backfill on start** by reading what is already there — nastv plan 4's backfill step,
  homed where the file lives.
- **Command surface, deliberately tiny**: `health.check` (re-run checks now). Re-run
  triggers for suites or deploys are a future capability and a decision, not a default —
  the hub manages work, it never does it.
- No secrets on the loopback path; the spoke's own hub credential is a `worker` identity
  (part 2), held in its unit environment exactly like the dev spoke's.

## Steps

- [ ] Build capability module (CI ingest listener, outbox enqueue, forward) composed into
      the spoke host beside the dev module's seams; **the forwarder itself is untouched**
      (if it needs a change, the seam is wrong).
- [ ] Health-check engine + config schema; change-driven events; snapshot in heartbeat.
- [ ] `history.jsonl` tailing + backfill; digest freshness check.
- [ ] Hub: capability rendering — presence chips, build-estate panel fed by health + CI
      records; `build.health_changed` reaches the activity panel through the existing
      default channel (new event types must keep appearing without a UI change).
- [ ] nastv coordination: their `notify-agent-hq` step pointed at `localhost:5318`
      (URL-only change; `continue-on-error` stays); their plan 4's mechanism steps marked
      superseded by this part in their Status Log.
- [ ] Deploy script spoke profile for builds; unit install; ops notes.
- [ ] Tests: capability registration shape; loopback ingest → outbox → hub (E2E with the
      build spoke as a second spoke actor); hub-down → queue → drain, no loss; health
      checks against fake check sources; tail/backfill against a fixture `history.jsonl`;
      freshness boundary tests.

## Verification Criteria

1. A `notify-agent-hq` post to `localhost:5318` on builds appears as a hub record within
   seconds, with `ci:<workflow>/<run>` as actor and the spoke as transport.
2. Hub stopped → N CI posts queue; hub returns → drain in order, no loss, no duplication.
   Spoke stopped → the CI step's `continue-on-error` annotation is the visible floor.
3. Stopping a runner service flips the hub's build-estate panel within a minute, unit name
   in the detail; recovery flips it back; only transitions emitted (no churn).
4. Backfill: a fresh hub plus an existing `history.jsonl` reproduces the run history at
   the hub (spot-check rows against the file).
5. Presence: the builds spoke renders with `build-ci` + `build-health` chips and the
   laptop spoke with its dev capabilities — one model, two instances, visible as such.
6. The forwarder code is unchanged by this part (diff evidence) — capabilities extended
   the spoke; they did not fork it.

## Documentation

- `docs/features/distributed.md`: the capability table, the build spoke's checks and
  config, the loopback ingest contract.
- `docs/operations.md`: the builds spoke unit, env vars, check-list ownership.
- nastv builds-stack plan 4: supersession note in its Status Log.

## Status Log

- 2026-11-15 — drafted after Andy's capability-based-clients steer: abstract the build
  server as a spoke too — one general client, two instances (dev, build). Scope kept
  deliberately small: no monitoring framework, no re-run triggers; the data rail and a
  bounded, deterministic check list.
