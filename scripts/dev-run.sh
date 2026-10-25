#!/usr/bin/env bash
# Dev convenience: run agent-hq from source on the default port.
# The deployed service owns the same port (and database), so this refuses to start while the
# service is up — use `scripts/agent-hq-deploy.sh` to replace the running build, or
# `AGENTHQ_PORT=5399 scripts/dev-run.sh` for a scratch instance.
set -euo pipefail
cd "$(dirname "$0")/.."
export AGENTHQ_PORT="${AGENTHQ_PORT:-5317}"

if systemctl --user is-active --quiet agent-hq 2>/dev/null; then
  active_port="$(systemctl --user show -p Environment --value agent-hq \
    | grep -o 'AGENTHQ_PORT=[0-9]*' | cut -d= -f2 || true)"
  if [ "${active_port:-5317}" = "$AGENTHQ_PORT" ]; then
    echo "agent-hq.service is already running on :$AGENTHQ_PORT — deploy there instead:" >&2
    echo "  scripts/agent-hq-deploy.sh" >&2
    echo "or run a scratch instance:  AGENTHQ_PORT=5399 scripts/dev-run.sh" >&2
    exit 1
  fi
fi

exec dotnet run --project src/AgentHQ.Api -- "$@"
