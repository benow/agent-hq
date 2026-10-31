# Web UI (wwwroot) — feature knowledge

`index.html`, `app.js`, `ui.css`, `favicon.svg`. Plain ES modules — **no framework, no build step,
no external requests**. Anything that needs a bundler or a CDN is out of scope by design: the
assets are served straight from the assembly, are reviewable in a diff, and work identically under
`dotnet run`, systemd and the in-memory test host.

Behaviour is documented in [docs/features/web-ui.md](../../../docs/features/web-ui.md). This file
records the rules a change has to respect.

## Parity with the state machine

- `ACTIONS` in `app.js` lists each action with the statuses it is legal from. It **mirrors**
  `PlanStateMachine` — it does not replace it. The server is still the authority and its refusal is
  shown verbatim. When a transition is added or removed in Core, update `ACTIONS` **and**
  `STATUS_LABEL`/`STATUS_ORDER` in the same change; the contract test asserts the UI's action route
  and the fields it reads, but it cannot read your intent, so the mirror is a human obligation.
- **All actions go through `POST /plans/{id}/commands`** as `/plan <command>`, so the browser writes
  the same history vocabulary as an agent. The single exception is blocking, which is a flag and not
  a status: it uses `PATCH /plans/{id}/status` with `{blocked: …}`.
- Filters map onto server-side queries wherever one exists: the Ready chip is `?ready=true`, the
  Blocked chip `?blocked=true`, and the status dropdown is `?status=`. Do not reimplement these
  rules in the client.

## The event stream

- Connect with `/events?lastEventId=<lastSeq>`; `EventSource` reconnects by itself and sends
  `Last-Event-ID`, but when it gives up (`readyState === CLOSED`) the client falls back to
  `?sinceSeq=` polling every 2 s and probes until the stream returns.
- Known types get named listeners; **unknown types are still surfaced** (default-channel listener
  plus a slow scan). Keep this property: a new event type must appear without a UI change.
- `applyEvent` dedupes by sequence; replay overlap is expected and harmless.

## Interaction rules (learned, not arbitrary)

- **Overlay dismissal consumes the press** (capture-phase `pointerdown`, default prevented): on a
  narrow screen the same tap that closes the fly-out must not also open the row underneath.
- **A strip of the table stays visible** while the fly-out is open, so "click anywhere outside"
  always means something. Escape and ✕ also close.
- **Scroll containers use visible, themed scrollbars** (`scrollbar-width: thin` plus WebKit thumbs).
  Browser overlay scrollbars hide when idle, and an invisible scrollbar reads as unscrollable
  content — that was a real user report.
- **The top bar's height is measured** into `--topbar-h` with a `ResizeObserver`; overlays anchor
  under it. Do not hard-code the bar height (it wraps below ~560 px).
- The table has its own sort state (click a header to sort, click again to reverse) over the loaded
  rows, because the plan list is small and already whole.

## Tests

`WebUiContractTests` boots the host and checks: the shell and assets load, **no external URLs appear
in any asset**, every field the UI reads exists in the API response, the command route records
history and events, and an unknown event type is serialized generically. Changing a response field
or a route means updating that test deliberately, not by loosening it.

When verifying a UI change by hand, use the browser: navigate, take a snapshot **and** a screenshot,
read the console for errors, and investigate any non-2xx response. A DOM snapshot alone will not
show overflow, clipping or stray gaps.
