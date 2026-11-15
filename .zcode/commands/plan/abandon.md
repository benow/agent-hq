<!-- agent-hq v1 -->
---
name: plan:abandon
description: Abandon a plan — remove its worktree and branch, archive the plan, keep the record
argument-hint: <plan path or name> [reason]
---

# /plan:abandon — abandon the plan, keep the record

The plan file on main is durable by design: abandon never deletes it. What
abandons is the CODE container — worktree and branch — with the plugin's
snapshot as the safety net for anything uncommitted.

Typing `/plan:abandon <file> <reason>` already recorded the abandonment in
Agent HQ (legal from any state) and kept the reason in the hub history. The
reason should be restated in the plan's abandonment record.

## 1. Identify the plan
- Path-or-name: `/` or `.md` in `$ARGUMENTS` → path, name = basename minus
  `.md`; otherwise the name. Resolve on main; require the `Worktree:` header
  (a legacy plan is not part of this flow — say so and stop). First word
  after the file, if any, is the reason.

## 2. Show what abandoning means — then confirm
Gather, and show the user before touching anything:
- worktree state via the git-worktrees `worktrees_status` tool: commits on
  `zcode/<name>` (`git log --oneline main...HEAD`), dirty file count, size
- what happens: worktree removed · branch `zcode/<name>` deleted (it is
  unmerged — this is a force operation) · uncommitted changes auto-snapshotted
  to a restorable directory by the plugin · plan file KEPT, archived
- because the branch is deleted, capture the branch's commit list in the
  plan's abandonment record NOW (step 3) — after deletion it survives only in
  reflog until GC.
Require an explicit go from the user. No go → stop, nothing touched.

## 3. Write the abandonment record on main
Edit `docs/plans/<name>.md` BEFORE the removal (the worktree diff is still
available as evidence). Append an **Abandoned** section: why it was abandoned,
what (if anything) was built — the branch commit list verbatim — and what
reviving the work would take. Commit with an explicit pathspec:
`git -C <main> commit -m "plan: <name> — abandoned, <one-line reason>" -- docs/plans/<name>.md`

## 4. Remove the worktree and branch
Call the git-worktrees `worktrees_remove` tool with
`{name: "<name>", force: true, deleteBranch: true}` — force is required
because the branch is unmerged; the plugin still snapshots uncommitted work
first. On `isError`: show the message and stop — never fall back to raw git
commands.
- Worktree already absent (cleanup sweep): skip the tool call; delete the
  branch directly only if it still exists (`git branch -D zcode/<name>` — the
  user already confirmed the force semantics in step 2). Run the git-worktrees
  `worktrees_prune` tool if state entries look stale.

## 5. Archive and commit on main — never push
- Append the snapshot path (from the remove result) to the Abandoned section.
- `git mv docs/plans/<name>.md docs/plans/done/<name>.md`, commit once with
  explicit pathspecs covering both paths. Local commit only: it rides the
  next push. Do not push, do not fire deploys for an abandonment. Agent HQ
  records the archive move itself.

## 6. Report
One block:
- abandoned: worktree removed · branch deleted · snapshot at <path> (if any)
- plan archived at `docs/plans/done/<name>.md` with the abandonment record
- hub shows the plan `abandoned` with the reason in its history
- leftovers check via `worktrees_list` + `git branch --list "zcode/<name>"`:
  gone ✓
- `Next:` none — or, if the user signalled a retry intent,
  `/plan start <new-name>` noting the old plan as prior art.
