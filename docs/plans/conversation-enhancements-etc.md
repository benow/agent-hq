# Conversation in Agent HQ (Exchange v2)

> **Status: drafted and reviewed; nothing is built.** Every checkbox below is open and no code
> exists for this plan. Any new status update belongs in the Status Log at the bottom — append,
> never rewrite.

## The decision

Agent HQ moves **plans** between a project-manager agent and worker sessions. It has no way to
move a **question**: when a ZCode session needs an answer from Linda (V1 or V2), or wants to send
Andy a screenshot, Andy copies text between two windows.

This plan adds addressed conversation to Agent HQ — questions, answers, messages and files
between named actors, delivered through a surface each actor already uses — and retires the
separate Fleet Room once it works.

**What exists now.** Plans, a lifecycle, an event stream, an MCP façade, a dashboard and a
session mirror, all local and working (`docs/features/`). Separately: a hosted chat room on the
NASTV box that the agents and Andy have used since 2026-10-11. It works, but it lives outside
this system and is invisible to the dashboard.

**What is new.** A small set of MCP tools (named in "Terms used throughout") plus their REST
equivalents;
`exchange_items` and presence tables; blob storage on disk; an inbox digest injected into ZCode
sessions by the existing hook; an Exchange view in the dashboard; a scoped TLS route for Linda
V1; and then the room is stopped.

**What it is worth.** The clipboard stops being a transport. Questions sit in the audit trail
next to the plans they block instead of in a chat log nobody cites. One system instead of two.
This is not a quantified saving; it is the removal of a manual step done many times a day.

**Where it stands.** Nothing is built. The source is the *Exchange v2 Implementation Spec* from
Linda V1 (2026-10-24). **That spec is not in this repository, and the implementer should not
depend on it.** Everything an implementer needs is restated in this plan; where this plan and
the spec differ, this plan wins.

### The three piles

| Live | Built, switched off | Not built |
|---|---|---|
| Plans, lifecycle, events, MCP, dashboard, session mirror, hook feed (one-way) | — | `exchange_items`, presence, blob store, twelve tools, hook injection, dashboard view, V1 route, room retirement |

### The decisions that were taken (no sign-off needed)

These were ruled on in the review of this plan on 2026-10-24. They are listed so the
implementer knows what is fixed and does not re-open them by accident.

| # | Decision | As resolved |
|---|---|---|
| 1 | Exchange is a feature of Agent HQ, not a project beside it | Inside Agent HQ; shares the event stream, dashboard and MCP server |
| 2 | The hook becomes a script instead of an inline curl | Yes — prerequisite for injection |
| 3 | Hook trust must be re-approved by hand after any hook change | Accepted; phase 0 proves it, phase 3 verifies it with a synthetic prompt |
| 4 | Injected digests carry structure only, never message bodies | Yes — the digest is a routing hint (D15) |
| 5 | The verification short-cut must record the real implementer | Do **not** set `implemented_by` on the short-cut; keep the intermediate row attributed to the verifier and mark it `via_shortcut` (D2). This supersedes an earlier revision that said the opposite. |
| 6 | Retention keeps item rows forever; only blobs are pruned | Rows kept, blobs pruned, size alarmed (D8) |
| 7 | Linda V1 gets a non-loopback TLS route | Only in phase 5, after the round trip works over loopback (D19) |

---

## Goal

Replace copy-paste between agents with addressed conversation in Agent HQ: questions with a
required purpose, answers that reach the asking session at its next turn, hash-verified file
transfer between actors, an operator view that shows every open question, and a retirement path
for the Fleet Room — without giving any agent authority over another and without weakening the
plan lifecycle's evidence rules.

## Context

Andy runs several agents at once: ZCode sessions doing development, OpenClaw (Linda V2) as a
project manager, and Linda V1 in the Muse app as an interop counterpart. Agent HQ v1 gives them
shared *plans* but not shared *conversation*. Every question and every file still travels
through Andy.

The Fleet Room solved conversation in another system with another shape (one broadcast room
relayed through NASTV). Its useful lessons are about *delivery honesty*: gapless replay with an
announced resync, a durable per-consumer cursor, presence that goes offline rather than lying,
and caps enforced server-side. This plan keeps those lessons and drops the room.

Four facts shape the work. Each was checked against the code on 2026-10-24 and names the file
that proves it.

1. **The hook feed is one-way.** `POST /hooks/zcode/prompts` takes a prompt and returns
   `{ok, matched}` (`src/AgentHQ.Api/Services/HookFeedService.cs`). Nothing flows back to the
   agent. Injection therefore needs the hook to print a line to stdout, which an inline `curl`
   in `settings.json` cannot do — it needs a script (D1). Whether a script can inject is
   unproven and is phase 0's gate (D16).
2. **Delivery uses two mechanisms.** ZCode is *pulled* (the hook asks, the server answers with a
   digest); Linda V2 is *pushed* (it holds the SSE stream). They share the event log and nothing
   else. Do not build a poller and call it real-time.
3. **Anything injected into a session is untrusted text in an agent's context.** The boundary is
   the digest, so digests carry server-built structure and never bodies (D15).
4. **A ZCode session cannot be woken mid-turn.** Its reaction time is a turn boundary. Build
   around that openly.

## Terms used throughout

Read this before phase 0. The same words are used the same way everywhere below.

