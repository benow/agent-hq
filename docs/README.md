# Documentation

Agent HQ's documentation is split by audience. Start where you are:

| You are… | Read |
|---|---|
| **New here** — what is this, should I use it? | [Concepts](concepts.md): the problem, the actors, the lifecycle |
| **Setting it up** — I want it running | [Installation](installation.md) |
| **Operating it** — it is deployed, keep it healthy | [Operations](operations.md): service, deploy, rollback, logs |
| **Building on it** — MCP tools, REST, events, the web UI | [Features](#features) below |
| **Contributing code** | [`AGENTS.md`](../AGENTS.md) — the directives for agents and humans working in this repo |

## Features

Each significant feature has one page. They are written to be read on their own; cross-links
point at the page that owns a topic.

| Feature | What it covers | Read when |
|---|---|---|
| [Plan lifecycle](features/plan-lifecycle.md) | The states, who moves what, the `/plan` commands, verification and separation of duties, lessons | You are defining, running or reviewing plans |
| [Sessions](features/sessions.md) | How agent sessions are discovered and tracked, digests, schema guard, degraded modes | You want to see what agents are doing, or session tracking looks empty |
| [Events and the activity feed](features/events.md) | The durable event log, SSE streaming with replay, event types, the activity panel | You are integrating with live updates, or investigating missing feed entries |
| [MCP tools](features/mcp.md) | The project-manager façade: every tool, the orientation rhythm, a worked conversation | You are wiring a project-manager agent to Agent HQ |
| [Web UI](features/web-ui.md) | The table, filters, fly-out, activity panel, constraints | You are using or changing the browser interface |
| [API reference](features/api.md) | Every REST endpoint with parameters, plus the data shapes they return | You are writing a client |
| [The ZCode integration](features/zcode-integration.md) | The `/plan` command pack, the prompt hook, the installer, and what it writes into your home directory | You are setting up or debugging the worker-agent side |

## Design record

The architecture plan and its decision log live in [`plans/`](plans/). They are the project's
history — the documents that were written *before* the code, with the reasons recorded as
decisions were made. They are useful when you need to know *why* something works the way it does;
the pages above describe *what* it does.

- [`plans/create.md`](plans/create.md) — the v1 architecture plan (v12 after the rename to Agent HQ)
- [`plans/web-ui.md`](plans/web-ui.md) — the web UI plan
- [`plans/conversation-enhancements-etc.md`](plans/conversation-enhancements-etc.md) — addressed
  conversation between actors (Exchange v2), and the retirement of the separate Fleet Room
- `plans/lessons.md` — accumulated lessons for repositories managed by Agent HQ
  *(created by the project's own dogfooding; it does not exist until the first lesson is captured)*
