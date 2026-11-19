using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Reads and writes <c>character_identities</c> and the <c>identity_id</c> of a run.
///
/// The digest goes in and is compared here; it never comes back out. Nothing that reads this table
/// for the pipe, a log or an export can carry it, because no method returns it.
/// </summary>
public sealed class CharacterIdentityRepository
{
    private const string Columns = "identity_id, region, profile_id, ignored, first_seen_at_utc, last_seen_at_utc, " +
        "(SELECT r.profile_id FROM character_profile_registrations r WHERE r.identity_id = character_identities.identity_id)";

    // When the offered character is declined, ask about the oldest unanswered character in
    // the same region. An empty queue leaves the slot open for a subsequent login.
    private const string ReplaceRegistrationCandidate = """
        UPDATE character_profile_registrations SET identity_id =
        (SELECT i.identity_id FROM character_identities i
         JOIN character_profiles p ON p.region = i.region
         WHERE p.profile_id = character_profile_registrations.profile_id
           AND i.profile_id IS NULL AND i.ignored = 0
           AND i.identity_id IS NOT character_profile_registrations.identity_id
         ORDER BY i.identity_id LIMIT 1)
        """;

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to stamp writes.</param>
    public CharacterIdentityRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>Reads one identity, or null when it does not exist.</summary>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterIdentity? Find(long identityId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(transaction, $"SELECT {Columns} FROM character_identities WHERE identity_id = $id;");
        command.Parameters.AddWithValue("$id", identityId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>The identity a digest was recorded as in a region, or null for one never seen.</summary>
    /// <param name="region">Region the character is logged in to.</param>
    /// <param name="keyId">Fingerprint of the key the digest was made with.</param>
    /// <param name="digest">Keyed digest of the character's identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterIdentity? FindByDigest(Region region, string keyId, string digest, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(
            transaction,
            $"SELECT {Columns} FROM character_identities WHERE region = $region AND key_id = $key AND digest = $digest;");
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.Parameters.AddWithValue("$key", keyId);
        command.Parameters.AddWithValue("$digest", digest);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Every identity, or one region's: CN, then TC, then the other regions; inside a region the
    /// ones waiting for an answer first, then in the order they were first seen. Self-gating when
    /// no transaction is supplied.
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    /// <param name="region">Region to list, or null for all of them.</param>
    public IReadOnlyList<CharacterIdentity> List(SqliteTransaction? transaction = null, Region? region = null) =>
        transaction is null ? _database.Read(_ => ListCore(null, region)) : ListCore(transaction, region);

    private IReadOnlyList<CharacterIdentity> ListCore(SqliteTransaction? transaction, Region? region)
    {
        using var command = Command(
            transaction,
            $"SELECT {Columns} FROM character_identities " +
            (region is null ? string.Empty : "WHERE region = $region ") +
            "ORDER BY CASE region WHEN 'CN' THEN 0 WHEN 'TC' THEN 1 ELSE 2 END, region, ignored, identity_id;");
        if (region is { } owner)
        {
            command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(owner));
        }

        using var reader = command.ExecuteReader();
        var identities = new List<CharacterIdentity>();
        while (reader.Read())
        {
            identities.Add(Read(reader));
        }

        return identities;
    }

