# API reference

JSON is camelCase; null fields are omitted. Requests arriving on **loopback are trusted** — no
credentials (the ZCode hook keeps its no-token contract). Everything else — the LAN bind, the
external MCP point — requires a **bearer token or session cookie on every route**: REST, SSE,
MCP and the static UI. See [Authentication](#authentication).

Conventions used below:

- **IDs** — a plan id is a GUID; a repository id is the slug of its absolute root path. Wherever a
  repository is accepted, its full root path is accepted too.
- **Times** — ISO-8601 with offset in responses; integer epoch milliseconds in the database.
- **Status codes** — `400` malformed or unknown values, `401` missing/invalid credentials or bad
  login, `403` an authenticated role not permitted on the route, `404` unknown plan, `422` a rule
  refused the request (the body says which: `{"ok":false,"error":"…"}`), `429` login rate limit.

## Authentication

Name/password identities, per the recorded simplification. Passwords are PBKDF2 hashes
(210k iterations, per-user salt); bearer tokens are random 256-bit values stored **hashed**;
revocable individually and per identity. No expiry by default — a LAN with a handful of named
principals; revisited if exposure widens beyond the external MCP point.

### Roles (not scopes)

| Role | Gets |
|---|---|
| `admin` | Everything, including identity management |
| `agent` | Reads, plan commands, exchange, MCP — the Linda (project-manager) role |
| `worker` | The spoke endpoints only (they arrive with the spoke part; nothing yet) |
| `ci` | The hook ingest endpoint only |

Loopback requests carry full trust regardless of identities.

### `POST /auth/login`

```json
{"username": "linda", "password": "…", "grant": "token"}
```

- `grant: "token"` (default) → `{"ok":true,"token":"…","identity":{"username","role"}}` — for
  API/MCP clients, spokes and CI. Send as `Authorization: Bearer <token>`.
- `grant: "cookie"` → `Set-Cookie: agent_hq_token=…; HttpOnly; SameSite=Lax` and no token in the
  body — for the browser; the EventSource SSE reconnect keeps working, and there are no
  query-string tokens (they leak into logs).

Failures: `401 {"ok":false,"error":"bad_credentials"}`; after repeated failures from one source,
`429 {"ok":false,"error":"rate_limited"}` (five failures in ten minutes arms a five-minute
backoff per source). Every login, failure, rate-limit and denial writes an `auth_audit` row and a
log line with `Subsystem: auth`.

### `POST /auth/logout`

Revokes the presented credential (cookie or bearer). `422` on loopback (no token to revoke).

### Identity management (admin only)

- `GET /auth/identities` — list.
- `POST /auth/identities` `{"username","password","role"}` → `201`; `422` on a taken username or
  an unknown role.
- `DELETE /auth/identities/{username}` — deletes the identity and its tokens (audit rows kept).
- `POST /auth/identities/{username}/revoke-tokens` — revoke every live token of one identity.
- `POST /auth/tokens/{id}/revoke` — revoke one token (`404` unknown).

Bootstrap: on first start with `AGENTHQ_ADMIN_USER` and `AGENTHQ_ADMIN_PASSWORD` set, the admin
identity is created before the port opens. Later starts with the same variables warn and ignore
(bootstrap is first-start only; to change the password, delete and recreate the identity).

### External access point (Linda V1)

Requests through the trusted edge proxy (identified by the `X-AgentHQ-Edge` secret header,
`AGENTHQ_EDGE_TOKEN`) are restricted to `POST /auth/login` and `/mcp`. **Every other path
answers `404`, never `401`** — a remote caller must not be able to confirm which surfaces exist.
The UI and everything else stay LAN-only, permanently. Standing the edge up is a recorded
security decision — see [Operations](../operations.md#identities-and-authentication).

### MCP

MCP requires the `agent` role (or `admin`). A tool call by an authenticated identity records
that identity as the actor in what the tool writes (plan history, exchange rows) — attribution
parity with sessions.

## Health and overview

### `GET /health`

```json
{"status":"ok","provider":"zcode","providerSchema":"ok","providerDetail":"schema version 24"}
```

`providerSchema` is `ok`, `schema-ahead` or `missing` and describes only the optional
worker-agent integration; the service is healthy in all three cases.

### `GET /overview`

One-call operational picture: `repositories`, `plans` grouped by status, `blocked` (with
reasons), `verificationQueue`, `recentSessions`, `lastEventSeq`.

## Repositories

### `GET /repositories`

List: `id`, `rootPath`, `provider`, `externalId`, `plansDir`, `default`, `firstSeen`.

### `POST /repositories`

```json
{"rootPath": "/home/me/projects/my-app", "isDefault": false}
```

Registers an existing directory, starts its plans watcher and indexes its lessons file. Returns
`201` with `{id, rootPath}`. `400` if the path is not an existing directory.

Repositories are also auto-registered when a worker session or a `/plan` hook reports a working
directory Agent HQ has not seen before.

### `GET /repositories/{id}/lessons`

Parsed entries from `docs/plans/lessons.md`: `entryId` (`L-<n>`), `title`, `category`,
`sourcePlanId`, `contentHash`, `firstSeenAt`.

## Plans

### `POST /plans`

```json
{"repo": "workspace", "name": "Export CSV", "markdown": "# Export CSV\n\n## Goal\n…"}
```

Writes `docs/plans/<slug>.md`, indexes it at status `submitted`, emits `plan.created`. Returns
`201` with `{id, slug, status, warnings, file}`. `warnings` lists convention sections that are
missing (soft validation — the plan is still created).

### `GET /plans`

| Parameter | Effect |
|---|---|
| `repo` | Repository id |
| `status` | Any lifecycle code (`in_review`, `implemented`, …) |
| `ready=true` | `in_review`, not blocked, every dependency `verified` or `done` |
| `blocked=true` | Only blocked plans (the needs-attention queue) |

Rows: `id`, `slug`, `name`, `status`, `priority`, `blocked`, `blockedReason`, `session`,
`implementedBy`, `progress {checkedCount, total}`, `version`, `updated`.
Ordered by priority desc, then last update desc.

### `GET /plans/{id}`

Metadata plus `dependencies {id, slug, status}` and `history` — one row per transition: `fromStatus`,
`toStatus`, `actorType`, `actorSessionId`, `command`, `gitHead`, `note`, `separationOfDutiesWarning`,
`at`.

### `GET /plans/{id}/content` — markdown
### `PUT /plans/{id}/content`

```json
{"markdown": "…"}
```

Rewrites the plan file on Agent HQ's behalf. The indexed hash and progress are updated in the
same step, so this is **not** treated as an external edit: no version bump and no
`plan.updated` event. Returns `{id, version, progress}`.

### `PATCH /plans/{id}`

```json
{"priority": 10, "sessionId": "ses_…"}
```

Sets priority (integer, higher = sooner) and/or manually links the implementing session of record
— the fallback when a hook was missed. Emits `plan.session_linked` when a session is linked for
the first time.

### `PATCH /plans/{id}/status`

Two distinct uses on one route:

```json
{"status": "verifying", "by": "openclaw", "note": "criteria reviewed"}
```

Manual lifecycle transition. Enforces the same state machine as `/plan` commands; the history row
records the equivalent command (for example `PATCH:/verify`). `422` if the move is illegal.

```json
{"blocked": true, "note": "waiting on credentials"}
```

Toggles the orthogonal blocked flag instead of moving the lifecycle. Blocking requires a reason.

### `POST /plans/{id}/commands`

The path the web UI uses, and a convenient way to drive the lifecycle with attribution:

```json
{"command": "fail", "note": "tab-indented steps are dropped", "by": "andy"}
```

Equivalent to `/plan <command> <slug> [note]`. `422` with the refusal message when the command
is not legal from the current status.

### `POST /plans/{id}/dependencies`

```json
{"requiresPlanId": "…"}
```

Adds a dependency edge (cycle-checked). `422` on self-reference or cycle.

## Sessions

### `GET /sessions`

| Parameter | Effect |
|---|---|
| `repo` or `repoRootPath` | Restrict to one repository |
| `proper` | Only sessions updated after this time (exclusive) |
| `status` | `active`, `idle`, `archived`, `deleted` |

Read-through from the worker agent's index. Rows: `id`, `title`, `rootPath`, `taskType`,
`lastOutcome`, `timeCreated`, `timeUpdated`, `status`.

### `GET /sessions/{id}`

The session's metadata and a first-user-message excerpt. `404` if the provider does not know it
and it is not mirrored.

### `GET /sessions/{id}/digest?budget=8000`

Deterministic markdown digest, `text/markdown`. The budget is a hard upper bound on characters.
See [Sessions](sessions.md#digests-the-reading-model-for-a-project-manager).

### `GET /sessions/{id}/messages?after=&include=`

Filtered delta: `after` is a sequence cursor; `include` is a comma list of `reasoning`, `tool`,
`steps`. Returns `{id, messages, maxSequence}`.

## Events

### `GET /events` — SSE stream

| Parameter / header | Effect |
|---|---|
| `types=a,b` | Only these event types |
| `?lastEventId=N` or header `Last-Event-ID: N` | Replay from N, then stream live |
| `?sinceSeq=N` | **JSON poll form** instead of SSE: `{events, lastSeq}` |

Each SSE message: `id: <seq>`, `event: <type>`, `data: <payload JSON>`. Heartbeat every 15 s.
See [Events](events.md).

## Hook feed

### `POST /hooks/zcode/prompts`

Called by the ZCode `UserPromptSubmit` hook for every prompt:

```json
{"session_id": "ses_…", "prompt": "/plan implemented docs/plans/export-csv.md", "cwd": "/home/me/projects/my-app"}
```

- Prompts that are not `/plan <command> <file>` are acknowledged with
  `{ok:true, matched:false}` and change nothing.
- A matched command resolves the repository from `cwd`, the session from the provider, and the
  plan from the file argument, then applies the transition with the session as actor.
- Anything unresolvable returns `4xx` with `{ok:false, error}` **and** records a `hook.rejected`
  event carrying the prompt and the reason. That event is how "I ran `/plan …` and nothing
  happened" gets diagnosed.

## MCP

### `POST /mcp`

Streamable-HTTP MCP endpoint. The tools are documented in [MCP tools](mcp.md).

## Web UI

### `GET /`

The single-page UI. Assets are `/app.js`, `/ui.css`, `/favicon.svg` from the same directory.

## See also

- [MCP tools](mcp.md) — the tool-shaped equivalents of these calls
- [Plan lifecycle](plan-lifecycle.md) — the rules behind `422` responses
- [Operations](../operations.md) — health, logs, deploy
