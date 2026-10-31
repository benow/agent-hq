# The ZCode integration

Agent HQ records a plan transition when a `/plan <command> <file>` prompt reaches its hook feed,
attributed to the session that sent it. This page covers the worker-agent side of that: the
command pack, the hook, the installer, and exactly what it changes on your machine.

Everything here is optional. Without it, Agent HQ still tracks plans and sessions, and you can
drive transitions from the web UI or the API — you just lose automatic attribution.

## Two halves

| Half | Where it lives | What it does |
|---|---|---|
| **Commands** | `~/.zcode/commands/plan/<name>.md` | Prompt templates the agent expands: what the phase means and how to do the work |
| **Hook** | `~/.zcode/settings.json` → `hooks.UserPromptSubmit` | Posts every prompt to Agent HQ before the agent acts on it |

The templates and the hook are installed together by one idempotent command.

## Installing

```bash
./scripts/install-zcode-integration.sh --repo /path/to/project
```

Options:

| Option | Default | Meaning |
|---|---|---|
| `--repo DIR` | current directory | Repository whose `AGENTS.md` gets the Agent HQ section |
| `--port N` | `AGENTHQ_PORT` or `5317` | Port baked into the hook URL — it must match the running service |
| `--home DIR` | `$HOME` | Where the commands and settings are written (used by the tests) |

The same installer is built into the binary as `AgentHQ.Api install-zcode`, so an installed
release can install it without a source checkout:

```bash
~/.local/share/agent-hq/current/AgentHQ.Api install-zcode --repo /path/to/project
```

### What it changes

1. **`~/.zcode/commands/plan/*.md`** — one file per command. A file Agent HQ installed carries a
   version marker on its first line (`<!-- agent-hq v1 -->`). On re-run, only files bearing that
   marker are refreshed; a template you edited loses the marker and is left alone with a
   warning (exit code `2`). Files installed under the project's previous name (`dev-bridge`) are
   recognised by their older marker and upgraded unless you edited them.
2. **`~/.zcode/settings.json`** — merges one `UserPromptSubmit` entry:

   ```bash
   curl -sf -X POST http://127.0.0.1:5317/hooks/zcode/prompts \
     -H 'Content-Type: application/json' \
     -d '{"session_id":"${CLAUDE_SESSION_ID}","prompt":"$PROMPT","cwd":"$CWD"}' || true
   ```

   The merge is key-based and idempotent: running it twice adds the entry once, and unrelated
   keys in the file are preserved. `|| true` guarantees a down or stopped Agent HQ never blocks
   you from prompting.
3. **`<repo>/AGENTS.md`** — appends a short section pointing at `docs/plans/` and
   `docs/plans/lessons.md`, guarded by a `<!-- agent-hq -->` marker so it is added once.

The installer prints how many command files it copied and any warnings it raised.

## Why the hook exists

ZCode's agent environment does not expose the session id to the agent, but a hook receives it as
`${CLAUDE_SESSION_ID}` — the same id that appears as `session.id` in ZCode's database. That is
what makes attribution work without anybody typing a session id: the prompt carries the id, the
plan transition is recorded against it, and a later verifier can prove which session implemented
what.

The hook carries **intent and attribution only**. Agent output never flows through it. Evidence —
commit hashes, test results, review findings, verdicts — is written by the agent into the plan's
Status Log, which Agent HQ watches as a file. This split is deliberate: prompts are a reliable
signal of intent, and files are the natural, versioned home for evidence.

## What happens to a `/plan` prompt

1. The hook posts `{session_id, prompt, cwd}`.
2. Agent HQ resolves the repository from `cwd`: the longest registered root path that prefixes
   it — or, when the prompt was typed inside a ZCode worktree
   (`~/.zcode/worktrees/<repo-directory-name>/<worktree>`), the registered repository with that
   directory name.
3. It resolves the session: from its mirror, or directly from ZCode if the watcher has not seen
   it yet — a race should not lose a transition.
4. It matches the prompt against `/plan <command> <file> [reason]` or its workflow twin
   `/plan:<command> <file> [reason]` — the two spellings are one-to-one
   ([plan workflow](plan-workflow.md)). Anything else is acknowledged and ignored.
5. It resolves `<file>` against the repository (slug, repository-relative path, absolute path) and
   applies the command.
6. A registered plan with a legal command transitions, with the session as actor and git `HEAD`
   snapshotted for `implemented`/`verified`.
7. Anything else — unregistered plan, ambiguous file, illegal transition — records a
   `hook.rejected` event with the raw prompt and the reason, and changes no state.

## Diagnosing "I ran /plan and nothing happened"

Work through these in order:

| Check | How |
|---|---|
| Is the service up, on the right port? | `curl -s http://127.0.0.1:5317/health`. The hook URL has the port baked in — a mismatch fails silently (`\|\| true`) |
| Did the prompt reach the hook? | The activity panel or `GET /events?sinceSeq=…&types=hook.rejected` |
| Was the prompt rejected? | The `hook.rejected` payload carries the prompt and the reason: unknown session, `cwd` not under a registered repository, no plan resolved, ambiguous plan, or illegal transition |
| Was the transition legal? | `GET /plans/{id}` shows the current status; the [lifecycle table](plan-lifecycle.md#the-plan-command-reference) shows which commands are legal from it |
| Is the plan registered? | A file under `docs/plans/` that was never submitted to Agent HQ is ignored by design — submit it first |
| Is the command installed? | `ls ~/.zcode/commands/plan/` — if empty, run the installer |
| Is the hook in settings? | `grep -n agent-hq ~/.zcode/settings.json` |

A rejection is not a silent failure: it is exactly what `hook.rejected` exists to surface.

## Updating the commands

Command templates ship in `src/AgentHQ.Commands.ZCode/Commands/` and carry a version marker.
When they change, bump `Installer.CommandVersion` and the marker together, then re-run the
installer — untouched templates are refreshed, edited ones are reported and kept.

The [workflow pack](plan-workflow.md) (`/plan:*` — project scope, mastered in
`src/AgentHQ.Commands.ZCode/WorkflowCommands/`) is synced per repository, not installed
globally:

```bash
AgentHQ.Api sync-workflow --repo /path/to/project   # add --force for a pre-marker pack
```

Same marker rules: marked files upgrade, unmarked files are preserved and reported (exit 2).

## Undoing the installation

```bash
rm -rf ~/.zcode/commands/plan
# remove the UserPromptSubmit entry whose command contains /hooks/zcode/prompts from
# ~/.zcode/settings.json
```

The `AGENTS.md` section can stay — it is harmless without the rest, and points agents at plans
that may still be useful. Remove it by deleting the block under the `<!-- agent-hq -->` marker.

## See also

- [Plan lifecycle](plan-lifecycle.md) — every command and the transition it causes
- [Events](events.md) — `hook.rejected`, `plan.status_changed`, `plan.session_linked`
- [Installation](../installation.md) — where this step sits in a fresh setup
