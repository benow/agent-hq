# Agent HQ

**A local web service that gives your AI agents somewhere to agree on work.**

Coding agents are excellent at the task in front of them and blind to everything else. Each
session starts fresh, the context of what was agreed last week evaporates, and nobody can say
which plans are waiting, which are stuck, or whether the work that was just declared "done" was
ever checked.

Agent HQ is the missing middle. It keeps a durable record of **what was planned, who did it, in
which session, and with what evidence** — and makes that state cheap for humans and agents to
read. It manages the work; it never does it.

```bash
scripts/agent-hq-deploy.sh && scripts/agent-hq-deploy.sh install
./scripts/install-zcode-integration.sh --repo /path/to/your/project
```

Then open <http://127.0.0.1:5317/>.

> New here? [Start with the concepts](docs/concepts.md) — five minutes, no prerequisites.

## The problem it solves

| Without Agent HQ | With Agent HQ |
|---|---|
| Plans live in someone's chat history | Plans are markdown in your repository, committed with the code |
| "Done" is whatever the agent says | Claims carry evidence, and a **different session** verifies them |
| Nobody knows which session did what | Every transition names the session, the command and the commit |
| Blocked work silently stalls | Blocking is a flag with a reason; blocked plans are a live queue |
| The project manager can't read every transcript | Deterministic digests and a one-call operational picture |
| Lessons get relearned every session | `docs/plans/lessons.md` is consulted, extended and distilled into `AGENTS.md` |

## How it fits together

```
        you (browser)                     project-manager agent
              │  REST + SSE                      │  MCP  /mcp
              ▼                                  ▼
      ┌────────────────────── Agent HQ ──────────────────────┐
      │   plans  ·  lifecycle  ·  events  ·  session mirror  │
      └───────────────▲───────────────────────┬──────────────┘
                      │ POST /hooks/zcode/prompts            │ read-only
                      │ (every /plan prompt)                 │
        worker agents (ZCode sessions) ──────────────────────┘
```

- **Plans** are markdown at `docs/plans/<slug>.md`, moving through
  `submitted → refining → in_review → implementing → implemented → verifying → verified → done`,
  with a rework loop through `fixing`, and `deferred` / `abandoned` from anywhere.
- **Transitions** are recorded when a `/plan <command> <file>` prompt arrives on the hook feed —
  attributed to the agent session that sent it — or when a human acts in the browser or through
  the API. All paths obey the same state machine.
- **Evidence** lives in the plan's own Status Log: commits, test output, review findings. Agent
  HQ versions and watches that file, and never parses prose into decisions.
- **Sessions** are read through from the worker agent's own index, never mirrored beyond
  metadata, and condensed deterministically — no AI summarisation in the hub.
- **Events** are durable rows: SSE with `Last-Event-ID` replay, or `?sinceSeq=` JSON polling for
  turn-based agents.

None of this is specific to software: a plan can be research, operations or writing, and the
lifecycle is identical.

## Documentation

| | |
|---|---|
| [Concepts](docs/concepts.md) | The problem, the actors, the lifecycle — start here |
| [Installation](docs/installation.md) | Requirements, first deploy, configuration, uninstall |
| [Operations](docs/operations.md) | Service, deploys and rollback, logs, health, security posture |
| [Plan lifecycle](docs/features/plan-lifecycle.md) | States, commands, verification, separation of duties, lessons |
| [Sessions](docs/features/sessions.md) | Session tracking, digests, the schema guard |
| [Events](docs/features/events.md) | The event log, SSE replay, the activity feed |
| [MCP tools](docs/features/mcp.md) | Wiring a project-manager agent |
| [Web UI](docs/features/web-ui.md) | The table, the fly-out, the activity panel |
| [API reference](docs/features/api.md) | Every REST endpoint |
| [ZCode integration](docs/features/zcode-integration.md) | `/plan` commands, the hook, the installer |

The full index, including the design record, is in [docs/](docs/README.md).

## Requirements

Linux with systemd user services, .NET SDK 10 to build from source (the deployed binary is
self-contained), and optionally a ZCode installation to track sessions. SQLite is embedded; the
web UI has no build step. Details in [Installation](docs/installation.md).

## Status

Version 1, and **dogfooding**: Agent HQ's own development runs through Agent HQ, under the plans
in [`docs/plans/`](docs/plans/). The architecture plan and the decisions made while building it
are recorded there.

## Contributing

Read [`AGENTS.md`](AGENTS.md) first — it is written for both human and agent contributors, and it
is the hub for the per-feature knowledge files. Run `dotnet test` before claiming anything is done;
the suite is the gate.

## License

Not yet licensed. The repository is public for reading; no permission to reuse is granted until a
`LICENSE` file is added.
