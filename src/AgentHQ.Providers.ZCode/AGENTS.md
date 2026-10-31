# AgentHQ.Providers.ZCode — feature knowledge

Read-only access to ZCode's private session index (`~/.zcode/cli/db/db.sqlite`). This is the only
project that may know ZCode's schema; everything else consumes `IAgentProvider` models. Behaviour is
documented in [docs/features/sessions.md](../../docs/features/sessions.md). This file records what
matters when the schema moves.

## The schema guard

- `TestedZCodeSchemaVersion` (currently **24**) is the highest ZCode migration this provider has
  been checked against. At construction it reads `schema_migration.id`, parses the leading digits,
  takes the max, and:
  - `≤ 24` → `ProviderHealth.Ok`;
  - `> 24` → `SchemaAhead`, every query returns empty results, a warning is logged;
  - unreadable / missing table → `Missing`, same degraded behaviour.
- **It never throws.** A provider is a service; a schema surprise must not stop Agent HQ from
  serving plans.
- Degraded results are *empty*, not partial. Callers must treat "no sessions" as a possible
  degraded state — `/health` is the place that disambiguates.

### Bumping the tested version

Only after verifying, against the live database:

1. `sqlite3 ~/.zcode/cli/db/db.sqlite .schema` still has `schema_migration`, `session`, `message`
   and `part` with the columns this provider selects (`session`: id, project_id, directory, title,
   task_type, last_outcome, time_created, time_updated, time_archived, time_deleted; `message`:
   id, session_id, data, sequence; `part`: message_id, session_id, data, sequence).
2. The part `type` values this provider maps (`text`, `reasoning`, `tool`, `step-start`,
   `step-finish`) still exist, and `data` is still JSON with the shapes parsed in `MapPart`.
3. `tests/AgentHQ.Tests/Fixtures/zcode_schema.sql` is refreshed from the new schema in the same
   change.
4. `AGENTHQ_LIVE=1 dotnet test` passes (the smoke tests assert the schema guard, a real session
   list and a real digest).
5. The docs page's `schema version 24` example is updated.

## Path discipline

- The constructor **requires an explicit path** and throws on `null`. There is deliberately no
  fallback to the user's real `~/.zcode` database: a misconfigured or test host must degrade to
  "no sessions", never mirror someone's live work. The host resolves `AGENTHQ_ZCODE_DB`.
- The connection string is `Mode=ReadOnly`. Every connection is opened per call and disposed —
  read-only connections are cheap and there is no write path to protect ordering for. (The
  `ZCodeDbWatcher` in Infrastructure has its own separate, long-lived probe connection; do not
  confuse the two.)

## Query shape

- `session.project_id` maps to a repository by the directory most recently seen for that project.
- `updatedSince` filters with a **strict** `time_updated > @since` — the boundary row was already
  mirrored by the sync service. Keep the strictness in both places (the sync's high-water mark and
  this query) or the same rows are processed forever.
- Repository filtering is done in memory by path prefix (`RootPath == root ||
  RootPath.StartsWith(root + "/")`) after trimming trailing slashes; the SQL side has no
  notion of a repository.
- Derived status filtering (`active`/`idle`/…) is applied in memory using `SessionInfo.Status(now)`.
- The first-user-message excerpt is extracted with SQLite JSON functions (`json_extract`) and
  truncated to 200 characters. If ZCode changes the message `data` shape, this is the first query
  to break — and it fails softly (returns null).

## Part mapping

| ZCode part type | Included | Mapped to |
|---|---|---|
| `text` | always | `SessionPart.Text` |
| `reasoning` | `include=reasoning` | `SessionPart.Type = "reasoning"` |
| `tool` | `include=tool` | `ToolName` + `ToolSummary` |
| `step-start`, `step-finish` | `include=steps` | type only |
| `timeline`, `compaction`, `file` | never | — |

`ToolSummary` deliberately reduces a tool call to its salient input — the file path for
edit/write-style tools, the command for shell tools — and never includes output. Raw tool output
in a session index is huge and rarely the thing a reader needs; it is dropped at the provider so
nothing downstream has to remember to do it.

Adding a part type means: this table, `IncludePart`, `MapPart` if it carries text, and the digest
builder's inclusion rules in Core.

## Testing

- Unit and fixture tests run against `tests/AgentHQ.Tests/Fixtures/zcode_schema.sql`, a checked-in
  snapshot. Do not replace it with hand-written minimal tables — Dapper binding bugs and JSON shape
  mismatches only show up against the real DDL.
- The live smoke tests are env-gated (`AGENTHQ_LIVE=1`) and strictly read-only. They are the only
  tests that touch the real database, and they must never write to it.
