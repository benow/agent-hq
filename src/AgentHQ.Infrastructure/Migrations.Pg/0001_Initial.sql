-- 0001_Initial.sql (PostgreSQL) — the port of the SQLite Migrations 0001 + 0002 + 0003.
-- The hub's own schema on builds. Same tables, same CHECK rules; INTEGER stays INTEGER
-- (epoch milliseconds and boolean-as-int, so the shared row models in AgentHQ.Models bind
-- identically on both sides), AUTOINCREMENT becomes BIGSERIAL. Journaled by filename in
-- AgentHqPgSchemaVersions; never edit an applied script.

CREATE TABLE IF NOT EXISTS repositories (
  id TEXT PRIMARY KEY,
  root_path TEXT NOT NULL,
  provider TEXT NOT NULL,
  external_id TEXT,
  plans_dir TEXT NOT NULL DEFAULT 'docs/plans',
  is_default INTEGER NOT NULL DEFAULT 0,
  first_seen_at BIGINT NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  provider TEXT NOT NULL,
  external_id TEXT NOT NULL,
  repository_id TEXT,
  title TEXT NOT NULL,
  task_type TEXT NOT NULL,
  last_outcome TEXT,
  external_time_created BIGINT NOT NULL,
  external_time_updated BIGINT NOT NULL,
  first_user_message_excerpt TEXT,
  archived INTEGER NOT NULL DEFAULT 0,
  deleted INTEGER NOT NULL DEFAULT 0,
  ingested_at BIGINT NOT NULL,
  PRIMARY KEY (provider, external_id)
);
CREATE INDEX IF NOT EXISTS sessions_repo_updated_idx ON sessions(repository_id, external_time_updated);

CREATE TABLE IF NOT EXISTS plans (
  id TEXT PRIMARY KEY,
  slug TEXT NOT NULL,
  name TEXT NOT NULL,
  repository_id TEXT NOT NULL REFERENCES repositories(id),
  file_path TEXT NOT NULL,
  status TEXT NOT NULL,
  priority INTEGER NOT NULL DEFAULT 0,
  is_blocked INTEGER NOT NULL DEFAULT 0,
  blocked_reason TEXT,
  session_external_id TEXT,
  implemented_by_session_id TEXT,
  content_hash TEXT NOT NULL,
  checked INTEGER NOT NULL DEFAULT 0,
  total INTEGER NOT NULL DEFAULT 0,
  version INTEGER NOT NULL DEFAULT 1,
  created_at BIGINT NOT NULL,
  updated_at BIGINT NOT NULL,
  UNIQUE (repository_id, slug)
);

CREATE TABLE IF NOT EXISTS plan_blocks (
  plan_id TEXT NOT NULL REFERENCES plans(id),
  reason TEXT NOT NULL,
  resolution TEXT,
  blocked_at BIGINT NOT NULL,
  unblocked_at BIGINT,
  actor TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS plan_dependencies (
  plan_id TEXT NOT NULL REFERENCES plans(id),
  requires_plan_id TEXT NOT NULL REFERENCES plans(id),
  PRIMARY KEY (plan_id, requires_plan_id)
);

CREATE TABLE IF NOT EXISTS lessons (
  repository_id TEXT NOT NULL REFERENCES repositories(id),
  entry_id TEXT NOT NULL,
  title TEXT NOT NULL,
  category TEXT,
  source_plan_id TEXT,
  content_hash TEXT NOT NULL,
  first_seen_at BIGINT NOT NULL,
  PRIMARY KEY (repository_id, entry_id)
);

CREATE TABLE IF NOT EXISTS plan_status_history (
  seq BIGSERIAL PRIMARY KEY,
  plan_id TEXT NOT NULL REFERENCES plans(id),
  from_status TEXT,
  to_status TEXT NOT NULL,
  actor_type TEXT NOT NULL,
  actor_session_id TEXT,
  command TEXT,
  git_head TEXT,
  note TEXT,
  separation_of_duties_warning INTEGER NOT NULL DEFAULT 0,
  via_shortcut INTEGER NOT NULL DEFAULT 0,
  at BIGINT NOT NULL
);

CREATE TABLE IF NOT EXISTS events (
  seq BIGSERIAL PRIMARY KEY,
  type TEXT NOT NULL,
  payload TEXT NOT NULL,
  at BIGINT NOT NULL
);

-- Exchange v2: addressed conversation between actors. Same rules as the SQLite 0002 script;
-- see the commentary there for the thread/idempotency/rate-ledger reasoning, which carries
-- over unchanged.
CREATE TABLE IF NOT EXISTS exchange_items (
  id TEXT PRIMARY KEY,
  kind TEXT NOT NULL CHECK (kind IN ('question','answer','message','file','status')),
  state TEXT NOT NULL CHECK (state IN ('queued','delivered','claimed','answered','acknowledged','expired','killed')),
  latency_class TEXT NOT NULL CHECK (latency_class IN ('realtime','turn','minutes','human')),
  thread_id TEXT NOT NULL REFERENCES exchange_items(id),
  hop_count INTEGER NOT NULL DEFAULT 0 CHECK (hop_count >= 0),
  sender_actor_type TEXT NOT NULL,
  sender_actor_id TEXT NOT NULL,
  recipient_actor_type TEXT NOT NULL,
  recipient_actor_id TEXT NOT NULL,
  repository_id TEXT REFERENCES repositories(id),
  subject TEXT NOT NULL,
  body TEXT,
  context_refs TEXT,
  purpose TEXT,
  claimed_by_session_id TEXT,
  claimed_at BIGINT,
  created_at BIGINT NOT NULL,
  expires_at BIGINT,
  answered_at BIGINT,
  acknowledged_at BIGINT,
  killed_at BIGINT,
  idempotency_key TEXT,
  blob_path TEXT,
  blob_size BIGINT,
  blob_content_type TEXT,
  blob_sha256 TEXT,
  CHECK (kind != 'question' OR purpose IS NOT NULL),
  CHECK (recipient_actor_type != 'zcode' OR recipient_actor_id != '*' OR repository_id IS NOT NULL),
  CHECK (hop_count <= 3)
);

CREATE UNIQUE INDEX IF NOT EXISTS exchange_items_idempotency_idx
  ON exchange_items(sender_actor_type, sender_actor_id, idempotency_key)
  WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS exchange_items_inbox_idx
  ON exchange_items(recipient_actor_type, recipient_actor_id, repository_id, state);

CREATE INDEX IF NOT EXISTS exchange_items_thread_idx ON exchange_items(thread_id);

CREATE INDEX IF NOT EXISTS exchange_items_expiry_idx ON exchange_items(state, expires_at);

CREATE INDEX IF NOT EXISTS exchange_items_open_idx
  ON exchange_items(kind, state, repository_id, created_at);

CREATE TABLE IF NOT EXISTS exchange_presence (
  actor_type TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  last_seen_at BIGINT NOT NULL,
  latency_class TEXT NOT NULL CHECK (latency_class IN ('realtime','turn','minutes','human')),
  PRIMARY KEY (actor_type, actor_id)
);

CREATE TABLE IF NOT EXISTS exchange_cursors (
  actor_type TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  last_seq BIGINT NOT NULL,
  updated_at BIGINT NOT NULL,
  PRIMARY KEY (actor_type, actor_id)
);

CREATE TABLE IF NOT EXISTS exchange_rate (
  sender_actor_type TEXT NOT NULL,
  sender_actor_id TEXT NOT NULL,
  at BIGINT NOT NULL
);
CREATE INDEX IF NOT EXISTS exchange_rate_sender_idx ON exchange_rate(sender_actor_type, sender_actor_id, at);
