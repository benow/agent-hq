<!-- agent-hq v3 -->
---
name: plan:verify
description: Adversarially verify the plan — re-run every automatable check, record per-criterion evidence, deliver a verdict
argument-hint: <plan path or name>
---

# /plan:verify — prove the plan is implemented

Adversarial verification, NOT re-implementation: you check that everything the
plan promised is actually there — by running things yourself and reading the
diffs — and you never fix what you find. A gap gets recorded, not repaired.
Fixing is /plan:implement's job.

Typing `/plan:verify <file>` already moved the plan to `verifying` (from
`implemented`; from `implementing`/`fixing` Agent HQ first records the
implementer's claim and warns). Run this in a DIFFERENT session from the one
that ran /plan:implement — if you are the same session, say so once and
compensate: treat the implementation report as a claim, not evidence.

## 1. Identify the plan
- Path-or-name: `/` or `.md` in `$ARGUMENTS` → path, name = basename minus
  `.md`; otherwise the name. Resolve on main; require the `Worktree:` header.
- Already `verified` → re-verify (append fresh evidence, dated). Any other
  hub state → confirm with the user before proceeding.

## 2. Load the contract
- The plan on main: Decision, every phase/deliverable, the **Verification
  Criteria** section, trip-hazard notes, any Deferred items.
- Code reality: the git-worktrees `worktrees_status` tool for the header's
  worktree, plus `git -C "<worktree-path>" diff main...HEAD --stat`, the
  commit list (`git log --oneline main...HEAD`), and the actual diffs of the
  recorded commits.
- The implementer's claim in the Status Log: read it, then re-derive it.

## 3. Work each criterion
For every Verification Criterion (and every plan commitment lacking one):
- **First: build the criterion → test map.** Before running anything, write
  a table in your working notes: every criterion → the named test(s) that
  prove it (`sqlite-xunit` filter), plus the Postgres/live equivalent where
  the criterion is store-level. Run the mapped tests by name. A criterion
  whose coverage you can only DESCRIBE is not covered — it is a gap, and this
  is the single most expensive lesson in this repository (part 3 failed
  verification three times because each pass checked one half of a behaviour
  and credited the other; a second failure of the same criterion is a
  method defect, not new evidence).
- **A failing expectation is not automatically a code failure.** When a test
  fails, establish what is wrong before recording a FAIL: read the contract
  line the criterion cites. If the expected value contradicts the contract,
  the test (or your reading of it) is wrong — say so, cite the line, and fix
  the test in the plan's fix cycle rather than reporting a defect that does
  not exist. Part 3's second verify accused the code of a bug the contract
  did not have.
- **A behaviour test must be mutation-checked when it is new or load-bearing.**
  Break the behaviour in the code (a one-line revert), confirm the test goes
  red, restore. A test that passes against a broken implementation is worse
  than no test: it is a false PASS.
- Automatable → RUN it yourself: the tests, builds, greps, API probes the
  criterion names. Never trust a green claim — re-run it and record the
  actual output (trimmed to the verdict lines).
- Inspectable → read the diff for that commitment; confirm the change exists,
  is wired (registered/routed/referenced, not dead code), and matches the
  plan's intent. Note anything implemented but diverging from the plan.
- User-required → surface it and work through it WITH the user: drive the
  browser pass, prep device-check or elevated-run steps, run what you can and
  hand over only what genuinely needs their hands or eyes. Record each as
  user-verified/pending with what was checked.
- Criterion references something absent → that is a FAILURE entry, evidence
  attached. Do not soften it, do not fix it.

## 4. Record the evidence
Append a **Verification Evidence** section to the plan on main — one entry per
criterion: criterion → evidence (command + outcome, or diff pointer, or user
sign-off) → PASS/FAIL/PENDING-user. Commit with an explicit pathspec.

## 5. Verdict
- **All PASS** (user items signed off) → `/plan pass docs/plans/<name>.md`
  records `verifying → verified`, then `/plan done docs/plans/<name>.md` to
  land it — do NOT stop and ask "merge or not?" between the two. `done` is
  the operator's gate, and typing `verified` is not it: a verified plan that
  is left unmerged and unarchived is an unfinished job, and asking again
  after the operator already asked for it is a defect. `done` performs the
  merge, the push, the archive and the cleanup (its template, step 7).
- **Any FAIL or declined user item** → `/plan fail docs/plans/<name>.md
  <reason>` records `verifying → fixing`; the reason is the failure summary.
  Classify each failure: a foreseeable oversight becomes a drafted lesson in
  `docs/plans/lessons.md`; a genuinely novel problem gets a note only.
  `Next: /plan implement docs/plans/<name>.md` (fix, then re-verify).
- **User items still pending** → leave the verdict unrecorded rather than
  guessing; work them through this session or report them as pending.

## 6. Report
- verdict line: N/N criteria pass | k FAIL | j pending-user
- failures verbatim (they ARE the punch list)
- worktree snapshot: commits, dirty state
- the filled-in `Next:` line from step 5.
