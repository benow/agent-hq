<!-- agent-hq v1 -->
---
name: plan:done
description: Finish a verified plan — audit completeness, archive it, merge the worktree branch, push once, clean up every trace
argument-hint: <plan path or name>
---

# /plan:done — land the plan

The verify gate's far side: this runs only after verification passed. A plan
may land as complete, or complete with acknowledged deferrals — never as a
silent partial. It leaves zero leftovers: worktree gone, branch gone, code on
main and pushed, plan archived in `docs/plans/done/`.

Typing `/plan:done <file>` already recorded `verified → done` — Agent HQ
enforces that a plan can only be done from `verified`; if the hub rejected it,
the plan was not verified yet: stop and run /plan:verify first.

## 1. Identify the plan
- Path-or-name: `/` or `.md` in `$ARGUMENTS` → path, name = basename minus
  `.md`; otherwise the name. Resolve on main; require the `Worktree:` header.
  Check the hub: status `verified` → proceed. Anything else → confirm with
  the user before proceeding (and expect the hub to have flagged it).

## 2. Pre-flight on main
- `git status --porcelain` — staged files you did not stage are not yours to
  commit (shared checkout). Foreign staged content → stop and report.
- `git fetch origin`, then `git log --oneline origin/main..HEAD`: every local
  commit must be a `plan: <name> —` commit of THIS plan or explicitly
  intended. Anything else (another agent's local work) → stop, show the list,
  coordinate — never sweep silently.

## 3. Final commit in the worktree
Using `git -C "<worktree-path>"` (its index is private to this plan):
- `git status --porcelain` → review what is uncommitted; eyeball the diff
  (`git diff --stat`, untracked list) for junk: build outputs, caches,
  anything secret. Remove/exclude junk, then stage the reviewed remainder
  (`git add -A` is acceptable INSIDE the worktree, only after this review).
- Commit with a real message. Nothing left → skip.
- `git log --oneline main...HEAD` — capture the branch's commit list for the
  report.

## 4. Audit completeness — the deferral gate
Build the completion ledger from the plan itself: every commitment (each
phase, deliverable, Verification Criterion) → implemented / partial / not
implemented, evidenced against the branch. If a **Verification Evidence**
section exists (from /plan:verify), cite it per criterion instead of
re-deriving; spot-check FAIL-adjacent and Deferred items yourself.
- Everything implemented → done.
- Gaps exist → write them into the plan as an explicit **Deferred** section,
  one entry per gap: what was not implemented, why, and what finishing it
  takes. Present the gap list to the user verbatim and require explicit
  acknowledgement that each may remain unimplemented.
- User declines or is unavailable → STOP: report what remains and point back
  to `/plan implement docs/plans/<name>.md`.
- Never mark gaps complete without evidence; never bury a gap in prose.

## 5. Archive the plan on main
- `git mv docs/plans/<name>.md docs/plans/done/<name>.md` (create `done/`
  first if absent); commit once with explicit pathspecs covering both paths:
  `plan: <name> — done, complete` (or `complete with N deferrals`).
  Agent HQ watches the plans directory and records the archive move itself —
  the registration re-points to `done/`; nothing else to do.
- If anything after this step aborts the finish, update the Status Log
  honestly (commit `plan: <name> — merge pending, conflicts`) before stopping.

## 6. Remove the worktree
Call the git-worktrees `worktrees_remove` tool with `{name: "<name>"}` —
after step 3 it is clean, so no force; the branch `zcode/<name>` is kept
deliberately until the merge lands.

## 7. Merge and push — in main
- `git pull --rebase` (local `plan: <name> —` docs commits rebase cleanly over
  an advanced origin; if the rebase itself conflicts, stop and report).
- `git merge zcode/<name>` with message `merge plan: <name> — <title>`.
  - Clean → continue.
  - Conflicts → attempt resolution first, using the plan and the branch
    history as ground truth (the plan file cannot conflict — it never lived
    in the branch). Test after resolving. If you cannot resolve cleanly:
    stop, list the conflicted files with each side's intent, and work through
    them with the user interactively.
- `git push origin main` — the ONE push: plan commits + merge together.

## 8. Clean up and verify zero leftovers
- `git branch -d zcode/<name>` — must succeed as merged; if it refuses,
  investigate, never force-delete blindly.
- Confirm with `worktrees_list`: no worktree `<name>`. Confirm
  `git branch --list "zcode/<name>"` is empty.

## 9. Report
One block: completion outcome (complete / with N acknowledged deferrals, one
line each mirroring the Deferred section) · commits landed (branch list +
merge sha) · push sha · plan archived at `docs/plans/done/<name>.md` ·
leftovers check: worktree gone ✓ branch gone ✓ · hub shows the plan `done`.
