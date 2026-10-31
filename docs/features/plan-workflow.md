# The plan workflow commands

Agent HQ's `/plan` lifecycle and the worktree-based plan workflow are one mechanism with two
command surfaces. The **workflow commands** (colon spelling: `/plan:start`) do a phase's work —
drafting on main, the plan worktree, evidence, archiving. The **state commands** (space
spelling: `/plan start`) are the hub's state interface, installed everywhere by
[the ZCode integration](zcode-integration.md). Both spellings are one-to-one: typing either
moves the hub identically, because the prompt hook parses both and runs the same
[state machine](plan-lifecycle.md).

## The mapping

Each workflow command maps to exactly one state command; the remaining state commands have no
workflow wrapper and are used directly.

| Workflow command | State command | Hub transition | What the workflow command adds |
|---|---|---|---|
| `/plan:start <name>` | `/plan start <file>` | `submitted → refining` | Drafts the plan on main, creates the namesake worktree (`zcode/<name>`), registers the plan |
| `/plan:review <file>` | `/plan review <file>` | `refining → in_review` | The adversarial plan review: gaps, inconsistencies, trip hazards, lessons; rewrites on main |
| `/plan:implement <file>` | `/plan implement <file>` | `in_review → implementing` | The worktree work contract; ends by claiming via `/plan implemented` with evidence |
| `/plan:verify <file>` | `/plan verify <file>` | `implemented → verifying` | Per-criterion adversarial verification; records `## Verification Evidence`; ends with `/plan pass` or `/plan fail` |
| `/plan:done <file>` | `/plan done <file>` | `verified → done` | The completeness audit (deferrals acknowledged, never silent), archive to `docs/plans/done/`, worktree merge/remove, the one push |
| `/plan:abandon <file>` | `/plan abandon <file>` | any → `abandoned` | Confirmation with the user, the abandonment record, snapshot, worktree/branch removal, archive |

State-only commands: `sendback`, `block`, `unblock`, `defer`, `reactivate`, `implemented`,
`pass`, `fail`. They are orthogonal to the worktree mechanics and are used as-is in both flows
(the workflow templates name the moments that call for them).

## Rules of the unified flow

- **One identifier everywhere.** The plan name is the worktree name, the branch `zcode/<name>`,
  and the plan file `docs/plans/<name>.md`. The hub slug is derived from the same name, so the
  plan file, the slug and the worktree all address identically.
- **The plan lives on main; code lives in the worktree.** Plan files are committed locally on
  main (pathspec commits, pushed only at `/plan:done`) and never written inside the worktree.
  For this to work, the repository's `.zcode/worktree.json` must set `"autoSession": false` —
  an auto-session worktree cannot edit main's plan file.
- **The hub is the single status authority.** No `Status:` header line is written into the plan
  file — that was a second state machine. The header block carries mechanical facts only
  (`Worktree:` / `Branch:` / `Started:`); status is read from Agent HQ (web UI, MCP, or a
  command's response). Evidence and narrative live in the plan file (Status Log, Verification
  Evidence, Deferred), exactly as in [the lifecycle](plan-lifecycle.md#how-a-transition-is-recorded).
- **New plans are registered, not auto-ingested.** `/plan:start` on a brand-new plan does the
  file and worktree work; Agent HQ records the state once the plan is registered (hub UI or
  `submit_plan`), then `/plan start docs/plans/<name>.md` records the ownership. A command for
  an unregistered plan is rejected with a `hook.rejected` event, never silently dropped.
- **Worktree prompts resolve back to the repository.** Sessions run inside
  `~/.zcode/worktrees/<repo-directory-name>/<worktree>`; the hook maps that cwd back to the
  registered repository (override the tree root with `AGENTHQ_WORKTREES_ROOT`).
- **Archiving is recorded, not lossy.** `/plan:done` and `/plan:abandon` move the plan file to
  `docs/plans/done/<name>.md`; the plans watcher re-points the registration at the archived
  copy, so the plan stays addressable by slug and by `done/<name>.md`.

## One plan file format

Header block (worktree facts) · `## Goal` · `## Context` · `## Decision` · `## Steps`
(checkboxes — these drive hub progress) · `## Dependencies` · `## Verification Criteria` ·
`## Documentation` · `## Status Log`, with `## Verification Evidence` appended by verification
and `## Deferred` written at done when gaps were acknowledged. See
[the lifecycle page](plan-lifecycle.md#anatomy-of-a-plan) for what each section means to the hub.

## Master location and sync

The workflow pack is **mastered in this repository** at
`src/AgentHQ.Commands.ZCode/WorkflowCommands/`. Target repositories — including the ones the
flow originated in — sync from here:

```
dotnet run --project src/AgentHQ.Api -- sync-workflow --repo <target-repo>
```

The verb writes `<target-repo>/.zcode/commands/plan/<verb>.md` (project scope, which is what
makes the colon spelling). Overwrite rules mirror [the installer](zcode-integration.md): a file
carrying the `<!-- agent-hq v<N> -->` marker is upgraded; a file without the marker is treated
as user-authored and left alone (exit code 2, a visible partial install); `--force` overwrites
regardless — the documented one-time cutover for repositories whose pack predates the marker.
Re-running is a no-op.

## See also

- [Plan lifecycle](plan-lifecycle.md) — the states, transitions and separation of duties
- [ZCode integration](zcode-integration.md) — the installer, the state command pack, the hook
