-- Only observations recorded with a scoped player round carry its identity.
-- Existing research observations remain available for the maintainer's full-ledger export.
ALTER TABLE candidate_observations ADD COLUMN player_round_id TEXT NULL;
CREATE INDEX ix_candidate_player_round ON candidate_observations(player_round_id, profile_id)
    WHERE player_round_id IS NOT NULL;
