# Agent HQ web UI (v1)

## Goal

Give Agent HQ a browser front end so the whole V1 loop can be verified by eye — and driven
through the UI — before OpenClaw is introduced. Today the API is only reachable by curl, the
MCP client, or tests. The web UI makes current activity, plans and their status visible, and
lets hours-of-work decisions (review, sendback, verify, pass/fail, block, priority) be made in
a browser and observed live through SSE.

This is plan #2 managed by Agent HQ itself (plan #1 was create.md, which this UI is the
follow-on to; the service was called dev-bridge until 2026-10-24). It is V1-scoped: V2
(agent-to-agent conversation over SSE) will be presented later and must be able to land as new
event types without redesigning this UI.

## Context

- The REST surface (README → API) is complete and tested; SSE `/events` carries typed events
  with `Last-Event-ID` replay and a `?sinceSeq=` poll form.
- The host is ASP.NET Core (`src/AgentHQ.Api/Program.cs`, `AgentHqHost.BuildAsync`). There is
  no static file host today, no JS toolchain, and no frontend build step in CI.
- Constraints: single-user local machine, bound to `127.0.0.1`, no auth, no external CDN
  (the service must work offline), and the CI runner has no Node-based build today.
- The UI must be testable from its endpoints (API-level tests) and from the web (browser
  automation against the real host, with real SSE).

## Steps

- [x] Add `wwwroot/` to `AgentHQ.Api`: `index.html` (shell), `app.js` (plain ES modules, no
      build step), `ui.css`. Serve it with `UseDefaultFiles` + `UseStaticFiles`, and map
      `GET /` to the shell. Vendor nothing from a CDN.
- [x] Layout: a single page with three regions —
      1. **Header**: health + provider schema status, last event seq, connection indicator
         (live / reconnecting / polling), and a refresh-rate/auto-update toggle.
      2. **Plan board** (main): plans grouped by status, each card showing name, slug, repo,
         status chip, blocked chip + reason, progress bar (`checked/total`), priority, session
         link, and last updated. Filter chips: all / ready / blocked / verification queue /
         per-repository. Sort by priority then updated (the API default).
      3. **Detail drawer** for the selected plan: rendered plan markdown content, transition
         history (from → to, actor, command, git head, note, separation-of-duties warning),
         dependencies, and action buttons.
- [x] **Actions in the UI** (all call existing endpoints, no new server logic):
      send back (→ refining), mark implemented → verify, pass, fail (with reason), block /
      unblock (with reason), defer / reactivate, done, set priority, link session. Illegal
      moves are hidden/disabled from the same lifecycle rules the server enforces, and the
      server's 422 message is shown if it still refuses.
- [x] **Activity panel**: a live tail of the SSE stream (session.created/updated,
      plan.created/updated/status_changed/session_linked/blocked/unblocked, hook.rejected),
      each row self-describing from its payload with a relative timestamp; and a
      **recent sessions** list (title, repo, derived status, updated) that consumes
      `session.updated` events.
- [x] **Resilience**: on SSE disconnect, transition the indicator to reconnecting and fall
      back to `GET /events?sinceSeq=<lastSeq>` polling every 2 s; on reconnect use
      `Last-Event-ID` so nothing is missed or duplicated (dedupe by seq client-side). Keep the
      last rendered state on screen while reconnecting.
- [x] **Submit plan** form (name, repository, markdown) so the browser can play OpenClaw for
      testing: soft-validation warnings are surfaced, and the card appears live via
      `plan.created`.
- [x] Extensibility note for V2: the activity panel renders unknown event types generically
      (type + pretty JSON), so conversation events will show up before any dedicated UI exists.
- [x] Tests (endpoint level): static assets are served (`GET /` is HTML, `app.js` is
      JavaScript with correct content type), and every endpoint the UI calls is already
      covered — add an explicit test asserting the exact routes/payload fields the UI relies on
      (a "UI contract" test) so UI breakage is caught server-side.
- [x] Browser verification against the real host: load the page, see seeded plans and live
      sessions; submit a plan from the form; drive it through the lifecycle with the action
      buttons while a second tab reads the activity panel; disconnect the SSE (restart the
      host) and confirm the poll fallback and replay; check the blocked and verification
      queues filter correctly. Record findings and fix what they reveal.
- [x] README: a "Web UI" section with the URL, what each region shows, and how to verify the
      UI without agents (`scripts/seed-demo.sh` or equivalent that registers a repo and submits
      sample plans).

## Dependencies

