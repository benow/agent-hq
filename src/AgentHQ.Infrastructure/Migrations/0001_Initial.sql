CREATE TABLE IF NOT EXISTS repositories (
  id TEXT PRIMARY KEY,
  root_path TEXT NOT NULL,
  provider TEXT NOT NULL,
  external_id TEXT,
  plans_dir TEXT NOT NULL DEFAULT 'docs/plans',
  is_default INTEGER NOT NULL DEFAULT 0,
  first_seen_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sessions (
  provider TEXT NOT NULL,
  external_id TEXT NOT NULL,
  repository_id TEXT,
  title TEXT NOT NULL,
  task_type TEXT NOT NULL,
  last_outcome TEXT,
  external_time_created INTEGER NOT NULL,
  external_time_updated INTEGER NOT NULL,
  first_user_message_excerpt TEXT,
  archived INTEGER NOT NULL DEFAULT 0,
  deleted INTEGER NOT NULL DEFAULT 0,
  ingested_at INTEGER NOT NULL,
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
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  UNIQUE (repository_id, slug)
);

CREATE TABLE IF NOT EXISTS plan_blocks (
  plan_id TEXT NOT NULL REFERENCES plans(id),
  reason TEXT NOT NULL,
  resolution TEXT,
  blocked_at INTEGER NOT NULL,
  unblocked_at INTEGER,
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
  first_seen_at INTEGER NOT NULL,
  PRIMARY KEY (repository_id, entry_id)
);

CREATE TABLE IF NOT EXISTS plan_status_history (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  plan_id TEXT NOT NULL REFERENCES plans(id),
  from_status TEXT,
  to_status TEXT NOT NULL,
  actor_type TEXT NOT NULL,
  actor_session_id TEXT,
  command TEXT,
  git_head TEXT,
  note TEXT,
  separation_of_duties_warning INTEGER NOT NULL DEFAULT 0,
  at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  type TEXT NOT NULL,
  payload TEXT NOT NULL,
  at INTEGER NOT NULL
);
