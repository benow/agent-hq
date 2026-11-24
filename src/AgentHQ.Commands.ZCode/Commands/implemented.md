<!-- agent-hq v3 -->
---
description: Claim completion of a plan with evidence
---
Claim completion with evidence: $ARGUMENTS.

Append to the Status Log: a summary of what was implemented, commit hashes and subjects, tests run with results, documentation updated, lessons consulted and how each was accounted for, and deviations from the plan. If you are in the `fixing` phase: root-cause the verification failures, state the fix, and refine the drafted lesson if it misses the nuance.

**Gates first.** Before anything else, read the Verification Criteria and state, as a visible `Gates:` list, any criterion that cannot be evidenced yet — with exactly what unblocks it and who must act (a tag push, a credential, a human decision). A gate is not a deviation: deviations are choices you made; gates are things waiting on someone else. If a gate needs the operator, end your claim with the one specific question that unblocks it. A gate buried in prose is how verifications fail late — surface it here, and if a gate is the *only* thing outstanding, flag the plan blocked (`/plan block <file> <reason>`) instead of leaving it silent.

Be precise — a different session will check every claim against the Verification Criteria; overclaiming is the failure mode this evidence exists to catch. Never tick a step whose named content was not built; record it as not-done with the reason instead. Do not verify your own work.
