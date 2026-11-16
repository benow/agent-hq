# Operations

This page is for keeping a running Agent HQ healthy: the service, deploys, rollback, logs, data,
and the security posture it assumes. For a first install, start with [Installation](installation.md).

## Running as a service

Agent HQ runs as a systemd **user** unit, `agent-hq.service`. `install` enables lingering, so the
service survives logout and comes back after a reboot.

| Command | What it answers |
|---|---|
| `scripts/agent-hq-deploy.sh status` | Is it running? Enabled? Healthy? Which release and version? Where is the data? |
| `scripts/agent-hq-deploy.sh check` | Health JSON, version, and whether the running process is the release `current` points at |
| `scripts/agent-hq-deploy.sh logs` | Follow the journal (`Ctrl-C` to stop) |

`check` exits non-zero when health fails or when the running binary is not the current release —
use it in scripts and CI. That second clause matters: health can be answered by an older process
that systemd restarted, so "healthy" alone is not "the deploy took".

Lifecycle: `start`, `stop`, `restart` all wait for health (cold start is seconds, see below).
`stop` is respected by systemd despite `Restart=always`.

The unit is `ops/agent-hq.service`; `install` writes it to
`~/.config/systemd/user/agent-hq.service` with the base directory and port you used baked in.

## Deploys

One script is both the manual deploy and the CI deploy step:

```bash
scripts/agent-hq-deploy.sh              # build from this checkout, switch in, restart, verify
STAGE_DIR=/path/to/publish scripts/agent-hq-deploy.sh   # deploy a prebuilt pile instead
```

What a deploy does:

1. Publishes a single-file, self-contained binary into
   `~/.local/share/agent-hq/releases/<utc>-<sha>/`.
2. Stops the unit and waits for it to actually stop (otherwise the restart races the old
   process for the port).
3. Switches the `current` symlink with one atomic rename — a crash mid-deploy leaves either the
   old release or the new one live, never a half-copied one.
4. Starts the unit and polls `/health` for up to `HEALTH_DEADLINE` (60 s by default).
5. On success, prunes old releases to `KEEP_RELEASES` (default 3, the live one included).

**Rollback is automatic.** If the new release does not become healthy, the script switches
`current` back to the previous release, starts it, and prints the last 30 journal lines. If you
need an older release than the one kept, redeploy that commit.

Cold start takes roughly 13–20 seconds because the first ZCode session sync completes before
Kestrel opens the port; the deploy reports how long it took. A warm restart is fast.

The deploy refuses to start if the port is held by something other than the service — a stray
`dev-run.sh` instance, say — because health would otherwise answer for the wrong process.

## Container mode (the hub on builds)

The hub can run as a **container + PostgreSQL sidecar** instead of the systemd unit — this is
the runtime for the always-on hub on the builds machine
([docs/plans/distributed-agent-hq](plans/distributed-agent-hq/00-OVERVIEW.md), part 1). It is
the same binary and the same deploy script; only storage and packaging change:

```bash
cd ~/apps/agent-hq            # a clone of the repository on the target machine
set -a; . ./.env; set +a      # .env holds AGENTHQ_PG_PASSWORD (compose + script read it)
scripts/agent-hq-deploy.sh container up     # build image (tag = git sha), compose up, health-wait, check
scripts/agent-hq-deploy.sh container check  # the same check gate as systemd mode
scripts/agent-hq-deploy.sh container logs   # docker compose logs
scripts/agent-hq-deploy.sh container down   # stop; volumes are kept
```

- **compose.yaml** defines two services: `hub` (the `agent-hq` image) and `db`
  (postgres:17-alpine, healthchecked). The hub starts only after the database is healthy.
- **Volumes, not directories**, carry the state: `agent-hq-pgdata` (the PostgreSQL data) and
  `agent-hq-data` (logs and exchange blobs, mounted at `/data`). The "everything under
  `$AGENTHQ_DATA`" rule carries over as "everything in the named volumes".
- The port mapping is **`127.0.0.1:5317` on the host** — loopback-only until authentication
  lands (part 2). Widening it is a recorded security decision, never a config default.
