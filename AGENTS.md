# Agent Directives

- You are running on Linux with bash. The solution is .NET 10; tests are xUnit.
- **AGENTS.md is a hub + feature files.** This file holds only cross-cutting directives that apply
  to all work: the domain rules, the working rules, testing, naming, deploy. Per-feature knowledge
  lives in the per-project `AGENTS.md` files — read the relevant one before starting feature work
  and record feature-specific discoveries there. Only knowledge that applies across features
  belongs here. See [Feature Knowledge Files](#feature-knowledge-files).
- AGENTS.md and the feature files must be kept current. When you discover a new pattern, changed
  tooling, or a stale instruction, update this file or the relevant feature file. Prefer concise
  directives over verbosity.
- **Check the request first.** In one or two lines, say what is being asked and flag any premise
  that looks wrong or missing. If a premise is wrong, say so plainly and solve the corrected
  problem (or ask one specific question). Do not silently accept a broken premise.
- **Doubt is not evidence.** To reopen a settled conclusion you must name a concrete reason in one
  line: a failing check, a contradicting fact, a specific error, a counterexample. A vague sense
  of uncertainty is not one. Do not revise just to agree: if pushed back without new evidence,
  restate the conclusion with its justification and ask what specific fact contradicts it.
- **Verify against outside facts, not by rethinking.** When a real check exists (tests, build, the
  source record, a command you can run), use it and let the result decide.
- **Fix easy failing tests** as part of the work rather than leaving the suite red. Report
  pre-existing failures you choose not to fix, with the reason.

<!-- agent-hq -->
## Development is managed via Agent HQ

This repository is **dogfooded**: its own plans live in `docs/plans/` and move through the
managed lifecycle. Before working:

1. Read `docs/plans/lessons.md` — the repository's accumulated lessons. Consult it when drafting
   verification criteria and when deciding how to avoid known mistakes. It may not exist until the
   first lesson is captured; that is expected.
2. Read the plan you are acting on, including its Status Log.
3. The Agent HQ MCP tools are self-describing; orient with `get_overview`, then stay current with
   `get_events(sinceSeq)` — never re-list.

The lifecycle, who may move what, and every `/plan` command are documented in
[docs/features/plan-lifecycle.md](docs/features/plan-lifecycle.md). The invariant that matters
most here: **the session that claims `implemented` must not claim `verified`** — verifier commands
run in a different session, and evidence goes in the plan's Status Log, not in a vague "done".

## Domain invariants (do not break these)

- **Agent HQ manages work; it never does it.** It does not edit code, run the user's tests, or
  interpret prose as a decision. Any change that makes the hub do the work is a design change,
  not a feature.
- **Evidence lives in the plan file; the database stays mechanical.** History rows carry who, when,
  which command, which commit, and a short reason. Never start storing narrative evidence,
  transcripts, or Status Log text in `plan_status_history`.
- **One state machine.** The `/plan` command path, the browser, `PATCH /plans/{id}/status` and the
  MCP `update_plan_status` tool all go through `PlanService`. Never add a second path that moves
  status directly in a store.
- **Separation of duties is soft but visible.** It warns and flags; it never blocks. Do not turn it
  into a hard refusal without a decision, and do not remove the flag.
- **`blocked` is a flag, not a status.** It is orthogonal to the lifecycle; the lifecycle never
  moves when a plan is blocked. Do not add a `blocked` status.
- **No AI summarisation in the hub.** Digests are deterministic. A summary produced by a model in
  Agent HQ is a defect: it is unreproducible and can silently drop the line that mattered.
- **Sessions are read through, not mirrored.** Only metadata needed for joins and events may be
  persisted. Message content belongs to the provider's index.
- **The hub is turn-based-first.** A project-manager agent re-derives its picture from one cheap
  call (`get_overview`) plus a cursor (`get_events(sinceSeq)`). Never make a tool that requires it
  to remember state between calls.

## Working rules

- **`dotnet test` is the gate.** Run it before claiming anything done.
- The .NET 10 test harness intermittently aborts before running any test (CLR `0x80131506`, no test
  output, usually the first run after a build). Re-run; do not treat it as a failure. CI retries
  once for this reason.
- `dotnet build` does not always refresh project references; if a change seems ignored, do a clean
  rebuild before debugging.
- **SQLite parameter names are case-sensitive** and Dapper binds only explicit names. Pass
  parameters as `@Name = value` with consistent casing, and alias snake_case columns explicitly
  (see `src/AgentHQ.Infrastructure/Stores/Columns.cs`). Open connections with `AgentHqDb.Open()`.
- **Every schema change is a new DbUp migration** in `src/AgentHQ.Infrastructure/Migrations/`,
  numbered after the highest existing file. Never edit an applied migration: DbUp journals each
  script by filename and will not re-run it. Migration files are embedded resources (the csproj
  globs `Migrations/*.sql`), so a new file needs no csproj edit — but it does need a clean rebuild
  to be picked up from a stale build.
- **No silent catches.** A `catch` that neither rethrows nor logs is a nothing-hole — it converts
  a failure into wrong-but-plausible behaviour. Narrow it to the named failure it handles and log
  a Warning naming the input and the fallback. Debug level is acceptable only for high-frequency
  watcher noise that has a visible escalation path elsewhere.
- **Never run a filesystem-wide walk** (`find /`, `rg /`). Scope every search to the repository.
- **Long-running commands need a deadline.** Wrap anything that can hang (network, a build under a
  watcher, a shell loop) in `timeout N …`; poll in short bounded calls rather than sleeping.
- **Removal is part of development.** When a mechanism is superseded, remove its code, tests and
  config flags in the same effort — dormant experiments are deleted, not parked. Git preserves
  them.
- **Automate at the third repetition.** A manual step done more than three times is a defect in
  the process; make it a script or a deploy step in the same effort. The deploy script is the
  example done right: one command, used by hand and by CI.
- **Deploy is done only when `check` passes.** A deploy that rolled back is not a deploy; a running
  process that is not the `current` release is not the new build.
- **Never commit or push unless asked.** The deploy script reads `git rev-parse HEAD` for release
  names; that is not an invitation to commit.
- **Commits, when asked for**: explicit pathspecs, never `git add -A`, and check
  `git status --porcelain` first. Do not sweep unrelated work into a commit.

## Naming and identity

Everything is named Agent HQ. There is exactly one spelling per surface:

- Projects, namespaces and types: `AgentHQ.*` (e.g. `AgentHQ.Api`, `AgentHQ.Core`), types
  `AgentHq*` (e.g. `AgentHqHost`, `AgentHqDb`).
- Runtime identity: lowercase-hyphen — `agent-hq.service`, `AGENTHQ_*` environment variables,
  `~/.local/share/agent-hq`, `scripts/agent-hq-deploy.sh`, the `AgentHQ.Api` binary.
- Prose: "Agent HQ". MCP server name: `agent-hq`.
- **`dev-bridge` is only the previous name.** It survives in exactly two places, both deliberate:
  the legacy-unit refusal in the deploy script and the legacy command-marker upgrade in the
  installer. Never introduce it anywhere else.

Adding a new surface means adding it in all four spellings above, consistently.

## Architecture governance

- **`AgentHQ.Core` has no I/O.** Domain models, the state machine, parsers (plan sections,
  checkboxes, lessons, `/plan` commands), slug rules and the digest builder live here. If it reads
  a file, opens a database, or calls `git`, it is in Infrastructure or a provider.
- **One composition root.** `AgentHqHost.BuildAsync` builds the real host and the in-memory test
  host. New services, endpoints and background loops are registered there, not wired ad hoc — the
  tests exist to exercise exactly the production composition.
- **Provider coupling is quarantined.** Knowledge of a worker agent's private schema lives only in
  its `IAgentProvider` implementation, behind a health/schema guard that degrades instead of
  throwing. Core and Infrastructure must not reference ZCode-specific shapes.
- **The ZCode provider never falls back to the real `~/.zcode` database.** The path is explicit
  (`AGENTHQ_ZCODE_DB`); a misconfigured or test host must degrade to "no sessions", never mirror
  live work.
- **REST and MCP stay in sync by construction.** MCP tools are thin wrappers over the same
  services the REST endpoints use. If a tool needs logic, the logic goes into the service.
- **Content root is the application directory.** `wwwroot/` and `Commands/` are served from
  `AppContext.BaseDirectory`, so behaviour is identical under `dotnet run`, systemd and the test
  host. Do not resolve either from the working directory.

## MCP tool rules

- **No optional parameters on MCP tool methods.** The SDK's argument binding fails on missing
  optional arguments; use required parameters with empty-string sentinels and trim them in the
  body. `long.MinValue` and the empty string are the established "not supplied" sentinels.
- **Tool descriptions carry the contract.** They are the agent's only documentation at call time:
  say what filters mean, and what to do instead of a more expensive call.
- **Embedded payloads are serialized as JSON text.** The SDK's serializer renders a `JsonElement`
  as `{"ValueKind": …}`; serialize payload objects yourself (see `GetEvents`).
- **Plain property names in returned anonymous objects.** MCP results are serialized by the SDK,
  not by the host's camelCase options, so name the properties the way you want them to appear.
- **Arguments are all required by contract.** The empty-string sentinels are documented in each
  tool's description, so a client is told to pass `""` rather than left to infer it.

## JSON and wire contracts

- Host responses use camelCase with nulls omitted (`ConfigureHttpJsonOptions` in the host). Event
  payloads use `AgentHqEvent.JsonOpts` — the same policy.
- **Status codes and event types are contracts.** Plan statuses serialize through
  `PlanStatusCodes.Code()` / `FromCode()`, never `ToString().ToLowerInvariant()`: the wire form is
  snake_case and multi-word (`in_review`, `plan.status_changed`). Adding a status means: the enum,
  `Code()` and `FromCode()`, `AllCodes`, the state machine map, the web UI's status list and action
  table, and a test that pins the wire form.
- Event type strings are constants in `EventTypes`. The web UI listens for known types *and* the
  default channel, so a new type appears in the activity panel without a UI change — keep that
  property when touching the SSE client.

## Testing

- **The E2E simulation is the acceptance test.** It plays every actor against the in-memory host:
  synthetic ZCode database writes, hook-fed `/plan` commands, plan-file edits through the real
  watchers, SSE replay, and the project-manager loop through an MCP client. A change to the
  lifecycle, the hook feed, or the MCP façade must keep it green, and a new cross-actor behaviour
  belongs in it.
- **Tests boot the real composition** (`AgentHqApp`) on temporary directories, with real watchers
  and real streams. Never point a test at `$HOME` or the live ZCode database; the live smoke tests
  are env-gated (`AGENTHQ_LIVE=1`) and read-only.
- **The provider is tested against a real-shaped database**, not mocked rows:
  `tests/AgentHQ.Tests/Fixtures/zcode_schema.sql` is a checked-in snapshot of the ZCode schema, and
  the schema guard is tested against bumped versions. Do not replace it with hand-built minimal
  fixtures; that is how binding mismatches survive review.
- **Watcher tests time-box polls instead of sleeping** and assert on observable outcomes (events,
  versions), not on internal timing.
- **The web UI has contract tests** (`WebUiContractTests`): assets load, no external URLs, every
  field the UI reads exists in the API response, the action route is what the UI calls. Changing a
  response shape means changing that test deliberately.
- **The installer is tested against a fake `HOME`**, twice, to prove idempotency and that
  unrelated settings keys survive.

## Logging and errors

- Serilog everywhere, with a `Subsystem` property (`watcher`, `provider`, `plans`, `hooks`, `mcp`,
  `sessions`, `events`, `db`) so logs are filterable by area.
- Ingest and transitions log at Information; watcher detail at Debug.
- **Errors must say what, why and what to do.** When catching, name the operation and the specific
  item, diagnose the likely cause, and if the fix is known say so (an operator should not have to
  open the source). The existing example: the deploy script names the legacy unit and prints the
  exact commands to retire it.
- **A failure a user could cause must become a visible event, not just a log line.** The hook feed
  records `hook.rejected` with the prompt and reason; do the same for any new ingest path.

## Dependencies

- **Evaluate, don't accumulate drift.** When touching a project, check
  `dotnet list package --outdated` and flag anything more than a few point releases behind.
- **Pin only with a reason on record**, next to the pin, with the condition to revisit.
- **Upgrade in dedicated commits**, one dependency per commit, never mixed with a feature.
- Keep the stack on .NET 10 unless the whole solution moves together.

## Documentation

- **`docs/` is for the public**; `AGENTS*.md` and `docs/plans/` are for contributors. Docs pages
  describe *what* the system does; `docs/plans/` records the decisions and *why*.
- **After changing a feature, update its documentation page** (`docs/features/*.md`) and its
  feature knowledge file in the same effort. A stale doc is a defect, and a reader acting on it
  gets burned.
- **The docs index is `docs/README.md`**; the front door is `README.md`. A new page must be linked
  from one of them, or nobody will find it.
- Links between docs are relative paths; check they resolve before finishing.

## Deploy and operations

See [docs/operations.md](docs/operations.md) for the operational contract. The rules that bind
code changes:

- One deploy path: `scripts/agent-hq-deploy.sh`, used by hand and by CI (`STAGE_DIR`). Do not add a
  second way to ship the service.
- Everything the service creates lives under `$AGENTHQ_DATA`; everything the unit needs is in the
  release directory. A deploy must never need to write into the database or the logs.
- Startup completes its first sync before the port opens; health waits are seconds, not
  milliseconds. Do not move work that can fail after `RunAsync` without a visible health signal.
- The service binds `127.0.0.1` and has no authentication. Any change that widens exposure is a
  security decision, not a configuration change.

## Feature Knowledge Files

Per-feature knowledge accumulates in its own file, not here. Before working in an area, read its
file; during the session, append discoveries, pitfalls and conventions to it.

| Area | File | Covers | Read when working on |
|---|---|---|---|
| Plan lifecycle & parsers | [src/AgentHQ.Core/AGENTS.md](src/AgentHQ.Core/AGENTS.md) | State machine map, short-cut entry, separation of duties, soft dependencies, plan/slug/checkbox/lesson parsing, digest rules | Any lifecycle change, a new command, progress parsing, digest output |
| Database & services | [src/AgentHQ.Infrastructure/AGENTS.md](src/AgentHQ.Infrastructure/AGENTS.md) | Dapper/SQLite traps, column aliasing, self-write suppression, both watchers and their debounce/backstop, event store, lessons indexing | Migrations, stores, watchers, `PlanService`, session sync |
| ZCode provider | [src/AgentHQ.Providers.ZCode/AGENTS.md](src/AgentHQ.Providers.ZCode/AGENTS.md) | Schema guard and the tested version, read-only access, part-type mapping, what a schema bump requires, live smoke | Anything reading the ZCode database |
| Host, REST, MCP, SSE | [src/AgentHQ.Api/AGENTS.md](src/AgentHQ.Api/AGENTS.md) | Composition root, endpoint and wire shapes, SSE and `SseHub`, hook ingest order, MCP binding rules, Serilog setup | An endpoint, a tool, the stream, the hook feed |
| Web UI | [src/AgentHQ.Api/wwwroot/AGENTS.md](src/AgentHQ.Api/wwwroot/AGENTS.md) | No-build-step rule, action-table parity with the state machine, stream reconnect, overlay dismissal, scrollbars, top-bar measurement, contract tests | Any change to `index.html`, `app.js`, `ui.css` |
| `/plan` commands & installer | [src/AgentHQ.Commands.ZCode/AGENTS.md](src/AgentHQ.Commands.ZCode/AGENTS.md) | Template format and version markers, hook command shape, idempotent merge, legacy-name upgrade, AGENTS.md snippet | Changing a command template or the installer |
| Tests | [tests/AgentHQ.Tests/AGENTS.md](tests/AgentHQ.Tests/AGENTS.md) | Harness shape, fixture rules, how to add an actor to the E2E simulation, live smoke gating, flake handling | Adding or debugging tests |

- If `AGENTS.user.md` exists in the repository root, read it — it holds local, personal working
  preferences (gitignored, never committed).

<!-- agent-hq -->
## Development is managed via Agent HQ

Plans live in `docs/plans/` and move through a managed lifecycle (`/plan` commands).
Before working: read `docs/plans/lessons.md` — the repository's accumulated lessons.
The Agent HQ MCP tools are self-describing; orient with `get_overview`.
