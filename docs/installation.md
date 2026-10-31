# Installation

Agent HQ installs in two steps: deploy a release, then install it as a systemd user service.
Both are the same script, [`scripts/agent-hq-deploy.sh`](../scripts/agent-hq-deploy.sh).

This page assumes you are installing for the first time on a fresh checkout. If it is already
running and you want to know how to upgrade it, see [Operations](operations.md).

## What you need

| Requirement | Notes |
|---|---|
| Linux with systemd running user services | Developed on Ubuntu; any distribution with `systemctl --user` works |
| .NET SDK **10.0** | Only for building from source (`dotnet --version` should print `10.x`). The deployed binary is self-contained: the machine that *runs* it needs no SDK |
| `git`, `curl` | The deploy script uses both |
| A ZCode installation *(optional)* | Agent HQ reads ZCode's session index. Without it, everything except session tracking still works — see [degraded modes](features/sessions.md#when-zcode-is-missing) |

Nothing else is needed. There is no database server (SQLite is embedded), no Node or npm (the
web UI is plain HTML/CSS/JS), and no build step for the front end.

## Install

### 1. Clone

```bash
git clone <this-repository-url> ~/Projects/agent-hq
cd ~/Projects/agent-hq
```

### 2. Check the port is free

Agent HQ binds **`127.0.0.1:5317`**. Nothing else should hold it:

```bash
ss -ltn | grep 5317 || echo "free"
```

To use a different port, export `AGENTHQ_PORT` **before every deploy and install** — the
installed unit bakes the port in, and the hook URL written by the ZCode integration has to match
it. A custom port is also part of the unit file, so changing it later means reinstalling.

### 3. Deploy and install

```bash
scripts/agent-hq-deploy.sh          # builds a release, switches it in, health-checks it
scripts/agent-hq-deploy.sh install  # installs the unit, enables linger and boot start, starts it
```

The first run builds a self-contained single-file binary into
`~/.local/share/agent-hq/releases/<utc>-<sha>/` and points the `current` symlink at it. Because
no unit exists yet on a first install, it does not start anything — it prints a hint to run
`install`, which is the second command above.

`install` writes `~/.config/systemd/user/agent-hq.service`, enables lingering (so the service
survives logout), enables the unit at boot, starts it, and finishes by running `check`.

### 4. Confirm

```bash
scripts/agent-hq-deploy.sh check
curl -s http://127.0.0.1:5317/health
```

A healthy install prints something like:
`{"status":"ok","provider":"zcode","providerSchema":"ok","providerDetail":"schema version 24"}`.

Open <http://127.0.0.1:5317/> — you should see the plans table (empty, or already populated if
Agent HQ is dogfooding itself).

Cold start takes roughly 13–20 seconds because the first ZCode session sync completes before the
port opens; the deploy script waits for it and reports the elapsed time. `check` exits non-zero
when health fails or when the running process is not the release `current` points at, so it is
safe to use in scripts and CI.

### 5. Connect your worker agent (ZCode)

```bash
./scripts/install-zcode-integration.sh --repo /path/to/your/project
```

This copies the `/plan` command pack into `~/.zcode/commands/plan/`, merges a `UserPromptSubmit`
hook into `~/.zcode/settings.json`, and appends a short section to the repository's `AGENTS.md`.
It is idempotent and never overwrites a command template you have edited. What it writes, and
how to undo it, is documented in [The ZCode integration](features/zcode-integration.md).

### 6. (Optional) See it populated without agents

```bash
scripts/seed-demo.sh /tmp/demo-repo
```

Registers a demo repository and creates plans in every interesting state, ticking their
checkboxes the way an implementer would. Re-running it adds nothing new.

## Running from source (development)

```bash
./scripts/dev-run.sh                              # same port, same database as the service
AGENTHQ_PORT=5399 ./scripts/dev-run.sh            # scratch instance on another port
AGENTHQ_PORT=5399 AGENTHQ_DATA=/tmp/ah-dev ./scripts/dev-run.sh   # scratch data too
```

`dev-run.sh` refuses to start while the installed service holds the same port — deploy your
change instead (`scripts/agent-hq-deploy.sh`), or use a scratch port. A scratch instance shares
the service's database unless you also set `AGENTHQ_DATA`; be deliberate about which you want.

## Configuration

All configuration is environment variables. Defaults are correct for a single-user machine.

| Variable | Default | Meaning |
|---|---|---|
| `AGENTHQ_PORT` | `5317` | Listen port; the host always binds `127.0.0.1` and never `*` |
| `AGENTHQ_DATA` | `~/.local/share/agent-hq` | Data directory (database, logs) |
| `AGENTHQ_DB` | `$AGENTHQ_DATA/agent-hq.sqlite` | Agent HQ's own SQLite database |
| `AGENTHQ_ZCODE_DB` | `~/.zcode/cli/db/db.sqlite` | ZCode's session index, opened **read-only** |
| `AGENTHQ_LOG_ASPNET` | `Warning` | Framework log floor; `Information` restores per-request lines |
| `AGENTHQ_LIVE` | unset | `1` enables the read-only live smoke tests in the test suite |

Script-level knobs (deploy only, not read by the service): `BASE_DIR`, `SERVICE`, `KEEP_RELEASES`
(default 3), `STAGE_DIR` (deploy a prebuilt pile instead of building), `HEALTH_DEADLINE`.

The repository-path override matters for tests and scratch instances: the ZCode provider never
falls back to your real `~/.zcode` database, so a misconfigured host degrades to "no sessions"
rather than mirroring live work.

## Uninstall

Nothing outside two directories is touched: the unit lives in `~/.config/systemd/user/` and all
data in `~/.local/share/agent-hq/`.

```bash
scripts/agent-hq-deploy.sh stop
systemctl --user disable agent-hq
rm ~/.config/systemd/user/agent-hq.service && systemctl --user daemon-reload
# optional, deletes releases, database and logs:
rm -rf ~/.local/share/agent-hq
# optional, if nothing else needs your session to survive logout:
loginctl disable-linger "$USER"
```

To undo the worker-agent side, remove `~/.zcode/commands/plan/` and the `agent-hq`-tagged hook
entry from `~/.zcode/settings.json` — see [The ZCode integration](features/zcode-integration.md#undoing-the-installation).

## Troubleshooting a first install

| Symptom | Cause and fix |
|---|---|
| `deploy` prints "port 5317 is in use by something other than agent-hq" | Something else holds the port. Find it with `ss -ltnp \| grep 5317` and stop it, or choose another `AGENTHQ_PORT` |
| `deploy` prints "no agent-hq unit installed for …" | Expected on a first deploy: run `scripts/agent-hq-deploy.sh install` |
| `install` refuses: a `dev-bridge.service` unit exists | This project was previously named dev-bridge and both want the same port and data dir. Retire the old unit first: `systemctl --user disable --now dev-bridge && rm ~/.config/systemd/user/dev-bridge.service && systemctl --user daemon-reload`. Its data directory can stay |
| Health stays `providerSchema: "missing"` | Agent HQ could not read a ZCode database at `AGENTHQ_ZCODE_DB`. Plans and the UI still work. See [Sessions](features/sessions.md#when-zcode-is-missing) |
| `/health` reports `providerSchema: "schema-ahead"` | Your ZCode is newer than the version Agent HQ was tested against. Session features degrade gracefully; bump `TestedZCodeSchemaVersion` only after checking the schema |
| The service dies when you log out | Lingering is off: `loginctl enable-linger "$USER"` |
| New release is not healthy and the deploy rolled back | `scripts/agent-hq-deploy.sh logs` shows the journal; the previous release is running again |
