-- Local import provenance and durable commit receipts. Source record time is not gameplay time.
CREATE TABLE run_import_metadata (
    run_id TEXT NOT NULL PRIMARY KEY REFERENCES mentor_runs(run_id) ON DELETE RESTRICT,
    source_kind TEXT NOT NULL,
    source_name TEXT NULL,
    source_recorded_at TEXT NULL,
    source_recorded_at_utc TEXT NULL,
    imported_at_utc TEXT NOT NULL,
    source_fingerprint TEXT NOT NULL UNIQUE,
    mentor_confirmed INTEGER NOT NULL DEFAULT 1 CHECK (mentor_confirmed IN (0, 1))
);
CREATE TABLE run_import_batches (
    preview_id TEXT NOT NULL PRIMARY KEY,
    request_id TEXT NOT NULL UNIQUE,
    selection_fingerprint TEXT NOT NULL,
    response_json TEXT NOT NULL,
    committed_at_utc TEXT NOT NULL
);

-- Older personal diaries retain their selected mood. Missing source moods stay unknown.
CREATE TABLE run_reflections_import (
    run_id TEXT NOT NULL PRIMARY KEY REFERENCES mentor_runs(run_id) ON DELETE CASCADE,
    mood TEXT NOT NULL CHECK (mood IN ('good', 'ok', 'bad', 'unknown')),
    text TEXT NOT NULL CHECK (length(text) BETWEEN 1 AND 2000),
    created_at_utc TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) = created_at_utc),
    updated_at_utc TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);
INSERT INTO run_reflections_import SELECT * FROM run_reflections;
DROP TABLE run_reflections;
ALTER TABLE run_reflections_import RENAME TO run_reflections;
CREATE INDEX ix_run_reflections_updated ON run_reflections(updated_at_utc DESC);
CREATE TRIGGER reflection_unknown_import_insert BEFORE INSERT ON run_reflections
WHEN NEW.mood = 'unknown' AND NOT EXISTS (SELECT 1 FROM mentor_runs WHERE run_id = NEW.run_id AND source = 'IMPORT')
BEGIN SELECT RAISE(ABORT, 'unknown reflection mood requires imported source'); END;
CREATE TRIGGER reflection_unknown_import_update BEFORE UPDATE ON run_reflections
WHEN NEW.mood = 'unknown' AND NOT EXISTS (SELECT 1 FROM mentor_runs WHERE run_id = NEW.run_id AND source = 'IMPORT')
BEGIN SELECT RAISE(ABORT, 'unknown reflection mood requires imported source'); END;
