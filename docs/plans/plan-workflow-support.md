# Plan workflow support

## Goal

Unify the nastv worktree-based plan workflow and Agent HQ's `/plan` lifecycle into one
mechanism: the same six workflow commands do the work everywhere, Agent HQ's state commands
record the state, and a typed command moves the hub regardless of which spelling carried it.

## Context

Two plan workflows were drafted independently and both are barely used, which makes this the
moment to converge them. The nastv flow (`plan:start` … `plan:abort` at
`~/Projects/nastv/.zcode/commands/plan/`) is file-and-git encoded: the plan lives on main, code
lives in a worktree named after the plan, and status is a header line in the file. Agent HQ's
flow is hub-encoded: 14 state commands, a database state machine, hook-fed attribution. The
agreed parity: **Agent HQ's vocabulary and state authority win; nastv's mechanics win.** The
workflow commands keep the colon spelling (`plan:start`, from the `plan/` subdirectory naming);
Agent HQ's state commands keep the space form (`plan start`); each workflow command maps
one-to-one onto exactly one state command, so typing either spelling moves the hub the same way.

Non-goals: no change to the state machine itself, no second status store in the plan file (the
`Status:` header line is dropped — the hub stays the single state authority), no worktree
management inside the hub (the templates drive the git-worktrees plugin, as nastv does today).

## Steps

- [x] Parser accepts `/plan:<sub>` alongside `/plan <sub>` (`PlanCommand`), with tests pinning
      both spellings, the reason-tail rule, and `plan:start <name> <title words>` tolerating a
      multi-word tail
- [x] Hook ingest resolves a worktree cwd (`~/.zcode/worktrees/<repo-leaf>/…`) back to the
      registered repository, with tests
- [x] Plan file resolution and the watcher accept the `docs/plans/done/` archive location
      (done/abandon archive moves must not orphan a plan row), with tests
- [x] Colon workflow pack: six templates (`plan:start`, `plan:review`, `plan:implement`,
      `plan:verify`, `plan:done`, `plan:abandon`) with `name: plan:*` frontmatter, carrying the
      nastv mechanics (plan-on-main, worktree-per-plan, evidence-per-criterion, archive-at-done)
      and ending each phase with the hub state command that records it
- [x] Master/sync: the workflow pack lives in Agent HQ, a sync path installs it into a target
      repository's `.zcode/commands/plan/`, installer tests cover idempotency
- [x] Docs: contract page (`docs/features/plan-workflow.md`) with the command mapping table and
      status-authority rules; plan-lifecycle, zcode-integration and the Commands feature file
      updated; index links
- [x] Nastv cutover: sync the pack into nastv, `plan:abort` becomes `plan:abandon`, `abort.md`
      retired, no templates left that the hub cannot parse
- [x] `.zcode/worktree.json` with `"autoSession": false` so plan-file edits on main are possible
      in this repository

## Dependencies

None — no other open plan touches the parser, the hook path or the command pack.

## Verification Criteria

- `dotnet test` (via `scripts/test.sh`) passes with the new parser, resolution and archive tests
  included
- A `/plan:<verb>` prompt and its `/plan <verb>` twin produce the identical transition in the
  E2E simulation (same state, same attribution, same event)
- A hook prompt from `~/.zcode/worktrees/<registered-repo>/<name>` resolves to that repository
  instead of being rejected with "cwd not under a registered repository"
- The six workflow templates install into a target repo's `.zcode/commands/plan/`, are
  idempotent on re-run, and no `abort.md` remains in nastv after cutover
- Every workflow template ends with the hub state command that records the phase, and none
  instructs writing a `Status:` line into the plan file

## Documentation

- `docs/features/plan-workflow.md` (new contract page)
- `docs/features/plan-lifecycle.md`, `docs/features/zcode-integration.md`,
  `src/AgentHQ.Commands.ZCode/AGENTS.md`, `docs/README.md` index

## Status Log

- 2026-10-31 — plan drafted during the parity discussion; direction agreed with Andy: colon
  spelling for workflow commands, space form for state commands, one-to-one mapping, hub stays
  the state authority.
- 2026-10-31 — implemented in worktree `plan-workflow-support` (branch `zcode/plan-workflow-support`),
  based on the carried main-checkout working state. Full suite via `scripts/test.sh`:
  **171 passed, 0 failed** (baseline before this effort: 157 passed). New coverage:
  `PlanCommandTests` (colon spellings), `WorkflowSyncTests` (5 sync cases),
  `PlanWorkflowTests` (identical transitions per spelling, worktree-cwd store + hook
  resolution, archive-move re-point). Cutover done: nastv `.zcode/commands/plan/` synced
  `--force`, `abort.md` removed, `abandon.md` added (all unstaged in nastv for review);
  this repository's project-scope pack synced into `.zcode/commands/plan/`. Note: the deployed
  service (127.0.0.1:5317) still runs the old build — parser/resolution/archive behaviour goes
  live on the next deploy. Files entangled with the carried main-checkout work (uncommitted
  there, edited here too): `Program.cs`, `PlanService.cs`, `tests/AgentHqApp.cs`,
  `docs/README.md`, `docs/features/plan-lifecycle.md`, `docs/features/zcode-integration.md`,
  `src/AgentHQ.Commands.ZCode/AGENTS.md` — commit order with the main-checkout work needs a
  human decision, so nothing was committed.
