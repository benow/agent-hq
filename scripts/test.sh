#!/usr/bin/env bash
# Agent HQ fast test loop: one build, then test runs without rebuild.
#
# Usage:
#   scripts/test.sh                    build, then run the whole suite
#   scripts/test.sh ExchangeTests      build, then one class (shorthand for
#                                      --filter 'FullyQualifiedName~ExchangeTests')
#   scripts/test.sh 'name~Expiry'      any raw dotnet --filter expression
#   TEST_TIMEOUT=120 scripts/test.sh ExchangeTests   (seconds, default 600)
#
# Why this shape (see docs/plans/lessons.md): a dotnet build costs ~15 s and a
# `dotnet test` host start ~10 s regardless of test count, so iterating with bare
# `dotnet test` rebuilds and re-hosts every time. Build once here, then every run
# is --no-build. Output goes to a log file, never through a pipe: a crashed
# testhost can orphan the pipe and wedge the terminal for minutes. The CLR
# 0x80131506 first-run-after-build abort documented in AGENTS.md is retried once.
set -uo pipefail
cd "$(dirname "$0")/.."

FILTER="${1:-}"
TIMEOUT_SECS="${TEST_TIMEOUT:-600}"

dotnet build -v q --nologo || exit 1

ARGS=(test tests/AgentHQ.Tests --no-build --nologo)
if [[ -n "$FILTER" ]]; then
  if [[ "$FILTER" == *"~"* || "$FILTER" == *'|'* ]]; then
    ARGS+=(--filter "$FILTER")
  else
    ARGS+=(--filter "FullyQualifiedName~${FILTER}")
  fi
fi

LOG="$(mktemp /tmp/agenthq-test-XXXXXX.log)"
echo "output: $LOG"
run() {
  timeout --kill-after=15 "$TIMEOUT_SECS" dotnet "${ARGS[@]}" >"$LOG" 2>&1
  local code=$?
  if (( code == 124 || code == 137 )); then
    echo "ERROR: test run exceeded ${TIMEOUT_SECS}s and was killed; last output:" >&2
    tail -40 "$LOG" >&2
    return 124
  fi
  if grep -q "0x80131506" "$LOG"; then
    echo "CLR 0x80131506 abort (known first-run flake, AGENTS.md); retrying once" >&2
    timeout --kill-after=15 "$TIMEOUT_SECS" dotnet "${ARGS[@]}" >"$LOG" 2>&1
    return $?
  fi
  return "$code"
}

run
code=$?
if (( code != 0 )); then
  echo "--- failed; last 80 lines (full log: $LOG):" >&2
  tail -80 "$LOG" >&2
else
  grep -E "passed!|Failed|error" "$LOG" | tail -5 || true
  rm -f "$LOG"
fi
exit "$code"
