# MCP tools

Agent HQ exposes itself as a **Model Context Protocol** server at `POST /mcp` (streamable HTTP).
This is the façade a project-manager agent talks to: MCP gives it tool discovery, typed schemas
and descriptions, so no endpoint knowledge has to live in its prompts and drift.

The REST API remains available for humans, scripts and debugging; the MCP tools are thin
wrappers over the same service handlers, so the two cannot disagree about behaviour.

## The contract in one paragraph

The project-manager agent **manages the work and never does it**. It scopes and submits plans,
keeps them moving, surfaces what is blocked, and verifies finished work from recorded evidence.
It never touches code, and it never verifies something it implemented — a rule that holds by
construction because it never implements anything.

This paragraph is delivered twice: as the server's `instructions` during MCP initialisation, and
in full in the `get_guide` tool.

## Connecting

Add Agent HQ to your MCP client's server list. The reference client is OpenClaw, which reads
`mcp.servers` from its configuration; any MCP client that supports streamable HTTP works.

```json
{
  "mcp": {
    "servers": {
      "agent-hq": {
        "type": "http",
        "url": "http://127.0.0.1:5317/mcp"
      }
    }
  }
}
```

There is no authentication: Agent HQ binds to loopback only. Do not expose `/mcp` beyond the
machine without putting an authenticated reverse proxy in front of it — see
[Security and limits](../operations.md#security-and-limits).

## The tools

| Tool | Purpose |
|---|---|
| `get_overview` | One-call operational picture: repositories, plans grouped by status, blocked plans with reasons, the verification queue, last event sequence |
| `get_events` | Catch-up poll: events after a sequence number, optionally filtered by type. Returns `lastSeq` for the next call |
| `submit_plan` | Submit a plan (repository, name, markdown). Returns the slug, status and soft-validation warnings |
| `list_plans` | Plans, filtered by repository, status, `ready`, `blocked` |
| `read_plan` | One plan: metadata, progress, content, transition history, dependencies |
| `update_plan_status` | Manual/fallback transition (the same lifecycle rules as `/plan` commands) |
| `update_plan` | Set priority, or manually link the implementing session (the fallback for missed hooks) |
| `list_lessons` | Parsed `docs/plans/lessons.md` entries for a repository — consult when drafting verification criteria |
| `list_repositories` | Registered repositories |
| `list_sessions` | Worker sessions, with `updatedSince` for cheap catch-up |
| `get_session_digest` | Deterministic markdown digest of one session |
| `get_session_messages` | Filtered message delta for one session |
| `get_guide` | The full playbook as text |

Every tool's description carries its own contract, so a client that shows only the tool surface
still has a usable interface.

### Arguments are all required

The tools take required parameters with empty-string sentinels rather than optional ones —
pass `""` to mean "no filter". The C# MCP SDK's argument binding fails on missing optional
arguments, so this is a deliberate constraint of the implementation, not an API preference. It
is documented in each tool's description.

## The orientation rhythm

A project-manager agent that re-reads everything each turn burns its context on state. The tools
are designed around the opposite discipline:

1. **Orient once, at the start of a conversation** — `get_overview`. It answers "what is the
   state of everything" in one call.
2. **Stay current with a cursor, each turn** — `get_events(sinceSeq)` returns only what happened
   since. Never re-list to find out what changed.
3. **Use `updatedSince` on list calls** when you need a list rather than a picture.

The rule behind it: **never remember what you can re-derive in one cheap call.** A fresh context
re-orients rather than recalling what it believed last time.

## A worked conversation

```
agent: get_overview()
  → repositories: [workspace]
    plans: { submitted: 2, refining: 1, in_review: 1, implementing: 1, implemented: 2 }
    blocked: [ { slug: billing-schema, reason: "waiting on API credentials" } ]
    verificationQueue: [ { slug: demo-verification-queue, implementedBy: ses_01… } ]
    lastEventSeq: 1042

agent: get_events(sinceSeq: 1042)
  → 3 events: plan.updated (progress 2/3 on report-draft), plan.status_changed
    (implementing → implemented on export-csv, actor ses_02…), session.created
    lastSeq: 1045
```

Two things are now known without reading anything else: one plan needs attention (blocked, with
its reason), and the verification queue grew by one.

```
agent: list_lessons(repositoryId: "workspace")
agent: read_plan(planId: "…export-csv")
  → verification criteria, Status Log evidence, history with commit heads

agent: update_plan_status(planId: "…export-csv", status: "verifying", sessionId: "", by: "", note: "")
  → ok, status: verifying

agent: get_session_digest(sessionId: "ses_02…")
  → deterministic digest of the implementing session — what it ran, which files it touched

… the agent checks each criterion against the evidence, then:

agent: update_plan_status(planId: "…export-csv", status: "verified", …)
  → ok, status: verified
```

If a criterion cannot be checked from evidence — "the export works against the real warehouse" —
the agent leaves the plan in `implementing`/`implemented` and lets a worker session verify it,
rather than passing a claim it could not test.

## What the project manager may do

All of these are recorded with actor `openclaw`:

- submit plans, drafted against `list_lessons`;
- send a plan back to refining after a needs-author review;
- set priority and dependencies (herding);
- judge verification for criteria checkable from evidence and session digests;
- close `verified → done`;
- surface blocked plans with their recorded reasons.

What it never does: touch code, or verify a plan it implemented.

## Scheduling the loop

Agent HQ is passive — it has no scheduler. A typical loop rides the project manager's own
scheduling (a heartbeat, a cron-like wake, a morning routine):

1. `get_overview`
2. Judge the verification queue where criteria permit
3. Check blocked plans and their reasons
4. Report a short state-of-work summary to the human

Each wake starts a fresh context, so orientation is recomputed, never remembered.

## Delivering the guidance

The instructions arrive in four layers, so no single prompt has to carry everything:

1. **Server instructions** at MCP initialisation — mission and workflow, a paragraph.
2. **Per-tool descriptions** — the contract and etiquette for that tool.
3. **`get_guide`** — the full playbook: plan convention, lifecycle semantics and who moves
   what, digest usage, catch-up rhythm, separation of duties, lesson consultation.
4. **Self-describing event payloads** — what changed, naming the plan and the transition.

The workspace side needs only three small artifacts: the server entry above, a short section in
the workspace `AGENTS.md` saying the tools are self-describing and to orient with
`get_overview`, and the schedule entry that runs the loop.

## See also

- [API reference](api.md) — the REST equivalents of every tool
- [Sessions](sessions.md#digests-the-reading-model-for-a-project-manager) — what a digest contains
- [Plan lifecycle](plan-lifecycle.md) — the rules `update_plan_status` enforces
