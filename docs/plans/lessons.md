# Lessons

The repository's accumulated lessons. Consult when drafting verification criteria and when
deciding how to avoid known mistakes. Append new lessons at the top; one lesson per entry, with
the date and the evidence that taught it.

- **2026-11-23 — A plan that fails verify twice is a verification-method defect, not new
  evidence.** Part 3 failed verification three times; each pass tested one half of a
  behaviour and credited the other, and the second pass accused the code of a bug the
  contract did not have (a live lease is leased, not queued — `commandsPending` counts
  un-leased envelopes). Before running anything, map every criterion to the named test that
  proves it, and run those by name; when a test fails, check the contract line the criterion
  cites before recording a FAIL; mutation-check any new load-bearing test (break the code,
  watch it go red, restore). And a `verified` plan is not landed: `/plan done` merges, pushes,
  archives and cleans up, and the operator asking for it is the gate — never stop between
  `verified` and `done` to ask again.

- **2026-11-22 — A lease has two halves; test the expiry half too.** Part 3 shipped a 60 s
  command lease. The suite proved "a leased envelope is not returned again before expiry" and
  the conformance kit's check was described as "lease redelivery inputs", but no code path and
  no test redelivered an *expired* lease: `ClaimAsync` selects `WHERE lease_until IS NULL` in
  both stores, the `now` argument it receives is unused, and nothing but a result clears the
  lease — so a crashed spoke's command is lost permanently while the docs and rule 4 said
  "expiry redelivers". Evidence: a probe enqueued, claimed, backdated `lease_until` five
  minutes, and claimed again — 0 envelopes. A check whose name promises a behaviour it never
  asserts hides a missing implementation better than no check does. For any timeout or
  visibility window, assert both sides: too early → withheld, past the deadline → redelivered.

- **2026-11-16 — The 128-inotify budget bites the suite once a second host-per-class fixture
  lands.** With `PostgresHubTests` added (one in-memory host per test, real watchers), the full
  suite intermittently fails 1–2 watcher-heavy tests (e.g. ExchangeTests) with `IOException:
  inotify instances reached`; the same tests pass in isolation. The deployed hub holds many
  instances already (scratch 16 + deployed 15 observed 2026-10-31). When a run shows that
  IOException, re-run the failed class alone before debugging anything — it is contention, not
  a regression. The durable fix (raising `fs.inotify.max_user_instances` on dev machines, or
  trimming watcher lifetime in test hosts) is open.
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