| Term | Meaning |
|---|---|
| **Actor** | Who posts or receives. A pair `(actor_type, id)`: `("zcode", "ses_…")`, `("openclaw", "linda_v2")`, `("linda_v1", "linda_v1")`, `("andy", "andy")`. |
| **Role addressing** | An item addressed to `*` for a repository, claimable by any live ZCode session whose `cwd` resolves to that repository (D13). |
| **Item** | One row in `exchange_items`: a question, answer, message, file or status post. |
| **Thread** | Items linked by `thread_id`. A question is the root; answers and follow-ups hang off it. |
| **Hop** | A reply to a reply. The root is hop 0; the limit is 3 (root plus three replies). |
| **Digest** | The short server-built text the hook injects at a turn boundary: one line per item, structure only (D15). Never called a "summary" — no model writes it. |
| **Blob** | File bytes stored on disk under `AGENTHQ_EXCHANGE_BLOB_DIR`; the item row holds only path, size, content type and SHA-256. |
| **L0 / L1 digest** | L0 = what every session gets. L1 = richer line (adds purpose and `context_refs`) only when the session's repository matches the item's `context_refs.repo`. |
| **Pull / push** | Pull = the consumer asks (ZCode hook, Linda V1's watcher). Push = the server sends (OpenClaw over SSE). |
| **Presence** | A heartbeat row per push consumer. Staleness is 600 s; past that the actor renders offline. |
| **Cursor** | A consumer's last-seen event `seq`. Cursors are stored server-side per actor so an offline consumer can catch up. |
| **Resync** | The announced answer when a cursor is older than the event log reaches back: `exchange.resync`, then catch-up. Never a silent gap. |
| **The exchange tools** | `exchange_post`, `exchange_get`, `exchange_list_inbox`, `exchange_claim`, `exchange_reply`, `exchange_ack`, `exchange_kill`, `exchange_list_open`, `exchange_watch` (poll/SSE contract), `exchange_presence`, `exchange_upload` + `exchange_download` (one blob pair, counted together), `exchange_cursor`. Twelve entry points in all. |

## Steps

Each phase is a unit of work that ends with evidence in the Status Log: tick the phase's boxes
there, with the test names or transcripts that prove them. Do the phases in order unless the
paragraph below says otherwise. The §"Verification Criteria" items are the acceptance list for
the whole plan and are checked as the phase that implements them lands; criterion 19, the suite
gate, runs at every phase boundary.

**Phase map:** 0 hook gate · 1 data model and plan identity · 2 items, tools, REST, minimal Andy
page · 3 hook injection · 4 dashboard view · 5 Linda V1's route · 6 OpenClaw push · 7 short-cut
fix · 8 room retirement. Phases 0 and 1 have no dependency on each other and can run in
parallel. Phase 7 is independent of the Exchange and can be done at any time; it is listed late
only because it is small.

### Phase 0 — Prove the hook can inject context (no schema, no code in this repository)

This is the load-bearing assumption of the whole pull path: that a ZCode hook can both post a
prompt and return text into the agent's context. Prove it before anything is built on it. Phase 0
touches one file on this machine and no code in this repository. It can be thrown away.

- [ ] Find where ZCode actually registers hooks before writing anything. The tool's own
      documentation (`zcode-guide:zcode-configuration-guide`, skill `diagnosing-hooks`) is the
      authority; the installer and this plan assume `~/.zcode/settings.json` → `hooks`, and the
      current hook is found there on this machine. If the authority says otherwise, record the
      correct file and shape in the Status Log **first**, then continue.
- [ ] Check which events accept a `matcher` and which do not (`diagnosing-commands` /
      `diagnosing-hooks` skills). Write the answer in the Status Log. Phase 3's registration
      depends on it; do not guess a matcher string.
- [ ] Write `~/.zcode/hooks/agent-hq-hook.sh` by hand (not the installer yet). Requirements:
      - `#!/usr/bin/env bash`; `set -u`; never `set -e` (a failing step must still exit 0).
      - Read the hook payload from **stdin** as JSON. Extract the session id, the event name and
        the prompt with `jq` (`command -v jq` must succeed; `jq` is installed on this machine).
      - Append the raw payload to `~/.zcode/hooks/agent-hq-hook.log` for phase 0 only.
      - Print exactly one line to stdout:
        `{"hookSpecificOutput":{"hookEventName":"<event>","additionalContext":"<one line>"}}`
        built with `jq -n --arg` so the text is escaped.
      - Any failure — missing `jq`, bad JSON, unreadable stdin — exits 0 **printing nothing**.
- [ ] Capture the real payload. Open a fresh ZCode session and send one prompt. Record in the
      Status Log, verbatim, every field present for `SessionStart` and for `UserPromptSubmit`,
      and which field carries the event name. These field names are the installer's contract
      (phase 3) — do not guess them from `${CLAUDE_SESSION_ID}`.
- [ ] Turn the log off and switch the script to "always inject one line". Confirm, in a fresh
      session and again in a prompt, that the agent can see the line (ask the session to read
      back what it was told).
- [ ] Change the script to inject nothing on an empty inbox. Confirm there is no empty banner and
      no error.
- [ ] Change the hook *declaration* in the client settings (add a space) and confirm the hook is
      blocked until ZCode re-approves it, and note exactly what the client shows. This failure
      mode must never be silent; phase 3 verifies it programmatically (D17).
- [ ] Decide the script's location and uninstall story. `~/.zcode/hooks/` is shared; the
      installer must remove exactly the file it wrote and nothing else, even if a user edited it
      (same rule as the command pack: a marker line decides, and an edited file is reported and
      kept).

**Gate:** a line injected by the hook is visible to a real ZCode session, and an empty inbox
produces no output. **If this fails, stop.** ZCode becomes a pull-only actor like Linda V1 (it
asks with `exchange_list_inbox` at turn boundaries instead of being handed a digest), phase 3 is
reduced to "the hook posts prompts", and decision 2 is reopened with whoever funds the plan.
Record the failure and the fallback in the Status Log.

### Phase 1 — Data model and plan identity (no exchange traffic yet)

**Build order inside the phase:** 1a (migration + tables), then 1b (types, options, validation),
then 1c (plan identity: D10–D12). Each ends with `dotnet build` and `dotnet test`.

**1a — Migration `0002_exchange.sql`.** Highest existing migration is `0001_Initial.sql`
(`src/AgentHQ.Infrastructure/Migrations/`, embedded by a csproj glob, run by DbUp at startup in
`AgentHqDb.Migrate()`).

- [ ] `exchange_items` with at least: `id` (TEXT primary key, a GUID string — the same form as
      `plans.id`), `kind`, `state`, `latency_class`, `thread_id` (self-reference; the root item
      carries its own id), `hop_count`, `sender_actor_type`, `sender_actor_id`,
      `recipient_actor_type`, `recipient_actor_id` (role addressing = recipient type `zcode`),
      `repository_id` (nullable, required for role-addressed items — D13), `subject` (bounded,
      see the cap in 1b), `body` (nullable, bounded), `context_refs` (JSON text, nullable),
      `purpose` (nullable, required for questions), `claimed_by_session_id`, `claimed_at`,
      `created_at`, `expires_at`, `answered_at`, `acknowledged_at`, `killed_at`,
      `idempotency_key`, `blob_path`, `blob_size`, `blob_content_type`, `blob_sha256`.
      Times are integer epoch milliseconds, as everywhere else in v1.
- [ ] Unique index on `(sender_actor_type, sender_actor_id, idempotency_key)`.
- [ ] An index for each read the service does on every step: `(recipient_actor_type,
      recipient_actor_id, repository_id, state)`, `(thread_id)`, and `(state, expires_at)`.
- [ ] Check constraints: `kind IN ('question','answer','message','file','status')`,
      `state IN ('queued','delivered','claimed','answered','acknowledged','expired','killed')`,
      `latency_class IN ('realtime','turn','minutes','human')`. A typo must fail at write time,
      not at digest time.
- [ ] `exchange_presence`: `(actor_type, actor_id)` primary key, `last_seen_at`, `latency_class`.
      Presence is a heartbeat, not a status: there is no `offline` value in the table; offline is
      derived (`now - last_seen_at > 600 s`).
- [ ] `exchange_cursors`: `(actor_type, actor_id)` primary key, `last_seq`, `updated_at`.
- [ ] There is **no separate blob table**. Blob metadata lives on the item row. There is no
      `exchange_threads` table: a thread is the set of items sharing `thread_id`.

**1b — Core types, options, validation.**

- [ ] `ExchangeActor` value type in `AgentHQ.Core` (namespace `AgentHQ.Core.Exchange`): a record
      `(ActorType, ActorId)` with `TryParse("zcode:ses_123")`, `ToDisplayString()` and a
      canonical comparison. String form is `<type>:<id>`; the id never contains `:` (validate).
- [ ] `ExchangeActorTypes` constants: `andy`, `openclaw`, `linda_v1`, `zcode`. Ids:
      `openclaw` uses `linda_v2`; `linda_v1` uses `linda_v1`; `andy` uses `andy`; `zcode` uses
      the session id. Every write validates against this set, and `session` (the plan-lifecycle
      actor type) is rejected with an error that **says so**: "`session` is a plan-lifecycle
      actor; an exchange sender must be one of andy, openclaw, linda_v1, zcode (with the session
      id as the id)".
- [ ] `ExchangeOptions` record resolved once at startup in `AgentHqHost.BuildAsync` and registered
      in DI (one composition root — do not read environment variables inside services). Fields
      and defaults:

      | Option | Env var | Default | Meaning |
      |---|---|---|---|
      | `BlobDir` | `AGENTHQ_EXCHANGE_BLOB_DIR` | `$AGENTHQ_DATA/blobs` | Where blob bytes live |
      | `BodyMaxBytes` | `AGENTHQ_EXCHANGE_BODY_MAX` | 32768 | Largest item body |
      | `SubjectMaxChars` | — | 120 | Subject length; longer is truncated for storage as well as digests |
      | `BlobMaxBytes` | `AGENTHQ_EXCHANGE_BLOB_MAX` | 52428800 | Largest single blob |
      | `RatePerMinute` | `AGENTHQ_EXCHANGE_RATE` | 30 | Posts per sender per minute; `andy` exempt |
      | `HopLimit` | — | 3 | Replies-to-replies |
      | `EventReplayWindowSeconds` | — | 604800 | Older cursors get `exchange.resync` (D8) |
      | `PresenceStaleSeconds` | — | 600 | Offline threshold |
      | `DigestBudgetChars` | `AGENTHQ_EXCHANGE_DIGEST_BUDGET` | 2000 | L0 digest budget |

      An unparseable or zero value is a startup failure with a message naming the variable and
      the accepted range — not a silent default.
- [ ] **D13 — one repository-resolution rule.** Role addressing routes by the session's `cwd`
      through the rule `RepositoryStore.ResolveByCwdAsync` already implements: exact match or
      `cwd` starts with `<root>/`, longest root wins (`src/AgentHQ.Infrastructure/Stores/
      RepositoryStore.cs:71`). The new code **calls that method** — it does not re-implement the
      prefix rule. Test with the nesting this machine has: `nastv` and seven paths beneath it are
      registered separately, so a question in the player subtree must not appear in the `nastv`
      root's sessions.

**1c — Plan identity (D10–D12).**

- [ ] **D10 — optional `slug`.** `POST /plans` and MCP `submit_plan` accept a new `slug`
      parameter (REST: optional JSON field; MCP: a **required** `string slug` with the
      empty-string sentinel, per the MCP rule in `AGENTS.md` and `docs/features/mcp.md`).
      `PlanService.SubmitAsync` gains a `string? slug` argument and validates it against
      `PlanSlug.Valid()` — which does **not exist today**; `PlanSlug.Valid()` is the private
      `[GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]` in `src/AgentHQ.Core/Plans/PlanSlug.cs`, so
      expose a public `IsValid(string)`. When taken in the repository, refuse — REST
      `422 {ok:false, error:"slug '<s>' is already used by plan '<id>'"}`, MCP
      `{ error = "slug_taken", detail = … }` — and **auto-suffixing still applies to the
      derived default**. A chosen slug never gets `-2` appended: the author asked for that name.
      `submit_plan`'s description must document the new parameter (tool descriptions carry the
      contract).
