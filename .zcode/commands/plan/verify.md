<!-- agent-hq v1 -->
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
  records `verifying → verified`.
  `Next: /plan done docs/plans/<name>.md` — the user's gate.
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