- create.md v1 (built): REST + SSE surface, plan lifecycle, events.
- Browser automation available for the web-side verification.

## Verification Criteria

- `dotnet test` green, including new UI-contract and static-asset tests.
- `GET /` on the running host returns the app shell; assets load with no external network
  requests (verified from the page: no request leaves `127.0.0.1`).
- Plan board shows every plan with status chip, blocked chip + reason, and progress; the
  ready / blocked / verification-queue filters show exactly the sets the corresponding API
  queries return (cross-checked against `GET /plans?ready=true` etc.).
- Performing each action from the UI produces the same `plan_status_history` row and
  `plan.status_changed` event as the equivalent `/plan` command, with the correct actor
  (`andy`/`openclaw` as sent by the UI); illegal actions are refused with the server's message.
- The activity panel updates live for a plan transition made through the hook endpoint (not the
  UI), within a second.
- Killing and restarting the host while the page is open: the indicator leaves "live", shows
  polling, and after restart the missed events appear with no duplicates.
- A V2-style unknown event type appended to `/events` renders in the activity panel generically.
- Browser-verification notes recorded in this plan's Status Log, with screenshots of the board,
  a detail drawer, and the activity panel.

## Documentation

- README "Web UI" section: how to open it, what it shows, how it relates to `/plan` commands
  and MCP tools (same transitions, same actor model), and the demo seed script.
- Note in README → API that `GET /` serves the UI.

## Status Log

### 2026-10-24 — built and browser-verified

- UI: `src/AgentHQ.Api/wwwroot/` (index.html, ui.css, app.js) served by the host; no build
  step, no external requests. Board, filters, detail drawer with actions/history/dependencies,
  live activity tail, recent sessions, submit-plan and register-repository dialogs.
- API additions the UI needed: `GET /plans/{id}` now carries `repositoryId`;
  `POST /plans/{id}/commands` (attributed `/plan` commands, shared with the hook feed via
  `PlanService.ApplyCommandByFileAsync`); `blocked` accepted on `PATCH /plans/{id}/status`.
- Tests: `WebUiContractTests` (static assets, no external URLs, field contract, command route,
  blocked toggle, unknown-type events) — suite 94 passed / 4 live skipped.
- Browser verification against the real host (in-app browser, real ZCode db + demo repo):
  board with six seeded states, blocked reason, 2/3 progress; filters cross-checked against
  `?ready`, `?blocked`, `?status=implemented` (all matched); submit through the form; full
  rework loop driven from the drawer (verify → fail → implemented → verify → pass) with history
  `submitted→…→verified`; block/unblock; server refusal shown for an illegal move; live
  activity for hook-driven transitions within a second; host restart → stream recovered with
  no duplicate/missed rows; a `conversation.message` event (V2-shaped) rendered in the panel.

Defects found by the browser pass, all fixed:

1. `WebApplication` without a pinned content root uses the working directory → `wwwroot` 404
   under systemd (`WorkingDirectory=%h`) and when started outside the output dir.
