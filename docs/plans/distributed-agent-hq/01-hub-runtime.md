# 1 — hub runtime: container + PostgreSQL sidecar on builds

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15

## Goal

The hub runs as a container on the builds Docker stack with a PostgreSQL sidecar, deployed
by the one deploy script, and **answers exactly as it does today** — same routes, same wire
shapes, same tests green. This part changes storage and packaging only; no contract, no
auth, no spokes. It is the behaviour-preserving re-platform that everything else lands on.

## Context

- Today's hub is SQLite + a systemd user unit on the laptop, hard-bound to
  `http://127.0.0.1:{port}` (`Program.cs:73`). Andy has decided: **PostgreSQL sidecar for
  the main hub**; spokes keep file-based SQLite (`00-OVERVIEW`).
- Two store layers therefore exist and they are **not shared SQL**. The spoke keeps today's
  SQLite stores untouched. The hub gets Postgres-native stores for hub-owned data (events,
  exchange, identities in part 2, workers/queue/projections in part 3). Row models move to a
  shared I/O-free assembly so both sides bind the same POCOs — the SQL never shares.
- Why Postgres is worth it here: the hub becomes multi-writer (CI ingest, two Lindas, spoke
  replication, command queue) and always-on; WAL-tuned SQLite would work but buys a
  ceiling we already know we'll hit. Decision is Andy's; the cost to manage is the test
  story, below.
- The live laptop hub **keeps serving** through this part. The containerized hub on builds
  binds loopback there (no LAN until part 2's auth lands). Nothing points at it yet.

## Decisions

- **Compose, not a fat container**: two services — `hub` (AgentHQ.Api image) and `db`
  (postgres:17-alpine, volume `agent-hq-pgdata`, healthcheck). Exchange blobs and any other
  filesystem state get a named volume; the "everything under `$AGENTHQ_DATA`" rule carries
  over as "everything in volumes named in the compose file".
- **DbUp stays; two migration sets.** The spoke keeps `Migrations/*.sql` (SQLite, existing
  journal). The hub gets `Migrations.Pg/*.sql` with its own journal — new schema, same
  tables to start (this part is a port), numbered from 0001. Never share one file between
  dialects.
- **Dapper stays.** Npgsql takes `@name` parameters too, but nothing else is assumed
  compatible: every hub store statement is ported deliberately (snake_case aliases, explicit
  `RETURNING` for inserts, `BIGSERIAL`/identity for event seq). The SQLite traps in
  `src/AgentHQ.Infrastructure/AGENTS.md` (case-sensitive names, aliasing) apply verbatim to
  Npgsql.
- **Bind becomes config**: `AGENTHQ_URL` (default `http://127.0.0.1:5317`) replaces the
  hard-coded loopback. Loopback stays the default; widening is part 6, after part 2.
- **One deploy path, extended not replaced**: `scripts/agent-hq-deploy.sh` gains a container
  mode — build the image (tag = git sha, as release names are today), `compose up`, wait on
  the health endpoint (seconds, not milliseconds — the existing readiness rule), run `check`.
  Used by hand and by CI exactly as today. The legacy unit-name refusal logic moves to the
  laptop-spoke unit name where it still applies.
- **Tests**: hub store and E2E tests need Postgres → Testcontainers (Docker on the runners
  is verified; laptop verified in step 0). `scripts/test.sh` keeps working without Docker by
  skipping Postgres-bound classes with a named reason (not silently). The SQLite-side tests
  (spoke stores, installer, parsers) always run.
- **Graceful stop**: container stop sends SIGTERM; the host already treats SIGINT/SIGTERM
  via `UseSystemd()` wiring — verify the drain window (unit used `TimeoutStopSec=10`;
  compose gets `stop_grace_period: 10s`).

## Steps

- [ ] `AGENTHQ_URL` binding in `Program.cs`; remove the hard-coded loopback; health and
      content-root behaviour unchanged.
- [ ] Move row models out of `AgentHQ.Infrastructure` stores into a shared I/O-free project
      (`AgentHQ.Models`) — pure POCOs, settable properties (Dapper's constructor binding
      rule still applies). Spoke stores re-bind; no behaviour change.
- [ ] Port hub stores to Npgsql + `Migrations.Pg` (repositories, plans, history, blocks,
      session mirror, events, exchange, lessons) — same shapes, this is a port not a
      redesign. Composition root selects store implementations by provider.
- [ ] `Dockerfile`: multi-stage, non-root user, `AgentHQ.Api` publish; content root is
      already `AppContext.BaseDirectory`, which is container-correct.
- [ ] `compose.yaml` in the repo: hub + db, volumes, healthcheck, `stop_grace_period`.
- [ ] Deploy script container mode (build, tag, up, health-wait, `check`).
- [ ] Stand it up on builds, loopback bind; smoke via `curl` on builds (`/health`,
      `/overview`).
- [ ] Test wiring: Testcontainers Postgres for hub-bound test classes; `scripts/test.sh`
      profile; CI (runners are on builds) runs the full set.
- [ ] Nightly `pg_dump` timer on builds to a dated folder on the data volume (ops note in
      `docs/operations.md`; hub-owned rows — exchange, identities — are the only data the
      spoke cannot rebuild, per the overview's rebuildability rule).
- [ ] Docs: `docs/operations.md` (container deploy, volumes, backup),
      `docs/installation.md` refresh, Infrastructure feature file.

## Verification Criteria

1. On builds: `compose up` → the hub's `/health` reports ready (first-sync-before-port
   semantics preserved) and `/overview` answers over loopback.
2. Full suite green against Postgres in CI (evidence: CI run link in the Status Log);
   without Docker, Postgres-bound classes skip with named reasons and the rest runs.
3. Stop/start preserves all data (volume); SIGTERM drains within the grace period and the
   process exits 0.
4. **Behaviour-preserving**: `WebUiContractTests` and the E2E simulation pass unchanged —
   same routes, same shapes, no test rewrites beyond the store provider.
5. The deploy script performs the whole path from a clean clone, and its `check` gate
   refuses a deploy that rolled back (existing rule: a rolled-back deploy is not a deploy).
6. The live laptop hub is untouched by this part (still serving, still SQLite) — verified by
   the pre-cutover smoke in part 6 having a baseline to compare against.

## Documentation

- `docs/operations.md`: container deploy, volumes, backup timer, compose location.
- `src/AgentHQ.Infrastructure/AGENTS.md`: two migration sets, provider selection, the
  Npgsql traps that carry over.
- `docs/README.md` links stay current.

## Status Log

- 2026-11-15 — drafted. Scope deliberately narrowed to a behaviour-preserving port after the
  architecture session: contract, auth and spoke work all land on top of this, so this part
  must not smuggle any of them in.
