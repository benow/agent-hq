#!/usr/bin/env bash
# agent-hq prompt hook: posts the expanded prompt to the local Agent HQ hub with this
# session's attribution, so /plan commands are recorded against the session that typed
# them. Hook processes receive CLAUDE_SESSION_ID, PROMPT and CWD as environment — the
# agent shell does not. The JSON body is built with jq from those variables (never shell
# interpolation: prompts legally contain quotes, newlines and backslashes). Every failure
# path is silent and non-blocking — prompting must never wait on the hub being up.
set +e
[ -n "${CLAUDE_SESSION_ID:-}" ] || exit 0
[ -n "${PROMPT:-}" ] || exit 0

body="$(jq -nc --arg id "$CLAUDE_SESSION_ID" --arg prompt "$PROMPT" --arg cwd "${CWD:-$PWD}" \
  '{session_id: $id, prompt: $prompt, cwd: $cwd}' 2>/dev/null)" || exit 0
[ -n "$body" ] || exit 0

printf '%s' "$body" \
  | curl -sf -X POST http://127.0.0.1:5317/hooks/zcode/prompts \
      -H 'Content-Type: application/json' --data-binary @- --max-time 3 >/dev/null 2>&1 || true
exit 0