- No ZCode integration inside the container: the provider degrades to `missing` by design
  (repositories and sessions arrive via spokes from part 4 onward).
- A failed health wait rolls the stack back (`compose down`, volumes untouched) and the
  script exits non-zero: a deploy that rolled back is not a deploy.
- SIGTERM gets a 10 s grace period (`stop_grace_period`); the host drains and exits 0.

### Backups

Everything Agent HQ owns is the single SQLite file plus the logs. For a consistent copy:

```bash
sqlite3 ~/.local/share/agent-hq/agent-hq.sqlite ".backup '/path/to/backup.sqlite'"
```

In container mode the database is PostgreSQL; the nightly `agent-hq-pg-backup.timer`
(user timer on the target machine, 04:30, 14 retained) writes a dated `pg_dump` to
`~/apps/agent-hq/backups/<date>/agent-hq-pg.sql.gz`. The dump covers **all** hub-owned data —
identities and exchange records included — and is the restore source for the
rebuildability criterion (overview criterion 8).

Plan markdown lives in your repositories and is already in git there; it is not Agent HQ data.

## Releases and data are separate

```
~/.local/share/agent-hq/
├── current -> releases/<utc>-<sha>/    # the live release (symlink)
├── releases/                            # kept releases; nothing here is edited in place
├── agent-hq.sqlite                      # Agent HQ's database
├── agent-hq.sqlite-wal / -shm           # SQLite WAL side files
└── logs/agent-hq-<date>.log             # rolling log files
```

A deploy never touches the database or the logs — they live beside the releases, not inside
them. Deleting old releases is safe; deleting `current` is not (the next deploy recreates it).