    /// <summary>Records a character seen for the first time and returns it as stored.</summary>
    /// <param name="region">Region the character is logged in to.</param>
    /// <param name="keyId">Fingerprint of the key the digest was made with.</param>
    /// <param name="digest">Keyed digest of the character's identifier.</param>
    /// <param name="profileId">Profile it is bound to straight away, or null to wait for an answer.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterIdentity Insert(
        Region region, string keyId, string digest, long? profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        using var command = Command(
            transaction,
            "INSERT INTO character_identities (region, key_id, digest, profile_id, ignored, first_seen_at_utc, last_seen_at_utc) " +
            "VALUES ($region, $key, $digest, $profile, 0, $now, $now); SELECT last_insert_rowid();");
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.Parameters.AddWithValue("$key", keyId);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(now));
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return new CharacterIdentity(id, region, profileId, Ignored: false, now, now);
    }

    /// <summary>Whether the upgraded profile has an open slot for a recognised character.</summary>
    internal bool NeedsRegistration(long profileId, SqliteTransaction transaction)
    {
        using var command = Command(transaction, """
            SELECT 1 FROM character_profile_registrations WHERE profile_id = $profile AND identity_id IS NULL;
            """);
        command.Parameters.AddWithValue("$profile", profileId);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Offers the upgraded profile for confirmation at the next dashboard read.</summary>
    internal bool OfferRegistration(long profileId, long identityId, SqliteTransaction transaction)
    {
        using var command = Command(transaction, """
            UPDATE character_profile_registrations SET identity_id = $identity
            WHERE profile_id = $profile AND identity_id IS NULL;
            """);
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$identity", identityId);
        return command.ExecuteNonQuery() != 0;
    }

    /// <summary>Offers a newly seen character only when the original profile has no known binding to wait for.</summary>
    internal void OfferUnboundRegistration(Region region, long identityId, SqliteTransaction transaction)
    {
        using var command = Command(transaction, """
            UPDATE character_profile_registrations SET identity_id = $identity
            WHERE identity_id IS NULL AND profile_id IN (SELECT profile_id FROM character_profiles WHERE region = $region)
              AND NOT EXISTS (SELECT 1 FROM character_identities i WHERE i.profile_id = character_profile_registrations.profile_id);
            """);
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.Parameters.AddWithValue("$identity", identityId);
        command.ExecuteNonQuery();
    }

    /// <summary>Closes the upgraded profile's registration after the user confirms it.</summary>
    internal void FinishRegistration(long profileId, SqliteTransaction transaction)
    {
        using var command = Command(transaction,
            "DELETE FROM character_profile_registrations WHERE profile_id = $profile;");
        command.Parameters.AddWithValue("$profile", profileId);
        command.ExecuteNonQuery();
    }

    /// <summary>Notes that a known character has logged in again.</summary>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Touch(long identityId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(
            transaction, "UPDATE character_identities SET last_seen_at_utc = $now WHERE identity_id = $id;");
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)));
        command.Parameters.AddWithValue("$id", identityId);
        command.ExecuteNonQuery();
    }

    /// <summary>Writes the player's answer about one identity.</summary>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="profileId">Profile it is, or null.</param>
    /// <param name="ignored">True to keep no progress for it; never together with a profile.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void SetAnswer(long identityId, long? profileId, bool ignored, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(
            transaction,
            "UPDATE character_identities SET profile_id = $profile, ignored = $ignored WHERE identity_id = $id;");
        command.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ignored", ignored ? 1 : 0);
        command.Parameters.AddWithValue("$id", identityId);
        command.ExecuteNonQuery();

        using var registration = Command(transaction, ReplaceRegistrationCandidate +
            " WHERE identity_id = $id AND profile_id IS NOT $profile;");
        registration.Parameters.AddWithValue("$id", identityId);
        registration.Parameters.AddWithValue("$profile", (object?)profileId ?? DBNull.Value);
        registration.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns every identity bound to a profile to "not answered", and says how many there were.
    /// Runs keep their owner.
    /// </summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public int ReleaseProfile(long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        // Pick a replacement before unbinding: none of the characters just released should
        // immediately be proposed again in place of the original binding.
        using var registration = Command(transaction, ReplaceRegistrationCandidate +
            " WHERE profile_id = $profile AND (identity_id IS NULL OR identity_id IN " +
            "(SELECT identity_id FROM character_identities WHERE profile_id = $profile));");
        registration.Parameters.AddWithValue("$profile", profileId);
        registration.ExecuteNonQuery();

        using var command = Command(
            transaction, "UPDATE character_identities SET profile_id = NULL WHERE profile_id = $profile;");
        command.Parameters.AddWithValue("$profile", profileId);
        return command.ExecuteNonQuery();
    }

    /// <summary>How many identities each profile has bound to it.</summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public IReadOnlyDictionary<long, int> CountByProfile(SqliteTransaction? transaction = null) =>
        transaction is null ? _database.Read(_ => CountByProfileCore(null)) : CountByProfileCore(transaction);

    private IReadOnlyDictionary<long, int> CountByProfileCore(SqliteTransaction? transaction)
    {
        using var command = Command(
            transaction,
            "SELECT profile_id, COUNT(*) FROM character_identities WHERE profile_id IS NOT NULL GROUP BY profile_id;");
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (reader.Read())
        {
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>
    /// Live runs recorded for each identity that are assigned to no profile: what binding the
    /// identity would move.
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public IReadOnlyDictionary<long, int> CountUnassignedRuns(SqliteTransaction? transaction = null) =>
        transaction is null ? _database.Read(_ => CountUnassignedRunsCore(null)) : CountUnassignedRunsCore(transaction);

    private IReadOnlyDictionary<long, int> CountUnassignedRunsCore(SqliteTransaction? transaction)
    {
        using var command = Command(
            transaction,
            "SELECT identity_id, COUNT(*) FROM mentor_runs " +
            "WHERE identity_id IS NOT NULL AND profile_id IS NULL AND soft_deleted = 0 GROUP BY identity_id;");
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (reader.Read())
        {
            counts[reader.GetInt64(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>Notes on a run which character was logged in when it was matched.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void StampRun(string runId, long identityId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(transaction, "UPDATE mentor_runs SET identity_id = $identity WHERE run_id = $run;");
        command.Parameters.AddWithValue("$identity", identityId);
        command.Parameters.AddWithValue("$run", runId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Takes the character off a run the player has filed by hand. From then on the run is where
    /// the player put it - under a profile or under none - and a later answer about the character
    /// leaves it there.
    /// </summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void ClearRunStamp(string runId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(
            transaction, "UPDATE mentor_runs SET identity_id = NULL WHERE run_id = $run AND identity_id IS NOT NULL;");
        command.Parameters.AddWithValue("$run", runId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Files every run recorded for an identity that has no owner - the recycle bin included -
    /// under a profile, and says how many moved. This is the automatic stamp arriving late, not an
    /// edit: no revision, no update time, and a run somebody has since assigned is left alone.
    /// </summary>
    /// <param name="identityId">Identity identifier.</param>
    /// <param name="profileId">Profile the identity is.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public int AssignUnownedRuns(long identityId, long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = Command(
            transaction,
            "UPDATE mentor_runs SET profile_id = $profile WHERE identity_id = $identity AND profile_id IS NULL;");
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$identity", identityId);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes the identities written under another key, which match nothing any more, and says how
    /// many there were. Their runs stay as they are, without a character.
    /// </summary>
    /// <param name="keyId">Fingerprint of the key in use.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public int DeleteUnderOtherKeys(string keyId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        using (var runs = Command(
                   transaction,
                   "UPDATE mentor_runs SET identity_id = NULL WHERE identity_id IN " +
                   "(SELECT identity_id FROM character_identities WHERE key_id != $key);"))
        {
            runs.Parameters.AddWithValue("$key", keyId);
            runs.ExecuteNonQuery();
        }

        using var command = Command(transaction, "DELETE FROM character_identities WHERE key_id != $key;");
        command.Parameters.AddWithValue("$key", keyId);
        return command.ExecuteNonQuery();
    }

    private SqliteCommand Command(SqliteTransaction? transaction, string text)
    {
        var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static CharacterIdentity Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        EnumWire<Region>.Parse(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetInt64(2),
        reader.GetInt32(3) == 1,
        UtcTimestamp.Parse(reader.GetString(4)),
        UtcTimestamp.Parse(reader.GetString(5)),
        reader.IsDBNull(6) ? null : reader.GetInt64(6));
}
