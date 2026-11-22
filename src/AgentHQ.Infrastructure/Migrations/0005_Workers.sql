-- 0005_Workers.sql — workers, the command queue, pairing nonces, per-worker projections
-- (distributed part 3). See Migrations.Pg/0003_Workers.sql — same rules, each dialect has its
-- own script. The (worker_id, origin_seq) uniqueness lives in the service rule (three-case
-- ordering); next_expected_seq restates exactly that service rule, nothing stronger (lessons.md
-- 2026-10-24). Row models: AgentHQ.Models.WorkerRow et al; column aliases in Columns.cs.

CREATE TABLE workers (
  id TEXT PRIMARY KEY,
  identity_id TEXT NOT NULL UNIQUE REFERENCES identities(id),
  name TEXT NOT NULL DEFAULT '',
  machine TEXT NOT NULL DEFAULT '',
  version TEXT NOT NULL DEFAULT '',
  capabilities TEXT NOT NULL DEFAULT '[]',
  registered_at BIGINT NOT NULL,
  last_heartbeat_at BIGINT,
  last_accepted_seq BIGINT NOT NULL DEFAULT 0,
  next_expected_seq BIGINT NOT NULL DEFAULT 0
);

CREATE TABLE worker_repos (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  repo_name TEXT NOT NULL UNIQUE,
  root_path TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (worker_id, repo_name)
);

CREATE TABLE worker_plans (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  id TEXT NOT NULL,
  slug TEXT NOT NULL DEFAULT '',
  name TEXT NOT NULL DEFAULT '',
  repository_id TEXT NOT NULL DEFAULT '',
  file_path TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL DEFAULT 'submitted',
  priority BIGINT NOT NULL DEFAULT 0,
  is_blocked BIGINT NOT NULL DEFAULT 0,
  blocked_reason TEXT,
  session_external_id TEXT,
  implemented_by_session_id TEXT,
  content_hash TEXT NOT NULL DEFAULT '',
  checked BIGINT NOT NULL DEFAULT 0,
  total BIGINT NOT NULL DEFAULT 0,
  version BIGINT NOT NULL DEFAULT 1,
  created_at BIGINT NOT NULL DEFAULT 0,
  updated_at BIGINT NOT NULL DEFAULT 0,
  markdown TEXT NOT NULL DEFAULT '',
  PRIMARY KEY (worker_id, id)
);
CREATE INDEX worker_plans_slug_idx ON worker_plans(worker_id, slug);

CREATE TABLE worker_history (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  seq BIGINT NOT NULL,
  plan_id TEXT NOT NULL DEFAULT '',
  from_status TEXT,
  to_status TEXT NOT NULL DEFAULT '',
  actor_type TEXT NOT NULL DEFAULT '',
  actor_session_id TEXT,
  command TEXT,
  git_head TEXT,
  note TEXT,
  separation_of_duties_warning BIGINT NOT NULL DEFAULT 0,
  via_shortcut BIGINT NOT NULL DEFAULT 0,
  at BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (worker_id, seq)
);

CREATE TABLE worker_sessions (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  provider TEXT NOT NULL DEFAULT '',
  external_id TEXT NOT NULL DEFAULT '',
  repository_id TEXT,
  title TEXT NOT NULL DEFAULT '',
  task_type TEXT NOT NULL DEFAULT '',
  last_outcome TEXT,
  external_time_created BIGINT NOT NULL DEFAULT 0,
  external_time_updated BIGINT NOT NULL DEFAULT 0,
  first_user_message_excerpt TEXT,
  archived BIGINT NOT NULL DEFAULT 0,
  deleted BIGINT NOT NULL DEFAULT 0,
  ingested_at BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (worker_id, provider, external_id)
);

CREATE TABLE worker_lessons (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  entry_id TEXT NOT NULL,
  title TEXT NOT NULL DEFAULT '',
  category TEXT,
  source_plan_id TEXT,
  content_hash TEXT NOT NULL DEFAULT '',
  first_seen_at BIGINT NOT NULL DEFAULT 0,
  PRIMARY KEY (worker_id, entry_id)
);

CREATE TABLE worker_commands (
  id TEXT PRIMARY KEY,
  worker_id TEXT NOT NULL REFERENCES workers(id),
  kind TEXT NOT NULL,
  repo TEXT,
  actor TEXT NOT NULL DEFAULT '',
  payload TEXT NOT NULL DEFAULT '{}',
  issued_at BIGINT NOT NULL,
  lease_until BIGINT,
  result_ok BIGINT,
  result_error TEXT,
  result_hash TEXT,
  finished_at BIGINT
);
CREATE INDEX worker_commands_queue_idx ON worker_commands(worker_id, lease_until, issued_at);

CREATE TABLE worker_pairing (
  nonce TEXT PRIMARY KEY,
  created_at BIGINT NOT NULL,
  expires_at BIGINT NOT NULL,
  state TEXT NOT NULL DEFAULT 'pending' CHECK (state IN ('pending','approved','consumed','expired')),
  identity_id TEXT,
  token_raw TEXT,
  token_id TEXT
);

CREATE TABLE hub_state (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
