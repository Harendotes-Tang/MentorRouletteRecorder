-- Telling the player's characters apart (docs/data-model.md, character_identities).
--
-- Since schema 14 a run belongs to a character profile, and the player said by hand which profile
-- is being played. The Collector can now tell the logged-in character apart in the game traffic;
-- each character it has seen gets a row here, and the row says which profile that character is.
--
-- A row holds no game identifier and no name. digest is an HMAC-SHA256 of the identifier under a
-- key that exists only on this machine, encrypted for the Windows user (key_id is the key's own
-- fingerprint). A copy of this database therefore says how many characters were seen and nothing
-- about which ones; rows written under a key that is gone match nothing and are removed.
--
-- profile_id NULL with ignored = 0 is a character the player has not been asked about, or has not
-- answered for, yet; ignored = 1 is one the player chose to keep no progress for. Several rows may
-- point at one profile: the same character can come with another identifier.
CREATE TABLE character_identities (
    identity_id       INTEGER PRIMARY KEY AUTOINCREMENT,
    region            TEXT NOT NULL CHECK (region IN ('CN', 'TC', 'GLOBAL', 'UNKNOWN')),
    key_id            TEXT NOT NULL CHECK (length(key_id) BETWEEN 1 AND 16),
    digest            TEXT NOT NULL CHECK (length(digest) = 32),
    profile_id        INTEGER NULL REFERENCES character_profiles(profile_id),
    ignored           INTEGER NOT NULL DEFAULT 0 CHECK (ignored IN (0, 1)),
    first_seen_at_utc TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', first_seen_at_utc) = first_seen_at_utc),
    last_seen_at_utc  TEXT NOT NULL
        CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', last_seen_at_utc) = last_seen_at_utc),
    CHECK (profile_id IS NULL OR ignored = 0)
);
CREATE UNIQUE INDEX ux_character_identities_digest ON character_identities(region, key_id, digest);
CREATE INDEX ix_character_identities_profile ON character_identities(profile_id) WHERE profile_id IS NOT NULL;
CREATE TRIGGER trg_character_identities_region_insert BEFORE INSERT ON character_identities
WHEN NEW.profile_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM character_profiles p WHERE p.profile_id = NEW.profile_id AND p.region = NEW.region)
BEGIN SELECT RAISE(ABORT, 'a character identity can only be bound to a profile of its own region'); END;
CREATE TRIGGER trg_character_identities_region_update BEFORE UPDATE OF profile_id, region ON character_identities
WHEN NEW.profile_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM character_profiles p WHERE p.profile_id = NEW.profile_id AND p.region = NEW.region)
BEGIN SELECT RAISE(ABORT, 'a character identity can only be bound to a profile of its own region'); END;

-- The character that was logged in when the run was matched; NULL for every run recorded without
-- identification (all runs so far, manual entries, imports, regions that are switched by hand).
-- It is what lets the runs of a character the player has not answered for yet find their profile
-- once the answer comes. Never shown, never exported.
ALTER TABLE mentor_runs ADD COLUMN identity_id INTEGER NULL REFERENCES character_identities(identity_id);
CREATE INDEX ix_runs_identity ON mentor_runs(identity_id) WHERE identity_id IS NOT NULL;
