# Events and the activity feed

Everything that happens in Agent HQ — a plan submitted, a status moved, a plan blocked, a session
touched, a `/plan` prompt rejected — becomes a row in one durable, ordered event log. The log is
the hub's heartbeat: it is what the web UI's activity panel shows, what a project-manager agent
polls to stay current, and what an integration streams from when it wants live updates.

## Two ways to read the log

Both read the same rows from the same table; the difference is how you want them delivered.

| Mode | Request | Shape | Use it when |
|---|---|---|---|
| **Stream** | `GET /events` | `text/event-stream` (SSE) | You can hold a connection open and want pushes |
| **Poll** | `GET /events?sinceSeq=N` | JSON `{ events, lastSeq }` | You are an MCP-only or turn-based consumer |

```bash
# one cursor query per turn — the project-manager agent's rhythm
curl -s 'http://127.0.0.1:5317/events?sinceSeq=1042&types=plan.status_changed'

# live stream, resuming from where you left off
curl -N 'http://127.0.0.1:5317/events' -H 'Last-Event-ID: 1042'
```

Both accept `?types=a,b,c` to filter by event type.

### Streaming details

Each SSE message is named by its event type and carries the event's sequence number as its
`id:` line, so a browser's `EventSource` reconnects with `Last-Event-ID` automatically and gets
only what it missed:

```
id: 1043
event: plan.status_changed
data: {"planId":"…","slug":"…","from":"implementing","to":"implemented","actor":"ses_…"…}
```

- A `retry: 5000` hint is sent when the connection opens.
- A `: heartbeat` comment is sent every 15 seconds so idle connections stay alive.
- Replay reads **to the current position** in one pass. A client that reconnects with an old
  cursor cannot silently resume far behind live — there is no small replay window.
- Subscribers are registered *before* the replay query runs, so an event appended in between is
  not lost. Overlap between replay and live is de-duplicated by sequence number.

The connection carries no per-client state; if it drops, reconnect with the last `id` you saw.

## Event types

| Type | Emitted when | Payload highlights |
|---|---|---|
| `plan.created` | A plan is submitted | `planId`, `slug`, `name`, `repositoryId`, `status`, `progress` |
| `plan.status_changed` | Any lifecycle transition, including defer, abandon, reactivate | `from`, `to`, `actor`, `actorType`, `command`, `gitHead`, `note`, `separationOfDutiesWarning` |
| `plan.blocked` | The blocked flag is set | `slug`, `actor`, `reason`, `status` |
| `plan.unblocked` | The blocked flag is cleared | `slug`, `actor`, `reason` (the resolution) |
| `plan.updated` | The plan **file** changed outside Agent HQ (an agent edited it) | `version`, `progress`, `reason: "external-edit"` |
| `plan.session_linked` | A work command claimed a plan for a session | `planId`, `sessionId` |
| `session.created` | A session first appears in the mirror | `sessionId`, `title`, `repositoryId`, `status` |
| `session.updated` | Session metadata that consumers act on changed | `sessionId`, `title`, `lastOutcome`, `status` |
| `hook.rejected` | A `/plan` prompt could not be applied | `sessionId`, `reason`, `prompt` (truncated) |

Two distinctions are easy to miss:

- **`plan.status_changed` vs `plan.updated`.** The first is a decision by an actor; the second
  is the document changing. An agent ticking checkboxes or appending to the Status Log produces
  only `plan.updated`. Agent HQ's own writes (submit, `PUT /content`) produce neither — they
  update the indexed hash in the same step, which is how self-writes are suppressed and how a
  burst of file-system events for one edit becomes at most one version bump.
- **`session.updated` is signal-shaped.** ZCode bumps a session's `time_updated` on every
  internal step. Agent HQ emits an event only when title, task type, last outcome, archive state
  or repository actually changed, so the stream stays readable.

## How changes are noticed

Three mechanisms feed the log:

1. **Agent HQ's own actions.** Submitting, transitioning, blocking — every service method
   appends its event synchronously as part of the action.
2. **Plan files.** A file-system watcher covers each registered repository's plans directory.
   An edit that changes the content hash bumps the plan version and emits `plan.updated`. Edits
   to `lessons.md` re-index the lessons instead.
3. **The ZCode database.** A watcher covers the ZCode database directory (`db.sqlite` and its
   WAL side files). After a 500 ms debounce it compares SQLite's `data_version` on a dedicated
   long-lived connection, and only if it changed runs the session diff. A one-minute
   `data_version` check is the backstop for dropped watch events. `data_version` is
   per-connection, which is why the watcher holds its own connection open rather than borrowing
   one from a pool.

If you are debugging "the activity panel did not show X", ask which of the three should have
produced it — Agent HQ action, plan-file edit, or session change — and check the corresponding
subsystem in the logs.

## The activity panel

The web UI's **Activity** button opens a live tail built on the stream: it connects with
`/events?lastEventId=<last seen>`, shows new events as they arrive, and counts events that
arrived while the panel was closed on a badge.

When the service restarts, the panel does not lose events: it falls back to `?sinceSeq=` polling
every two seconds and probes the stream until it comes back. Event types it does not know about
still appear — the UI listens for the default SSE channel and scans for unknown types — so a
new event type added later (by a future version) is visible without changing the UI.

See [Web UI](web-ui.md#activity) for the interaction details.

## Retention

The event log grows without bound by design: it is the audit trail, and it is small (one row per
significant action, not per request). If it ever matters, the table can be trimmed to a
sequence floor — but anything that trims it must keep `lastSeq` monotonic, because clients
reconnect by cursor.

## See also

- [Plan lifecycle](plan-lifecycle.md) — which transitions produce which events
- [MCP tools](mcp.md#the-orientation-rhythm) — `get_events` and the catch-up rhythm
- [API reference](api.md#events) — exact parameters
