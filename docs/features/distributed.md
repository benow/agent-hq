# Hub and spokes: the wire contract

Agent HQ is becoming hub-and-spoke ([plan](../plans/distributed-agent-hq/00-OVERVIEW.md)):
the hub is the lens — one place where state is visible from every machine that needs it —
and spokes live beside the agents, keeping everything physically coupled to them (the plan
engine, the file watchers, the ZCode provider). Spokes talk to the hub over a versioned HTTP
contract shipped as the `AgentHQ.Client` NuGet package on the private feed
(`nuget.benow.ca`). The hub implements the contract; a spoke consumes it and nothing else —
contract-first, or it isn't a contract.

This page covers the **contract side** (part 3). The spoke host, outbox and offline drain
extend it in part 4; the build spoke in part 5.

## The shape

| Direction | Mechanism | Notes |
|---|---|---|
| Up (spoke → hub) | `POST /workers/{id}/snapshot`, `POST /workers/{id}/events` | Bulk state once, then small ordered deltas |
| Down (hub → spoke) | `GET /events` (SSE) first, `GET /workers/{id}/commands?wait=25` as fallback | Signals are advisory; the leased claim is the correctness floor |
| Presence | `POST /workers/{id}/heartbeat` every 300 s | Offline after 900 s; response carries the self-update signal |
| Bootstrap | `POST /workers/pairing` → browser approval → poll | No token is ever typed by a human or shown to an LLM |
| CI | `POST /ci/events` | Hub-origin events with actor `ci:<workflow>/<run>` |

Every contract request carries `X-AgentHQ-Contract: 1` (middleware on `/workers/` and
`/ci/`; a missing or unknown major answers 400 naming both versions). One worker identity
owns exactly one worker record; a `/workers/{id}/…` route answers 404 unless `{id}` is the
caller's own worker — unknown and foreign ids are indistinguishable. The routes sit on the
same services REST and MCP use; there is no second path.

## Ordering and idempotence (the three cases)

Events carry `(workerId, originSeq)`. A batch is judged by its first origin sequence against
the hub's `nextExpected` for that worker:

| Case | Meaning | Hub behaviour |
|---|---|---|
| `fromOriginSeq < nextExpected` | The ordinary ack-lost resend after a crash | Trim the overlapping prefix, apply the tail, acknowledge normally (a fully-duplicate batch applies zero rows) |
| `fromOriginSeq = nextExpected` | The normal case | Apply the whole batch |
| `fromOriginSeq > nextExpected` | A gap — the spoke's cursor is ahead of the hub's | Reject `422` with the expected value; the spoke rewinds and resends |

The rows and the cursor commit **together**, which is what turns a lost acknowledgement into
a trimmed duplicate instead of a double application. No server-side gap buffering — resend
from the cursor is the protocol.

