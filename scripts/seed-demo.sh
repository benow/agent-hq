#!/usr/bin/env bash
# Seed a running agent-hq with a demo repository + plans at every lifecycle stage, so the
# web UI can be exercised without agents. Idempotent: re-running adds nothing new.
#   PORT=5399 ./scripts/seed-demo.sh [repo-root]
set -euo pipefail
PORT="${PORT:-${AGENTHQ_PORT:-5317}}"
API="http://127.0.0.1:${PORT}"
REPO="${1:-$(mktemp -d -t agenthq-demo-XXXXXX)}"
mkdir -p "$REPO/docs/plans"
echo "[seed] repository: $REPO"

json() { python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('$1',''))"; }

curl -sf -X POST "$API/repositories" -H 'Content-Type: application/json' \
  -d "{\"rootPath\":\"$REPO\",\"isDefault\":false}" >/dev/null
echo "[seed] registered"

# Transitions are attributed to a ZCode session, and the hook feed rejects sessions the
# provider does not know — so drive them as the newest real session when one exists.
SEED_SESSION="${SEED_SESSION:-$(curl -sf "$API/sessions" | python3 -c "
import json,sys
s=json.load(sys.stdin)
print(s[0]['id'] if s else '')")}"
if [ -z "$SEED_SESSION" ]; then
  echo "[seed] no ZCode sessions found; transitions will fall back to the status API"
fi

submit() { # name → prints plan id
  local name="$1" body
  body="$(python3 - "$name" <<'PY'
import json, sys
name = sys.argv[1]
md = f"""# {name}

## Goal
Demonstrate the {name.lower()} flow in the Agent HQ board.

## Context
Seeded by scripts/seed-demo.sh so the UI can be exercised without agents.

## Steps
- [ ] first step
- [ ] second step
- [ ] third step

## Verification Criteria
- `dotnet test` passes
- the UI shows the plan in the expected column

## Documentation
README section updated.

## Status Log
"""
print(json.dumps({"repo": "REPO", "name": name, "markdown": md}))
PY
)"
  body="${body/REPO/$REPO}"
  curl -sf -X POST "$API/plans" -H 'Content-Type: application/json' -d "$body" | json id
}

hook() { # prompt  — attributes to SEED_SESSION via the hook feed, or uses the status API
  local prompt="$1" file sub to
  if [ -n "$SEED_SESSION" ]; then
    curl -sf -o /dev/null -X POST "$API/hooks/zcode/prompts" -H 'Content-Type: application/json' \
      -d "{\"session_id\":\"$SEED_SESSION\",\"prompt\":\"$prompt\",\"cwd\":\"$REPO\"}" || true
    return
  fi
  # Fallback: manual status path (same state machine), actor openclaw.
  sub=$(awk '{print $2}' <<<"$prompt"); file=$(awk '{print $3}' <<<"$prompt")
  case "$sub" in
    start|reactivate) to=refining ;; review|sendback) to=in_review ;;
    implement) to=implementing ;; implemented) to=implemented ;;
    verify) to=verifying ;; pass) to=verified ;; fail) to=fixing ;; done) to=done ;;
    defer) to=deferred ;; abandon) to=abandoned ;; *) return ;;
  esac
  local id
  id=$(curl -sf "$API/plans" | python3 -c "
import json,sys
slug='$file'.split('/')[-1].removesuffix('.md')
print(next((p['id'] for p in json.load(sys.stdin) if p['slug']==slug), ''))")
  [ -n "$id" ] && curl -sf -o /dev/null -X PATCH "$API/plans/$id/status" -H 'Content-Type: application/json' \
    -d "{\"status\":\"$to\",\"by\":\"openclaw\"}" || true
}

existing=$(curl -sf "$API/plans" | python3 -c "
import json,sys
print('\n'.join(p['name'] for p in json.load(sys.stdin)))")
existing_slugs=$(curl -sf "$API/plans" | python3 -c "
import json,sys
print('\n'.join(p['slug'] for p in json.load(sys.stdin)))")

make() { # name commands...
  local name="$1"; shift
  if grep -qx "$name" <<<"$existing"; then echo "[seed] skip '$name'"; return; fi
  local id; id=$(submit "$name")
  for cmd in "$@"; do hook "/plan $cmd"; done
  echo "[seed] '$name' → $(curl -sf "$API/plans/$id" | json status)"
}

make "Demo: awaiting review"    "start docs/plans/demo-awaiting-review.md" "review docs/plans/demo-awaiting-review.md"
make "Demo: blocked on details" "start docs/plans/demo-blocked-on-details.md" \
                                "block docs/plans/demo-blocked-on-details.md waiting on credentials from Andy"
make "Demo: in progress"        "start docs/plans/demo-in-progress.md" "review docs/plans/demo-in-progress.md" \
                                "implement docs/plans/demo-in-progress.md"
make "Demo: verification queue" "start docs/plans/demo-verification-queue.md" \
                                "review docs/plans/demo-verification-queue.md" \
                                "implement docs/plans/demo-verification-queue.md" \
                                "implemented docs/plans/demo-verification-queue.md"
make "Demo: fixes requested"    "start docs/plans/demo-fixes-requested.md" \
                                "review docs/plans/demo-fixes-requested.md" \
                                "implement docs/plans/demo-fixes-requested.md" \
                                "implemented docs/plans/demo-fixes-requested.md" \
                                "verify docs/plans/demo-fixes-requested.md" \
                                "fail docs/plans/demo-fixes-requested.md parser drops tab-indented steps"
make "Demo: completed"          "start docs/plans/demo-completed.md" \
                                "review docs/plans/demo-completed.md" \
                                "implement docs/plans/demo-completed.md" \
                                "implemented docs/plans/demo-completed.md" \
                                "verify docs/plans/demo-completed.md" \
                                "pass docs/plans/demo-completed.md" \
                                "done docs/plans/demo-completed.md"
make "Demo: done"               "start docs/plans/demo-done.md" "review docs/plans/demo-done.md" \
                                "implement docs/plans/demo-done.md" "implemented docs/plans/demo-done.md" \
                                "verify docs/plans/demo-done.md" "pass docs/plans/demo-done.md"

# Tick progress in each plan the way an implementer would, straight into the file so the
# watcher picks each edit up: the work that is claimed in the status must be visible in the
# checkboxes, otherwise a Verified card at 0/3 is just a seed artifact.
tick() { # file step-name...
  local file="$1"; shift
  for n in "$@"; do
    if ! grep -q "\[x\] $n step" "$REPO/$file" 2>/dev/null; then
      sed -i "s/- \[ \] $n step/- [x] $n step/" "$REPO/$file"
    fi
  done
  echo "[seed] $file: $* checked (watcher emits plan.updated)"
}
tick "docs/plans/demo-awaiting-review.md" first second
tick "docs/plans/demo-in-progress.md" first second
tick "docs/plans/demo-verification-queue.md" first second third
tick "docs/plans/demo-fixes-requested.md" first second third
tick "docs/plans/demo-done.md" first second third
tick "docs/plans/demo-completed.md" first second third
sleep 1

echo "[seed] done. Open $API/ and select the demo repository."
