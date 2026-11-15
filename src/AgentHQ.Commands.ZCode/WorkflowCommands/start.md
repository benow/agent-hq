<!-- agent-hq v1 -->
---
name: plan:start
description: Start a plan — draft it on main, create its namesake worktree, keep Agent HQ tracking it
argument-hint: <name> [title words...]
---

# /plan:start — open a plan (worktree isolation)

One identifier everywhere: the plan name is the worktree name, the branch
`zcode/<name>`, and the plan file `docs/plans/<name>.md`. THE PLAN LIVES ON
MAIN (committed locally, pushed only at /plan:done) — it is the source of
truth and the durable record even if the plan is later abandoned. CODE lives
in the worktree. Never write the plan file inside the worktree.

Agent HQ records the state; this command does the work. Typing
`/plan:start <name>` already recorded `submitted → refining` for a registered
plan. If Agent HQ did not record it (a brand-new plan is not registered yet —
the hub shows a `hook.rejected` event), register it first (the hub UI, or your
project manager via `submit_plan`), then run `/plan start docs/plans/<name>.md`
to record it. The hub database — not any header line — is the single status
authority: never write a `Status:` line into the plan file.

## 1. Parse `$ARGUMENTS`
- Name-shaped first word (kebab-case: letters/digits/`.`/`_`/`-`): that is the
  plan name; remaining words are the title. Otherwise slugify the whole input,
  show it, let the user correct it. No input: ask — do not auto-generate.

## 2. Pre-flight
- Confirm `.zcode/worktree.json` sets `"autoSession": false`. If not, warn:
  this flow edits and commits main's plan file, which an auto-session cannot
  do (main checkout is read-only there). Continue only if the user accepts.
- Call the git-worktrees `worktrees_list` tool. A worktree named `<name>` or
  branch `zcode/<name>` with a `plan:` task already exists → offer to resume
  it (/plan:review) instead.
- `docs/plans/<name>.md` or `docs/plans/done/<name>.md` exists on main → name
  collision; ask for a different name.

## 3. Draft the plan ON MAIN
At `<main-checkout>/docs/plans/<name>.md`:
- Header block first: `Worktree: <name> · Branch: zcode/<name> · Started:
  <date>` — mechanical facts only; status lives in Agent HQ.
- Then the standard sections: `## Goal`, `## Context`, `## Decision` (what we
  have now, what this changes, what it is worth, the decision asked,
  recommendation), `## Steps` (markdown checkboxes — they drive Agent HQ's
  progress count), `## Dependencies`, `## Verification Criteria` (concrete
  checks /plan:verify will audit), `## Documentation`, `## Status Log`.
  Open questions listed explicitly.
- Commit locally with an explicit pathspec — never push, never a bare commit:
  `git -C <main> commit -m "plan: <name> — draft, <title>" -- docs/plans/<name>.md`

## 4. Create the worktree
Call the git-worktrees `worktrees_create` tool with `{name: "<name>"}` —
nothing else: no `agentId`, no `sessionId`, no `task` on create (the plan is
worked by THIS conversation; an agentId would lock the worktree). Default base
unless the user asked otherwise. On `isError`: show the message, stop — never
fall back to raw `git worktree` commands.

## 5. Label it
Call `worktrees_set_task` with `{name, task: "plan: <title>"}` — task only,
no agentId. This label is the reverse-lookup registry for /plan:review,
/plan:implement, /plan:verify, /plan:done and /plan:abandon.

## 6. Keep Agent HQ in the loop
If the plan is not registered yet, register it now (hub UI, or your project
manager via `submit_plan`) so progress and state are tracked from the first
draft. Discoveries that change the plan update MAIN's plan file with a local
pathspec commit as they land.

## 7. Scope it with the user
Walk the draft; record every decision in the plan file as it lands, each
refinement its own local pathspec commit on main. If the worktree will need
gitignored env files, mention `copyFiles` in `.zcode/worktree.json` (never
copy secrets manually).

## 8. Report
One block, one line each: plan file (main, copyable path) · worktree path ·
branch · hub state · reminder: plan edits = local commits on main; code =
commits on `zcode/<name>` ·
`Next: /plan review docs/plans/<name>.md` ·
`/plan abandon docs/plans/<name>.md` to give up.
