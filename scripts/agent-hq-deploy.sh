#!/usr/bin/env bash
# agent-hq deploy: build or stage a release, swap it in, restart, health-check, and roll back
# to the previous release if the new one does not come up. Single local target, so no drain:
# systemd stops the service (waiting up to TimeoutStopSec for in-flight requests) before the swap.
#
#   scripts/agent-hq-deploy.sh                 build + deploy from this checkout (day to day)
#   STAGE_DIR=/path scripts/agent-hq-deploy.sh deploy a staged/published pile (CI)
#   scripts/agent-hq-deploy.sh install         install/replace the unit, enable linger,
#                                                enable + start it so it comes back after reboot
#   scripts/agent-hq-deploy.sh check|status|start|stop|restart|logs
#
# Typical first run:  scripts/agent-hq-deploy.sh && scripts/agent-hq-deploy.sh install
# Day to day:         scripts/agent-hq-deploy.sh
#
# Releases are directory-named and switched by one atomic symlink rename, so a crash mid-deploy
# leaves either the old or the new release live, never a half-copied one. The unit runs
# $BASE_DIR/current/AgentHQ.Api; the database and logs live in $BASE_DIR beside the releases and
# are never touched by a deploy.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# The unit's base dir (releases, database, logs) and the name of its unit file. BASE_DIR may
# differ from the unit's own base dir only to build a release without touching the service.
BASE_DIR="${BASE_DIR:-$HOME/.local/share/agent-hq}"
UNIT_BASE_DIR="${UNIT_BASE_DIR:-$BASE_DIR}"
SERVICE="${SERVICE:-agent-hq}"
PORT="${AGENTHQ_PORT:-5317}"
HEALTH="http://127.0.0.1:${PORT}/health"
KEEP_RELEASES="${KEEP_RELEASES:-3}"
APP=AgentHQ.Api
UNIT_DIR="$HOME/.config/systemd/user"

log()  { echo "[deploy] $*"; }
die()  { echo "[deploy] error: $*" >&2; exit 1; }
health_ok() { curl -sf --max-time 2 "$HEALTH" >/dev/null 2>&1; }
unit_installed() { systemctl --user cat "$SERVICE" >/dev/null 2>&1; }
# The unit baked the port it was installed with; a custom port is part of the unit file.
unit_port() {
  local p
  p="$(sed -n 's/^Environment=AGENTHQ_PORT=\([0-9]*\).*/\1/p' "$UNIT_DIR/$SERVICE.service" 2>/dev/null)"
  echo "${p:-$PORT}"
}

# An installed unit owns the port it was installed with, even across an edit of the env file.
use_unit_health() {
  local p; p="$(unit_port)"
  [ -n "$p" ] && PORT="$p" && HEALTH="http://127.0.0.1:${PORT}/health"
}
show_health() { curl -s --max-time 2 "$HEALTH" || echo "(no response)"; }

unit_installed && use_unit_health

install_unit() {
  mkdir -p "$UNIT_DIR"
  local want="$ROOT/ops/agent-hq.service" have="$UNIT_DIR/$SERVICE.service" tmp
  [ -f "$want" ] || die "unit template missing: $want"
  # BASE_DIR is baked into WorkingDirectory/ExecStart, so a custom base dir installs as a
  # unit that actually runs from it; the shipped template's %h/.local/share/agent-hq is the
  # default and is written verbatim.
  tmp="$(mktemp)"
  sed -e "s#%h/.local/share/agent-hq#$UNIT_BASE_DIR#g" \
      -e "s#^SyslogIdentifier=.*#SyslogIdentifier=$SERVICE#" \
      -e "s#^Environment=AGENTHQ_PORT=.*#Environment=AGENTHQ_PORT=$PORT#" "$want" > "$tmp"
  if cmp -s "$tmp" "$have"; then
    rm -f "$tmp"
  else
    install -m 644 "$tmp" "$have"
    rm -f "$tmp"
    log "installed $have"
    systemctl --user daemon-reload
  fi
}

# `start` on a user unit does not survive a reboot unless lingering is on: without it the
# user manager exits after logout and nothing restarts the bridge.
ensure_linger() {
  [ "$(loginctl show-user "$USER" --property=Linger --value 2>/dev/null)" = "yes" ] && return 0
  if loginctl enable-linger "$USER" 2>/dev/null; then
    log "enabled linger for $USER (service keeps running across logout/reboot)"
  else
    log "warning: could not enable linger — run as an admin: loginctl enable-linger $USER"
  fi
}

