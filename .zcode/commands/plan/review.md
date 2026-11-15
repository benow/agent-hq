<!-- agent-hq v1 -->
---
name: plan:review
description: Review the plan — hunt gaps, inconsistencies and trip hazards, rewrite it with fixes
argument-hint: [plan path or name]
---

# /plan:review — harden the plan

The plan's canonical copy lives ON MAIN at `docs/plans/<name>.md` (header
block carries `Worktree:` / `Branch:` / `Started:`). Review edits that copy;
the worktree holds code only — never write the plan inside the worktree.
Status lives in Agent HQ — never write a `Status:` line into the file.

Typing `/plan:review <file>` already recorded `refining → in_review`.

## 1. Pick the target
- `$ARGUMENTS` contains `/` or ends `.md` → it is a path; plan name is the
  basename without `.md`. Otherwise it is the name directly.
- No argument: scan main's `docs/plans/*.md` (NOT `done/`) for files with a
  `Worktree:` header — those are this flow's in-flight plans. Exactly one →
  use it. Several → list them (name · hub status · title) and ask. None →
  suggest /plan:start.
- Cross-check with the git-worktrees `worktrees_list` tool: a `plan:` task
  with no matching main plan file is an orphaned worktree — flag it, do not
  adopt it silently.

## 2. Load context
- Read the plan file from main — missing or unreadable: stop and report, do
  not scan for substitutes. Check the plan's status in Agent HQ (hub, MCP
  `get_overview`) so you review the state the hub says it is in.
- From the header's `Worktree:` name, call `worktrees_status`: recent commits
  and dirty state show how much implementation already exists — review against
  that reality, not a blank slate.
- If the header names a worktree that no longer exists, say so explicitly:
  the plan survived on main — that is the design — but re-creating the
  worktree is the user's call, not yours.

## 3. Hunt
Read as a skeptical junior dev about to implement this. Check, in order:
- **Gaps** — unstated prerequisites, missing steps, files or APIs the plan
  names that do not exist (verify with quick greps; a plan referencing a
  nonexistent file is the classic trip hazard), unowned edge cases.
- **Inconsistencies** — sections that contradict each other; plan vs repo
  reality (naming, paths, current behavior).
- **Ambiguity** — any instruction readable two ways; vague quantifiers
  ("some", "handle errors", "etc."); undefined terms.
- **Repo trip hazards** — the cross-cutting AGENTS.md rules and the relevant
  `AGENTS.{feature}.md` for every area the plan touches; read the
  repository's `docs/plans/lessons.md` and check the plan against every
  relevant lesson.
- **Verification** — the plan must state how to prove done: a
  **Verification Criteria** section listing the checks (tests to run,
  device/browser checks, live-verification steps) that /plan:verify and
  /plan:done will audit. If it does not exist, write it.

## 4. Rewrite — on main
Apply every fix directly in the main checkout's plan file, preserving its
structure and header block. Questions you cannot settle from the repo or the
conversation go in an explicit **Open questions** section — ask them now (they
block implementation), do not guess.
Commit locally with an explicit pathspec, never push:
`git -C <main> commit -m "plan: <name> — review, <one-line summary>" -- docs/plans/<name>.md`

## 5. Report
- findings applied: N, grouped by class (gap / inconsistency / ambiguity /
  hazard / verification)
- open questions for the user, if any — verbatim, blocking
- code-side snapshot from `worktrees_status`: commits so far, dirty state
- `Next: /plan implement docs/plans/<name>.md` — ideally a different session
  from the one that ran /plan:start; the same path handed to a fresh session
  resumes the work.
