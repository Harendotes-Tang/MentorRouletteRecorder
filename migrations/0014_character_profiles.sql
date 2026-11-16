-- Achievement progress per character (docs/data-model.md, character_profiles).
--
-- Until schema 13 one goal and one baseline existed per region. A region may now hold several
-- profiles, one per character the player names; each has its own goal and baseline, and a run
-- belongs to at most one of them. Exactly one profile of a region is the current one: the profile
-- a newly recorded run is filed under.
--
-- The ids are AUTOINCREMENT so an id is never handed out twice: run_revisions keeps the profile a
-- run used to belong to, and a reused id would name somebody else's character on an undo.
--
-- display_name is typed by the player. It is never read from the game (docs/privacy-boundary.md);
-- the empty name marks the profile the software created by itself, which the Desktop words.
CREATE TABLE character_profiles (
    profile_id               INTEGER PRIMARY KEY AUTOINCREMENT,
    region                   TEXT NOT NULL CHECK (region IN ('CN', 'TC', 'GLOBAL', 'UNKNOWN')),
    display_name             TEXT NOT NULL DEFAULT '' CHECK (length(display_name) <= 24),
    is_current               INTEGER NOT NULL DEFAULT 0 CHECK (is_current IN (0, 1)),
    goal_count               INTEGER NOT NULL DEFAULT 2000 CHECK (goal_count >= 1),
    baseline_completed_count INTEGER NOT NULL DEFAULT 0 CHECK (baseline_completed_count >= 0),
    baseline_effective_at    TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', baseline_effective_at) = baseline_effective_at),
    created_at_utc           TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', created_at_utc) = created_at_utc),
    updated_at_utc           TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', updated_at_utc) = updated_at_utc)
);
CREATE UNIQUE INDEX ux_character_profiles_current ON character_profiles(region) WHERE is_current = 1;
CREATE UNIQUE INDEX ux_character_profiles_name ON character_profiles(region, display_name);
CREATE TRIGGER trg_character_profiles_region_fixed BEFORE UPDATE OF region ON character_profiles
WHEN OLD.region != NEW.region
BEGIN SELECT RAISE(ABORT, 'a character profile cannot move to another region'); END;

-- Every region's goal, baseline and whole history move into one profile, so each number the
-- player saw before the upgrade is the number shown after it.
INSERT INTO character_profiles
    (region, display_name, is_current, goal_count, baseline_completed_count, baseline_effective_at,
     created_at_utc, updated_at_utc)
SELECT region, '', 1, goal_count, baseline_completed_count, baseline_effective_at,
       $migration_now_utc, updated_at_utc
FROM achievement_settings ORDER BY id;
-- A region that has runs but never had a settings row was shown with the defaults.
INSERT INTO character_profiles
    (region, display_name, is_current, goal_count, baseline_completed_count, baseline_effective_at,
     created_at_utc, updated_at_utc)
SELECT DISTINCT r.region, '', 1, 2000, 0, $migration_now_utc, $migration_now_utc, $migration_now_utc
FROM mentor_runs r
WHERE NOT EXISTS (SELECT 1 FROM character_profiles p WHERE p.region = r.region);

-- NULL is "not assigned to a character": the run counts in the region's listings and in no
-- character's progress. Only the owning column changes here: no revision, no update time.
ALTER TABLE mentor_runs ADD COLUMN profile_id INTEGER NULL REFERENCES character_profiles(profile_id);
UPDATE mentor_runs
SET profile_id = (SELECT p.profile_id FROM character_profiles p WHERE p.region = mentor_runs.region);
CREATE INDEX ix_runs_profile ON mentor_runs(profile_id, soft_deleted, entered_at_utc);
CREATE TRIGGER trg_runs_profile_region_insert BEFORE INSERT ON mentor_runs
WHEN NEW.profile_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM character_profiles p WHERE p.profile_id = NEW.profile_id AND p.region = NEW.region)
BEGIN SELECT RAISE(ABORT, 'a run can only belong to a character profile of its own region'); END;
CREATE TRIGGER trg_runs_profile_region_update BEFORE UPDATE OF profile_id, region ON mentor_runs
WHEN NEW.profile_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM character_profiles p WHERE p.profile_id = NEW.profile_id AND p.region = NEW.region)
BEGIN SELECT RAISE(ABORT, 'a run can only belong to a character profile of its own region'); END;

-- The baseline change history follows its settings: one list per profile.
INSERT INTO application_settings (key, value_json, updated_at_utc)
SELECT 'achievement.baseline_history.profile.' || p.profile_id, s.value_json, s.updated_at_utc
FROM application_settings s
JOIN character_profiles p
  ON s.key = CASE p.region WHEN 'CN' THEN 'achievement.baseline_history'
                           ELSE 'achievement.baseline_history.' || p.region END;
DELETE FROM application_settings
WHERE key IN ('achievement.baseline_history', 'achievement.baseline_history.TC',
              'achievement.baseline_history.GLOBAL', 'achievement.baseline_history.UNKNOWN');

DROP TABLE achievement_settings;