# A unit file left by the previous name would not collide by filename, but both would try to
# own the same port and data dir. Refuse until it is gone, so the cutover is explicit.
legacy_base_dir() {
  local wd
  wd="$(systemctl --user show -p WorkingDirectory --value "$1" 2>/dev/null || true)"
  if [ -n "$wd" ]; then echo "$wd"; else
    sed -n 's/^WorkingDirectory=//p' "$UNIT_DIR/$1.service" 2>/dev/null | sed "s#%h#$HOME#"
  fi
}

refuse_legacy_unit() {
  local legacy="dev-bridge"
  [ "$SERVICE" = "$legacy" ] && return 0
  [ -f "$UNIT_DIR/$legacy.service" ] || return 0
  if [ "$BASE_DIR" != "$(legacy_base_dir)" ]; then
    log "warning: $UNIT_DIR/$legacy.service still exists (different base dir); it is left alone"
    return 0
  fi
  die "$UNIT_DIR/$legacy.service owns $BASE_DIR and the port — retire it first:
  systemctl --user disable --now $legacy && rm $UNIT_DIR/$legacy.service && systemctl --user daemon-reload"
}

cmd_install() {
  refuse_legacy_unit
  if unit_installed && [ "$(unit_base_dir)" != "$UNIT_BASE_DIR" ]; then
    die "the $SERVICE unit runs from $(unit_base_dir); install from that checkout (or remove the unit first)"
  fi
  [ -e "$BASE_DIR/current/$APP" ] ||
    die "no release for $BASE_DIR yet — build one first, then install"
  install_unit
  ensure_linger
  if ! unit_installed; then
    die "$SERVICE unit missing at $UNIT_DIR/$SERVICE.service"
  fi
  [ -e "$BASE_DIR/current/$APP" ] ||
    die "no release yet — run: scripts/agent-hq-deploy.sh   (builds and switches current →)"
  systemctl --user enable --now "$SERVICE"
  log "enabled and started $SERVICE at boot"
  sleep 1
  cmd_check
}

# Stopping the unit has to wait for systemd to finish the stop job, otherwise a deploy can
# restart right into the port the old process still holds (address already in use).
wait_stopped() {
  local i
  for i in $(seq 1 100); do
    systemctl --user is-active --quiet "$SERVICE" || return 0
    sleep 0.1
  done
  die "$SERVICE did not stop within 10s"
}

# Single-file publish plus the assets it reads from its own directory (wwwroot, Commands).
# PDBs are build detail, not runtime.
build_release() {
  local dest="$1"
  dotnet publish "$ROOT/src/$APP" -c Release -r linux-x64 --self-contained \
    -p:PublishSingleFile=true -o "$dest"
  find "$dest" -name '*.pdb' -delete
  [ -x "$dest/$APP" ] || die "publish did not produce $APP in $dest"
}

# A staged pile is either the publish output (AgentHQ.Api) or a single executable named
# agent-hq. Catching anything else here beats systemd's exit 203.
find_binary() {
  local dir="$1" hit
  [ -x "$dir/$APP" ] && { echo "$dir/$APP"; return; }
  hit="$(find "$dir" -maxdepth 1 -type f -perm -u+x ! -name '*.dll' ! -name '*.so' | head -1)"
  [ -n "$hit" ] && { echo "$hit"; return; }
  die "no $APP (or a single executable) in $dir — wrong STAGE_DIR?"
}

print_versions() {
  local cur
  cur="$(current_release)"
  log "release: ${cur:-none}"
  if [ -n "$cur" ] && [ -x "$cur/$APP" ]; then
    log "version: $("$cur/$APP" --version 2>/dev/null || echo 'unknown')"
  fi
}

# Never this script's own base dir: BASE_DIR=/.../current would make `current` a self-link,
# which readlink -f reports as the link itself — a rollback would then "switch" to it and the
# base dir would lose its release pointer entirely.
switch_current() {
  target="$(dirname "$(readlink -f "$1")")/$(basename "$1")"
  [ "$target" = "$BASE_DIR" ] && die "refusing to point current at $BASE_DIR itself"
  ln -sfn "$1" "$BASE_DIR/current.tmp"
  mv -Tf "$BASE_DIR/current.tmp" "$BASE_DIR/current"
}

# The live release directory, or empty. Read the link itself (not readlink -f): a broken or
# self-referencing link must not be mistaken for a usable release.
current_release() {
  local link; link="$(readlink "$BASE_DIR/current" 2>/dev/null || true)"
  [ -n "$link" ] && [ -d "$link" ] && [ "$link" != "$BASE_DIR" ] && echo "$link"
  return 0
}

