# 6 — cutover: the hub moves to builds, the laptop flips to a spoke

Component of: [00-OVERVIEW.md](00-OVERVIEW.md) · Started: 2026-11-15

## Goal

The flip itself. The containerized, authenticated hub goes live on builds on the LAN; the
laptop service restarts in spoke mode against it; nastv's CI ingestion retargets; both
Lindas prove their routes; the docs and fleet records catch up. Every step has a rollback,
and the last verification is a rollback drill — a cutover you cannot undo is not a cutover,
it's a commitment.

## Preconditions (all gates green)

Parts 1–5 verified by other sessions; the merge prereq and step-0 items from the overview
done. The laptop hub is still the live system right up to the flip below.

## Steps

- [ ] **Preflight on builds**: deploy the hub via the container path; bind LAN
      (`AGENTHQ_URL=http://192.168.0.178:5317`); auth mandatory (part 2) — anonymous
      probe from the NAS is refused; loopback on builds still trusted for ops.
- [ ] **Shadow run (one day minimum)**: laptop stays the live hub; a second instance of
      the spoke mode runs against the builds hub on an alternate port with the same
      `~/.zcode` read access — no, simpler and honest: skip the second instance (the
      inotify budget lesson), run the spoke-mode binary against a COPY of the laptop
      SQLite in a temp dir, snapshot it at the builds hub, and diff the hub's reads
      against the live laptop hub's. Evidence, not shadow traffic.
- [ ] **The flip**: set `AGENTHQ_HUB_URL` on the laptop unit (`agent-hq-spoke.service`
      rename lands with part 4), restart. The spoke registers, snapshots, streams. Verify
      criteria 2 and 3 of the overview before touching anything else.
- [ ] **Retarget nastv**: the `notify-agent-hq` step's URL → builds, secret → a `ci`
      identity token; one deploy run produces its record (criterion 4).
- [ ] **Linda V2**: her OpenClaw MCP config points at the hub with her credentials; she
      answers a PM question from `get_overview` + `get_events` alone (criterion 7, her
      captured transcript).
- [ ] **Linda V1 / external point**: edge vhost live (part 2), MCP-only posture verified;
      her probe repeated from her side (criterion 7's second half).
- [ ] **Records**: nastv `AGENTS.infrastructure.md` gains the builds fleet row (role,
      ports incl. 5317 and the Postgres sidecar, verified date) and the reconciled feed
      hostname; `docs/operations.md` reflects the new topology; this plan's Status Log
      records the flip with evidence links.
- [ ] **Observation window**: one lid-closed-laptop day — the hub stays fully usable, the
      spoke's queued events drain on wake in order (criterion 3's real-world run).
- [ ] **Rollback drill** (last, deliberately): unset `AGENTHQ_HUB_URL`, restart, confirm
      hub mode serves from the intact SQLite; then flip back. The drill result is recorded
      either way — an untested rollback is a rumour.

## Verification Criteria

The overview's program-level criteria 1–9, evidenced in this plan's Status Log as they
pass, plus:

1. The flip itself is boring: one env var, one restart, snapshot visible at the hub within
   the first minute; no data-copy step ran (the SQLite file never moved).
2. The laptop's former hub surfaces are gone in spoke mode (UI/MCP/reads) — one truth —
   and `/health` + the hook endpoints still answer on loopback.
3. ZCode on the laptop never noticed: a `/plan` command issued mid-flip (hub briefly
   restarting) applied locally and appears at the hub after drain.
4. nastv evidence: a deploy record within one minute of a run; the annotation path still
   works when the hub is stopped (their criterion, re-run post-cutover).
5. Both Linda transcripts (V2 internal, V1 external) captured with request/response and
   the actor identity her calls carry.
6. The rollback drill passes and is re-flipped; final state is spoke mode.

## Documentation

- This file's Status Log is the cutover record (who flipped what, when, evidence links).
- `docs/operations.md`: final topology, units (builds container, laptop spoke), env vars,
  the rollback runbook.
- nastv: fleet row, feed hostname, builds-stack plan 4's reachability line updated to the
  builds endpoint (superseding its V1-cloud-VM framing where this plan's probes showed
  otherwise).
- `docs/README.md` / `README.md`: the architecture sketch from the overview, public-facing.

## Status Log

- 2026-11-15 — drafted. The "shadow run" was reduced from live dual-running to a
  snapshot-and-diff against a copied database — dual live hubs on the laptop burn the
  inotify budget and buy nothing the diff doesn't (lesson from the scratch-hub incident).
