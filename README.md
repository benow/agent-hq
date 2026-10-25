# Agent HQ

A local web service at `127.0.0.1` that is the headquarters for agent-managed work: one place
where a conversational project-manager agent (**OpenClaw**, via MCP) and development
**agents** (**ZCode**, behind a provider abstraction) meet. OpenClaw scopes and submits plans;
humans hand plans to ZCode sessions; Agent HQ tracks session activity, plan state and the
conversation between the two so OpenClaw can see the state of the work without doing any of it.

It manages the work; it never replaces it. The name is deliberately wider than development:
the plan lifecycle, session tracking, event stream and MCP façade are not specific to code, so
non-development plans (research, operations, writing) run through the same machinery. The
architecture and all decisions live in [`docs/plans/create.md`](docs/plans/create.md).

## Quick start

```bash
scripts/agent-hq-deploy.sh && scripts/agent-hq-deploy.sh install  # first time: deploy, then run as a service
./scripts/install-zcode-integration.sh   # /plan commands + hook + AGENTS.md section
```

It runs as the systemd user unit `agent-hq.service` on this machine: <http://127.0.0.1:5317/>.

For hacking on it, `./scripts/dev-run.sh` runs from source (it refuses while the service holds
the port; `AGENTHQ_PORT=5399 ./scripts/dev-run.sh` gives a scratch instance).

Environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `AGENTHQ_PORT` | `5317` | Listen port (bound to `127.0.0.1` only) |
| `AGENTHQ_DATA` | `~/.local/share/agent-hq` | Data dir (db, logs) |
| `AGENTHQ_DB` | `$AGENTHQ_DATA/agent-hq.sqlite` | Agent HQ database |
| `AGENTHQ_ZCODE_DB` | `~/.zcode/cli/db/db.sqlite` | ZCode's session index (read-only) |
| `AGENTHQ_LOG_ASPNET` | `Warning` | Framework log floor (`Information` restores per-request lines) |
| `AGENTHQ_LIVE` | unset | `1` enables the live smoke tests in the test suite |

## How it works

```
OpenClaw ── MCP /mcp ───────┐                     ┌── read-only ── ~/.zcode/cli/db
           REST /events ────┤   Agent HQ         │
                            ├── plans (SQLite) ────┤
ZCode hook ── POST /hooks ──┘   + docs/plans/*.md  └── FileSystemWatcher → events → SSE
```

- **Plans** live as markdown in each repository at `docs/plans/<slug>.md` and move through
  `submitted → refining → in_review → implementing → implemented → verifying → verified → done`,
  with a rework loop `verifying → fixing → implemented`, plus `deferred`/`abandoned` from any
  state. `blocked` is an orthogonal flag with a reason.
- **Transitions** are recorded by Agent HQ when a `/plan <command> <file>` prompt arrives on
  the hook feed (attributed to the ZCode session that sent it), or manually via
  `PATCH /plans/{id}/status`. The commands are prompts for the agent; the evidence (commits,
  test output, findings) is written by the agent into the plan's Status Log, which Agent HQ
  versions and emits events for but never parses into transitions.
- **Sessions** are read through from ZCode's index (never mirrored except metadata) and can be
  condensed deterministically with `GET /sessions/{id}/digest?budget=N` — no AI summarization.
- **Events** are durable rows: `GET /events` streams SSE with `Last-Event-ID` replay, and
  `GET /events?sinceSeq=N` returns the same rows as JSON for MCP-only consumers.
- **MCP** exposes the OpenClaw façade: `get_overview`, `list_plans`, `submit_plan`,
  `read_plan`, `update_plan_status`, `get_events`, `get_guide`, and the session readers.

The ZCode schema guard reads `schema_migration` at startup. A schema newer than the tested
version (`TestedZCodeSchemaVersion`) or a missing db degrades the session-mirror features and
`GET /health` reports `providerSchema: "schema-ahead"` / `"missing"`; plan features keep working.

## Web UI

Open **<http://127.0.0.1:5317/>** (plain HTML/CSS/JS in `src/AgentHQ.Api/wwwroot/`, no build
step, no external requests). One page, a table, and two overlays:

- **Plans table** — one row per plan: name + slug, status, blocked badge + reason, step progress,
  priority, session, age. Every column header sorts (click again to reverse); the status
  dropdown filters by any lifecycle state, including **Done** and *Verification queue*
  (`implemented`), and the Ready / Blocked chips ask the server for the same sets the API
  returns (`/plans?ready=true`, `?blocked=true`). Chips toggle off; repository and text filters
  combine with all of it.
