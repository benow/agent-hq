# Tests — feature knowledge

xUnit, one assembly. `dotnet test` is the gate for every change.

## The fast loop

- **`scripts/test.sh [filter]` is the iteration loop**: build once, then `--no-build` runs
  filtered to one class (or a raw `--filter` expression). The full suite is ~5 s and a single
  class ~5 s once built; a bare `dotnet test` pays a rebuild plus host startup on every call.
- **Never pipe test output** (`| tail`, `| tee`): a CLR-aborted testhost can orphan the pipe and
  wedge the pipeline for minutes while doing no work. Redirect to a file; `scripts/test.sh` does.
  It also bounds runs with `timeout --kill-after` and retries the documented `0x80131506`
  first-run abort once.
- **Always rebuild before a `--no-build` run** that follows a code edit — a stale DLL
  "reproduces" failures that are already fixed.

## The harness

- **`AgentHQApp` boots the full host in memory via `AgentHqHost.BuildAsync`** — the same composition
  root as production — on temporary directories: its own SQLite file, a synthetic ZCode database
  (`ZCodeFixture`), a real repository tree with `docs/plans/`, real `FileSystemWatcher`s, real SSE.
  It is an `IAsyncLifetime` fixture.
- **Each test class owns its own directory tree.** Classes run in parallel and repositories are
  matched by session directory, so sharing a tree between classes produces cross-talk that looks
  like a logic bug.
- **Isolation inside a class is per test via `NewRepoAsync()`, not via a second host**: a host per
  test exhausts inotify instances. Anything that routes by repository (the Exchange digest, the
  role inbox) is isolated by giving each test its own registered repository; rate windows are per
  sender, so no test may burn a sender's budget another test shares.
- Helpers on the fixture cover the common actors: `RegisterRepositoryAsync`, `NewRepoAsync`,
  `SubmitPlanAsync`, `HookAsync(sessionId, prompt)` (the hook feed), plan reads.
- `AgentHQApp.Log` captures into `XunitLogSink` (in memory — console output is invisible under the
  xUnit host). A failing test that needs log evidence calls `XunitLogSink.Drain()` in its
  assertion message.
- **Trigger watchers by writing files, then poll time-boxed** for the observable result (an event,
  a version bump). Never sleep a fixed time and assert once.
- Nothing may touch `$HOME` or the live ZCode database: the provider path is always explicit
  (`AGENTHQ_ZCODE_DB` is never defaulted in tests), and the installer tests run against a fake
  `HOME`.

## What lives where

| File | Covers |
|---|---|
| `UnitTests.cs` | State machine, parsers, slug, `/plan` parsing, digest projection and budget elision |
| `ProviderTests.cs` | ZCode provider against the checked-in schema fixture, including bumped-version guards |
| `RepositorySessionTests.cs` | Repository registration/discovery, session mirror, listing filters |
| `WatcherEventTests.cs` | Plan-file edits through the real watchers, self-write suppression, SSE replay |
| `PlanLifecycleTests.cs` | Transitions through the service: legal/illegal, short-cut, SoD, blocking, dependencies |
| `ExchangeTests.cs` | The Exchange end to end on the shared class host: posting and validation, the claim race and the D14 claimant rule, digest boundaries, blobs, expiry, cursors/watch, MCP round trip |
| `ExchangeDeputyTests.cs` | Pure-Core actor parsing and the digest's escaping/budget rules |
| `ExchangeStateMachineTests.cs` | The Exchange state machine's transitions in isolation |
| `McpClientTests.cs` | The MCP façade through a real SDK client |
| `E2eSimulationTests.cs` | The whole loop, every actor (see below) |
| `WebUiContractTests.cs` | UI assets, no external requests, fields the UI reads, the command route |
| `InstallerTests.cs` | Idempotent install into a fake HOME |
| `LiveSmokeTests.cs` | Read-only checks against the real ZCode db, `AGENTHQ_LIVE=1` only |

## Adding to the E2E simulation

The simulation is the acceptance test: it plays the human, the worker agent and the project
manager. When you add cross-actor behaviour, extend it rather than writing a new harness:

- the **worker** is `HookAsync(sessionId, "/plan …")` plus file edits (Status Log, checkboxes);
- the **project manager** is MCP client calls against the TestServer;
- the **consumer** is an SSE reader held open across the action, asserting the events and the
  `Last-Event-ID` replay path.

Both verification outcomes are covered (`FullLoopPassPath`, `FailPathReworkLoop`) and the herding
queues are asserted (`OverviewShowsHerdingQueues`). A new command or a new event type belongs in
one of those flows.

## Fixtures and the live gate

- `Fixtures/zcode_schema.sql` is a checked-in snapshot of the real ZCode schema. It is the reason
  provider binding bugs are caught; do not hand-write a smaller fixture in its place.
- Live smoke tests are `[SkippableFact]` gated on `AGENTHQ_LIVE=1`, read-only, and must stay that
  way. They are the only place that asserts an assumption about real data (for example, that
  message writes bump `session.time_updated`).

## Flakes

- **CLR `0x80131506` with no test output**, usually the first run after a build: the .NET 10 harness
  aborting before any test runs. Re-run; never treat it as a failure. CI retries once for this
  reason.
- A test that is green alone but red in the suite is almost always shared state (a directory tree, a
  port, a database path) or a timing assumption. Fix the isolation; do not add sleeps.