2. Plan-dir watcher read a stale row back after its compare-and-set update (a concurrent
   submit could make it emit a dropped edit's version); the UPDATE now returns the row it wrote.
3. Event replay was capped at 500 rows, so a client replaying from an old cursor resumed far
   behind live and never caught up.
4. `app.js` closed and reopened the stream after every event (silent reconnect loop, activity
   panel effectively frozen); unknown (V2) types were not surfaced at all.
5. Board layout overflowed horizontally, pushing the activity panel off-screen; columns now
   share the width and scroll only when there are more states than fit.
6. Blocking from the drawer required a resolvable hook `cwd`; blocking/unblocking is now the
   flag on the status endpoint, and every UI action is an attributed `/plan` command.
7. Markdown headings rendered one level too deep in the drawer.
8. Test-host robustness: SSE readers in tests are always disposed/cancelled (removed an
   intermittent runtime abort on host teardown: 10/10 clean runs after the fix), and the
   provider no longer falls back to the user's real ZCode db.

### 2026-10-24 — review observations (Linda) and follow-up

Observations raised against the first demo board, checked against the code:

1. **Verified cards at 0/3, one named "in progress"/"verification queue".** Two causes, both in
   the demo seed, not the lifecycle: the seed advanced status through the hook feed but never
   ticked the checkboxes the way an implementer would, and the demo plan *names* describe the
   scenario being staged rather than the current state (the card shows a status badge, so a
   "verification queue" card that has passed verification reads as a contradiction). Fixed:
   `scripts/seed-demo.sh` now ticks the steps that each state implies (review 2/3, implementing
   2/3, implemented/fixing/verified/done 3/3). A real card was then walked implemented →
   verifying → verified from the drawer and the badge and column both followed.
2. **No Submitted or Done column.** Not a mapping gap: the board renders every non-empty
   lifecycle state (`STATUS_ORDER`), and only "fixing" shares a column (with implementing). The
   states were missing because no demo plan was in them. Added "Demo: completed" (→ done); the
   Submitted column appears whenever a plan is in it (the UI-submitted plan did while it was
   submitted).
3. **`session.updated` noise.** Confirmed and caused, in part, by a real defect alongside real
   churn. In the demo log: 4,841 of 4,897 events were session rows; one session had 116 rows
   with a *single* distinct payload apart from `timeUpdated`, and ZCode bumps
   `session.time_updated` on every internal model step (205 `step-finish` parts in that
   session). So it was both "no feedback loop" (the watcher only fires on db change) and "every
   bump became an event". Fixed: `SessionSyncService.MirrorAsync` still updates the mirror on
   every change but emits `session.updated` only when the metadata an observer can act on
   changed (title, task type, outcome, archived/deleted, repository); a bare `time_updated`
   bump is silent. Verified against the live db: zero events in 45 s of idle churn, and the
   tests now assert both directions (substantive change emits, bare touch does not). Event seq
   growth is therefore proportional to work, not to model steps.

### 2026-10-24 — table layout, overlay dismissal, scrollbars (Andy's review)

Requested: drop the card board for a table, keep the detail fly-out, dismiss it on the ✕ *and* on a
click outside it, hide the activity feed behind a button, provide a Done-state filter, and fix the
sizing/scroll problems (a clipped element top-right, activity cut off, scrollbars that only appear
briefly on wheel scroll).

Done:

1. **Table replaces the board.** `#columns`/`renderBoard`/`planCard` removed; rows render into
   `#plans-body` with columns Plan (slug under it), Status, Blocked (+reason), Steps, Priority,
   Session, Updated. All headers sort (click to reverse, `aria-sort` set), default Updated desc.
   A status dropdown filters by any lifecycle state — including Done and *Verification queue* —
   which is the "filter by done state" ask; Ready/Blocked chips remain server-side queries and
   now toggle off (second press). Found while testing: the chips never cleared on a second
   click (the handler only ever set the new filter) — fixed.
2. **Fly-out dismissal.** ✕, Escape, and any press outside it (capture-phase `pointerdown`,
   so the scrollbar gutter and backdrop count). The fly-out is now `min(880px, 100vw - 120px)`
   so a strip of table — and thus an outside-click target — always remains; previously at a
   ~1026px viewport it covered almost the whole table. The closing press is consumed so it cannot
   also open the row underneath (verified at 480/720/1026/1440px).
3. **Activity behind a button.** `#btn-activity` (aria-expanded/controls) opens a right slide-over
   with a scrim; a count badge shows events that arrived while it was closed. It closes on ✕,
   Escape, and outside press, same as the fly-out.
4. **Scrolling.** Table and activity body are `overflow: auto` with always-visible themed
   scrollbars (`scrollbar-width: thin` + WebKit thumb; measured 10px at every width). The page
   itself never scrolls horizontally (`pageOverflow 0` at every width). The old clipped element
   top-right was the activity panel pushed past the viewport by the board's horizontal overflow;
   with the board gone it is structurally impossible, and the narrow top bar now wraps (measured
   height into `--topbar-h`) instead of squeezing the Repositories button out of reach (it was
   cut at 480px before that fix).
5. Session chips show the distinguishing part of `session_<hex>` rather than `session_` for
   every row; added `favicon.svg` (the only console request that 404'd).
6. Verified in a real browser against a seeded host at 480/720/1026/1440px: table renders and
   sorts, Done filter → `Demo: completed` only, full-width fly-out at ≤720px closes on outside
   press / ✕ / Escape, a status change made from the fly-out updates the row live, activity
   panel scrolls with a visible bar. `dotnet test`: 96 passed / 4 skipped.

### 2026-10-24 — project renamed to Agent HQ

The service this plan belongs to was renamed dev-bridge → Agent HQ (see create.md, same
date). Only identifiers moved: `src/AgentHQ.Api/wwwroot/`, the page title and brand now read
"Agent HQ", and the plan's own wording follows. The UI itself — table, filters, fly-out,
activity slide-over — is unchanged.
