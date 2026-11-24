<!-- agent-hq v3 -->
---
description: Adversarially verify an implemented plan
---
Adversarial verification, not re-implementation: $ARGUMENTS.

Read the Verification Criteria, the Status Log evidence, and the lessons. Work through each criterion: run the tests and commands yourself; inspect the recorded commits' diffs. Run in a different session than the one that ran `/plan implemented`, or act as OpenClaw's judge.

**Gates before verdicts.** First enumerate every criterion that has no recorded evidence in the Status Log. If one is gated on a human action (a push, a credential, a decision the code cannot make), do not burn a fail cycle on it: ask the operator the one specific question that unblocks it, flag the plan blocked (`/plan block <file> <reason>`), and keep `verifying` until it is answered. Reserve `/plan fail` for what the implementation can actually fix — missing tests, false claims, unrecorded deviations.

Then run `/plan pass <file>` or `/plan fail <file> <reason>` — this command only enters verification; it never moves the plan out of `verifying` itself.