- **Plan fly-out** (click a row) — rendered plan markdown, transition history (actor, command,
  git head, notes, separation-of-duties warnings), dependencies, and the actions legal from the
  current state: send back, mark implemented, verify, pass/fail, done, block/unblock,
  defer/reactivate, abandon, set priority. Closed by its ✕, by Escape, or by clicking anywhere
  outside it; a strip of the table stays visible while it is open so that target always exists.
- **Activity** — behind the *Activity* button in the top bar (badge counts events that arrived
  while it was closed): a live tail of `/events` (sessions, plan changes, blocks, hook
  rejections) and the most recently active ZCode sessions. Closed the same three ways.

Scrollable regions (table, fly-out body, activity list) show always-visible themed scrollbars —
overlay bars that only appear on scroll were the earlier "there is no way to scroll" problem.

Every action goes through `POST /plans/{id}/commands` as a `/plan <command>` with actor `andy`,
so the browser, the hook feed and MCP all write the same history and obey the same state
machine; refusals are shown verbatim. Blocking uses the orthogonal flag (`blocked` in
`PATCH /plans/{id}/status`). The stream reconnects with `Last-Event-ID` and falls back to
`?sinceSeq=` polling when the service restarts; event types added later (V2) still appear in the
activity panel.

Seed a demo table without any agents:

```bash
scripts/seed-demo.sh /tmp/demo-repo   # registers a repo and plans in six states, ticks steps
```

## API (REST)

`GET /health`, `GET /overview`, `GET|POST /repositories`, `GET /repositories/{id}/lessons`,
`GET /sessions` (`repo`, `repoRootPath`, `updatedSince`, `status`), `GET /sessions/{id}`,
`GET /sessions/{id}/digest`, `GET /sessions/{id}/messages?after=&include=`,
`POST /plans`, `GET /plans` (`repo`, `status`, `ready=true`, `blocked=true`),
`GET /plans/{id}`, `GET|PUT /plans/{id}/content`, `PATCH /plans/{id}`,
`PATCH /plans/{id}/status` (also `blocked: true|false`), `POST /plans/{id}/commands`,
`POST /plans/{id}/dependencies`, `POST /hooks/zcode/prompts`, `GET /events`, `POST /mcp`,
`GET /` (web UI).

## /plan commands

`start`, `review`, `sendback`, `implement`, `implemented`, `verify`, `pass`, `fail`, `block`,
`unblock`, `defer`, `reactivate`, `done`, `abandon` — templates ship in
`src/AgentHQ.Commands.ZCode/Commands/` and install to `~/.zcode/commands/plan/`.
See the appendix of [`docs/plans/create.md`](docs/plans/create.md) for each command's intent.

## Tests

```bash
dotnet test                    # unit + integration + E2E simulation (in-memory host, temp dirs)
AGENTHQ_LIVE=1 dotnet test   # adds read-only smoke tests against the real ZCode db
```

The E2E simulation plays every actor: synthetic ZCode db writes, hook-fed `/plan` commands,
plan-file edits through the real watchers, SSE replay, and OpenClaw's MCP loop. The web UI has
its own contract tests (`WebUiContractTests`: assets, no external URLs, every field the UI
reads, the action route, blocked toggling, forward-compatible event types).

Known environmental quirk: on this machine the .NET 10 test harness occasionally aborts before
running any test (CLR `0x80131506`, no test output, usually the first run after a build). Re-running
passes; CI retries once.

## Operations

Logs: Serilog to console and `$AGENTHQ_DATA/logs/agent-hq-<date>.log` (daily, 10 files,
10 MB cap). Each record carries a `Subsystem` property (`watcher`, `provider`, `plans`,
`hooks`, `mcp`, `sessions`, `events`).

### Deployment (localhost, systemd user service)

One script, [`scripts/agent-hq-deploy.sh`](scripts/agent-hq-deploy.sh), is both the manual
deploy and the CI deploy step. It builds a single-file self-contained publish into
`~/.local/share/agent-hq/releases/<utc>-<sha>/`, switches the `current` symlink with one
atomic rename, restarts the unit, waits for `/health`, and rolls back to the previous release
if the new one does not come up.

```bash
scripts/agent-hq-deploy.sh            # build + deploy + restart + health (day to day)
scripts/agent-hq-deploy.sh install    # install unit, enable linger, enable at boot, start
scripts/agent-hq-deploy.sh check      # health, running release vs current, version
scripts/agent-hq-deploy.sh status     # state, data paths, versions
scripts/agent-hq-deploy.sh stop|start|restart|logs
./scripts/install-zcode-integration.sh  # /plan commands + hook arrive only at install time
```

Day to day: edit, run `scripts/agent-hq-deploy.sh`, watch it health-check (it rolls back on its
own if the new build does not come up). `check` exits non-zero when health fails or the running
process is not the `current` release, so it is usable from CI and scripts.

