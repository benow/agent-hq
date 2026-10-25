# AGENTS

Agent HQ is a local web service (127.0.0.1) that is the headquarters for agent-managed work: a
project-manager agent (OpenClaw, via MCP) and development agents (ZCode, behind an
`IAgentProvider` abstraction) meet here, and agent-to-agent conversation flows through it. It
manages the work; it never does it. The lifecycle is not specific to development — plans for
research, operations or writing run through the same states.

## Layout

- `src/AgentHQ.Core` — domain models, plan lifecycle state machine, parsers (plan sections,
  checkboxes, lessons, `/plan` commands), slug rules, digest builder. No I/O.
- `src/AgentHQ.Infrastructure` — Agent HQ SQLite (Dapper + DbUp), stores, event store, plan
  service, file watchers.
- `src/AgentHQ.Providers.ZCode` — read-only reader of `~/.zcode/cli/db/db.sqlite` behind a
  schema guard (`TestedZCodeSchemaVersion`).
- `src/AgentHQ.Api` — host: REST, SSE `/events`, MCP `/mcp`, hook ingest. `AgentHqHost.BuildAsync`
  is the composition root shared with tests.
- `src/AgentHQ.Commands.ZCode` — `/plan` command pack + installer.
- `tests/AgentHQ.Tests` — xUnit; `AgentHQApp` boots the full host in memory on temp dirs.

## Working rules

- Run `dotnet test` before claiming anything done; the suite is the gate.
- SQLite parameter names are case-sensitive and Dapper binds only explicit names — pass
  parameters as `@Name = value` with consistent casing, and alias snake_case columns
  explicitly (see `Stores/Columns.cs`). Use `AgentHqDb.Open()` (it opens the connection).
- `dotnet build` does not always refresh project references; if a change seems ignored, do a
  clean rebuild before debugging.
- The .NET 10 test harness intermittently aborts before running any test (CLR 0x80131506, no
  test output, usually the first run after a build). Re-run; do not treat it as a test failure.
  CI retries `dotnet test` once for this reason.
- Avoid optional (nullable) parameters on MCP tool methods: the SDK's argument binding
  fails on missing optional arguments; use required params with empty-string sentinels.
- Everything is named Agent HQ: projects and namespaces are `AgentHQ.*`, types `AgentHq*`,
  runtime identity lowercase-hyphen (`agent-hq.service`, `AGENTHQ_*`, `~/.local/share/agent-hq`),
  prose "Agent HQ". `dev-bridge` is only the previous name, mentioned in history.

<!-- agent-hq -->
## Development is managed via Agent HQ

Plans live in `docs/plans/` and move through a managed lifecycle (`/plan` commands).
Before working: read `docs/plans/lessons.md` — the repository's accumulated lessons.
The Agent HQ MCP tools are self-describing; orient with `get_overview`.
