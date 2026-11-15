# Plan lifecycle

A plan is the unit of work in Agent HQ: a markdown file in a repository that moves through a
fixed set of states, with every move recorded. This page covers what a plan is, who moves it,
what each state means, and the rules that keep claims honest.

## Anatomy of a plan

Plans live at `docs/plans/<slug>.md` inside the repository they belong to. The file is the
source of truth for the work itself; Agent HQ's database holds only the mechanical record
(identity, status, progress counts, history).

The slug is derived from the plan name when it is submitted — lowercase, spaces become `-`,
everything else is stripped, and a numeric suffix (`-2`, `-3`) is added on collision within the
repository. The slug is also the file name, so `/plan implement demo-awaiting-review` and
`/plan implement docs/plans/demo-awaiting-review.md` address the same plan.

Agent HQ recommends a section layout and warns (but never rejects) when parts are missing:

| Section | Purpose |
|---|---|
| `## Goal` | What this plan achieves, in a sentence or two |
| `## Context` | Why it is needed; what the implementer cannot infer |
| `## Steps` | Markdown checkboxes. **These drive progress** |
| `## Dependencies` | Other plans that must land first |
| `## Verification Criteria` | Concrete, testable statements a verifier works through |
| `## Documentation` | What documentation the work must produce |
| `## Status Log` | Append-only narrative: claims, evidence, review findings, verdicts |

Only `Steps`, `Verification Criteria` and `Documentation` are soft-validated on submit.

### Progress comes from checkboxes

Progress is parsed from the `## Steps` section only. Each line matching `^[ \t]*[-*] \[[ xX]\]`
counts, so nested sub-steps count too. Plans with no Steps section, or no checkboxes in it,
report `0 / 0`. Editing the file updates the count automatically — including edits made by the
agent with a text editor, not through the API.

### Lessons

