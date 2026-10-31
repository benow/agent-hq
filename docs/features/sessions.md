# Sessions

Worker agents do their work in sessions: a ZCode session is one conversation with an agent,
scoped to a working directory, with its own title, status and message history. Agent HQ tracks
those sessions so that plans can name the session that worked on them, and so a project-manager
agent can see *how* work is going without reading every transcript.

## Read-through, not mirrored

Agent HQ does **not** copy session transcripts. It opens ZCode's own session index
(`~/.zcode/cli/db/db.sqlite`) read-only and reads from it on demand. The only thing mirrored into
Agent HQ's database is metadata needed for joins and events: title, task type, last outcome,
timestamps, the first user message excerpt, and the repository each session belongs to.

That choice has three consequences worth knowing:

- **The provider is authoritative.** Filters like `updatedSince` are applied by the provider
  against its own column, not against a stale copy.
- **No secrets land in Agent HQ's database.** Message content is never persisted; it is read and
  returned or condensed on the way out.
- **A newer ZCode cannot corrupt anything.** If the schema changes underneath, the reader
  degrades instead of writing garbage (see the schema guard below).

Reads are tiered so that each question costs what it should:

| Tier | What you get | Endpoint |
|---|---|---|
| **L0** | Session metadata list (title, repository, timestamps, derived status) | `GET /sessions` |
| **L1** | Deterministic markdown digest of one session | `GET /sessions/{id}/digest?budget=N` |
| **L2** | Filtered message delta after a sequence number | `GET /sessions/{id}/messages?after=N&include=…` |

## Session status is derived

Agent HQ does not store a status column for sessions; it derives one at read time:

| Status | Rule |
|---|---|
| `deleted` | ZCode marked the session deleted |
| `archived` | ZCode archived it |
| `active` | Neither, and `time_updated` is within the last 30 minutes |
| `idle` | Neither, and older than that |

`GET /sessions?status=active` is therefore "what is being worked on right now", and it is what
the web UI's activity panel shows under *Recent sessions*.

## How sessions are discovered

Discovery is automatic. At startup Agent HQ performs one full sync:

1. Ask the provider for every session.
2. For each session whose working directory has no registered repository, register that
   directory as a repository.

After that, a watcher on the ZCode database directory notices writes and performs an incremental
sync: it diffs session rows above the last seen `time_updated` and mirrors the changed ones.
Every sixty seconds it also checks the database's `data_version` as a backstop for dropped
file-watch events. This is the machinery described in [Events](events.md#how-changes-are-noticed),
because everything it finds becomes an event.

A session that is mirrored emits `session.created` the first time. Updates emit `session.updated`
**only when something a consumer can act on changed** — title, task type, last outcome, archive
state or repository. ZCode bumps `time_updated` on every internal step, so emitting on every bump
would flood the stream with events carrying no information. The mirror row is always kept
current; the event is the signal.

## Digests: the reading model for a project manager

A digest is a condensed, deterministic rendering of a session: no model is involved, so the
same session always produces the same digest, and nothing is "summarised away".

The content order is fixed:

1. A header: session id, title, repository, status, last update.
2. **User messages, verbatim** — rare and high-signal.
3. **Assistant text**, tail-weighted — the most recent fifth is kept verbatim first.
4. **Tool activity as one-liners** — `Edit: src/Foo.cs`, `Bash: dotnet test`. Raw tool output
   is never included.
5. Reasoning, step markers, timelines, compaction records and file parts are dropped entirely.

`?budget=<chars>` (default 8000) is a **hard upper bound**. When the full render exceeds it, the
elision order is fixed and lossy in a predictable way:

1. oldest assistant text is dropped (keeping the most recent 20% verbatim), with a marker saying
   how many items were elided;
2. then the oldest tool calls are dropped;
3. then oversized user messages (usually pasted logs) are truncated head-and-tail;
4. as a last resort the text is hard-truncated with a `(… truncated …)` marker.

Because nothing is summarised, a reader can trust that anything present is verbatim, and
anything missing was dropped by a rule they can see.

```bash
curl -s 'http://127.0.0.1:5317/sessions/<id>/digest?budget=4000'
```

## The message delta (L2)

`GET /sessions/{id}/messages` returns messages after a sequence number, so a client that
polled once can ask for only what is new:

| Parameter | Effect |
|---|---|
| `after=<sequence>` | Only messages or parts newer than this |
| `include=reasoning` | Also include reasoning parts |
| `include=tool` | Also include tool-call parts |
| `include=steps` | Also include `step-start` / `step-finish` markers |

Text parts are always included. Timeline, compaction and file parts are never returned. The
response includes `maxSequence`, which is the cursor to pass on the next call.

## The schema guard

The ZCode database is a private implementation detail of ZCode. Reading it directly is a real
coupling, so it is quarantined in one place — `ZCodeAgentProvider` — behind a guard that runs at
startup:

| Situation | Health | Effect |
|---|---|---|
| Marker present and ≤ tested version | `ok` | Everything works |
| Marker newer than tested | `schema-ahead` | Session features return empty results; a warning is logged |
| Database or marker table missing, or unreadable | `missing` | Same degraded behaviour |

The tested version is the constant `TestedZCodeSchemaVersion` in that provider (24 at the time
of writing). The guard never throws: a degraded provider is a service that still runs.

## When ZCode is missing

Everything except session mirroring keeps working: repositories, plans, transitions, the web UI,
the event stream, MCP. `GET /health` reports it:

```json
{"status":"ok","provider":"zcode","providerSchema":"missing","providerDetail":"…"}
```

`status: "ok"` means Agent HQ itself is healthy; `providerSchema` describes the optional worker
agent integration. A missing provider is the normal state on a machine without ZCode.

If sessions are unexpectedly empty while ZCode is installed:

1. Check `/health` — `schema-ahead` or `missing` explains it immediately.
2. Confirm `AGENTHQ_ZCODE_DB` points at the right file (`~/.zcode/cli/db/db.sqlite` by default).
3. Confirm the ZCode session's working directory is a path Agent HQ can see — sessions are
   matched to repositories by path prefix.
4. Look at the logs for the `provider` and `watcher` subsystems (see [Operations](../operations.md#logs)).

## Provider abstraction

ZCode is the first implementation, not a special case. The interface is deliberately small:

```csharp
public interface IAgentProvider
{
    string Id { get; }
    ProviderHealth Health { get; }
    string? HealthDetail { get; }

    Task<IReadOnlyList<RepositoryInfo>> ListRepositoriesAsync();
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(SessionQuery q);
    Task<SessionDetail?> GetSessionAsync(string externalId);
    Task<SessionDelta> GetMessagesAsync(string externalId, SessionMessageQuery q);
    ValueTask SubscribeAsync(Func<ProviderEvent, Task> onEvent, CancellationToken ct);
}
```

A second worker agent would bring its own `IAgentProvider` implementation — including its own
health semantics — while plans, the lifecycle and the event stream stay unchanged.

## See also

- [Events](events.md) — `session.created` / `session.updated` and the activity feed
- [MCP tools](mcp.md) — how the project-manager agent reads sessions
- [API reference](api.md#sessions) — request and response shapes
- [The ZCode integration](zcode-integration.md) — the hook that attributes `/plan` commands to
  a session
