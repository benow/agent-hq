-- Phase 7 (carried fix #1): a short-cut verification records the intermediate → implemented row
-- itself, attributed to the verifying session. Marking it makes "the verifier recorded this, not
-- an implementer" legible from data instead of inferred from the command string.

ALTER TABLE plan_status_history ADD COLUMN via_shortcut INTEGER NOT NULL DEFAULT 0;