Each repository can accumulate a lessons file at `docs/plans/lessons.md`: entries headed
`### L-<n>: <title>`, with `Category:` and `Source:` lines in the body. Agent HQ indexes these
and exposes them to the project-manager agent when it drafts verification criteria. The
capture-and-consult loop is described in [the bottom of this page](#lessons-the-memory-that-lives-in-the-repository).

## States

| State | Meaning | Who usually moves it out |
|---|---|---|
| `submitted` | A plan exists and is waiting for an owner | A worker session: `/plan start` |
| `refining` | Someone is taking it to implementable quality — no code yet | A **different** session: `/plan review`; or the reviewer sends it back |
| `in_review` | Refined and under review. This is the "ready" queue | `/plan implement` or `/plan sendback` |
| `implementing` | Work is in progress | `/plan implemented` with evidence |
| `implemented` | Work is claimed done. This is the **verification queue** | `/plan verify` |
| `verifying` | A verifier is checking the claims | `/plan pass` or `/plan fail` |
| `fixing` | Verification failed; rework is in progress | `/plan implemented` again |
| `verified` | Criteria passed | `/plan done` — a human or the project manager |
| `done` | Closed out | — |
| `deferred` | Postponed with a reason | `/plan reactivate` returns to `refining` |
| `abandoned` | Superseded or no longer needed; the file stays | — |

```
submitted ─start→ refining ─review→ in_review ─implement→ implementing
                  ▲      ▲                  │                        │
                  │      │                  │ sendback               │ /plan implemented
                  │      └──────────────────┘                        ▼
                  │                                              implemented ◄──────────┐
                  │                                                  │ /plan verify      │ /plan implemented
                  │                                                  ▼                   │ (fixes are done)
                  │                                              verifying ──/plan fail──► fixing
                  │                                                  │ /plan pass        ▲
                  │                                                  ▼                   │
                  │                                              verified                │ /plan verify
                  │                                                  │ /plan done        │ (short-cut: records
                  │                                                  ▼                   │  → implemented first)
                  │                                              done                    └──────────────────┘
                  │
                  ├── deferred ──/plan reactivate──┘
                  └── (defer and abandon are legal from every state)
```

Two design points are worth knowing:

- **`implementing` only ever exits to `implemented`.** `verified` is reachable only from
  `verifying`. There is never a question of whether "implementing → verified" skips something.
- **Failed work loops through `fixing`** and re-enters the verification queue through
  `implemented`, so rework is legible from the status alone.

`defer` and `abandon` are legal from any state (including `done`). `reactivate` is legal only
from `deferred`. Any other move is rejected with a message naming the illegal transition — the
same rules apply whether the move comes from a `/plan` command, the browser, REST or MCP.

## Blocked is a flag, not a state

When an agent cannot proceed — missing credentials, an unanswered question, an upstream outage —
it marks the plan **blocked** with a reason:

```
/plan block demo-awaiting-review waiting on API credentials from the platform team
```

The lifecycle status does not move. After `/plan unblock <file>`, work resumes exactly where it
stopped, because it never left. Every block is also recorded as its own row with reason, actor,
and later resolution, and emits a `plan.blocked` / `plan.unblocked` event.

Blocked plans are the project-manager agent's primary herding trigger: the blocked list is the
needs-attention queue, and each entry carries the reason the agent recorded.

## How a transition is recorded

Every transition produces two things: a history row and an event.

The **history row** holds `from_status`, `to_status`, the actor (`session`, `openclaw`, or
`andy` via the browser), the actor's session id when there is one, the command that caused it,
the git `HEAD` at the time (for `implementing`, `implemented` and `verified` transitions, when
the repository is a git checkout), a short note (the reason tail of `/plan fail`, `sendback`,
`block`, `defer`, `abandon`) and a separation-of-duties flag.

The **event** is `plan.status_changed` with the same data in its payload, so any consumer
watching the [event stream](events.md) sees the transition as it happens.

The narrative — what was actually done, which commits, which test output — is deliberately
**not** in the database. It goes into the plan file's Status Log, which travels with the code in
the same commits. Agent HQ versions that file and emits `plan.updated` when it changes, but it
never parses prose into decisions.

### Three ways to move a plan

| Path | Used by | Attribution |
|---|---|---|
| `POST /hooks/zcode/prompts` — a `/plan` command prompt | Worker agents, via the hook | The agent's session id |
| `POST /plans/{id}/commands` | The browser | `andy` |
| `PATCH /plans/{id}/status`, MCP `update_plan_status` | The project-manager agent, or recovery | `openclaw` (or the session you name) |

All three run through the same service, so they enforce the same state machine and write the
same history vocabulary. A browser action is even recorded under its equivalent command name
(`PATCH:/verify`), so reading the history does not depend on where the action came from.

### The short-cut entry

`/plan verify` accepts a plan in `implementing` or `fixing` as well as `implemented`. If the
plan was not already `implemented`, Agent HQ first records `→ implemented` **attributed to the
verifying session**, then `→ verifying`. The response includes a warning saying so.

This exists because a verifier who starts early should not have to pretend the implementer made
a claim it never made. The history shows exactly who recorded what.

## Separation of duties

The cardinal rule: **the session that claims `implemented` cannot verify that plan**.

`/plan implemented` records the claiming session in `implemented_by_session_id`. When a later
`pass` comes from that same session, the transition still happens but:

- the API response carries a warning,
- the history row is flagged `separationOfDutiesWarning`,
- the event payload carries `separationOfDutiesWarning: true`.

This is soft enforcement by design: Agent HQ cannot prevent one session from doing both, but it
makes it permanently visible. The `/plan verify` template instructs the verifier to run in a
different session; a project-manager agent verifies from evidence and never implements, so the
rule holds by construction on that side.

### Dependencies are soft too

`POST /plans/{id}/dependencies` adds "this plan requires that plan" edges, with self-references
and cycles rejected. Moving a plan to `implementing` with unmet dependencies produces a warning
naming them — not a refusal. The `ready` filter (`GET /plans?ready=true`, and the **Ready**
chip in the UI) is the strict version: `in_review`, not blocked, and every dependency already
`verified` or `done`.

## The verification loop

The verification queue is simply `GET /plans?status=implemented`. Agent HQ never schedules
anything itself; someone drains the queue:

- the project-manager agent can judge criteria that are checkable from recorded evidence and
  session digests, as a scheduled task;
- heavier verification runs as a worker session executing `/plan verify`, then `/plan pass` or
  `/plan fail <reason>`.

```
implementing ──/plan implemented──▶ implemented ──/plan verify──▶ verifying
                                        ▲                             │
                                        │                     ┌───────┴────────┐
                                        │                  /plan pass      /plan fail <reason>
                                        │                     │                │
                                  /plan implemented ◀── fixing ◀────────────────┘
```

## Review: written for a junior implementer

The `/plan review` template asks the reviewer to rewrite the plan for an implementer who has
**no context beyond the plan and the repository**: list gaps and ambiguities, apply the fixes,
check the plan against every relevant lesson, and end with a verdict — approved, or needs-author
(the human or the project manager then runs `/plan sendback <reason>`).

Review runs in a different session from the one that ran `/plan start`. That separation is what
makes "refining" and "reviewing" two real phases rather than one self-approved pass.

## The `/plan` command reference

Every command is a prompt template installed by [the ZCode integration](zcode-integration.md).
The transition is recorded by Agent HQ when the prompt reaches the hook; the template tells the
agent what the phase means and how to do the work.

| Command | Transition | Takes a reason | Run it when |
|---|---|---|---|
| `/plan start <file>` | `submitted → refining` | — | Taking ownership of a plan's refinement |
| `/plan review <file>` | `refining → in_review` | — | Reviewing a refined plan (different session from `start`) |
| `/plan sendback <file> <reason>` | `in_review → refining` | yes | The review found author-level problems |
| `/plan implement <file>` | `in_review → implementing` | — | Executing an approved plan |
| `/plan implemented <file>` | `implementing`/`fixing → implemented` | — | Claiming completion **with evidence** in the Status Log |
| `/plan verify <file>` | `implemented → verifying` (short-cut from `implementing`/`fixing`) | — | Starting adversarial verification |
| `/plan pass <file>` | `verifying → verified` | — | Every criterion checked ✓ |
| `/plan fail <file> <reason>` | `verifying → fixing` | yes | A criterion failed; draft a lesson if foreseeable |
| `/plan block <file> <reason>` | sets the blocked flag | yes | Something outside your control stops the work |
| `/plan unblock <file>` | clears the blocked flag | — | Resuming after the obstacle cleared |
| `/plan defer <file> <reason>` | any → `deferred` | yes | Postponing, with the revival condition recorded |
| `/plan reactivate <file>` | `deferred → refining` | — | Reviving a deferred plan |
| `/plan done <file>` | `verified → done` | — | Closing out |
| `/plan abandon <file> <reason>` | any → `abandoned` | yes | Superseded or no longer needed |

Every command also accepts the colon spelling of its
[workflow twin](plan-workflow.md) (`/plan:start`, `/plan:review`, `/plan:implement`,
`/plan:verify`, `/plan:done`, `/plan:abandon`): the two spellings are one-to-one and move the
hub identically — the colon form carries the worktree workflow, the space form is the state
interface. When a plan is closed it may be archived to `docs/plans/done/<slug>.md`; the plans
watcher re-points the registration at the archived copy, so closed plans stay addressable by
slug and by `done/<slug>.md`.

The `<file>` argument may be a slug, a path relative to the repository root, or an absolute path.
It is resolved against the repository derived from the hook's working directory, then — for
`start` and `implement` — against the calling session's own repository. The working directory
also resolves when the prompt is typed inside a ZCode worktree
(`~/.zcode/worktrees/<repo-directory-name>/<worktree>`), which is how the
[worktree plan workflow](plan-workflow.md) addresses its repository.

Two behaviours are worth knowing because they are easy to trip over:

- **Only registered plans are acted on.** A file that exists under `docs/plans/` but was never
  submitted to Agent HQ is rejected, with a `hook.rejected` event carrying the prompt.
- **An ambiguous argument is rejected.** If `<file>` resolves to more than one plan, nothing
  moves and the rejection says how many matched.

## What makes a claim credible

`/plan implemented` is a self-report — the single trust gap in the system. The design narrows it
from both sides:

1. **The template forces evidence.** Summary, commit hashes and subjects, tests run with
   results, documentation updated, lessons consulted, and deviations from the plan. In the
   `fixing` phase it also requires the root cause and the refined lesson.
2. **A different session checks it.** The verifier reads the criteria, reads the evidence, runs
   the tests itself, and inspects the recorded commits' diffs. The verifier's own report goes
   into the Status Log, criterion by criterion.
3. **The history makes the actors legible.** Actor, session id, command, git head, and the
   separation-of-duties flag are all recorded next to each other.

If an agent never reports, the plan sits in `implementing` with an idle session. That stuck
state is itself the signal the project-manager agent herds on.

## Lessons: the memory that lives in the repository

Worker sessions are ephemeral; the accumulated knowledge must not be. Each repository keeps
`docs/plans/lessons.md`:

```markdown
### L-3: Migrations must be idempotent under re-run

Category: data
Source: docs/plans/billing-schema.md

Every migration script must be guarded (`IF NOT EXISTS`) because the loader re-runs the folder
on every start. A plain CREATE TABLE works exactly once.
```

The loop has three moments:

- **Capture** — on a failed verification, `/plan fail` requires the verifier to classify each
  failure. A foreseeable oversight becomes a drafted lesson entry; a genuinely novel problem
  gets a note only. The fixing session root-causes it and refines the draft as part of
  `/plan implemented`.
- **Consult** — every work command (`start`, `review`, `implement`, `verify`) instructs the
  agent to read the lessons first, and `/plan implemented` evidence lists which lessons
  applied and how. The project-manager agent reads them when drafting verification criteria,
  so past classes of oversight are caught at authoring time.
- **Distil** — periodically, recurring lessons are promoted out of the lessons file and into
  the repository's `AGENTS.md`, where they apply to every session without being asked for.

Agent HQ indexes the lessons file (re-indexed whenever it changes) and serves them at
`GET /repositories/{id}/lessons` and the MCP tool `list_lessons`.

## Planning non-development work

Nothing in the lifecycle is specific to code. A research plan verifies against "the dossier
cites primary sources and every open question in the brief is answered"; an operations plan
verifies against "the runbook was rehearsed on a staging box". The mechanics — Steps,
Verification Criteria, Documentation, Status Log, separation of duties — stay identical. The git
head recorded on transitions is simply `null` in a directory that is not a git checkout.

## See also

- [Sessions](sessions.md) — how the agent sessions behind those claims are tracked
- [Web UI](web-ui.md) — the table and fly-out that drive these transitions by hand
- [MCP tools](mcp.md) — how a project-manager agent submits, herds and verifies
- [API reference](api.md) — exact request shapes for every endpoint named here
