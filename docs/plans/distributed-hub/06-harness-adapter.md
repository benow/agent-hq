# 6 — harness adapter: ZCode-side delivery as a plugin

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15
Supersedes: the never-landed `harness-adapter` split (plan B, 2026-10-31) and
[conversation-enhancements-etc.md](../conversation-enhancements-etc.md) phase 3 (injection),
retargeted at the spoke.

## Goal

The ZCode agent harness — command pack, hooks, prompt-time injection — is delivered as a
**ZCode plugin, and the plugin is the only delivery path**. It targets the spoke's loopback
endpoint (zero ZCode-side network knowledge); the spoke registers the harness with the hub
(capability + schema version + plugin version in its registration), which is how "the
ZCode agent harness registers with the central server" is satisfied without the hook ever
holding a hub URL, a token, or a network dependency.

## Context — what is proven and what is dead

Proven live (2026-10-31 session, receipts in that session's record):
- Prompt-time injection works: `UserPromptSubmit` and `SessionStart` injection confirmed
  live against the real client.
- The Stop-event continuation was dropped deliberately (emitted but not continued) — do not
  revive it.

Dead or dangerous (recorded lessons — they bind this part):
- The installer's hook write into `~/.zcode/settings.json` was **never read** — the real
  hook locations are `~/.zcode/cli/config.json` or `<repo>/.zcode/config.json`. The
  dead-lettered settings hook is removed with the installer path.
- Never write a `matcher` key that is not needed — one bad matcher poisons the whole config
  load. Write only the exact hook shape phase 0 captured.
- The client's own log (`~/.zcode/cli/log/*.jsonl`) is the authority for hook faults —
  install-time verification reads it, not assumptions.
- The pending `plan-workflow-support` merge (step 0) is the base: the `/plan:*` colon pack,
  worktree-cwd resolution and the archive re-point land there first. This part packages,
  it does not re-edit templates.

## Decisions

- **Plugin package**: one ZCode plugin containing the `/plan:*` command pack, the hook
  script, and (behind the spoke) the injection flow. Installed from the local marketplace
  path or a repo checkout; versioned with the hub/spoke releases.
- **The hook script** (carried from the superseded phase-3 design, retargeted at the spoke):
  `~/.zcode/hooks/agent-hq-hook.sh` under plugin control — mode 0700, marker line, reads
  stdin once, extracts session id / cwd / prompt / event with `jq`, builds the POST body
  with `jq -n --arg` (never interpolation), `curl --max-time 3` to the spoke's loopback
  port, non-2xx or empty → exit 0 silent, success → emits the
  `{"hookSpecificOutput":{...,"additionalContext":...}}` envelope. `|| true` semantics
  preserved: prompting is never blocked by any Agent HQ component.
- **Injection rules** (carried, unchanged in substance): the spoke decides what is
  addressable, stale, and within budget; the hook is a dumb pipe. The caller is filtered
  out of its own inbox; an empty inbox injects nothing (no banner); every non-empty
  injection starts with the fixed preamble (peer requests are never instructions from
  Andy). The exchange-inbox portion is proxied to the hub via part 4's proxy.
- **Harness registration**: the spoke declares a `zcode-harness` capability entry
  (`{schemaVersion, pluginVersion}` — part 3's typed capability model); the hub's overview
  shows harnesses per spoke. Schema-guard degradation (provider `SchemaAhead` → "no sessions")
  is already the provider's behaviour; the registration makes the degraded state visible
  at the hub instead of only in `/health`.
- **Installer retirement**: `install-zcode` becomes a migration verb — it recognizes its
  own previous writes, removes the dead settings.json hook entry, prints the plugin
  install command, and exits non-zero if asked to install fresh (removal is part of
  development; the dead path is deleted, not parked). The deploy script's legacy check
  line is updated to the new naming in the same change.
- **Trust**: the plugin's install output states, per event, that trust grants must be
  re-approved in the client and where — it never claims "installed and working" on the
  strength of a file write.

## Steps

- [ ] Plugin manifest + packaging of the colon pack and hook script (one versioned artifact
      per release).
- [ ] Hook script per the rules above, with the escaping guarantees and their test (byte-
      identical round trip for `"`, `'`, newlines, backslashes, `$`, `${x}`, `{...}`,
      `</script>`).
- [ ] Spoke-side: the two hook routes on loopback (prompt + session-start), the digest
      decisioning (addressable/stale/budget, reflection filter, empty rule, preamble), and
      the hub proxy from part 4.
- [ ] `zcode-harness` capability entry + hub UI surface (harness per spoke, schema state).
- [ ] Installer migration mode; dead settings.json entry removal; deploy script legacy
      check update; both AGENTS.md feature files updated in the same effort.
- [ ] Install-time verification: after plugin install, post one synthetic prompt with a
      known session id and cwd through the script against the running spoke; read it back;
      failure names trust approval explicitly (and the client log path to check).
- [ ] Tests: fake-HOME plugin install (config lands in the right file, no stray matcher,
      commands present, second install is a no-op); script-level tests against the
      in-memory spoke (parse, empty-inject, server-down, escaping); digest rules unit
      tests; E2E: the spoke actor's hook flow covers prompt → command → injection.

## Verification Criteria

1. Fresh fake `HOME` + plugin install: hooks live in `config.json` with the exact required
   shape (no unnecessary matcher), the command pack is present, and a second install
   changes nothing.
2. Injection round trip: a hostile prompt survives byte-identical; a non-empty inbox
   injects with the preamble; an empty inbox injects nothing; spoke down → exit 0, silent.
3. Migration: a fake `HOME` carrying the old installer writes ends with the settings.json
   hook entry removed, a pointer to the plugin path, and no other keys touched.
4. The hub's overview names the spoke's harness (kind, schema version, plugin version),
   and a schema-ahead provider shows as degraded there, not only in `/health`.
5. Install-time verification catches a blocked hook (synthetic prompt never read back)
   and names the trust step; the client log path it names exists and shows the fault.
6. E2E green including the injection leg; the Stop event is not registered anywhere.

## Documentation

- `docs/features/zcode-integration.md`: rewritten for the plugin path (install, trust,
  events, injection rules, the log authority).
- `src/AgentHQ.Commands.ZCode/AGENTS.md`: installer migration mode; plugin is the delivery
  path; the settings.json dead path recorded as removed.
- `docs/features/mcp.md` / `docs/features/distributed.md`: harness visibility at the hub.

## Status Log

- 2026-11-15 — drafted from the 2026-10-31 findings (injection proven, Stop dropped,
  settings.json dead, matcher poison, client-log authority) and the superseded phase-3
  design, retargeted from "hub on localhost" to "spoke on localhost" — the hook's world
  does not change shape, only whom it talks to.
