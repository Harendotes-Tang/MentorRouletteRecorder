-- MentorRecorder schema version 3.
--
-- 导随心得 / run reflections. One optional diary entry per run: a mood and a short piece of
-- text the user typed after a duty ended.
--
-- It lives in its own table rather than as two more columns on mentor_runs, and that is a
-- deliberate boundary: a reflection is the user's own writing, not a captured fact about the
-- run. It therefore never bumps mentor_runs.revision, never appends to run_revisions, and
-- needs no reason -- there is nothing to audit, because nothing observed was corrected
-- (docs/data-model.md section 1.3).
--
-- The CHECK constraints are the enforcement, not a convention: the Collector trims the text
-- and deletes the row when nothing is left, so a stored row always carries between 1 and
-- 2000 characters, and the mood is always one of the three the contract declares.
--
-- ON DELETE CASCADE is declaratively correct rather than reachable: this code base has no
-- hard delete of a run anywhere, and removal is mentor_runs.soft_deleted. A soft-deleted run
-- keeps its reflection, and reading it back after a restore is the point.

CREATE TABLE run_reflections (
    run_id          TEXT NOT NULL PRIMARY KEY
                         REFERENCES mentor_runs(run_id) ON DELETE CASCADE,
    mood            TEXT NOT NULL CHECK (mood IN ('good', 'ok', 'bad')),
    text            TEXT NOT NULL CHECK (length(text) BETWEEN 1 AND 2000),
    created_at_utc  TEXT NOT NULL
                         CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) = created_at_utc),
    updated_at_utc  TEXT NOT NULL
                         CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);

-- The dashboard asks for the most recently edited reflections, newest first.
CREATE INDEX ix_run_reflections_updated ON run_reflections(updated_at_utc DESC);