# Keeps the live release plus the most recent others (the previous one is the rollback target).
prune_releases() {
  local live; live="$(current_release)"
  [ -n "$live" ] || return 0
  ls -1dt "$BASE_DIR/releases"/*/ 2>/dev/null | grep -vF "$live/" | tail -n +"$((KEEP_RELEASES))" |
    while read -r old; do
      log "pruning $(basename "$old")"
      rm -rf "$old"
    done
}

deploy() {
  local stage="${STAGE_DIR:-}" name rel bin prev
  name="$(date -u +%Y%m%dT%H%M%SZ)-$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo local)"
  rel="$BASE_DIR/releases/$name"
  mkdir -p "$BASE_DIR/releases" "$BASE_DIR/logs"

  if [ -n "$stage" ]; then
    [ -d "$stage" ] || die "STAGE_DIR $stage does not exist"
    bin="$(find_binary "$stage")"
    log "staging release $name from $stage"
    mkdir -p "$rel"
    cp -a "$stage/." "$rel/"
    # Older pipelines renamed the binary; the unit and the health check expect $APP.
    [ "$(basename "$bin")" = "$APP" ] || mv -f "$bin" "$rel/$APP"
  else
    log "building release $name"
    build_release "$rel"
  fi
  chmod +x "$rel/$APP"

  prev="$(current_release)"
  local started=$SECONDS

  # A manual ad-hoc run on the port (e.g. scripts/dev-run.sh) is not the unit's business to
  # kill, but it would make the health check below answer for the wrong process.
  if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null &&
     ! systemctl --user is-active --quiet "$SERVICE" &&
     ! unit_owns_port; then
    die "port $PORT is in use by something other than $SERVICE — stop it first"
  fi

  if unit_installed && [ "$(unit_base_dir)" = "$BASE_DIR" ]; then
    systemctl --user stop "$SERVICE" || true
    wait_stopped
  elif unit_installed; then
    log "unit runs from $(unit_base_dir), not $BASE_DIR — release built, unit not restarted"
  fi
  switch_current "$rel"
  log "current → $rel"

  if ! unit_installed || [ "$(unit_base_dir)" != "$BASE_DIR" ]; then
    log "no $SERVICE unit installed for $BASE_DIR — run: scripts/agent-hq-deploy.sh install"
    return 0
  fi

  systemctl --user start "$SERVICE"
  if wait_healthy; then
    log "healthy after $((SECONDS - started))s: $(show_health)"
    prune_releases
    return 0
  fi

  log "new release did not become healthy — rolling back"
  systemctl --user stop "$SERVICE" || true
  wait_stopped
  if [ -n "$prev" ] && [ -d "$prev" ]; then
    switch_current "$prev"
    log "current → $prev"
  else
    log "no previous release to roll back to"
  fi
  systemctl --user start "$SERVICE" || true
  wait_healthy exit || true
  wait_healthy || true
  systemctl --user is-active --quiet "$SERVICE" && log "previous release is running again" || log "previous release did NOT come up — inspect: scripts/agent-hq-deploy.sh logs"
  journalctl --user -u "$SERVICE" -n 30 --no-pager || true
  log "rollback complete"
  return 1
}

# Public API comes up after the first ZCode sync (13s cold start on the dev machine), so the
# budget is generous; the probe is every 0.25s, so a warm start reports promptly.
HEALTH_DEADLINE="${HEALTH_DEADLINE:-60}"

# Waiting has to follow the port: the service opens it only after its first ZCode sync
# (13–20 s, longer on a large catalog), so a fixed deadline reports a healthy release as dead.
wait_healthy() {
  local started=$SECONDS pid
  if [ "${1:-}" = "exit" ]; then
    while [ $((SECONDS - started)) -lt "$HEALTH_DEADLINE" ]; do
      pid="$(systemctl --user show -p MainPID --value "$SERVICE" 2>/dev/null || echo 0)"
      [ -n "$pid" ] && [ "$pid" != 0 ] || return 1
      sleep 0.25
    done
    return 0
  fi
  while [ $((SECONDS - started)) -lt "$HEALTH_DEADLINE" ]; do
    health_ok && return 0
    sleep 0.25
  done
  return 1
}

# A cold start is not ready until the first ZCode sync finishes (13–20s here), so every
# start/restart path waits for health rather than reporting the first failed probe.
cmd_start() {
  [ "$(unit_base_dir)" = "$BASE_DIR" ] ||
    die "the unit runs from $(unit_base_dir), not $BASE_DIR — install from that checkout first"
  unit_installed || die "$SERVICE unit not installed — run: scripts/agent-hq-deploy.sh install"
  [ -e "$BASE_DIR/current/$APP" ] || die "no release yet — run: scripts/agent-hq-deploy.sh"
  local started=$SECONDS
  if ! systemctl --user is-active --quiet "$SERVICE"; then
    if (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; then
      die "port $PORT is in use by another process — stop it first"
    fi
    systemctl --user start "$SERVICE"
  fi
  if wait_healthy; then
    log "healthy after $((SECONDS - started))s"
    cmd_check
  else
    die "$SERVICE did not become healthy within ${HEALTH_DEADLINE}s"
  fi
}

cmd_stop() {
  [ "$(unit_base_dir)" = "$BASE_DIR" ] ||
    die "the unit runs from $(unit_base_dir), not $BASE_DIR — refusing to stop it from here"
  if unit_installed; then systemctl --user stop "$SERVICE"; fi
  log "stopped (Restart=always does not restart a unit after an explicit stop)"
}

cmd_restart() {
  [ "$(unit_base_dir)" = "$BASE_DIR" ] ||
    die "the unit runs from $(unit_base_dir), not $BASE_DIR — install from that checkout first"
  unit_installed || die "$SERVICE unit not installed — run: scripts/agent-hq-deploy.sh install"
  local started=$SECONDS
  systemctl --user restart "$SERVICE"
  if wait_healthy; then
    log "healthy after $((SECONDS - started))s"
    cmd_check
  else
    die "$SERVICE did not become healthy within ${HEALTH_DEADLINE}s"
  fi
}

cmd_status() {
  local active enabled
  active="$(systemctl --user is-active "$SERVICE" 2>/dev/null || true)"
  enabled="$(systemctl --user is-enabled "$SERVICE" 2>/dev/null || true)"
  echo "service: ${active:-not installed} (enabled: ${enabled:-no})"
  echo "health:  $(show_health)"
  echo "data:    ${AGENTHQ_DB:-$BASE_DIR/agent-hq.sqlite}"
  print_versions
}

# Health alone can be answering for an older process: if the new release died right after its
# health check, systemd may have restarted an older `current` (or the check raced a stop).
# So the running process is also matched against the release the symlink points at.
running_release() {
  local pid
  pid="$(systemctl --user show -p MainPID --value "$SERVICE" 2>/dev/null || echo 0)"
  [ -n "$pid" ] && [ "$pid" != 0 ] || return 1
  readlink -f "/proc/$pid/exe" 2>/dev/null | sed 's#/AgentHQ.Api$##'
}

# The unit runs from its own base dir, which a test may have overridden. A service that is
# down while its own binary is holding the port is mid-restart (Restart=always), not a
# foreign process to refuse.
unit_base_dir() {
  local wd
  wd="$(systemctl --user show -p WorkingDirectory --value "$SERVICE" 2>/dev/null || true)"
  echo "${wd:-$UNIT_BASE_DIR}"
}

unit_owns_port() {
  [ -e "$(unit_base_dir)/current/$APP" ] || return 1
  ss -ltnH "sport = :$PORT" 2>/dev/null | grep -q . && return 0
  systemctl --user is-enabled --quiet "$SERVICE" 2>/dev/null
}

cmd_check() {
  health_ok || { echo "unhealthy: $(show_health)"; journalctl --user -u "$SERVICE" -n 30 --no-pager; return 1; }
  echo "healthy: $(show_health)"
  print_versions
  local live running
  live="$(current_release)"
  if running="$(running_release)"; then
    if [ "$running" = "$live" ]; then
      echo "running: $running"
    else
      echo "WARNING: running $running but current → $live (deploy did not stick; see logs)"
      journalctl --user -u "$SERVICE" -n 20 --no-pager
      return 1
    fi
  fi
}

main() {
  case "${1:-deploy}" in
    deploy)  install_unit; deploy ;;
    install) cmd_install ;;
    check)   cmd_check ;;
    status)  cmd_status ;;
    start)   cmd_start ;;
    stop)    cmd_stop ;;
    restart) cmd_restart ;;
    logs)    journalctl --user -u "$SERVICE" -f ;;
    *)       die "unknown command '$1' (deploy|install|check|status|start|stop|restart|logs)" ;;
  esac
}

main "$@"
