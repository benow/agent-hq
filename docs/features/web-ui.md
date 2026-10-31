# Web UI

Open <http://127.0.0.1:5317/>. The UI is one page: a plans table, a plan fly-out, and an
activity panel. It is plain HTML, CSS and JavaScript served from
`src/AgentHQ.Api/wwwroot/` — no build step, no framework, no external requests, no fonts or
analytics loaded from anywhere.

## The plans table

One row per plan: name and slug, status, blocked badge with reason, step progress,
priority, the session of record, and when it last changed.

Every column header sorts (click again to reverse). Filters combine freely:

| Control | What it does |
|---|---|
| Status dropdown | Any lifecycle state, plus *Verification queue* (`implemented`) and *Fixing (rework)* |
| **Ready** chip | `in_review`, not blocked, every dependency `verified` or `done` — the same set `GET /plans?ready=true` returns |
| **Blocked** chip | The needs-attention queue (`?blocked=true`) |
| Repository | Narrow to one registered repository |
| Search | Text match over the loaded rows |

Chips toggle off when clicked again. Sorting is client-side on the loaded rows, because the list
is small and already complete.

The table follows the event stream, so a plan that changes in another tab, by an agent, or by a
`/plan` command updates in place without a refresh.

## The plan fly-out

Clicking a row opens a fly-out. The main area has two tabs — **Plan**, the rendered markdown with
its Steps checkboxes and Status Log, so the evidence is readable without leaving the page, and
**History**, every transition with actor, command, git head, note, and a visible
separation-of-duties warning where one applies. The side panel holds the actions legal from the
current state, the blocked state and its reason, the priority control, the dependencies, and any
warnings the last action returned.

The actions, and the states they are offered in:

| Action | Legal from |
|---|---|
| Send back to refining | `in_review` |
| Mark implemented | `implementing`, `fixing` |
| Enter verification | `implemented` |
| Pass verification | `verifying` |
| Fail → fixing | `verifying` |
| Close out (done) | `verified` |
| Defer / Reactivate / Abandon | as the lifecycle allows |
| Block / Unblock | anywhere (the orthogonal flag) |
| Set priority | anywhere |

Every action goes through `POST /plans/{id}/commands` as the equivalent `/plan <command>` with
actor `andy`. The browser therefore writes exactly the same history vocabulary as an agent, and
obeys exactly the same state machine — refusals are shown verbatim instead of being second-guessed
by the UI. Blocking is the one exception, because it is a flag and not a status: it uses
`PATCH /plans/{id}/status` with `{blocked: true|false}`.

Close the fly-out with its ✕, with Escape, or by clicking anywhere outside it. A strip of the
table stays visible while it is open, so that "anywhere outside" always exists. The press that
dismisses an overlay is consumed, so on a narrow screen it cannot also open the row it landed on.

## Activity

The **Activity** button in the top bar opens a panel with:

- a live tail of the event stream — sessions, plan changes, blocks, hook rejections;
- the most recently active sessions, so "what is running right now" is one click away;
- a badge counting events that arrived while the panel was closed.

It closes the same three ways as the fly-out, and has a *clear* action for the visible list.

Under the hood the panel reconnects with `Last-Event-ID`, falls back to `?sinceSeq=` polling for
two seconds at a time while the service is restarting, and displays event types it does not
recognise — so a new event type from a future version shows up without a UI change. The details
are in [Events](events.md#the-activity-panel).

## Submitting and registering

**Submit plan** opens a dialog: pick a repository, give the plan a name, paste or write the
markdown. The dialog names the convention sections, and the response's soft-validation warnings
(missing Steps, Verification Criteria or Documentation) are shown in place.

**Repositories** lists what is registered and registers a new directory by absolute path.
Registering a directory starts its plans watcher immediately, so plans written there by hand are
found as well as ones submitted through the API.

## Health and connection indicators

The top bar shows two indicators:

- **health** — the service's own health, including whether the worker-agent integration is
  reading its session index (`providerSchema: ok | missing | schema-ahead`);
- **events** — `live` (stream connected), `polling` (stream down, catching up by cursor),
  `reconnecting`, or `offline`, with the last sequence number seen.

When the activity panel looks stale, read those two before anything else.

## Design constraints

These are deliberate and should survive future changes:

- **No build step, no dependencies.** Every asset is served from the assembly. This keeps the
  UI reviewable, testable in-process, and identical between `dotnet run`, a systemd deploy and
  the test host.
- **The server is the authority.** The UI never computes whether an action is legal on its own
  initiative; it offers the actions the lifecycle allows and displays refusals as they come.
- **No external requests.** A page load makes no request beyond `127.0.0.1`; a contract test
  enforces this.
- **Scrollable regions show their scrollbars.** Table, fly-out body and activity list use
  themed, always-visible scrollbars: an invisible scrollbar reads as unscrollable content.
- **The top bar's height is measured, not assumed.** Overlays anchor beneath a CSS variable set
  by a `ResizeObserver`, so they keep working when the bar wraps on a narrow screen.

## Seed data

To exercise the table without agents:

```bash
scripts/seed-demo.sh /tmp/demo-repo
```

Registers a repository and creates plans in several states, driving the transitions through the
hook feed when a real session exists (so attribution is realistic) and through the status API
otherwise. Steps are ticked directly in the files, so the watcher emits real progress changes.
Re-running is idempotent.

## See also

- [API reference](api.md) — every endpoint the UI calls
- [Events](events.md) — the stream the table and the activity panel follow
- [Plan lifecycle](plan-lifecycle.md) — the states the action menu mirrors
