<!-- agent-hq v1 -->
---
name: plan:implement
description: Implement the plan — all code work in its worktree, self-verify, claim completion with evidence
argument-hint: <plan path or name>
---

# /plan:implement — build the plan

Two trees, one rule each: CODE happens in the plan's worktree (branch
`zcode/<name>`); the PLAN happens on main (`docs/plans/<name>.md`, local
commits, pushed only at /plan:done). You stop before merge — /plan:done is
the user's gate.

Typing `/plan:implement <file>` already recorded `in_review → implementing`.

## 1. Identify the plan and check the hub state
- `$ARGUMENTS` contains `/` or ends `.md` → it is a path; plan name is the
  basename without `.md`. Otherwise it is the name directly. Resolve against
  main's `docs/plans/`; the file must carry a `Worktree:` header — no header
  means a legacy plan outside this flow: say so and stop.
- Read the plan's status in Agent HQ (hub, MCP `get_overview`). `in_review` →
  proceed. `implementing` → this is a RESUME: call `worktrees_status` on the
  header's worktree, read the plan, continue where it leaves off.
  `refining`/`draft` → review was skipped: warn and get the user's explicit
  go-ahead first. If Agent HQ does not know the plan at all, register it
  (hub UI, or the project manager via `submit_plan`) before working.
- Header names a worktree that no longer exists → stop and report: the plan
  survived on main; the branch `zcode/<name>` still holds prior code;
  re-creating the worktree is the user's call.

## 2. Read before building
The repository's AGENTS.md cross-cutting rules plus the `AGENTS.{feature}.md`
for every area the plan touches — before the first edit, not after the first
bug. Read `docs/plans/lessons.md` and account for each relevant lesson. The
plan's own trip-hazard notes (from /plan:review) are part of the contract.

## 3. Work contract
- Do ALL code work inside the worktree: `git -C "<worktree-path>"` and
  absolute paths for everything. Never modify, commit, or push code in the
  main checkout.
- Commit small and often on `zcode/<name>`, explicit pathspecs only, real
  messages — never a bare commit, never `git add -A`.
- Tick the plan's Steps checkboxes as you complete them — they are what Agent
  HQ counts as progress. Record any deviation in the Status Log. Discoveries
  that change the plan (design pivots, found constraints, settled open
  questions) update MAIN's plan file with a local pathspec commit. The plan
  is the record; do not let it drift from reality.
- Documentation is part of the deliverable: write/update it as the plan's
  Documentation section specifies, alongside the code.
- New code logs everything; catches are specific, named, and rethrow or log —
  never silent.

## 4. Self-verify
Run what the plan's Verification Criteria call for: the relevant test suites
per the repo's test bar, typecheck/build, diff eyeball
(`git -C <worktree> diff main...HEAD --stat` and skim). Fix what your own
work broke. Report pre-existing failures you choose not to fix, with output —
never imply green when it is not.

## 5. Claim with evidence
Write the claim into the plan's Status Log on main: summary, commit hashes
and subjects, tests run with results, documentation updated, lessons consulted
and how, deviations from the plan. Then the claim itself:
`/plan implemented docs/plans/<name>.md` (typed by the user, or via the hub) —
it records `implementing → implemented`, which is what puts the plan in the
verification queue. Never claim inside this command.

## 6. Report and stop
- what was built (files + commits on `zcode/<name>`)
- self-verification results (suites, outcomes)
- what a verifier should run, and anything the user must check by hand
- anything left open or deferred
- `Next: /plan verify docs/plans/<name>.md` — in a DIFFERENT session from
  this one; the session that claims completion cannot verify it (Agent HQ
  flags that permanently).
