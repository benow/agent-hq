# What Agent HQ is

Agent HQ is a small web service that runs on your own machine and gives a team of AI agents a
shared place to agree on work.

If you have used coding agents like ZCode or Claude Code, you know the pattern: each session is
brilliant, and each session forgets everything when it ends. Nobody is holding the whole picture.
Which plans are waiting for review? Which one is stuck because someone never answered a question?
Did the person who just marked something "done" also write it, and did anyone check?

Agent HQ is the answer to "who is keeping track?". It is deliberately not another agent. It is a
hub that records what was planned, who did what, in which session, and with what evidence — and
then makes that state cheap to read for both humans and agents.

> **One line:** Agent HQ manages agent work; it does not do the work. A plan can be software,
> research, operations or writing — the lifecycle is the same.

## The three actors

| Actor | What it is | What it does in Agent HQ |
|---|---|---|
| **You** (the human) | The operator | Submit and herd plans, read the web UI, decide what gets worked on and when |
| **A project-manager agent** | A conversational agent with MCP access — OpenClaw in the reference setup | Scopes and submits plans, keeps them moving, verifies finished work from evidence |
| **A worker agent** | A coding agent with a provider adapter — ZCode in the reference setup | Refines a plan, implements it, and verifies someone else's implementation, each in its own session |

The important rule is **separation of duties**: the agent that claims something is implemented
is never the one that verifies it. A single agent cannot fake a verification because it cannot
both be the implementer and the verifier.

```
          you (browser)                 your project-manager agent
                │                                │
                │  REST / SSE                    │  MCP
                ▼                                ▼
        ┌─────────────────────── Agent HQ ───────────────────────┐
        │  plans   ·  events   ·  sessions   ·  state machine    │
        └───────┬───────────────────────▲────────────────────────┘
                │  reads session index  │  POST /hooks/zcode/prompts
                ▼                       │  (every /plan prompt)
        worker agents (ZCode sessions) ─┘
```

## The lifecycle in ninety seconds

Every unit of work is a **plan**: a markdown file at `docs/plans/<slug>.md` inside its own
repository. Plans move through a fixed set of states, and every move is recorded with an actor,
a timestamp, the command that caused it, and the git commit that was current at the time.

```
submitted → refining → in_review → implementing → implemented → verifying → verified → done
                                                         ▲             │
                                                         └── fixing ◄──┘   (/plan fail)
                                                                │
                                                                └─────────→ back to implemented after fixes
```

A verification that fails sends the plan to `fixing`; when the fixes are done it re-enters the
queue as `implemented` and is verified again. Plans can also be **deferred** (postponed, with a
reason) or **abandoned** from any state.

A worker agent **claims** progress; Agent HQ records the claim but can never prove the work
happened. Trust comes from the surrounding rules rather than from the record itself:

- the agent writes its evidence (commits, test output, review findings) into the plan's own
  **Status Log**, which lives in the repository and therefore in git;
- a **different session** does the verification, and its verdict is a separate state transition;
- anything that blocks progress is a **flag** rather than a state, so a blocked plan keeps its
  place and its reason stays on the record.

Read the full mechanics — every state, command and rule — in
[Plan lifecycle](features/plan-lifecycle.md).

## Why not just a to-do list?

A to-do list knows *what*. It does not know *who*, *how*, or *whether it was checked*. Agent HQ
is built around three gaps that plain task lists leave open:

**Attribution.** Every transition names the session that made it. Because the worker agent's
prompt is posted to Agent HQ before the agent sees it, a `/plan implemented` command arrives
with the session id attached — nobody has to type it, and nobody can claim it was someone else.

**Evidence, kept where it belongs.** The narrative of what happened belongs in the repository,
with the code, in the same commits. Agent HQ versions that file and emits an event when it
changes, but it never tries to parse prose into decisions. The database stays mechanical: who,
when, which command, which commit.

**Cheap oversight.** A project-manager agent cannot read every session. Agent HQ reads the
worker agents' own session index, condenses sessions deterministically into digests, and
exposes a one-call operational picture. Watching the work costs a few tool calls, not a
context window.

## Three habits make it work

**Orientation over memory.** A project-manager agent starts each conversation with one call that
answers "what is the state of everything?". It does not remember; it re-derives. Anything it
would have to remember is a sign that the hub should be answering it instead.

**Deterministic condensing.** Session digests are produced by fixed rules — no AI summarisation
in the hub. A digest is reproducible, reviewable, and cannot silently lose the one line that
mattered.

**The repository is the memory.** Accumulated lessons live in `docs/plans/lessons.md` inside each
repository, not in an agent's context. Every fresh session reads them before working, and every
failed verification that was foreseeable becomes a new lesson entry.

## What Agent HQ is not

- **Not a coding agent.** It never edits your code and never runs your tests.
- **Not a scheduler.** It exposes queues (ready plans, the verification queue, the blocked list);
  whoever is driving — you, or your project-manager agent's own scheduling — decides what runs.
- **Not a cloud service.** It binds to `127.0.0.1`, stores data in one SQLite file under your
  home directory, and has no authentication because it has no network exposure. See
  [Security and limits](operations.md#security-and-limits) before changing that.
- **Not tied to one agent or one kind of work.** ZCode is the first provider behind an interface;
  the plan lifecycle is identical for research, operations and writing.

## Where to go next

| You want to… | Read |
|---|---|
| See it running | [Installation](installation.md) |
| Understand plans, states and verification | [Plan lifecycle](features/plan-lifecycle.md) |
| Wire your own project-manager agent to it | [MCP tools](features/mcp.md) |
| Connect a worker agent | [The ZCode integration](features/zcode-integration.md) |
| Call the HTTP API | [API reference](features/api.md) |
| Keep it running day to day | [Operations](operations.md) |
