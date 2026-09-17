-- MentorRecorder schema version 2.
--
-- Two additive columns on mentor_runs. Migration 0001 is never edited; its recorded
-- checksum is what makes an unexpected edit fail the integrity check.
--
--   pending_review  Crash recovery marks a run it closed as INTERRUPTED with this flag so
--                   the UI can list it under "待复核" without inferring the state from
--                   detection_confidence. The Collector never clears it on its own: only a
--                   human CorrectRun / SoftDeleteRun does (docs/state-machine.md 3.9).
--
--   note            Free-text note a human may attach through CorrectRun. It is user-typed
--                   local text; it is never derived from captured traffic and never leaves
--                   this machine (docs/manual-correction.md 7).
--
-- Both are added with a non-null default so that existing rows stay valid, and neither
-- carries a UNIQUE or PRIMARY KEY constraint, which ALTER TABLE ADD COLUMN forbids.

ALTER TABLE mentor_runs
    ADD COLUMN pending_review INTEGER NOT NULL DEFAULT 0 CHECK (pending_review IN (0, 1));

ALTER TABLE mentor_runs
    ADD COLUMN note TEXT NULL;

-- Backfill: a run that a previous build closed as INTERRUPTED with LOW confidence and that
-- nobody has corrected is exactly the population the flag describes.
UPDATE mentor_runs
   SET pending_review = 1
 WHERE result = 'INTERRUPTED'
   AND detection_confidence = 'LOW'
   AND manually_corrected = 0
   AND soft_deleted = 0;

CREATE INDEX ix_runs_pending_review ON mentor_runs(pending_review) WHERE pending_review = 1;
