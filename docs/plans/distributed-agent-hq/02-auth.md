# 2 — auth: name/password identities, internal and external access points

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15

## Goal

Real authentication before the first non-loopback bind, per the standing rule that widening
exposure is a security decision. Simple by Andy's explicit call: **name/password** —
per-identity credentials, login returns a bearer token (API/MCP clients, spokes, CI) or a
session cookie (browser). Loopback stays trusted so the ZCode hook keeps its no-token,
`|| true` contract. Linda V2 reaches the hub on the LAN; Linda V1 reaches MCP only, through
an explicit external access point at the existing edge.

## Context

- Verified 2026-11-15: **no auth of any kind exists in the codebase today**, and the bind is
  hard-coded loopback. The nastv #110 two-credential brief was never implemented; this part
  supersedes it with the simpler design Andy chose.
- Principals: Andy (browser), Linda V2 (MCP, LAN), Linda V1 (MCP, external), the laptop
  spoke (worker), the nastv CI step (`notify-agent-hq`), future spokes. Four roles cover
  them: `admin`, `agent`, `worker`, `ci`.
- Linda V1's network placement must be confirmed before the external point is built: the
  nastv builds-stack plan records V1 on a cloud VM with no LAN route, while the original
  distributed-hub draft called her NAS-hosted. Probe first; the access point's shape does
  not change, but the edge placement does.

## Decisions

- **One mechanism, two token shapes.** `POST /auth/login` `{username, password}` → for API
  clients a bearer token in the body; for browsers `Set-Cookie` (`SameSite=Lax`, `HttpOnly`)
  selected by a `grant=cookie|token` field. Tokens are random 256-bit values, stored
  **hashed** (like passwords), revocable individually and per-identity. No expiry by
  default — a LAN with a handful of named principals; recorded as an accepted risk to
  revisit if exposure widens beyond the V1 MCP point.
- **Password hashing**: PBKDF2 (`Rfc2898DeriveBytes`, 210k iterations, per-user salt) —
  framework-free, boring, adequate. No ASP.NET Identity.
- **Bootstrap**: first start with `AGENTHQ_ADMIN_USER`/`AGENTHQ_ADMIN_PASSWORD` set creates
  the admin identity (idempotent; warns and ignores on later starts). Further identities
  via an admin-only endpoint. Credentials live in Postgres (part 1's store).
- **Perimeter rule**: requests arriving on loopback are trusted and skip auth — the hook,
  local curl, the in-memory test host. Everything else (LAN bind, external point) requires
  a valid token/cookie on **every** route: REST, SSE, MCP, static UI. No anonymous
  anything on non-loopback.
- **Roles, not scopes** (simplification Andy asked for): `admin` everything; `agent`
  reads + plan commands + exchange; `worker` the spoke endpoints only (part 3);
  `ci` the ingest endpoint only. Enforcement is one authorization helper on the endpoint
  groups; a future scope split can subdivide without wire changes.
- **External access point (Linda V1), MCP-only**: a vhost at the existing benow.ca edge
  (exact host/placement verified against nastv `AGENTS.infrastructure.md` — candidate
  `hq.benow.ca`) proxying to `builds:5317`, TLS at the edge. The hub recognises
  edge-forwarded requests (trusted proxy header) and **restricts them to `/auth/login` and
  `/mcp`** — every other path returns **404, not 401** (do not confirm surfaces to a remote
  caller). Login is rate-limited (per-source backoff); failed attempts and denials write
  audit rows. UI and everything else stay LAN-only, permanently.
- **MCP identity is an actor.** A tool call by Linda carries her identity as the actor in
  whatever the tool records (command history, exchange rows) — attribution parity with
  sessions. The `actor` passthrough convention in part 3 is the same rule on the spoke side.
- SSE: browsers authenticate with the cookie; the EventSource reconnect keeps working
  (cookie rides along; no query-string tokens — they leak into logs).

## Steps

- [ ] Identities + tokens tables (Postgres migrations), PBKDF2 + token hashing, bootstrap
      env handling; admin identity-management endpoint.
- [ ] `POST /auth/login` (both grants), `POST /auth/logout`, token revocation; loopback
      trust middleware + non-loopback require-auth on all routes; the `{ok:false,error}`
      error shape per the repo's endpoint conventions.
- [ ] Roles: the authorization helper wired to endpoint groups; MCP tool surface gets
      `agent`-role enforcement; spoke/CI groups stub-registered now, enforced from part 3.
- [ ] Login rate limit + audit rows (login ok/fail, denial with route + source); audit visible
      in logs with the `Subsystem` convention (new `auth` subsystem).
- [ ] External point: trusted-proxy detection, route restriction (404 posture), edge vhost
      config (in nastv's edge config, cross-referenced not duplicated here).
- [ ] Linda V1 placement probe (before the edge work): confirm where she actually runs and
      that she can reach the edge host; record in Status Log.
- [ ] Tests: auth middleware unit tests (loopback trusted, LAN anonymous → 401, wrong
      password → 401 + audit, revoked token → 401); contract tests for both grants; a
      headless MCP round trip with a bearer token against the in-memory host; external-mode
      tests (forwarded header → /mcp allowed, /plans → 404).
- [ ] Docs: `docs/features/api.md` (auth section), `docs/operations.md` (identities,
      bootstrap, the external point as a recorded security decision), nastv
      `AGENTS.infrastructure.md` cross-link.

## Verification Criteria

1. Loopback: the existing hook flow works with no credentials (no regression — the E2E
   simulation passes unchanged).
2. Non-loopback anonymous request (any route, incl. `/overview` and static assets) → 401;
   with a valid token → 200. Cookie grant serves the web UI end-to-end incl. SSE reconnect.
3. Wrong password → 401 + audit row; login rate limit kicks in after repeated failures and
   says so; a revoked token stops working on the next call.
4. Headless MCP: a bearer-authenticated MCP client (the shape Linda V2 will use) completes
   `get_overview` + `get_events` — transcript captured as evidence (the B1 pattern).
5. External posture: through the edge, `/mcp` answers with V1's credentials; `/plans`,
   `/overview`, `/` and unknown paths all return 404 (not 401); login rate limit applies.
6. Linda V1 probe recorded: where she runs, which transport she used, and her captured
   request/response — criterion 4 repeated from her side before part 6 declares victory.

## Documentation

- `docs/features/api.md`: auth endpoints, token/cookie grants, roles table.
- `docs/operations.md`: identity bootstrap, token revocation runbook, the external point
  (host, TLS owner, restriction rule) as a deliberate security decision.
- nastv: edge vhost entry + link from `AGENTS.infrastructure.md`.

## Status Log

- 2026-11-15 — drafted. Supersedes the #110 two-credential brief (never implemented;
  recorded in the overview's deltas) with the simpler name/password design from the
  architecture session; keeps that brief's headless-bearer and 404-posture requirements.
