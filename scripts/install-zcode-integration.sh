#!/usr/bin/env bash
# Idempotent ZCode-side installation: /plan command pack, UserPromptSubmit hook,
# AGENTS.md section.  Usage: scripts/install-zcode-integration.sh [--repo DIR] [--port N]
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project src/AgentHQ.Api -c Release -- install-zcode "$@"