The hub re-sequences accepted events into its own global stream: origin ids, actors and
timestamps ride in the payload (`workerId`, `originSeq`, `actor`, `transportedBy`,
`occurredAt`, then the event's own fields). Every existing cursor — `?sinceSeq=`, MCP
`get_events`, SSE replay with `Last-Event-ID` — keeps working against the global space,
with events from all spokes and hub-origin events interleaved in one history. Actor
passthrough (rule 1) is preserved the whole way: the worker is transport, never the actor.

## Snapshots

A registration is followed by a bulk snapshot: repositories, plan rows **with their full
markdown**, plan history, session mirror metadata (never message content), lessons, and the
tail of the spoke's event log (last 24 h or 5 000 events, whichever is smaller). The hub
replaces that worker's projections **transactionally** and sets the cursor to the tail, so
subsequent deltas with lower `originSeq` trim as duplicates. The projections are stored —
rows and content — ready for the part-4 read switch; in this part the UI/MCP reads stay
file-backed for hub-owned repositories.

Ownership is exclusive: a snapshot claiming a repository another worker already owns is
rejected `409`, naming the other worker. The same-worker re-snapshot is the normal idempotent
refresh.

## Commands

Commands are queued, leased envelopes. The hub routes by repository ownership (a `plan.command`
goes to the worker that owns the repository) or by capability kind (a `health.check` to a
`build-health` holder). Capabilities are typed (`{kind, …fields}`); unknown kinds are stored
and surfaced but never routed. Claim returns up to **10** envelopes with a **60 s** visibility
lease; expiry redelivers; a leased envelope is not returned again before expiry, even to the
same worker.

Results are idempotent by `commandId`: the first result records with its canonical
`resultHash` (sha256 of `{ok, error}` as canonical JSON — `AgentHqClient.ResultHash` applies
the formula); a duplicate is acknowledged (`duplicate: true`) and ignored, and a **divergent**
hash raises a `command.result_diverged` warning event instead of double-applying.

The down-signal is SSE-first: a spoke subscribes to `GET /events?types=commands.pending,release.available`
and claims when signalled. The stream carries the hub's global seq as SSE ids, so
`Last-Event-ID` replay composes with the `?types=` filter. SSE is not the lease — when the
stream is down the spoke resumes the long-poll claim loop, which is the correctness floor.
`release.available` fires once when the hub's own version changes (at deploy/start); the
heartbeat response carries `serverVersion`/`contractVersion` as the steady self-update signal.
The hub never writes to machines — the spoke pulls.

## Pairing (credential bootstrap)

1. The spoke (unauthenticated, rate-limited to **10 creates per 10 min per source** → `429`)
   creates a nonce with a **5-minute TTL** → `{nonce, pairUrl, expiresAt}`.
2. A browser with an admin session approves it (`POST /workers/pairing/{nonce}/approve`,
   header and admin cookie required): the hub creates the worker identity, its bound worker
   record, and attaches a fresh `worker`-role bearer token.
3. The spoke polls `GET /workers/pairing/{nonce}`: `pending` → `approved` (token delivered
   **exactly once**) → `consumed`; `expired` after TTL; `unknown` for a bad nonce. Anything
   but `pending` ends the poll loop.

The raw token lives only in the pairing row until that one delivery and in the spoke's own
config file afterwards — never logged, never stored elsewhere in clear. The dashboard's
**Spokes** dialog renders presence and drives the approval.

## CI ingest

`POST /ci/events` (the `ci` role) turns a `notify-agent-hq` payload into a hub-origin event:

```jsonc
{ "workflow": "deploy.yml", "run": 4412, "repo": "benow/agent-hq",
  "conclusion": "success", "url": "https://…", "timestamp": "…" }
// → event type "ci.run", actor "ci:deploy.yml/4412"; response { "accepted": true, "seq": 123 }
```

The build spoke (part 5) serves the same ingest on its own loopback for co-located runners.

## Degradation

- Hub down: spokes keep working locally (their engine is where the files are); events queue
  in the spoke's outbox and drain in order on return — the three-case rule makes that drain
  lossless and duplicate-free.
- SSE down: the long-poll claim continues; no command is lost, only slightly delayed.
- A spoke restart: registration is idempotent — the same `workerId` returns, so its
  projections and queued commands are not orphaned.
- Hub-on-SQLite remains the rollback topology: every table in this part exists in both
  migration sets (`0003_Workers.sql` PostgreSQL, `0005_Workers.sql` SQLite) behind the same
  store interfaces.

## The package

`AgentHQ.Client` (DTOs + typed client, retries with backoff + jitter) and
`AgentHQ.Client.Conformance` (the kit that proves the contract against any hub) are one
version = the contract version. `.github/workflows/nuget.yml` publishes on a `v*` tag (the
tag must match the csproj `<Version>`); CI builds without a tag publish `-build.{run}`
prereleases. The suite uses ProjectReferences while developing — the feed is exercised by
the throwaway-consumer round trip (criterion 1), not by the tests.
