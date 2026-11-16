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

- [x] `AGENTHQ_URL` binding in `Program.cs`; remove the hard-coded loopback; health and
      content-root behaviour unchanged.
- [x] Move row models out of `AgentHQ.Infrastructure` stores into a shared I/O-free project
      (`AgentHQ.Models`) — pure POCOs, settable properties (Dapper's constructor binding
      rule still applies). Spoke stores re-bind; no behaviour change.
- [x] Port hub stores to Npgsql + `Migrations.Pg` (repositories, plans, history, blocks,
      session mirror, events, exchange, lessons) — same shapes, this is a port not a
      redesign. Composition root selects store implementations by provider.
- [x] `Dockerfile`: multi-stage, non-root user, `AgentHQ.Api` publish; content root is
      already `AppContext.BaseDirectory`, which is container-correct.
- [x] `compose.yaml` in the repo: hub + db, volumes, healthcheck, `stop_grace_period`.
- [x] Deploy script container mode (build, tag, up, health-wait, `check`).
- [x] Stand it up on builds, loopback bind; smoke via `curl` on builds (`/health`,
      `/overview`).
- [x] Test wiring: Testcontainers Postgres for hub-bound test classes; `scripts/test.sh`
      profile; CI (runners are on builds) runs the full set.
- [x] Nightly `pg_dump` timer on builds to a dated folder on the data volume (ops note in
      `docs/operations.md`; hub-owned rows — exchange, identities — are the only data the
      spoke cannot rebuild, per the overview's rebuildability rule).
- [x] Docs: `docs/operations.md` (container deploy, volumes, backup),
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
   the pre-cutover smoke in part 7 having a baseline to compare against.

## Documentation

- `docs/operations.md`: container deploy, volumes, backup timer, compose location.
- `src/AgentHQ.Infrastructure/AGENTS.md`: two migration sets, provider selection, the
  Npgsql traps that carry over.
- `docs/README.md` links stay current.

## Status Log

- 2026-11-15 — drafted. Scope deliberately narrowed to a behaviour-preserving port after the
  architecture session: contract, auth and spoke work all land on top of this, so this part
  must not smuggle any of them in.

- 2026-11-15/16 — IMPLEMENTED (session `zcode/distributed-hub-part1`, branch
  zcode/distributed-hub-part1, commit f80e4c2). Evidence against the Verification Criteria:
  1. **On builds**: fresh clone at `builds:~/apps/agent-hq` (pushed from the laptop via a
     bare repo `~/apps/agent-hq.git`) → `scripts/agent-hq-deploy.sh container up` → hub
     healthy after ~15 s of container runtime (image build was the bulk of 105 s);
     `/health` `{"status":"ok",…}` and `/overview` answer over loopback
     `127.0.0.1:5317`; provider degrades to `missing` inside the container by design
     (no ZCode db there — spokes arrive in part 4). All 13 tables + the DbUp
     `AgentHqPgSchemaVersions` journal verified via `psql`.
  2. **Suite**: PostgresHubTests (Testcontainers postgres:17-alpine, fresh database per
     test) 6/6 green locally: lifecycle round trip (start→review→implement through the real
     hook feed), dependencies/blocks/content-CAS, event seq ordering, sessions upsert,
     exchange round trip. Full SQLite suite green (181 tests; WebUiContractTests and the E2E
     simulation pass unchanged — only store *types* moved to interfaces). **CI evidence
     deferred**: the workflow is runner-gated (`RUNNER_ENABLED` unset), so there is no CI run
     link yet; the named-skip-without-Docker path is the `PostgresFixture` SkipException.
  3. **Stop/start**: `compose stop hub` → container `exit=0, oom=false` (SIGTERM drained
     within the 10 s grace period); `up` again → the repository registered inside the
     `/data` volume survived (criterion's data-preservation clause).
  4. **Behaviour-preserving**: no test rewrites beyond swapping concrete store types for
     interfaces; WebUiContractTests untouched and green.
  5. **Deploy path**: performed from a clean clone on builds; `container check` gates on
     health and `container up` rolls back (`compose down`, volumes kept) + exits non-zero on
     a failed health wait. The rollback path itself was not exercised with a deliberately
     broken build — code-reviewed only; the next deploy gets the chance.
  6. **Laptop hub untouched**: still systemd + SQLite (release da17ca6), the container
     experiment ran on builds only, and the scratch compose project name was never brought
     up on the laptop (port 5317 is the live hub's; the local docker build was image-only).
  - Environment notes: builds has Docker 29.9.3 + Compose v5.7.1; linger enabled for the
    backup timer; first `pg_dump` produced `backups/20261116/agent-hq-pg.sql.gz` (3.1 KB).

- 2026-11-15 — VERIFIED (independent verification session, branch zcode/distributed-hub-part1
  at 4f9862c). Each criterion re-proved first-hand, not taken from the record:
  1. Builds stack live and healthy: `agent-hq-hub-1` / `agent-hq-db-1` both `healthy`; 13
     public tables + 1 `AgentHqPgSchemaVersions` journal row via `psql`; `/overview` answers
     over loopback with the registered `/data/demo` repository.
  2. Full suite re-run locally with Docker up: **183 passed, 4 skipped, 0 failed** (skips are
     the env-gated `AGENTHQ_LIVE` live smoke tests, named as such); `PostgresHubTests` re-run
     in isolation against Testcontainers postgres:17-alpine: **6/6 green**. CI run link still
     outstanding — runner-gated (`RUNNER_ENABLED` unset); suite evidence is local, recorded
     above.
  3. Stop/start re-exercised live on builds: `docker stop` → `exit=0, oom=false`; `start` →
     healthy again; `/overview` sha256-identical before/after (volume persistence).
  4. Behaviour-preserving confirmed by diff: since `da17ca6` the only test-file changes are
     store-type/interface swaps plus the new `PostgresHubTests`; `WebUiContractTests` differs
     by exactly one line (`EventStore` → `IEventStore`), E2E simulation untouched.
  5. **The unexercised rollback path was exercised** on builds: `container up` with a wrong
     `AGENTHQ_PG_PASSWORD` → health wait failed within the deadline → `compose down` (db,
     hub, network removed; volumes kept) → script exit 1. Clean `container up` after →
     healthy in 9 s, image tag `f80e4c2`, the repository row survived the rollback. Note for
     the operator: the script's `:?` guard needs `AGENTHQ_PG_PASSWORD` exported in the shell
     (compose's `.env` does not satisfy it) — `set -a; . ./.env; set +a`.
  6. Laptop hub untouched: `agent-hq.service` active, provider schema 25, still on
     SQLite + loopback 5317.
  Residual: criterion 2's CI run link remains deferred as recorded above; everything else
  has first-hand evidence.
  - Deviations: (a) `ExchangeService` took an `IAgentHqConnectionFactory` instead of a raw
    `AgentHqDb` so its transaction path is provider-neutral (needed for criterion 2's
    PostgreSQL suite); (b) Dapper's automatic `IN @list` expansion is incompatible with
    Npgsql — the Pg stores expand lists explicitly (recorded in the Infrastructure AGENTS.md);
    (c) the plan's "Move row models … Spoke stores re-bind" was done as `AgentHQ.Models`
    with a reference to Core (for `ExchangeItemRow.ToModel()`); Core does not reference
    Models, so the layering stays acyclic. Transitive dependency note: restore surfaces
    NU1903 (high-severity advisory) against SQLitePCLRaw 2.1.11 via Microsoft.Data.Sqlite
    10.0.0 — pre-existing, flagged for a dedicated dependency-commit fix, not mixed into
    this one.
- 2026-11-16 — VERIFIED + CI evidence added. Criterion 2's deferred clause is now closed:
  full suite green on the builds runner with Docker available, Postgres-bound tests included —
  run https://github.com/benow/agent-hq/actions/runs/43797543767 (build-and-test + deploy,
  both success). The same run performed the first CI-driven container deploy: image
  agent-hq:6d938f8 built from the push, stack composed up, health-wait and container check
  green; the live hub on builds now runs that image. En route, two deploy-script defects were
  found and fixed (health probe must go via compose exec — the CI runner container's loopback
  is not the host's; `container <action>` read the action from the wrong positional after
  main's shift). The workflow itself had never executed on GitHub — every run since 2026-10-31
  failed at 0s because `runs-on` cannot use the `env` context; fixed (actionlint) and the
  runner (container `agent-hq-runner` on builds, socket-mounted, nastv NAS-runner pattern with
  DISABLE_AUTO_UPDATE=1) is online with repo variables RUNNER_ENABLED=true,
  RUNNER_LABELS=["self-hosted","agent-hq"].
