<!-- agent-hq v1 -->
---
description: Adversarially verify an implemented plan
---
Adversarial verification, not re-implementation: $ARGUMENTS.

Read the Verification Criteria, the Status Log evidence, and the lessons. Work through each criterion: run the tests and commands yourself; inspect the recorded commits' diffs. Run in a different session than the one that ran `/plan implemented`, or act as OpenClaw's judge.

Then run `/plan pass <file>` or `/plan fail <file> <reason>` — this command only enters verification; it never moves the plan out of `verifying` itself.
