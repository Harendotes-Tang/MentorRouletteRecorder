-- Controlled recycle-bin cleanup. Ordinary revision writes remain append-only.
ALTER TABLE mentor_runs ADD COLUMN deleted_at_utc TEXT NULL
    CHECK (deleted_at_utc IS NULL OR strftime('%Y-%m-%dT%H:%M:%fZ', deleted_at_utc) = deleted_at_utc);
-- The runner binds its injected clock, rather than guessing an old deletion date.
UPDATE mentor_runs SET deleted_at_utc = $migration_now_utc WHERE soft_deleted = 1;
CREATE INDEX ix_runs_retention ON mentor_runs(soft_deleted, deleted_at_utc);

CREATE TRIGGER trg_runs_deleted_at_insert AFTER INSERT ON mentor_runs
WHEN NEW.soft_deleted = 1 AND NEW.deleted_at_utc IS NULL
BEGIN UPDATE mentor_runs SET deleted_at_utc = NEW.updated_at_utc WHERE run_id = NEW.run_id; END;
CREATE TRIGGER trg_runs_deleted_at_update AFTER UPDATE OF soft_deleted ON mentor_runs
WHEN OLD.soft_deleted != NEW.soft_deleted
BEGIN UPDATE mentor_runs SET deleted_at_utc = CASE WHEN NEW.soft_deleted = 1 THEN NEW.updated_at_utc ELSE NULL END
      WHERE run_id = NEW.run_id; END;

CREATE TABLE history_retention_settings (
    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
    retention_days INTEGER NOT NULL DEFAULT 30 CHECK (retention_days BETWEEN 0 AND 36500)
);
INSERT INTO history_retention_settings(id, retention_days) VALUES (1, 30);

-- Authorization lives only for the duration of a purge transaction and targets exact IDs.
CREATE TABLE run_purge_authorizations (run_id TEXT NOT NULL PRIMARY KEY);
DROP TRIGGER trg_run_revisions_no_delete;
CREATE TRIGGER trg_run_revisions_no_delete BEFORE DELETE ON run_revisions
WHEN NOT EXISTS (SELECT 1 FROM run_purge_authorizations WHERE run_id = OLD.run_id)
BEGIN SELECT RAISE(ABORT, 'run_revisions is append-only: DELETE is forbidden'); END;

-- Fingerprints contain no notes, reflections, snapshots, or original import fields.
CREATE TABLE ipc_request_tombstones (
    request_id TEXT NOT NULL PRIMARY KEY,
    message_type TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    preview_id TEXT NULL,
    purged_at_utc TEXT NOT NULL
);
CREATE INDEX ix_purged_preview ON ipc_request_tombstones(preview_id) WHERE preview_id IS NOT NULL;
CREATE TABLE purged_run_tombstones (
    run_id TEXT NOT NULL PRIMARY KEY,
    source_fingerprint TEXT NULL,
    purged_at_utc TEXT NOT NULL
);
CREATE UNIQUE INDEX ix_purged_import_fingerprint ON purged_run_tombstones(source_fingerprint)
    WHERE source_fingerprint IS NOT NULL;
CREATE TRIGGER trg_runs_no_purged_reinsert BEFORE INSERT ON mentor_runs
WHEN EXISTS (SELECT 1 FROM purged_run_tombstones WHERE run_id=NEW.run_id)
BEGIN SELECT RAISE(ABORT, 'a permanently deleted run cannot be recreated'); END;
CREATE TABLE pending_image_cleanup (
    run_id TEXT NOT NULL PRIMARY KEY,
    queued_at_utc TEXT NOT NULL,
    last_attempt_order INTEGER NOT NULL DEFAULT 0 CHECK (last_attempt_order >= 0)
);
