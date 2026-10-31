# AgentHQ.Core — feature knowledge

Domain logic only. **No I/O**: nothing here reads files, opens a database, or runs a process. If a
change needs any of those, it belongs in Infrastructure or a provider, and Core exposes a pure
function or a rule the caller applies.

The public behaviour is documented in
[docs/features/plan-lifecycle.md](../../docs/features/plan-lifecycle.md) and
[docs/features/sessions.md](../../docs/features/sessions.md); this file records what a developer
touching the code needs to know.

## PlanStateMachine

- The transition table is one dictionary of `(from, command) → to`. It is the single authority for
  which command is legal from which status. `Validate(from, to)` derives the same answer for the
  manual path, so a command path and a `PATCH` path cannot disagree — if you add a command, both
  paths get it.
- **`CommandFor(from, to)` returns the first matching command.** Two distinct commands can produce
  the same move only if you add one; the history row for the manual path records whichever is found
  first, so avoid duplicates unless the ordering is deliberate.
- **Short-cut entry** (`verify` from `implementing` or `fixing`) is not in the map. It is a special
  case in `PlanService` that records an intermediate `→ implemented` first. If you add another
  entry shortcut, extend `IsShortCutEntry` rather than adding map entries that skip `implemented`.
- **`IsWorkCommand`** (`start`, `implement`) is what makes a command claim the plan's session of
  record. Adding a work command means deciding that question explicitly.
- `defer` and `abandon` are legal from every status including `done`; `reactivate` only from
  `deferred`. `block`/`unblock` are **not** in this machine — they are the orthogonal flag handled
  before the lookup in `PlanService`.

## Status wire form

`PlanStatusCodes.Code()` / `FromCode()` own the wire strings, and `AllCodes` is the canonical list.
Never serialize `PlanStatus` directly and never derive a code from `ToString()`. `fixing` is a real
status in the list even though it sorts after the main line in the UI's display order.

## Parsers (regex-based, deliberately literal)

- **Plan sections** (`PlanDocument`): a section starts at a `## <Name>` heading and ends at the next
  one or EOF. Name matching for `Section()` is case-insensitive. Sub-headings (`###`) do not split
  sections — which is why a Status Log can contain `### <date>` entries under `## Status Log`.
- **Checkbox progress**: only inside `## Steps`; lines matching `^[ \t]*[-*] \[[ xX]\]`. Indented
  sub-steps count. Both `-` and `*` bullets, both `x` and `X`. A plan with no Steps section reports
  `0/0`, not a warning.
- **Soft validation** (`MissingSections`) requires only `Steps`, `Verification Criteria` and
  `Documentation`. `Goal`, `Context`, `Dependencies`, `Status Log` are convention, not validated.
- **Lessons** (`LessonsParser`): entries are `### L-<n>: <title>` headings. The body runs to the
  next `### ` heading — so a lesson cannot contain its own third-level heading. `Category:` and
  `Source:` are the first matching lines in the body. Indexing is a full replace per repository
  (`UpsertLessonsAsync` deletes then inserts), so entry ids must be stable or the first-seen times
  churn.
- **`/plan` commands** (`PlanCommand.TryParse`): `^/plan\s+(\w+)\s+(.+)$`, case-insensitive
  subcommand (lowercased), first whitespace-delimited token is the file, the rest is the reason —
  and only for `sendback`, `fail`, `block`, `defer`, `abandon` (`TakesReason`). The templates in
  `AgentHQ.Commands.ZCode` must stay consistent with `TakesReason`; a template that asks for a
  reason the parser discards loses it silently. Note the file token never contains whitespace, so
  paths with spaces cannot be addressed by a `/plan` command — use the slug instead.

## Slugs

`PlanSlug.Derive` lowercases, maps spaces to `-`, strips everything that is not an ASCII letter,
digit or `-`, collapses runs, trims, falls back to `plan`, then suffixes `-2`, `-3`… on collision
**within a repository**. Repository ids come from `ForRootPath` (absolute path, lowercased, `/` →
`-`), so ids are stable as long as the path is.

## Digest

`DigestBuilder.Render` is the L1 session model. Rules that must hold (they are the contract
documented in [docs/features/sessions.md](../../docs/features/sessions.md#digests-the-reading-model-for-a-project-manager)):

- user text verbatim → assistant text tail-weighted (last 20%) → tool one-liners;
- reasoning, step markers, timeline, compaction and file parts never appear;
- the budget is a hard upper bound with a fixed elision order: oldest assistant text → oldest tool
  calls → head/tail truncation of oversized user messages → hard cut;
- every elision is marked, so a reader knows something was dropped.

Keep the elision markers accurate. A silent drop is the one failure mode this design exists to
avoid.

## Models

`SessionInfo.Status(now)` derives `deleted`/`archived`/`active`/`idle` (30-minute window) and is
used by both the provider and `SessionStore.DeriveStatus`. If one changes, change both — or better,
have the store call the model.
