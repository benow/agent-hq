-- 0004_Auth.sql — identities, hashed bearer tokens and the auth audit trail (distributed
-- part 2). Passwords are PBKDF2 hashes (Core.Auth.PasswordHasher's stored format); tokens are
-- stored only as SHA-256 hashes of the issued value. See Migrations.Pg/0002_Auth.sql — same
-- rules, each dialect has its own script.

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
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  at BIGINT NOT NULL,
  kind TEXT NOT NULL,
  username TEXT,
  route TEXT,
  source TEXT,
  detail TEXT
);
CREATE INDEX auth_audit_at_idx ON auth_audit(at);