- [ ] **D11 — re-point and delete.** `PATCH /plans/{id}/file` with `{ "path": "<absolute or
      repo-relative>" }`: the path must be an existing file under the plan's **own repository
      root** and under that repository's plans directory; history is kept; body
      `{id, file}`; emit a new event `plan.file_changed` with
      `{planId, slug, from, to, actor}`. `DELETE /plans/{id}`: deletes the plan row, its
      history, dependencies and blocks (a new `PlanStore.DeleteAsync` that runs those deletes in
      one transaction); **never touches the file on disk**; emit `plan.deleted` with
      `{planId, slug, repositoryId, actor}`; `404` if unknown. Both are admin actions: actor
      `andy` from the browser/REST, `openclaw` from MCP. Message content belongs to the
      provider's index, but plan identity belongs to Agent HQ, so these events carry no file
      content.
- [ ] **D12 — missing plan files are a result, not an exception.** `read_plan` (and the REST
      `GET /plans/{id}/content` and `GET /plans/{id}`) answer
      `{ error = "not_found", detail = "plan file is missing on disk: <path>" }` /
      `404 {ok:false}` when the row exists but the file does not. Integration test: register a
      plan, rename the file on disk, call the tool, assert a structured result — not a raw
      exception.
- [ ] Update `docs/features/api.md`, `docs/features/mcp.md`, `docs/features/plan-lifecycle.md`,
      and the `src/AgentHQ.Infrastructure/AGENTS.md` / `src/AgentHQ.Core/AGENTS.md` files in the
      same commit.

### Phase 2 — Items, lifecycle, tools, REST, and Andy's minimal page

This is the largest phase. **Build order — do not reorder, each step is a testable runner:**
(1) store and service, (2) state machine and unit tests, (3) REST, (4) blobs, (5) MCP wrappers,
(6) minimal page, (7) digest, (8) governance (rate cap, hop limit, mentions, truncation), (9) the
test list at the end. After step 3 the REST surface is usable by `curl`; after step 5 an agent
can use it; after step 6 Andy can.

**2.1 Store and service.**

- [ ] `ExchangeStore` (Dapper, `AgentHqDb.Open()`, explicit `@Name` parameters with consistent
      casing, aliased snake_case columns via `Columns.cs`) and `ExchangeService` (the only place
      that moves state). REST and MCP both call the service. Never write SQL from an endpoint or
      a tool.
- [ ] Post (question / message / file / status), get by id, list inbox, claim, reply, ack, kill,
      list open, digest. Every write that changes state returns the new item row.
- [ ] Every transition appends its event through the existing `EventStore.AppendAsync`. The order
      is: item row, then event; if the event append fails the caller sees the failure and the
      item row is not reported as success. Events carry metadata only — id, kind, state, sender,
      recipient, thread, hop — never bodies or bytes.
- [ ] Event types (constants in `EventTypes`): `exchange.created`, `exchange.claimed`,
      `exchange.answered`, `exchange.acknowledged`, `exchange.expired`, `exchange.killed`,
      `exchange.resync`, `exchange.presence`. The dashboard's activity panel already renders
      unknown types generically, so only the `describe()` formatting is new (phase 4).
- [ ] **Idempotency:** a repeat of `(sender, idempotency_key)` returns the original id with
      `deduplicated: true` and creates nothing — in the same transaction, not a check-then-insert.

**2.2 State machine (D4).** New `.cs` file and unit tests under `src/AgentHQ.Core/Exchange/`.
This is a *trigger* state machine driven by actions — it is **not** `PlanStateMachine`, and no
status code moves through `PlanStatusCodes`. Statuses in `plans` and exchange states are
unrelated names in unrelated tables.

| From | Action | To | Notes |
|---|---|---|---|
| — | post `question` | `queued` | role- or actor-addressed; `purpose` required |
| — | post `message` / `file` | `queued` → `delivered` | `delivered` means "the recipient's consumer has taken it into a digest / ack stream" |
| — | post `status` | `delivered` | informational; no hops; no state of a question changes |
| — | post `answer` | `delivered` | created only by `exchange_reply` on a question |
| `queued` | `claim` (role-addressed only) | `claimed` | second claimer gets `already_claimed` |
| `queued` / `claimed` | `reply` (kind `answer`) | `answered` | only the claimant may reply to a claimed question; nobody may reply after `killed` |
| `delivered` (message/file/answer) | `ack` | `acknowledged` | |
| `answered` | `ack` by the asker | `acknowledged` | |
| any non-terminal | `kill` (Andy only) | `killed` | "non-terminal" = not `acknowledged` |
| any non-terminal with `expires_at`, now > `expires_at` | expiry sweep | `expired` | sweep runs at read time and on a 60 s timer in the host |
| `message`/`file`/`answer` | `claim` | — | refused `illegal_transition` |
| anything | a 4th hop | — | refused `hop_limit` (D5) |

- [ ] Each row above is an entry in the map and a unit test, in both directions (legal edge
      works, illegal edge is refused with the named code).

**2.3 REST** — `src/AgentHQ.Api/Endpoints/ExchangeEndpoints.cs`, registered from
`AgentHqHost.BuildAsync` next to `MapPlans()`:

