# AgentHQ.Infrastructure — feature knowledge

Agent HQ's own SQLite database (Dapper for queries, DbUp for migrations), the stores, the plan
service, the event store, and the two file-system watchers.

Behaviour is documented in [docs/features/events.md](../../docs/features/events.md) and
[docs/features/plan-lifecycle.md](../../docs/features/plan-lifecycle.md). This file records the
traps.

## SQLite + Dapper

- **Parameter names are case-sensitive.** `new { repoId }` binds `@repoId`, not `@RepoId`. Mixing
  casing inside one statement throws at runtime, not at compile time. Be consistent per query.
- **Alias every snake_case column explicitly** in `Stores/Columns.cs` and select from those
  constants (`"SELECT " + Columns.Plan + " FROM plans"`). Dapper matches column names to property
  names only; a raw `root_path` silently binds to nothing and the property keeps its default.
  When adding a column: migration → `Columns` → row model, in that order.
- **Materialize into classes with settable properties.** No positional records or value tuples for
  query results, and no `init`-only setters — Dapper's constructor binding is all-or-nothing and
  fails on nullable/non-matching parameters.
- **Connections come from `AgentHqDb.Open()`**, which opens them. Use `await using`.
- **Migrations are embedded and journaled by filename.** Add `0002_…sql`; never rename or edit an
  applied one. DbUp runs `Migrate()` at startup, before the first sync.
- Reference lists (`status IN @statuses`) and `IN` lists need Dapper's list handling; keep values
  typed.

## Self-write suppression (plan files)

The plan file is authoritative, and Agent HQ also writes it on submit and `PUT /content`. Both
sides use the content hash:

- Agent HQ's write path calls `PlanDirWatcher.NoteSelfWrite()` and updates `content_hash` in the
  same step (`UpdateContentAsync(..., externalEdit: false)`): no version bump, no `plan.updated`.
- The watcher hashes what it finds; an equal hash means "we wrote this" and is ignored.
- `UpdateContentAsync` is a compare-and-set (`WHERE id = @Id AND content_hash != @Hash`): a burst
  of watcher events for one edit hashes the same every time, so only the first bumps the version
  and the row returned is the one that statement produced — a concurrent writer cannot be read
  back in its place.

If you add another write path in Agent HQ, it must do both halves. Doing only one produces either
a duplicate event (missed NoteSelfWrite) or a lost external edit (skipped hash update).

## The two watchers

| Watcher | Covers | Trigger | Then |
|---|---|---|---|
| `PlanDirWatcher` | `<repo>/docs/plans/*.md`, non-recursive | changed / created / renamed | 500 ms debounce → hash → external edit bumps version + emits `plan.updated`; `lessons.md` re-indexes instead |
| `ZCodeDbWatcher` | `db.sqlite*` in the ZCode db directory | changed / created / renamed | 500 ms debounce → `PRAGMA data_version` → only if changed, run the session diff |

- **`data_version` is per-connection.** The probe connection is opened once and held for the
  watcher's lifetime — never take it from a pool, because a fresh connection resets the counter and
  the check silently stops firing. This was true in the design plan and is why the code holds
  `_probe` as a field.
- Both watchers debounce; the plan watcher also stretches a settle window on Agent HQ's own writes.
  The hash check is what actually suppresses self-writes; the window only limits churn.
- **A one-minute backstop timer** in `ZCodeDbWatcher` re-runs the `data_version` check, so a
  dropped inotify event costs at most a minute — not data.
- Watcher callbacks must never throw: log and return. A throwing callback on a `FileSystemWatcher`
  thread takes down the process or silently unsubscribes.
- Watcher file matching for plans is by full path, falling back to filename — a renamed plan file
  is still matched to its row.

### Watch out for recursive loops

A plan file that lives inside a *watched* ZCode database directory, or a repository registered at
`$HOME` (which contains `~/.zcode`), can make the two watchers observe each other's output. Register
specific project roots, not a home directory.

## PlanService

- **The only place a status moves.** `ApplyCommandAsync` (command path, incl. the web UI through
  `POST /plans/{id}/commands`) and `ApplyManualTransitionAsync` (PATCH / MCP) both end in
  `RecordTransitionAsync`, which writes the history row and appends `plan.status_changed`.
- **`ApplyCommandByFileAsync` is the shared resolution path** for the hook feed and the UI: it
  resolves the file argument, and for work commands falls back to the calling session's repository
  before giving up. Ambiguity is an error with a count, never a guess.
- **Order inside `ApplyCommandAsync` matters**: short-cut entry first, then session linking, then
  dependency warnings (`implement`), then `SetImplementedBy` (`implemented`), then the
  separation-of-duties check (`pass`), then the transition. Moving the SoD check before
  `SetImplementedBy` would compare against a stale value on rework.
- **Git head** is captured with a 5-second `git rev-parse HEAD` in the repository root for
  `implementing`, `implemented` and `verified`; any failure is `null`, not an exception.
- **Blocking** is handled before the state-machine lookup, requires a non-empty reason to block, and
  records a `plan_blocks` row (resolved on unblock) plus `plan.blocked`/`plan.unblocked`.
- **Lesson indexing** is a delete-and-reinsert per repository on every `lessons.md` change and at
  startup for each registered repository.

## EventStore

- `AppendAsync` inserts, then fans out to `Published` subscribers **sequentially** (each handler
  awaited). A slow subscriber therefore delays the append's caller — subscribers are in-process and
  write to a stream body, so this is deliberate; do not add an unbounded queue here.
- `ReadSinceAsync` defaults to a 100,000-row limit on purpose: a replay must reach the current
  position in one call. Lowering it silently reintroduces "resumed behind live".
- Event payloads are serialized with `AgentHqEvent.JsonOpts` (camelCase, nulls omitted).

## SessionSyncService

- One gate (`SemaphoreSlim`) serialises full sync and diff; the ZCode watcher and startup both call
  in.
- **Full sync auto-registers repositories** from session directories, so a fresh install populates
  itself.
- **Diff uses a strict high-water mark** on mirrored `time_updated` (`>`): the boundary row was
  already mirrored. The same convention applies in the provider's `updatedSince` query.
- `MirrorAsync` emits `session.created`/`session.updated` only when metadata a consumer can act on
  changed (`Changed`). Extending what counts as "actionable" means extending both `Changed` and the
  event payload.

## RepositoryStore

`ResolveByCwdAsync` uses the **longest registered root path that is a prefix** of the cwd. Paths
are normalised before comparison; keep it that way or `/foo` will match `/foobar`.
