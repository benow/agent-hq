# 8 — install & onboarding: the `agent-hq` tool, pairing, setup wizard

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-22
Depends on: 2 (auth), 3 (contract/pairing routes), 4 (spoke), 6 (harness artifact)
Amends: 02 (bootstrap), 03 (SSE/pairing/rules 0, 8–10), 04 (transport, self-update, lazy
gate), 06 (user-level install) — those amendments are recorded in each file's Status Log.

## Goal

Onboarding that is **usable, not merely functional**: a fresh machine reaches a fully
working spoke with one command and one browser click; a fresh hub is initialized through a
wizard instead of env files; a repository joins Agent HQ by *using* `/plan`, never by being
configured. The repo tree is never modified; nothing machine-specific lands in git; no
credential is ever typed by a human or shown to an LLM.

## Decisions (locked in the 2026-11-22 design session with Andy)

- **The tool is the installer.** `agent-hq` is a `dotnet tool` on nuget.benow.ca (the
  existing feed; pull anonymous). It carries: the spoke service install (systemd unit,
  releases directory, `current` symlink), the harness install (part 6's plugin +
  `~/.zcode/cli/config.json` writes), the pairing client, and `agent-hq init` (hub-side
  helper that surfaces the setup code). One tool, machine-level, self-updating.
- **Pairing is a device flow.** `agent-hq install --url <hub>` → tool creates a nonce
  (`POST /workers/pairing`), prints and opens `<hub>/pair/<nonce>`, the user signs into
  the hub in their browser and clicks approve; the hub attaches a `worker`-role token to
  the nonce; the tool polls, receives it once, writes it to the spoke config, never
  displays it. No paste, no mDNS in v1 (one hub, one stable LAN address — the dashboard
  shows the exact command with a copy button; discovery is a later nicety if multi-hub
  ever exists).
- **Hub initialization is a setup-code wizard.** Fresh start, no identities → the hub
  generates a one-time setup code (container log + `agent-hq init`), the first web visit
  shows the wizard (code → chosen admin username/password in the browser → code burned).
  Env-var bootstrap remains for headless/CI installs only (02 amendment). Retire the
  interactive env path from deploy docs at the same time.
- **Repository participation = hub-side registration, triggered by use.** The first
  explicit `/plan` command from a registered spoke auto-registers the repository
  (`repo.registered` event; visible, one click to undo). Bare prompts never register —
  they keep today's `hook.rejected` behaviour. Opt-out is: don't type `/plan`, or remove
  the registration. No `.agent-hq` file, no per-repo config, nothing in git.
- **Self-update follows the deploy discipline** (04 amendment): hub signals via heartbeat
  `serverVersion` / SSE `release.available`; the spoke pulls the release into its
  releases directory, health-checks it, flips `current`, restarts its unit, verifies,
  and rolls back with an `update.rolled_back` event on failure.
- **The dashboard guides, it never installs.** Registration of *repositories* and
  *spoke approvals* are server-side and browser-side acts; installation is inherently
  local (only the machine can write `~/.zcode` and run systemd). The dashboard's job is
  the copyable install command, the pairing approve button, and the presence strip.

## The flows

```
Hub (fresh):
  docker compose up → log: "setup code XXXX-XXXX → open http://<lan>:5317"
  wizard: code + chosen admin credentials → done (code burned)

Machine (fresh):
  dotnet tool install -g agent-hq          # from nuget.benow.ca
  agent-hq install --url http://<lan>:5317
    → systemd unit + releases dir + harness (~/.zcode) + pairing browser flow
  → spoke registered, visible in the hub's presence strip

Repository (any, on a paired machine):
  /plan start …   # first explicit /plan command auto-registers the repo
  → plans, activity, CI events (part 5), and Linda's commands all flow
```

## Steps

- [ ] `AgentHQ.Tool` project (the `agent-hq` tool): `install` (spoke unit + harness +
      pairing), `init` (surface hub setup code), `workflow add` (insert the part-5
      `agent-hq-actions/notify` steps into a repo's GitHub workflows — the explicit
      build-integration opt-in), `uninstall` (removal is part of development). Pack/push
      rides part 3's feed pipeline.
- [ ] Setup-code wizard: one-time code generation on identity-less start (logged + `init`),
      `GET /setup/status`, `POST /setup/complete` (single-use, burned; refused once any
      identity exists), the wizard page in `wwwroot`; env bootstrap demoted to headless.
- [ ] Pairing: the part-3 routes wired to a dashboard pairing page (list pending nonces,
      approve with admin session, approve → token attached); rate limits per 03.
- [ ] Spoke auto-register: first explicit `/plan` command from a spoke-registered machine
      registers the repository with a `repo.registered` event; bare prompts stay
      `hook.rejected`.
- [ ] Dashboard: copyable install command on the presence strip; pending pairing notice.
- [ ] Docs: `docs/features/install.md` (the user-facing flow), operations.md (headless
      bootstrap, spoke update/rollback runbook), overview table, docs/README.md link.

## Verification Criteria

1. **Fresh-machine E2E**: a throwaway `$HOME` + fake systemd scope → `agent-hq install
   --url <test hub>` → pairing approved in the browser flow → spoke visible at the hub,
   harness files present in the fake `~/.zcode/cli/config.json` (exact hook shape, no
   stray matcher), second install is a no-op, `uninstall` removes everything it wrote.
2. **Fresh-hub E2E**: identity-less start → setup code logged → wizard completes with
   code + chosen credentials → code is burned (second use refused) → env bootstrap is
   ignored when identities exist.
3. **Auto-register**: first `/plan start` on a paired machine registers the repo
   (event + dashboard row); a bare prompt from the same repo before any `/plan` does not
   register anything (`hook.rejected` only).
4. **Credential hygiene**: the pairing token never appears in tool output, logs, or the
   LLM-visible transcript; the setup code is single-use; audit rows cover pairing
   approve/deny/expire.
5. **Update**: hub version bumped in test → spoke updates via symlink flip and reports
   the new version in its next heartbeat; a bad release rolls back per part 4's row.
6. The whole flow is documented in `docs/features/install.md` with the degradation
   rules (unconfigured → guidance; configured-but-down → visible) and linked from the
   docs index.

## Documentation

- `docs/features/install.md`: the onboarding story for users (hub init, machine install,
  repository by-use).
- `docs/operations.md`: headless bootstrap mode, spoke update/rollback runbook.
- `00-OVERVIEW.md`: this part's row.

## Status Log

- 2026-11-22 — drafted from the design session with Andy (the whole NASTV-integration-day
  conversation): dotnet-tool installer, device-flow pairing (his "redirect to a page and
  grab a token" refined so nothing is pasted), setup-code wizard (his `docker exec init`
  instinct, browser-first variant), hub-signaled spoke self-update with his SSE amendment,
  and auto-register-on-`/plan` (his simplification) with the bare-prompt guard kept.
