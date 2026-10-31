# AgentHQ.Api — feature knowledge

The host: `Program.Main` (process entry, Serilog, port), `AgentHqHost.BuildAsync` (composition),
the REST endpoints, the SSE stream, the MCP tool façade, the hook ingest, and the static web UI.

Behaviour is documented in [docs/features/api.md](../../docs/features/api.md),
[docs/features/events.md](../../docs/features/events.md) and
[docs/features/mcp.md](../../docs/features/mcp.md). This file records the wiring rules.

## Composition

- **`AgentHqHost.BuildAsync` is the only composition root**, used by `Main` and by
  `tests/AgentHQ.Tests/AgentHqApp`. New services, hosted work, endpoints and event wiring go
  there. Wiring something in `Main` only means the tests do not exercise it.
- Startup order in `BuildAsync` is load-bearing: `Migrate()` → `FullSyncAsync()` → register/warm
  watchers and index lessons per repository → start the ZCode watcher. Because the sync runs before
  `RunAsync`, the port does not open until it finishes — that is the 13–20 s cold start, and it is
  intentional: the health check then means "ready", not "listening".
- `plans.UseSessions(sessionStore.GetAsync)` wires attribution-aware file resolution. Without it a
  work command cannot fall back to the session's own repository.
- `events.Published += hub.PublishAsync` is the only bridge between the event store and SSE.
- **Content root is `AppContext.BaseDirectory`**, so `wwwroot/` and `Commands/` resolve identically
  under `dotnet run`, systemd and TestServer. Never resolve assets from `Directory.GetCurrentDirectory()`.
- `Main` handles two CLI verbs before starting the server: `--version` and `install-zcode`
  (delegates to `AgentHQ.Commands.ZCode.Runner`, passing the resolved ZCode db path).

## Endpoints

- Registered in `ApiEndpoints.MapRepositories/MapSessions/MapPlans`, `SseEndpoints.MapEvents`, and
  inline in `BuildAsync` (`/health`, `/overview`, `/hooks/zcode/prompts`, `/mcp`).
- **The `/events` route is special-cased** (`MapEvents` streams SSE; `?sinceSeq=` switches to JSON)
  — do not add `MapGet("/events", …)` elsewhere; minimal APIs reject a duplicate route at startup.
- **JSON policy** is set once in `BuildAsync` (camelCase, nulls omitted). Event payloads use
  `AgentHqEvent.JsonOpts`, which matches it.
- **Two ways to move a status**, deliberately: `PATCH /plans/{id}/status` (manual, records the
  equivalent `PATCH:/<command>` in history) and `POST /plans/{id}/commands` (a `/plan` command with
  attribution — what the UI uses). Both call `PlanService`; never update a store directly from an
  endpoint.
- `PATCH /plans/{id}/status` with `blocked` toggles the orthogonal flag instead of moving the
  lifecycle. The two modes are mutually exclusive in one request.
- Inputs are nullable record parameters bound from the body; missing fields are legitimate. Validate
  the ones that are required and return the error shape `{ok:false, error:"…"}` with `422` for rule
  refusals and `400` for malformed input.

## SSE / SseHub

- `SseHub` is an in-process fan-out; replay comes from the `events` table. Subscribers are registered
  **before** the replay query in `HandleEventsAsync`, so an event appended in between is not lost.
  Overlap is suppressed by sequence comparison, and clients dedupe by `id:` anyway.
- Each message is formatted as `id: <seq>`, `event: <type>`, `data: <payload>`. The event name *is*
  the type — the web UI depends on that to route known events and to surface unknown ones.
- Heartbeat comment every 15 s keeps idle proxies and browsers from dropping the connection.
- `WriteAsync` must honour `ctx.RequestAborted`; a disconnected client otherwise leaks a subscriber.

## Hook ingest (`HookFeedService`)

The order is the contract:

1. `session_id` and `cwd` are required → `400`.
2. Resolve the repository from `cwd` (longest prefix) → `422` if none.
3. Resolve the session from the mirror, else directly from the provider (and mirror it) → `422` if
   unknown. This race-handling is why a fast `/plan` right after a session starts still attributes.
4. Parse `/plan <cmd> <file>`; non-matching prompts return `{ok:true, matched:false}` and are ignored.
5. Apply the command; resolve errors and rule refusals return `422` **and** record `hook.rejected`.

**Every rejection path calls `RejectAsync`** — the event is how "I ran `/plan` and nothing happened"
is diagnosed. A new failure mode added here must record one too.

## MCP

- Tools live in `Mcp/AgentHqTools.cs`; `WithToolsFromAssembly()` discovers them.
- **No optional parameters.** Required parameters with empty-string sentinels (and `long.MinValue`
  for numbers) are converted in the body — the SDK's binding fails on missing optional arguments.
  The sentinel is documented in the description.
- **Descriptions are the interface**: each carries the contract and the etiquette (use
  `updatedSince`, use `ready=true`, prefer `get_events(sinceSeq)` over re-listing).
- **`JsonElement` payloads must be serialized to text** in tool results (see `GetEvents`), or the
  SDK renders `{"ValueKind": …}`.
- **MCP results are camelCase on the wire whatever you author** — the SDK serializer applies the
  naming policy. Author anonymous property names camelCase so the code reads like the wire
  (`Mcp/ExchangeTools.cs` is the example; tests assert camelCase).
- `GetGuide`'s `GuideText` and the server `ServerInstructions` are the layered guidance model: keep
  them consistent with the behaviour, and with `docs/features/mcp.md`.

## Logging

- Serilog is configured in `Main` (console + daily rolling file, 10 files × 10 MB) with a
  `Subsystem` property context. Bootstrap logging exists so failures before the host is built are
  visible.
- ASP.NET Core request logging is muted to `Warning` by `AGENTHQ_LOG_ASPNET`; the log is for
  Agent HQ's own events, not access logs.
- `UseSystemd()` adds journal integration and graceful SIGTERM; the unit sends SIGINT
  (`KillSignal=SIGINT`) with `TimeoutStopSec=10`.
