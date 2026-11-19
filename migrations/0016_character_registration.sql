-- An upgraded unnamed profile can be confirmed once, on a recognised login.
CREATE TABLE character_profile_registrations (
    profile_id INTEGER PRIMARY KEY REFERENCES character_profiles(profile_id) ON DELETE CASCADE,
    identity_id INTEGER UNIQUE REFERENCES character_identities(identity_id) ON DELETE SET NULL
);

INSERT INTO character_profile_registrations (profile_id)
SELECT p.profile_id FROM character_profiles p
WHERE p.region = 'CN' AND p.display_name = '' AND
    (p.goal_count != 2000 OR p.baseline_completed_count != 0 OR
     EXISTS (SELECT 1 FROM mentor_runs r WHERE r.profile_id = p.profile_id) OR
     EXISTS (SELECT 1 FROM character_identities i WHERE i.profile_id = p.profile_id));
