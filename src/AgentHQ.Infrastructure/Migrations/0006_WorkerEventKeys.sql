-- 0006_WorkerEventKeys.sql — the (worker_id, origin_seq) unique key (distributed part 3,
-- contract rule 2). The global `events` table carries no worker columns (the merged stream is
-- provider-neutral), so this table is the durable form of rule 2's uniqueness: one row per
-- applied spoke event, inserted in the same transaction as the global event row and the cursor
-- update. Under concurrent batches for one worker, the second insert conflicts, the event is
-- skipped, and the batch reports trimmed — the cursor alone cannot do that safely.
CREATE TABLE worker_event_keys (
  worker_id TEXT NOT NULL REFERENCES workers(id),
  origin_seq BIGINT NOT NULL,
  PRIMARY KEY (worker_id, origin_seq)
);
