-- 0002_Auth.sql (PostgreSQL) — the hub-side auth schema (distributed part 2): identities,
-- hashed bearer tokens, audit trail. Same CHECK rules as the SQLite 0004 script; BIGSERIAL
-- for the audit sequence. Journaled by filename in AgentHqPgSchemaVersions; never edit an
-- applied script.

CREATE TABLE identities (
  id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,
  role TEXT NOT NULL CHECK (role IN ('admin','agent','worker','ci')),
  password_hash TEXT NOT NULL,
  created_at BIGINT NOT NULL
);

CREATE TABLE auth_tokens (
  id TEXT PRIMARY KEY,
  identity_id TEXT NOT NULL REFERENCES identities(id),
  token_hash TEXT NOT NULL UNIQUE,
  label TEXT,
  created_at BIGINT NOT NULL,
  revoked_at BIGINT
);

CREATE TABLE auth_audit (
  seq BIGSERIAL PRIMARY KEY,
  at BIGINT NOT NULL,
  kind TEXT NOT NULL,
  username TEXT,
  route TEXT,
  source TEXT,
  detail TEXT
);
CREATE INDEX auth_audit_at_idx ON auth_audit(at);