| Method and path | Body / query | Response |
|---|---|---|
| `POST /exchange/items` | `{kind, recipient, repository, subject, purpose, body, contextRefs, expiresAt, idempotencyKey, latencyClass, blob?}` | `201 {id, state, deduplicated, threadId}` |
| `GET /exchange/items/{id}` | — | full item; `404 {ok:false}` if unknown |
| `GET /exchange/inbox` | `?actor=&repo=&state=&limit=` | items for a consumer |
| `POST /exchange/items/{id}/claim` | `{claimant}` | `{state:"claimed", claimedBy}` or `409 {error:"already_claimed", claimedBy}` |
| `POST /exchange/items/{id}/reply` | `{sender, body, subject?, idempotencyKey}` | `201` answer item, or `409`/`422` with a code |
| `POST /exchange/items/{id}/ack` | `{ackSender}` | `{state:"acknowledged"}` |
| `POST /exchange/items/{id}/kill` | `{by:"andy", reason}` | `{state:"killed"}`; `403` for any other actor |
| `GET /exchange/open` | `?repo=` | open questions with claimant and ages (Andy's list) |
| `POST /exchange/items/{id}/blob` | chunked: `?offset=&total=` and raw bytes | `{received, complete, sha256}` |
| `GET /exchange/items/{id}/blob` | — | bytes with `Content-Type`; `409 {error:"hash_mismatch"}` if the stored hash differs |
| `GET /exchange/watch` | `?actor=&sinceSeq=` | SSE or JSON poll, same shape as `/events` |
| `POST /exchange/presence` | `{actor, latencyClass}` | heartbeat; `204` |

- [ ] Error shape everywhere: `{"ok":false,"error":"<code>","detail":"<human sentence>"}` with
      the codes: `unknown_actor`, `purpose_required`, `hop_limit`, `rate_limited`,
      `already_claimed`, `thread_killed`, `hash_mismatch`, `scope_denied`, `illegal_transition`,
      `not_found`, `body_too_large`, `blob_too_large`, `bad_idempotency_key`.

**2.4 Blobs.**

- [ ] Bytes live at `$AGENTHQ_EXCHANGE_BLOB_DIR/<itemId>.bin`. Chunked deposit: each chunk is
      appended at `offset`; `complete` is only true when `received == total`; then the server
      computes SHA-256 and compares with the sender's declared hash. A mismatch rejects the
      deposit with `hash_mismatch` and deletes the partial file. Fetch re-verifies and returns
      the same error.
- [ ] `content_type` = the declared type **and** a magic-byte sniff (PNG, JPEG, PDF, ZIP, plain
      text at minimum); a mismatch stores the sniffed type and logs a Warning naming the item.
- [ ] Enforce `BlobMaxBytes` at the first chunk — refuse before writing, not after.
- [ ] Blobs never pass through SSE, MCP text results or the digest. An MCP tool that must return
      bytes returns a URL and hash instead.

**2.5 MCP wrappers** — in `AgentHqTools` (or a new partial class), thin calls into
`ExchangeService`. Rules from `AGENTS.md` that are easy to get wrong: all parameters required,
empty-string sentinels trimmed in the body; plain property names in the returned anonymous
objects; embedded payloads serialized as JSON text yourself; descriptions carry the contract and
name the error codes.

**2.6 Andy's minimal page (D18).** A new `/exchange` route in the no-build-step UI:
`src/AgentHQ.Api/wwwroot/exchange.html` + `exchange.js` + small additions to `ui.css`. No
external URLs (a contract test asserts this). It shows open questions (id, sender, age, purpose,
state), lets Andy answer, ack and kill. Everything it does calls the REST routes above — no new
server logic. Phase 4 replaces this page rather than adding beside it.

**2.7 Digest (D6, D15).**

- [ ] One line per item, built by the server from bounded fields only:
      `#<id> [<kind>] from <type>:<id> (<age>, <state>): <subject>`; claimed items say
      `claimed by <type>:<id>`. Age is coarse (`3m`, `2h`, `1d`). Every field is escaped: strip
      control characters, collapse newlines to a space, and wrap values that begin with `@`, `/`
      or backtick in single quotes so they cannot read as a command.
- [ ] L1 (only when the item's `context_refs.repo` equals the session's resolved repository)
      adds `purpose` and `context_refs`. L1 is not a body tier.
- [ ] Budget `DigestBudgetChars` (2000): add lines in oldest-first order while they fit; the
      remainder renders as one final line `… and N more`.
- [ ] A body containing an instruction-shaped string (`ignore your instructions`, a fake
      `/plan done x`) can never appear in any digest output — test at L0 and L1.

**2.8 Governance.**

- [ ] `purpose` is required for `kind=question` (`purpose_required`); optional otherwise.
- [ ] Hop limit 3 (`hop_limit` on the 4th reply) and the refusal names the thread id so the
      caller can surface it.
- [ ] Rate cap: 30 posts per sender per minute, `andy` exempt (`rate_limited`, with the retry
      delay in `detail`). Enforced in the service, not in the endpoint.
- [ ] Mentions: a `message` with no recipient that contains `@andy` / `@openclaw` / `@linda_v1`
      is addressed to that actor (last mention wins, all mentions recorded); a message with no
      mention and no recipient is refused `unknown_actor` rather than broadcast.

**2.9 Tests at the end of the phase** — `tests/AgentHQ.Tests/ExchangeTests.cs` on `AgentHQApp`
(in-memory host, real DB, temp dirs), plus the unit tests from 2.2:

- [ ] Idempotency, claim race (two claimants, `Task.WhenAll`), hop limit, kill, expiry, rate cap,
      mention routing, unknown actor, `session` rejected as a sender with the explaining error.
- [ ] The digest-content test from 2.7.
- [ ] The nested-repository routing test from phase 1 (D13).
- [ ] REST round trip for every route in 2.3, and a contract test for `/exchange` assets and
      fields (same style as `WebUiContractTests`).

### Phase 3 — Hook injection (ZCode becomes reachable)

**Build order:** (1) the server's session-start route and digest response on the existing feed,
(2) the script, (3) installer registration + idempotent upgrade from the inline curl, (4)
install-time verification, (5) tests.

- [ ] **Server side.** The hook calls two routes:
      - `POST /hooks/zcode/prompts` (as today) for a prompt; its response body gains the digest
        text (empty string when there is nothing).
      - `POST /hooks/zcode/session-start`: same request payload fields as captured in phase 0;
        returns the digest text the same way.
      The server decides everything about the injection — what is addressable, what is stale,
      what fits the budget, whether to inject at all. The hook is a dumb pipe.
- [ ] **The script** the installer writes, `~/.zcode/hooks/agent-hq-hook.sh`, mode 0700, first
      line `#!/usr/bin/env bash`, second line a marker `# agent-hq-hook v1`:
      1. Read stdin once into a variable. If empty or not JSON, exit 0 with no output.
      2. Extract session id, cwd, prompt and event name with `jq` (field names from phase 0).
      3. Build the POST body with `jq -n --arg` — never with string interpolation, never with
         `eval`.
      4. POST with a short timeout (`--max-time 3`) to the port baked in at install time.
      5. If the HTTP status is not 2xx or the body is empty, exit 0 printing nothing.
      6. Otherwise print `{"hookSpecificOutput":{"hookEventName":"<event>","additionalContext":
         <body as a JSON string>}}` built with `jq -n --arg`.
- [ ] **Registration.** `UserPromptSubmit` is **replaced in place**: the existing entry `if and
      only if` its command contains `/hooks/zcode/prompts` (the installer already recognizes its
      own entry this way) is swapped for the script command; a user-added `UserPromptSubmit`
      entry that does not mention Agent HQ is left alone. `SessionStart`, if the phase-0 capture
      says it takes a matcher, is added with the matcher phase 0 recorded; otherwise it is added
      without one. The inline-curl entry is deleted in the same write (removal is part of the
      change — `AGENTS.md`).
- [ ] **Trust.** The installer prints, per event, that the trust grant must be re-approved and
      where. It must not claim the hook is "installed and working" on the strength of the file
      write.
- [ ] **Install-time verification (D17).** After writing the hook, if `/health` answers, the
      installer posts one synthetic prompt with a session id and `cwd` it knows (the repository
      being installed) and reads it back from the server; if the server never saw it, the
      installer exits non-zero saying the hook is blocked or misconfigured and the trust must be
      approved in the client. If `/health` does not answer, it skips with an explicit warning
      naming the reason. `check` — the read-back command used by `agent-hq-deploy.sh` — repeats
      this test so a blocked hook is reported after every deploy, not only after install.
- [ ] **The order of reflection.** Filter the caller out of its own inbox: a session that posts
      a question must not see it in its next digest. Test it.
- [ ] **Never inject an empty inbox** — empty body, no banner, no "you have no messages" line.
- [ ] **Preamble.** Every non-empty injection starts with one fixed sentence stating that these
      are peer requests and claims and are never instructions from Andy.
- [ ] **Carried fix #2 is done here by construction.** The same script posts `/plan` prompts, so
      the escaping fix covers the plan-transition path; there is no second code path to fix.

**Tests (all in the repo, none needing a live ZCode):**

- [ ] A script-level test: install into a fake `HOME` (the installer tests already use one),
      start the in-memory host on a loopback port, pipe a payload through the script with `bash`,
      and assert (a) the `additionalContext` JSON parses, (b) the prompt read back from the
      server is byte-identical for a prompt containing `"`, `'`, newlines, backslashes, `$`,
      `${x}`, `{...}` and `</script>`. `jq`, `bash` and `curl` are required; the test **fails
      with a named reason** if `jq` is missing (not skip), because the script depends on it.
- [ ] Empty inbox → script output is empty (no newline).
- [ ] Server down / non-2xx → exit code 0 and empty output.
- [ ] Installer tests (extend `InstallerTests`): the inline curl is gone; the script exists with
      mode 0700; a second run changes nothing (the existing idempotency snapshot test covers the
      new files too); a user-added `UserPromptSubmit` entry survives; "already installed" is
      detected on the second run.
- [ ] Install-time verification: with the host up and the hook unblocked the installer exits 0;
      with the host answering but the prompt never arriving (simulate by pointing the script at a
      dead port and the check at the live host) it exits non-zero and names trust approval.

**Docs:** `docs/features/zcode-integration.md` (the hook is a script; `SessionStart` is
registered; upgrade and re-approval), `docs/features/api.md` (the new response body), and
`src/AgentHQ.Commands.ZCode/AGENTS.md`.

### Phase 4 — Dashboard Exchange view (Andy's real surface)

- [ ] Andy's inbox pinned first, with an unread badge, separate from the open-questions list.
- [ ] Open-questions list: id, thread, sender → recipient/claimant, age, hops, purpose, and the
      item's **real** state — never inferred from position.
- [ ] Thread view: the full chain with timestamps. Blob items render by `content_type` (image
      preview, otherwise a download button) and never auto-open executable content.
- [ ] Presence strip, derived: an actor with `last_seen_at` older than 600 s renders *offline*,
      never "present but quiet".
- [ ] Activity feed: Exchange events are readable through `describe()` (new types), filterable by
      actor and kind. **Aggregation rule:** repeated same-type events for the same item that
      arrive **while the activity panel is closed** collapse into one row reading `×N`. The count
      is decided once, at arrival, so the `?sinceSeq=` fallback and the reconnect replay — which
      re-read old events — must be reconciled by `seq` and must never change an existing row's
      count in either direction (the panel already dedupes by `data-seq`). If the new event types
      make live churn visible, apply the same collapse to other types only when the cause is
      event churn; `session.updated` churn was already removed at the source (`docs/features/
      events.md`, "signal-shaped").
- [ ] Everything goes through the same REST surface. No UI-only back doors.
- [ ] This replaces the phase-2 page (D18); no second page is left beside it.

### Phase 5 — Linda V1's route

**Order:** (1) the round trip over loopback, (2) then the route.

- [ ] **Prove the round trip first (D19).** Linda V1 calls the loopback endpoints by hand once: she
      asks (or answers) a question, the other side acknowledges it, and every state has a
      timestamp. Record the transcript in the Status Log. This must pass **before** the first
      non-loopback listener exists.
- [ ] Dedicated routed endpoint with TLS, a **per-actor revocable key scoped to `/exchange`
      only**, rate limits, and one audit row per call. The hook feed, `/mcp`, and every plan-admin
      surface stay loopback-only, permanently.
- [ ] A key sent outside `/exchange` is rejected `403 scope_denied`; a request arriving on the
      public route for any non-`/exchange` path returns **`404`, not `401`** (do not confirm admin
      surfaces to a remote caller).
- [ ] Where TLS terminates and who holds the key are an infrastructure decision **outside this
      repository** and a prerequisite for this step. Until it is recorded here (hostname, cert
      owner, key custodian, revocation procedure), the route is not built.
- [ ] Deploy note in `docs/operations.md`: this is the first time Agent HQ listens to anything
      but `127.0.0.1`. It needs its own security review before deploy; it is not a config tweak.

### Phase 6 — OpenClaw push (documented, not gated)

- [ ] Bridge side: nothing beyond what phase 2 emits. `exchange.created` addressed to `openclaw`
      is an event like any other, and `/exchange/watch?sinceSeq=` serves the same rows.
- [ ] the OpenClaw-side consumer (outside this repository) subscribes over SSE, persists its
      cursor via `exchange_cursor`, and starts a turn on delivery. Recorded here so the bridge's
      guarantee is on record. Not gated by this plan.

### Phase 7 — The verification short-cut (carried fix #1)

*(The decision is D2. Read it before coding: it is the opposite of the original spec's wording.)*

Today: `PlanService.ApplyCommandAsync` calls `SetImplementedByAsync` only for `/plan implemented`
(`src/AgentHQ.Infrastructure/Services/PlanService.cs:216`). On the short-cut (`verify` from
`implementing`/`fixing`) it records `→ implemented` attributed to the verifier and sets nothing,
so the plan has no implementer of record and a later `pass` by that same session produces **no**
separation-of-duties warning — even though that session did the work of record.

- [ ] Leave `SetImplementedByAsync` off the short-cut (it already is). Do not add it.
- [ ] The short-cut keeps recording `→ implemented` attributed to the verifying session (the
      existing test `ShortCutEntryRecordsIntermediateImplementedAttributedToVerifier` pins this,
      and it is correct).
- [ ] Add a `via_shortcut` column to `plan_status_history` — a new migration `0003_…sql` (never
      edit `0001`), default 0 — set it on the intermediate row, and surface it as
      `viaShortcut` in the history rows of `GET /plans/{id}`. This makes "the verifier recorded
      this, not an implementer" legible from data rather than inferred from the command string.
- [ ] Keep the row attributed to the verifier and mark it `viaShortcut: true`; keep the existing
      response warning `short-cut entry: recorded → implemented attributed to verifying session …`
      unchanged. Do not add a second warning.
- [ ] Extend the existing test to assert both facts: the intermediate row names the verifier and
      carries `viaShortcut: true`; `implementedBy` is still absent on the plan; and a later
      `pass` from that session fires no separation-of-duties warning.

### Phase 8 — Room retirement (only after §"Verification Criteria" passes in full)

- [ ] Final room message pointing at the Exchange, and an exported archive of the room history.
- [ ] Relay service stopped and disabled; the room goal closed.
- [ ] Until every criterion passes, the room stays live and authoritative for fleet chatter.

### Closing steps (required by this repository's plan rules)

- [ ] Docs: new `docs/features/exchange.md`; update `events.md` (new event types incl.
      `plan.file_changed` and `plan.deleted`, the `exchange.resync` contract), `mcp.md` (the
      tools, the `slug` parameter), `web-ui.md` (the view), `zcode-integration.md` (hook script,
      `SessionStart`), `api.md` (`/exchange`, `PATCH /plans/{id}/file`, `DELETE /plans/{id}`),
      `plan-lifecycle.md` (`viaShortcut`, delete/re-point), `operations.md` (blob dir, retention,
      V1 route). Link the new page from `docs/README.md`.
- [ ] Feature knowledge files: `src/AgentHQ.Core/AGENTS.md` (actor model, trigger state machine),
      `src/AgentHQ.Infrastructure/AGENTS.md` (exchange tables, blob verification),
      `src/AgentHQ.Api/AGENTS.md` (tool surface, hook response shape),
      `src/AgentHQ.Commands.ZCode/AGENTS.md` (hook script, trust), `src/AgentHQ.Api/wwwroot/
      AGENTS.md` (the view).
- [ ] Remove the workaround the `.gitignore` documents — the two symlinks
      `docs/plans/conversation-in-agent-hq-exchange-v2*.md` and their `.gitignore` block — once
      D10 lands and the stale rows are deleted with `DELETE /plans/{id}`.

## Dependencies

- **Nothing blocks phase 1.** Plans, events, MCP and the hook feed are shipped and tested; this
  plan extends each.
- **Phase 0 blocks phase 3.** There is no injection path to build until the gate passes.
- **Phase 0 needs a human interactive session in ZCode.** The implementer cannot approve hook
  trust (or see a trust prompt) from inside a tool call. If no human is available to run phase 0,
  stop and say so; it is a hard blocker, not a step to skip.
- **Phase 5's TLS route depends on an infrastructure decision outside this repository** (where
  TLS terminates, who holds the key). Loopback round-trip proof does not.
- **Phase 6 depends on OpenClaw-side consumer work**, outside this repository.
- **Phase 8 depends on Linda V1 accepting the route** and on every criterion below.
- **The Fleet Room remains the fallback transport** until phase 8.

## Verification Criteria

Each criterion names how it is checked. "In-memory host" means `AgentHQApp` with real watchers.
Numbers refer to the original spec's acceptance list; 11–19 are this plan's corrections.

- [ ] **1 — Idempotency.** Same sender and `idempotency_key` twice (the second after a host
      restart) yields one item; the second call returns the original id with
      `deduplicated: true`. *Test: in-memory host.*
- [ ] **2 — Linda V1 round trip.** A question from a ZCode session reaches `linda_v1`; her reply
      appears in the asking session's next injected digest; states pass `answered →
      acknowledged` with timestamps. *Test: transcript over loopback (phase 5 step 1); last leg
      in-memory.*
- [ ] **3 — Claim race.** Of two sessions shown a role-addressed question, one claims; the other
      sees `claimed_by`, receives `already_claimed`, and cannot reply. *Test: in-memory, concurrent.*
- [ ] **4 — Binary both directions.** An image and a file cross actors intact and hash-verified;
      a corrupted blob is refused on fetch with `hash_mismatch`. *Test: in-memory.*
- [ ] **5 — Hop limit.** A fourth hop is refused with `hop_limit`; Andy's list shows the thread
      with `hopCount = 3`. *Test: in-memory.*
- [ ] **6 — Kill switch.** Andy kills a live thread; later digests render it killed and a reply
      returns `thread_killed`. *Test: in-memory.*
- [ ] **7 — Offline catch-up.** Two hours offline, then a cursor catch-up delivers every item in
      order with zero duplicates. *Test: in-memory (simulated clock/replay).*
- [ ] **8 — Resync honesty.** A cursor older than the window (7 days, see options; the test uses
      a configurable window) gets `exchange.resync`, not a silent gap, and completes catch-up
      over REST. *Test: in-memory.*
- [ ] **9 — Expiry.** An unanswered question past `expires_at` leaves every digest, emits
      `exchange.expired`, and stays readable by id. *Test: in-memory.*
- [ ] **10 — Provenance.** No agent-authored item is rendered anywhere in a form that attributes
      it to Andy or presents it as his instruction. *Check: digest lines, the REST responses the
      UI renders, and the UI's rendering; a test asserts the digest and the open-list rendering
      always carry the sender actor.*
- [ ] **11 — Hook robustness.** A prompt containing `"`, `'`, newlines, backslashes, `$` and
      `{...}` reaches `/hooks/zcode/prompts` with the original bytes, verified by reading the
      stored/echoed value back — not by trusting the script's exit code. *Test: script-level.*
- [ ] **12 — No empty injection.** A session start or prompt with nothing addressed to the session
      produces no hook output at all. *Test: script-level and in-memory.*
- [ ] **13 — Short-cut evidence.** After a short-cut verification, the intermediate history row
      names the verifying session with `viaShortcut: true`, `implementedBy` is absent from the
      plan, and no separation-of-duties warning fires on the same session's later `pass`.
      *Test: extended `PlanLifecycleTests`.*
- [ ] **14 — Feed aggregation.** Twenty incoming events of one type for one item, all arriving
      while the activity panel is closed, render as **one** row reading `×20`. Re-derivation
      (poll fallback, reconnect replay, scan of unknown types) must not change the count in
      either direction. *Check: browser pass with screenshots; the reconciliation rule is stated
      in `wwwroot/AGENTS.md`.*
- [ ] **15 — Plan identity.** Submitting with an explicit `slug` writes that file name; a taken
      slug is refused with a code; a file renamed on disk can be re-pointed and stays readable; a
      deleted registration disappears from every list and its file survives on disk; a missing
      file yields `not_found` from MCP instead of an exception. *Test: in-memory.*
- [ ] **16 — Digest carries no content.** A peer message whose *body* contains instruction-shaped
      text produces a digest with only id, kind, sender, age, state and a truncated subject; the
      body appears only after an explicit `exchange_get`. The preamble is present on every
      non-empty injection and absent when empty. *Test: in-memory, L0 and L1.*
- [ ] **17 — Self-reflection and claim losers.** A session that posts a question does not see it
      in its own next digest; another session in the same repository sees it as claimed, cannot
      reply, and never has it re-injected as open; a question in the player subtree does not
      appear to the `nastv` root's sessions. *Test: in-memory, nested repositories.*
- [ ] **18 — Hook integrity.** After install, a synthetic prompt is seen by the server; with the
      hook declaration changed so the grant is invalid, `check` fails loudly. *Test: installer +
      script-level; the real client's block is proven in phase 0 only.*
- [ ] **19 — Suite green.** `dotnet test` passes, with the E2E simulation extended to cover
      question → answer → ack and the claim race. *Gate: the whole plan.*

## Documentation

- New page `docs/features/exchange.md`: actors, lifecycle (the phase-2.2 table), tools, digests,
  wake paths (pull vs push), security posture, retention, and a worked exchange between a ZCode
  session and Linda V1.
- The feature pages named in the phase-1 and closing steps, updated in the same commits as the
  code they describe.
- The design record for *why* the Deltas exist belongs in this plan's Status Log, not in the
  public docs.

## Status Log

*(append-only; claims and evidence go here — the file, not the database.)*

### 2026-10-24 — phase 2 red tests resolved; suite green; test loop established

The session that picked this up found five red Exchange tests and a full suite that appeared to
take 15 minutes. Both are resolved; evidence first:

- **`dotnet test`: Passed! Failed: 0, Passed: 157, Skipped: 4 (live-gated), Total: 161 — ~5 s wall.**
  The elapsed-time problem was never test volume: it was rebuild churn (every bare `dotnet test`
  pays a rebuild plus ~10 s host startup) and one wedged full-suite run — a CLR-aborted testhost
  orphaned the output pipe and the pipeline sat 15 minutes at 0.9 s CPU. The loop is now
  `scripts/test.sh [filter]` (build once, `--no-build` filtered runs, output to a file, bounded by
  `timeout --kill-after`; retries the documented `0x80131506` first-run abort once).
- **The five red tests were one schema bug plus knock-ons.** Migration 0002's CHECK demanded
  `recipient_actor_id = '*'` for *every* zcode recipient, which forbids addressing a concrete
  session — exactly what an answer to a worker's question does. The plan's contract (§ phase 1a,
  D13) is "repository_id required for role-addressed items"; the CHECK now enforces exactly that.
  Editing 0002 in place is safe: it is uncommitted and was never applied to any persistent
  database (the deployed service predates the Exchange).
- **Claimant rule corrected to D14**: while the question is claimed, only the claimant may reply;
  once answered, the thread's depth belongs to its two parties (claimant and asker) — a third
  party is refused. `Cursor_CatchUp`'s old setup used actor strings that cannot parse
  (`linda_v2`, `linda_v2:linda_v2` — `linda_v2` is an actor id, not a type); it now runs the
  ordinary claim-then-answer flow.
- **Digest**: a session's own posts are filtered out of its own digest (criterion 17 / D6); the
  subject fallback refuses an instruction-shaped first body line (§2.7's last bullet) — `Esc`
  itself quotes markup-shaped values, as the digest spec says (pinned by `ExchangeDeputyTests`).
- **MCP Exchange tools**: results are camelCase on the wire whatever the authored casing (the SDK
  serializer), so the tools now author camelCase property names and the tests assert them.
- **Test isolation without per-test hosts** (per-test hosts exhausted inotify): one host per
  class, one registered repository *per test* (`AgentHQApp.NewRepoAsync`) — the digest and the
  role inbox scope by repository, so that is enough.

Nothing deployed; all changes uncommitted in the working tree. Phases 3–7 remain untouched.

### 2026-10-24 — implementation started; phase 0 environment findings

Implementation has begun. Phase 0 was done against the tool's own guides first, per the plan;
the first step's check failed, and it is recorded here before anything depends on it.

- **The hook is not registered where this plan, the installer and the docs assume.** Phase 0's
  first check found `hooks: { UserPromptSubmit: [ { matcher, command } ] }` in
  `~/.zcode/settings.json`, so the plan's assumption looked right — but the authority says that
  file is not a hook location. `~/.zcode/cli/config.json` (keys `plugins`, `mcp`) and
  `~/.zcode/v2/setting.json` (UI settings) both have no `hooks` key; ZCode's warning
  `config.project_hooks.pending_trust` for `…/nastv/.zcode/config.json` shows a **different**
  file proving that file is the shape it expects. No hook run has ever been logged for the
  inline curl: `hook.rejected` count in the live database is 0, and liveness of the nastv hook
  cannot be observed from here. **Correct file and shape: workspace scope
  `<repo>/.zcode/config.json` → `hooks.enabled: true` plus `hooks.events.<Event>`, each event an
  array of `{ matcher?, hooks: [ { type: "command", command, timeout } ] }`; user scope
  `~/.zcode/cli/config.json` → the same `hooks` object.** `~/.zcode/settings.json` is not an
  input to the hook runner and nothing in v2 should write to it.
- **Matchers.** `SessionStart` takes one (`startup|resume|clear|compact`; nastv uses
  `startup|resume|compact`). `UserPromptSubmit` also takes one (the prompt text as a
  case-sensitive regex) — **omit it**, so every prompt matches. `matcher: ""` is a valid regex
  that matches everything and will be dropped for clarity.
- **Injection shape is known, not yet proven with a live session.** The hook contract: stdout
  is parsed as strict JSON (any extra key fails validation, and the event name must match);
  `{"hookSpecificOutput":{"hookEventName":"<Event>","additionalContext":"…"}}` is injected as
  context; empty stdout is valid and injects nothing; any non-zero exit is an error. The shell
  that runs a `command` hook does **not** expand `${CLAUDE_SESSION_ID}`, `$PROMPT` or `$CWD`
  (no hook run has ever shown these reaching a process; a hook is a shell string, not a
  template engine — only the documented `${CLAUDE_PROJECT_DIR}` / `${ZCODE_PROJECT_DIR}` /
  `${CLAUDE_SESSION_ID}` template variables are known), so the script **reads the hook payload
  from stdin and never depends on environment variables**. The spec's own environment is not
  available to an agent in the middle of a tool call — no interactive ZCode session can be
  opened from here, so the live capture remains with Andy (below).
- **Trust.** User-scope (`~/.zcode/cli/config.json`) hooks have no trust gate once
  `hooks.enabled: true`. Workspace-scope hooks are gated: the client records a trust decision
  per (workspace, declaration digest, event) in `~/.zcode/security/workspace-hook-trust-v1.json`
  and logs `config.project_hooks.pending_trust` — *"Project hooks are pending workspace trust
  and remain blocked"* — when a declaration was never approved. Editing a workspace hook
  declaration therefore invalidates the grant. The installer will use **user scope** (no trust
  gate, applies in every repository) and will still keep D17's install-time read-back, which
  works for both scopes and turns a blocked hook into a visible failure.
- **Where the plan's assumptions hold.** Everything after phase 0 that does not touch the hook
  (phases 1, 2, 4, 7) is unaffected and is being implemented now. Phase 3 will be built against
  the user-scope shape above and tested through the script with a piped stdin payload (no live
  ZCode needed). Phase 0's gate — *a line injected by the hook is visible to a real ZCode
  session* — needs one interactive ZCode session opened by Andy after the installer runs; the
  fallback in the plan (ZCode as a pull-only actor) is ready if it fails.

### 2026-10-24 — rewritten for a junior implementer

Reviewed the plan against the v1 code and rewrote it. Still no code, still no step ticked. The
gaps fixed, each checked against the source:

- **The spec is not in the repository.** The plan said "the attached spec" and relied on nine
  undefined deltas (D4–D9, D13 in particular) and the phase-2.2 state machine. Everything needed
  is now stated in the plan.
- **The short-cut decision contradicted itself.** The decision table said "record the real
  implementer on the short-cut"; D2 and phase 7 said not to set `implemented_by` and to keep the
  intermediate row attributed to the verifier. Phase 7 is right; decision 5 now says so and
  supersedes the old text. The code check confirms `SetImplementedByAsync` is only called for
  `implemented` (`PlanService.cs:216`).
- **Phase 0 assumed the hook mechanism instead of proving it.** Where ZCode registers hooks and
  which events take a matcher is now a documented check against the tool's own guide, recorded
  before anything depends on it. `jq` is installed on this machine and is now a hard test
  dependency.
- **`PlanSlug.Valid()` does not exist** (it is a private `GeneratedRegex`), `submit_plan` takes
  no `slug`, and `RepositoryStore.ResolveByCwdAsync` is at
  `src/AgentHQ.Infrastructure/Stores/RepositoryStore.cs:71` — the plan now names these.
- **Sizes and routes were ambiguous.** Phase 2 now fixes the build order, the REST table, the
  error-code list, the options record with env var names and defaults, and the event types.
- **Criterion 14 was wrong.** The activity panel already collapses nothing today and
  `session.updated` churn was already fixed at the source (see `docs/plans/web-ui.md`,
  2026-10-24). The criterion now asks for aggregation of *incoming* events while the panel is
  closed, with a stated reconciliation rule, so poll fallback cannot double-count.
- **The plan's own identity workaround is now part of the work.** `docs/README.md` links
  `conversation-enhancements-etc.md`, `.gitignore` documents two symlinks created because
  `POST /plans` mangled the name; the closing steps remove them after D10.
- **Phase 0 needs a human at the ZCode client.** Stated as a hard dependency so no calendar
  time is scheduled on it without one.
- **Verification criteria now name how each is checked** (in-memory, script-level, browser,
  live peer), so a verifier does not have to invent the method.

### 2026-10-24 — review round: delivery model, injection boundary, sequencing

Six refinements from a review conversation against the v1 code, folded into the steps and
criteria rather than left as commentary. Still no code, still no step ticked.

- **Phase 0 added (D16).** The pull path rests on one unproven assumption: that a ZCode hook can
  post and inject context. The hook declaration is a JSON string, so injection requires a script
  file rather than an inline curl — traceable through `~/.zcode/settings.json`, where the current
  hook is exactly that inline curl. Phase 0 proves injection on its own and captures the real
  payload fields; the plan stops if the gate fails.
- **The digest boundary is structural now (D15, decision 4).** An earlier revision relied on an
  "untrusted content" preamble. That is a mitigation, not a boundary: any digest carrying peer
  text puts peer text in context on every prompt. Digests now carry server-built fields only, and
  bodies are fetched deliberately.
- **Role routing has one rule (D13).** The hook supplies `cwd`, not a repository id, and this
  machine registers `nastv` plus seven descendants; "matching repo context" was ambiguous.
  Resolution is the most-specific registered root, tested with that nesting.
- **Claim losers defined (D14).** The spec did not say what a second session sees after a claim.
  It now sees `claimed_by`, cannot reply, and is never re-injected as open.
- **Andy's surface moved earlier (D18).** A minimal list/answer/kill page ships in phase 2 with
  the REST, so the Exchange is usable by a human before the full dashboard view.
- **Phase 5 proves the round trip over loopback before any TLS route (D19)**, so the first
  non-loopback listener is a hardening step, not a discovery step.
- **Retention restated (D8, decision 6).** 90-day items contradicted "readable by id"; rows are
  kept, blobs are pruned, size is alarmed.
- Decision list grew from four items to seven; each now carries a default-if-silent so the plan
  cannot stall on an unanswered question.

### 2026-10-24 — plan drafted from Linda V1's spec; corrections recorded

Drafted by reading the Exchange v2 Implementation Spec (2026-10-24) against the v1 codebase.
Four premises in the spec were checked; two hold, two do not. No code has been written and no
step is ticked.

- **§14 carried fix #1 (short-cut must not set `implemented_by_session_id`) does not hold
  verbatim** — see Deltas D2. The code only calls `SetImplementedByAsync` for the `implemented`
  command, not for the short-cut in `verify`. The real defect the spec is pointing at is
  different and real: a short-cut completion leaves `implemented_by` **null**, so the later
  separation-of-duties check silently passes for the verifier who recorded their own
  implementation.
- **§14 carried fix #2 (hook JSON escaping) holds and is worse than described.** The installed
  hook is a single `curl -d '{"session_id":"${CLAUDE_SESSION_ID}","prompt":"$PROMPT",…}'`
  command: a `"` in a prompt breaks the body, and `|| true` swallows the failure. Confirmed
  against `~/.zcode/settings.json` on this machine, and against `Installer.cs:71`.
- **§16 open question 2 (hook injection) has a known shape.** ZCode hooks emit context through
  `hookSpecificOutput.additionalContext` on stdout, and `SessionStart` is a registered hook event
  (the workspace hook trust store lists `SessionStart`, `UserPromptSubmit` and `Stop`). The
  existing v1 hook is POST-only; injection therefore needs the hook to become a script that both
  posts and prints.
- **§16 open question 1 (poll vs stream) is evidence-based in this repository**: the web UI
  already treats the stream as the main road with `sinceSeq` polling as the fallback while the
  service restarts (`app.js`, `startPolling`). For an actor whose harness can hold a socket
  (OpenClaw) the stream is primary; for one whose harness is a hook or a cron (ZCode, V1) the
  poll cursor is the main road, not a fallback.
- **§16 open questions 3, 4, 5 and 6 are answered in Deltas D3, D5, D6 and D7.** D3 is the only
  one that changes the spec's behaviour.
- **Registering this plan hit three v1 gaps** — D10 (`submit_plan` cannot honour a chosen file
  name), D11 (a missing file on a registered plan has no remedy) and D12 (`read_plan` propagates
  a raw `FileNotFoundException`; `AgentHqTools.cs:80` calls `ReadContentAsync` unguarded). The
  observations came from registering this plan on the running service.

## Deltas from the incoming spec

Where this plan departs from the Exchange v2 Implementation Spec, and why. Each is a correction
with evidence, not a preference. An implementer needs only the first sentence of each; the rest is
the record.

**D1 — The hook becomes a script, not an inline curl.** The installer writes
`~/.zcode/hooks/agent-hq-hook.sh` and replaces its own inline `curl -d '{…}'` entry with it, so
the prompt is parsed and re-serialized with `jq` rather than interpolated. The script is the only
place that knows the injection output shape.

**D2 — Carried fix #1 is restated.** Do not set `implemented_by_session_id` on the short-cut (the
code does not today). Keep the intermediate `→ implemented` row attributed to the verifying
session, mark it `viaShortcut: true`, and keep `implementedBy` absent. This preserves the spec's
intent — a short-cut and a full path are different claims — without reintroducing the defect of
letting a verifier become the implementer of record.

**D3 — Session identity: role + claim stays, with an evidence-based caveat.** ZCode session ids
are durable in the session index and hooks carry the same id, but a live session cannot be
reached by id once it has ended, which is exactly when role addressing is needed. Individual
addressing is honoured for the lifetime of the session; the fallback to role addressing is a
first-class, visible state.

**D4 — The implicit transitions are made explicit.** The table in phase 2.2 fixes every edge; each
is a map row in `src/AgentHQ.Core/Exchange/` and a unit test.

**D5 — No sidecar wake in v2.** A ZCode capability this repository does not control; hook
injection delivers the same value one turn later.

**D6 — The digest is injected only when non-empty, and the server decides.** The server returns
an empty body when there is nothing to inject, and L1 only when the session's repository matches
the item's `context_refs.repo`.

**D7 — Naming: Exchange stays.** The `Exchange` working name and the `exchange_*` namespace stay.
Renaming Agent HQ or the `plan_*` tables is not part of v2.

**D8 — Retention keeps item rows; only blobs are pruned.** Spec §3.4 suggests 90 days while also
promising items remain readable by id; those contradict on day 91, and items are cited in plans.
Item rows are kept (they are small). Retention is a blob policy: a size ceiling on the blob
directory that logs an error when exceeded, pruning of blobs no live item references, and the
same contract in `operations.md`. The event log is never trimmed (a cursor older than
`EventReplayWindowSeconds` gets `exchange.resync` instead).

**D9 — `status` kind.** Informational, non-answer post, optionally tied to a plan via
`context_refs`; it never moves a question's state and never counts as a hop.

**D10 — Plans get an explicit slug.** `POST /plans` and `submit_plan` take a validated `slug`;
a taken slug is refused (no auto-suffix on a chosen slug); the derived default keeps today's
`-2` suffix behaviour. Phase 1c.

**D11 — Plans can be re-pointed and deleted.** `PATCH /plans/{id}/file` and `DELETE /plans/{id}`;
the file on disk is never deleted. Phase 1c.

**D12 — Tool errors are codes, not stack traces.** The exchange tools return the codes of phase
2.3; `read_plan`'s missing-file case becomes `not_found`. Phase 1c.

**D13 — One repository-resolution rule.** `cwd` → repository via the existing
`ResolveByCwdAsync` (longest registered root prefix). Tested with the `nastv` nesting. Phase 1b.

**D14 — Claim losers stay visible, cannot act.** A claimed question shows `claimed_by` and its
age to the repository's other sessions, is never re-injected as open, and only the claimant may
reply. Closed in phase 2.2 and tested in criterion 17.

**D15 — The digest is a routing hint, never content.** Server-built from bounded, escaped fields
only; bodies are fetched with `exchange_get`. This is the one place the plan is stricter than the
incoming spec.

**D16 — The hook is proven before anything is built on it.** Phase 0 is a gate with a recorded
fallback.

**D17 — Trust is verified, never assumed.** The installer posts a synthetic prompt and reads it
back; `check` repeats it. Same principle as the deploy script's `check`.

**D18 — Andy's surface ships in phase 2.** A minimal page beside the REST, replaced in phase 4.

**D19 — Prove the round trip before widening the network surface.** Phase 5 starts with Linda V1
calling the loopback endpoints by hand.

**D20 — Event types are added as constants and documented together.** `exchange.*` plus
`plan.file_changed` and `plan.deleted`; the SSE client's unknown-type path already surfaces them,
so only `describe()` and the event page change.
