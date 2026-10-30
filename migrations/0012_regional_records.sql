-- MigrationRunner rebuilds capture_sessions and mentor_runs within this same transaction,
-- widening only their closed region CHECK and restoring their existing indexes/triggers.
-- FK enforcement is disabled before BEGIN and checked before COMMIT, then restored.
-- Keep the legacy id for old local SQL/audit extensions; region is the new unique identity.
CREATE TABLE achievement_settings_regional (
    id                       INTEGER NOT NULL PRIMARY KEY,
    region                   TEXT NOT NULL UNIQUE CHECK (region IN ('CN', 'TC', 'GLOBAL', 'UNKNOWN')),
    goal_count               INTEGER NOT NULL DEFAULT 2000 CHECK (goal_count >= 1),
    baseline_completed_count INTEGER NOT NULL DEFAULT 0 CHECK (baseline_completed_count >= 0),
    baseline_effective_at    TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', baseline_effective_at) = baseline_effective_at),
    updated_at_utc           TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);
INSERT INTO achievement_settings_regional
    (id, region, goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc)
SELECT id, 'CN', goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc
FROM achievement_settings;
DROP TABLE achievement_settings;
ALTER TABLE achievement_settings_regional RENAME TO achievement_settings;

INSERT OR IGNORE INTO achievement_settings
    (region, goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc)
VALUES ('CN', 2000, 0, $migration_now_utc, $migration_now_utc),
       ('TC', 2000, 0, $migration_now_utc, $migration_now_utc);
CREATE INDEX ix_runs_region ON mentor_runs(region, soft_deleted, entered_at_utc);
