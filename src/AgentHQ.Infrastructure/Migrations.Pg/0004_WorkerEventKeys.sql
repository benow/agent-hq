-- 0004_WorkerEventKeys.sql — PostgreSQL twin of Migrations/0006_WorkerEventKeys.sql (same
-- rules, each dialect has its own script). The (worker_id, origin_seq) unique key is contract
-- rule 2's durable form: inserted in the same transaction as the global event row and the
-- cursor update, so concurrent batches for one worker cannot double-apply.
CREATE TABLE worker_event_keys (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  origin_seq BIGINT NOT NULL,
  PRIMARY KEY (worker_id, origin_seq)
);