**Install / uninstall.** `install` writes `~/.config/systemd/user/agent-hq.service` from
[`ops/agent-hq.service`](ops/agent-hq.service) (the base dir and port you passed are baked in),
enables linger, `enable --now`s the unit, and runs `check`. Removing it is the standard
systemd ritual; nothing outside these two directories is touched:

```bash
scripts/agent-hq-deploy.sh stop
systemctl --user disable agent-hq
rm ~/.config/systemd/user/agent-hq.service && systemctl --user daemon-reload
# optional: rm -rf ~/.local/share/agent-hq        (releases, database, logs)
# optional: loginctl disable-linger andy          (if nothing else needs it)
```

`install` refuses while a `dev-bridge` unit from the previous name is installed, because both
would want the same port and data dir; remove that unit first (its data directory can stay).

- Nothing but `current/` changes on a deploy: the database (`~/.local/share/agent-hq/agent-hq.sqlite`),
  its logs, and the three kept releases sit beside it, so releases are independent of data and
  `KEEP_RELEASES=3` bounds the disk.
- The unit ([`ops/agent-hq.service`](ops/agent-hq.service)) runs `current/AgentHQ.Api`,
  `Restart=always` (a crash comes back in ~2 s; `kill -9` verified), `TimeoutStopSec=10`.
  `install` also runs `loginctl enable-linger` — without it a user unit dies at logout.
- Cold start is ~13–20 s (first ZCode sync finishes before Kestrel listens on the service
  connection), so health waits are seconds, not milliseconds; the deploy reports the elapsed
  time. Steady restarts after that are fast.
- CI: `deploy.yml` is gated on repo variable `RUNNER_ENABLED=true` with `RUNNER_LABELS`
  (JSON, e.g. `["self-hosted","agent-hq"]`), so nothing queues for a runner that does not
  exist yet. When it is on: build+test → publish → the same script with `STAGE_DIR` →
  `check`. See the deployment notes in the plan log for the one-time GitHub setup.

## Status log (implementation decisions)

Choices made during the v1 build that the plan left open, per its instruction to record them:

- The API host assembly is `src/AgentHQ.Api` with namespace `AgentHQ.Api`: projects, namespaces
  and types share one `AgentHQ` prefix.
- `AgentHqHost.BuildAsync` is shared by the real host and the in-memory test host, so tests
  exercise exactly the production composition.
- Plan status history rows store the verdict/reason tail of `/plan fail|sendback|block|defer|
  abandon` in `note`; narrative stays in the plan file.
- `PATCH /plans/{id}/status` records the equivalent lifecycle command (e.g. `PATCH:/verify`)
  so command-based and manual transitions share one history vocabulary.
- Session mirror events emit `session.updated` for rows above a **strict** `time_updated`
  high-water mark (the boundary row was already seen).
- Digest budget is a hard bound: oldest assistant text first, then tool activity, then
  oversized user messages (head+tail), then a hard truncation marker.
- Agent HQ rejects `/plan` commands for files that exist under `docs/plans/` but were never
  submitted — only registered plans are acted on.
- Recorded 2026-10-24 during the v1 build.

Web UI decisions (2026-10-24):

- The host pins its content root to the application directory (`AppContext.BaseDirectory`), so
  `wwwroot/` is served whether it is started by `dotnet run`, by systemd from `$HOME`, or under
  the in-memory test server.
- UI actions are recorded as `/plan` commands (`POST /plans/{id}/commands`) rather than raw
  status PATCHes, so the browser writes the same history vocabulary as agents. `blocked` was
  added to `PATCH /plans/{id}/status` because the flag is not a status.
- The ZCode provider never falls back to the real `~/.zcode` db: the path is explicit
  (`AGENTHQ_ZCODE_DB`), so a misconfigured or test host cannot mirror live sessions.
- Event replay reads to the current position (no small window), so a client that reconnects
  from an old cursor cannot silently resume behind live.
- The UI is a table, not a board (2026-10-24): every filter is a query over the same `/plans`
  list the API already offered, so "which done plans are there" needs no extra endpoint. Sorting
  is client-side on the loaded page (the list is small and already whole).
- Dismissal of an open overlay (fly-out or activity) consumes the press that closes it
  (capture-phase `pointerdown`, default prevented), so on a narrow screen the same press cannot
  also open the row it landed on.
- Scroll containers use `scrollbar-width: thin` plus themed WebKit thumbs: the browser's overlay
  scrollbars hide when idle, and an invisible scrollbar reads as unscrollable content.
- The top bar's height is measured into `--topbar-h` (ResizeObserver): below ~560px it wraps, and
  the overlays, anchored under it, follow the measured height rather than a hard-coded 52px.
