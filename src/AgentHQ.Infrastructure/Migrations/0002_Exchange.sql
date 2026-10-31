-- Exchange v2: addressed conversation between actors.
-- docs/plans/conversation-enhancements-etc.md, phase 1a. Items are rows; a thread is the set of
-- items sharing thread_id (the root carries its own id); blobs are files on disk referenced by
-- the item row. There is no threads table and no blobs table on purpose.

CREATE TABLE IF NOT EXISTS exchange_items (
  id TEXT PRIMARY KEY,
  kind TEXT NOT NULL CHECK (kind IN ('question','answer','message','file','status')),
  state TEXT NOT NULL CHECK (state IN ('queued','delivered','claimed','answered','acknowledged','expired','killed')),
  latency_class TEXT NOT NULL CHECK (latency_class IN ('realtime','turn','minutes','human')),
  -- The root item carries its own id; replies carry the root's id. Root-then-reply is one
  -- statement inside a transaction, so the self-reference holds for every row that is read.
  thread_id TEXT NOT NULL REFERENCES exchange_items(id),
  hop_count INTEGER NOT NULL DEFAULT 0 CHECK (hop_count >= 0),
  sender_actor_type TEXT NOT NULL,
  sender_actor_id TEXT NOT NULL,
  recipient_actor_type TEXT NOT NULL,
  recipient_actor_id TEXT NOT NULL,
  -- Required for role-addressed items (recipient_actor_type = 'zcode', recipient_actor_id = '*'),
  -- per D13. Rows addressed to a concrete zcode session carry it too when the thread has one
  -- (an answer to a session is scoped to the question's repository), but only the role address
  -- routes by it, so only the role address requires it here.
  repository_id TEXT REFERENCES repositories(id),
  subject TEXT NOT NULL,
  body TEXT,
  context_refs TEXT,
  purpose TEXT,
  claimed_by_session_id TEXT,
  claimed_at INTEGER,
  created_at INTEGER NOT NULL,
  expires_at INTEGER,
  answered_at INTEGER,
  acknowledged_at INTEGER,
  killed_at INTEGER,
  idempotency_key TEXT,
  blob_path TEXT,
  blob_size INTEGER,
  blob_content_type TEXT,
  blob_sha256 TEXT,
  CHECK (kind != 'question' OR purpose IS NOT NULL),
  CHECK (recipient_actor_type != 'zcode' OR recipient_actor_id != '*' OR repository_id IS NOT NULL),
  CHECK (hop_count <= 3)
);

-- A repeat post from one sender with the same key is the same item, in one transaction.
CREATE UNIQUE INDEX IF NOT EXISTS exchange_items_idempotency_idx
  ON exchange_items(sender_actor_type, sender_actor_id, idempotency_key)
  WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS exchange_items_inbox_idx
  ON exchange_items(recipient_actor_type, recipient_actor_id, repository_id, state);

CREATE INDEX IF NOT EXISTS exchange_items_thread_idx ON exchange_items(thread_id);

CREATE INDEX IF NOT EXISTS exchange_items_expiry_idx ON exchange_items(state, expires_at);

CREATE INDEX IF NOT EXISTS exchange_items_open_idx
  ON exchange_items(kind, state, repository_id, created_at);

-- Presence is a heartbeat, never a status: there is no offline row. Offline is derived
-- (now - last_seen_at > the configured staleness window).
CREATE TABLE IF NOT EXISTS exchange_presence (
  actor_type TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  last_seen_at INTEGER NOT NULL,
  latency_class TEXT NOT NULL CHECK (latency_class IN ('realtime','turn','minutes','human')),
  PRIMARY KEY (actor_type, actor_id)
);

-- One durable cursor per push/pull consumer: an actor that was offline catches up from here.
CREATE TABLE IF NOT EXISTS exchange_cursors (
  actor_type TEXT NOT NULL,
  actor_id TEXT NOT NULL,
  last_seq INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (actor_type, actor_id)
);

-- One rate-limit row per accepted post; counted inside the post transaction and pruned on
-- write, so the cap is enforced in the service, not by timing guesses in a client.
CREATE TABLE IF NOT EXISTS exchange_rate (
  sender_actor_type TEXT NOT NULL,
  sender_actor_id TEXT NOT NULL,
  at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS exchange_rate_sender_idx ON exchange_rate(sender_actor_type, sender_actor_id, at);