Backups: [above](#backups) — SQLite on the laptop, `pg_dump` in container mode. Plan markdown
lives in your repositories and is already in git there; it is not Agent HQ data.

## Logs

Two destinations, same records:

- the journal — `journalctl --user -u agent-hq` (or `scripts/agent-hq-deploy.sh logs`);
- files — `$AGENTHQ_DATA/logs/agent-hq-<date>.log`, daily rolling, at most 10 files of 10 MB.

Each record carries a `Subsystem` property so you can filter:

| Subsystem | What it covers |
|---|---|
| `watcher` | ZCode database watcher: start, `data_version` changes, backstop failures |
| `provider` | ZCode reader: schema guard outcome, degraded-mode warnings |
| `sessions` | Startup full sync count, incremental syncs |
| `plans` | Plan dir watcher, submissions, every transition (from → to, actor, command), lessons indexing |
| `hooks` | Hook-feed ingests and rejections (with truncated prompt and reason) |
| `events` | Every appended event with its sequence |
| `db` | Schema migration |

ASP.NET Core's per-request logging is muted to `Warning` by default because a bridge that talks
to itself all day produces noise, not information. Set `AGENTHQ_LOG_ASPNET=Information` when you
need to see requests.

## Health and degradation

`GET /health` answers two independent questions:

```json
{"status":"ok","provider":"zcode","providerSchema":"ok","providerDetail":"schema version 24"}
```

- `status: ok` — Agent HQ itself is fine.
- `providerSchema` — the optional worker-agent integration: `ok`, `schema-ahead` (ZCode newer
  than tested) or `missing` (no readable database). In both non-ok cases session features return
  empty results and plans keep working. See
  [Sessions](features/sessions.md#when-zcode-is-missing).

`providerSchema: "missing"` on a machine with no ZCode is normal, not an error.

## CI

`.github/workflows/deploy.yml` runs build → test → publish → deploy → check on pushes to `main`
(docs-only changes are ignored) and on manual dispatch.

It is **gated**: every job is skipped unless the repository variable `RUNNER_ENABLED` is set to
`"true"`, with `RUNNER_LABELS` holding the self-hosted runner labels as JSON (for example
`["self-hosted","agent-hq"]`). Until a runner exists, nothing queues forever waiting for one.
The deploy job uses the same script as a manual deploy, with `STAGE_DIR` pointing at the
publish artifact, then runs `check`.

One test quirk is handled in CI: on some machines the .NET 10 test harness occasionally aborts
before running any test (CLR `0x80131506`, usually the first run after a build). CI retries
`dotnet test` once; a genuine failure fails both runs.

## Development workflow

```bash
dotnet test                                   # the gate — unit, integration, E2E simulation
AGENTHQ_LIVE=1 dotnet test                    # adds read-only smoke tests against the real ZCode db
./scripts/dev-run.sh                          # run from source on the service's port (refuses if it is running)
AGENTHQ_PORT=5399 ./scripts/dev-run.sh        # scratch instance
scripts/agent-hq-deploy.sh                    # replace the running build
```

`dev-run.sh` refuses to take a port the installed service holds — deploy instead, or use a
scratch port. A scratch instance shares the real database unless `AGENTHQ_DATA` is also set.

Tests boot the full host in memory (`AgentHqHost.BuildAsync`, the same composition root the real
host uses) against temporary directories: a synthetic ZCode database, a temporary repository,
real file-system watchers, and real SSE streams. Nothing touches your live data. The E2E
simulation plays every actor: it writes to the synthetic ZCode database, drives `/plan` commands
through the hook feed, edits plan files through the real watchers, reconnects SSE with
`Last-Event-ID`, and runs the project-manager loop through an MCP client.

The test harness occasionally aborts before running any test immediately after a build (the CLR
`0x80131506` quirk above). Re-run before assuming a failure.

## Security and limits

Agent HQ is built for a **single-user machine**: it binds only to `127.0.0.1`, and it has **no
authentication** on any endpoint — REST, SSE, MCP, or the hook feed.

What that means, concretely:

- Any local process can read every session digest and plan, move any plan, and inject events.
  That is acceptable if the local user is the trust boundary; it is not acceptable if you share
  the machine with untrusted users.
- It stores no credentials and mirrors no message content into its own database — session
  transcripts are read from ZCode's index on demand. Logs can still contain plan slugs, session
  titles and rejected prompts (truncated).
- The hook endpoint validates that `cwd` resolves under a registered repository, so it cannot
  be used to move plans belonging to unknown paths through attribution, but it is not an
  authorisation boundary.

**Do not expose it beyond loopback without adding authentication.** If you need remote access,
the safe shape is an authenticated reverse proxy (TLS plus a token or client certificate) in
front of the whole service, and a scoped proxy in front of `/mcp` if you only want to expose the
agent façade. Binding `0.0.0.0` by changing the host is a deliberate decision, not a
configuration tweak.

## Data retention

| Artifact | Growth | Bound |
|---|---|---|
| Logs | One file per day | 10 files × 10 MB per sink, then rotated out |
| Releases | One directory per deploy | `KEEP_RELEASES` (default 3) |
| Events | One row per significant action | Unbounded by design — it is the audit trail; rows are small |
| Plans, history, sessions mirror | Proportional to work done | Unbounded by design; trivially small |
| Session transcripts | Not stored | Read from the worker agent's index |

## Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| Deploy rolled back | New release failed its health check | `logs`; fix, redeploy. The previous release is running |
| `check` warns "running X but current → Y" | The running process is not the current release (crash restart of an older one, or a raced stop) | `restart`, then `check` again |
| Service not restarted after deploy | The unit is installed for a different base directory (`BASE_DIR`) | Install from the checkout that owns the unit, or remove the unit and reinstall |
| Health OK but no sessions | `providerSchema` not `ok` | See [Sessions](features/sessions.md#when-zcode-is-missing) |
| `/plan` command did nothing | Hook rejected, or service/port mismatch | See [The ZCode integration](features/zcode-integration.md#diagnosing-i-ran-plan-and-nothing-happened) |
| Activity panel stale | Stream down; polling should be running | Check the top-bar `events` indicator; if `offline`, the service is not reachable |
| Service dies at logout | Lingering is off | `loginctl enable-linger "$USER"` |
| `dotnet test` aborts with no output right after a build | Known .NET 10 harness quirk | Re-run |
