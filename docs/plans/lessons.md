# Lessons

The repository's accumulated lessons. Consult when drafting verification criteria and when
deciding how to avoid known mistakes. Append new lessons at the top; one lesson per entry, with
the date and the evidence that taught it.

- **2026-10-24 — Never pipe `dotnet test` output; redirect to a file.** A CLR-aborted testhost can
  orphan the pipe and wedge the pipeline for minutes while doing no work (observed: 15 minutes at
  0.9 s CPU, zero test output). `timeout` kills only the leader process, not the orphan holding
  the pipe. Redirect (`> log 2>&1`) and bound with `timeout --kill-after=N`. `scripts/test.sh`
  does both.
- **2026-10-24 — The suite is fast; the loop was slow.** The full suite runs in ~5 s and one
  class in ~5 s with `--no-build`; a bare `dotnet test` pays a rebuild (~15 s) plus host startup
  (~10 s) every time. Use `scripts/test.sh [filter]`: build once, then filtered `--no-build`
  runs. After editing code, always rebuild before a `--no-build` run — a stale DLL "reproduces"
  failures that are already fixed.
- **2026-10-24 — MCP results are camelCase on the wire regardless of authored casing.** The SDK
  serializer applies the naming policy; PascalCase-authored anonymous properties reach clients as
  camelCase, and tests asserting PascalCase can never pass. Author camelCase so the code reads
  like the wire.
- **2026-10-24 — Per-test isolation without per-test hosts.** A second host per test exhausts
  inotify instances (limit 128 here). Scope shared state instead: one host per class, one
  registered repository per test — anything that routes by repository (digest, role inbox) is
  then isolated per test.
- **2026-10-24 — A schema CHECK must restate the service rule, not a stronger one.** 0002's CHECK
  demanded the role wildcard for every zcode recipient while the answer flow addresses concrete
  sessions; five test failures were all that one mismatch. When a constraint and a code path
  disagree, decide from the plan which is the contract before "fixing" either side.
