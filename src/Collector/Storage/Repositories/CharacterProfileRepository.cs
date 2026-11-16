using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Reads and writes <c>character_profiles</c>.
///
/// Two invariants are kept here rather than by every caller: a region that has a profile has
/// exactly one current profile, and a region asked for its current profile gets one created when
/// it has none - the same defaults a region without a settings row used to be shown with.
/// </summary>
public sealed class CharacterProfileRepository
{
    private const string Columns =
        "profile_id, region, display_name, is_current, goal_count, baseline_completed_count, " +
        "baseline_effective_at, created_at_utc, updated_at_utc";

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to stamp writes.</param>
    public CharacterProfileRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>
    /// Every profile, or one region's: CN, then TC, then the other regions; inside a region in the
    /// order the profiles were created. Self-gating when no transaction is supplied.
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    /// <param name="region">Region to list, or null for all of them.</param>
    public IReadOnlyList<CharacterProfile> List(SqliteTransaction? transaction = null, Region? region = null) =>
        transaction is null ? _database.Read(_ => ListCore(null, region)) : ListCore(transaction, region);

    private IReadOnlyList<CharacterProfile> ListCore(SqliteTransaction? transaction, Region? region)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM character_profiles " +
            (region is null ? string.Empty : "WHERE region = $region ") +
            "ORDER BY CASE region WHEN 'CN' THEN 0 WHEN 'TC' THEN 1 ELSE 2 END, region, profile_id;";
        if (region is { } owner)
        {
            command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(owner));
        }

        using var reader = command.ExecuteReader();
        var profiles = new List<CharacterProfile>();
        while (reader.Read())
        {
            profiles.Add(Read(reader));
        }

        return profiles;
    }

    /// <summary>Reads one profile, or null when it does not exist.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public CharacterProfile? Find(long profileId, SqliteTransaction? transaction = null) =>
        transaction is null
            ? _database.Read(_ => Single("profile_id = $key", profileId, null))
            : Single("profile_id = $key", profileId, transaction);

    /// <summary>Reads the current profile of a region, or null when the region has no profile yet.</summary>
    /// <param name="region">Region.</param>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public CharacterProfile? FindCurrent(Region region, SqliteTransaction? transaction = null)
    {
        var key = EnumWire<Region>.Format(region);
        return transaction is null
            ? _database.Read(_ => Single("region = $key AND is_current = 1", key, null))
            : Single("region = $key AND is_current = 1", key, transaction);
    }

    private CharacterProfile? Single(string where, object key, SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM character_profiles WHERE {where};";
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// The current profile of a region, created with the default goal and no baseline when the
    /// region has none. Every writer of a run goes through here, so a run of a region with one
    /// profile always belongs to it.
    /// </summary>
    /// <param name="region">Region.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterProfile EnsureCurrent(Region region, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (FindCurrent(region, transaction) is { } current)
        {
            return current;
        }

        // A region left without a current profile keeps its oldest one; only a region with no
        // profile at all gets a new one.
        if (ListCore(transaction, region) is { Count: > 0 } existing)
        {
            SetCurrent(existing[0].ProfileId, transaction);
            return existing[0] with { IsCurrent = true };
        }

        return Insert(region, string.Empty, AchievementSettings.DefaultGoalCount, 0, makeCurrent: true, transaction);
    }

    /// <summary>Inserts a profile and returns it as stored.</summary>
    /// <param name="region">Owning region.</param>
    /// <param name="displayName">Name the player typed; empty for the profile the software creates.</param>
    /// <param name="goalCount">Target completions.</param>
    /// <param name="baselineCompletedCount">Self-reported prior completions.</param>
    /// <param name="makeCurrent">Whether newly recorded runs are filed under it from now on.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public CharacterProfile Insert(
        Region region,
        string displayName,
        int goalCount,
        int baselineCompletedCount,
        bool makeCurrent,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(transaction);

        var now = UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow));
        if (makeCurrent)
        {
            ClearCurrent(region, transaction);
        }

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO character_profiles " +
            "(region, display_name, is_current, goal_count, baseline_completed_count, " +
            "baseline_effective_at, created_at_utc, updated_at_utc) " +
            "VALUES ($region, $name, $current, $goal, $baseline, $now, $now, $now); " +
            "SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$current", makeCurrent ? 1 : 0);
        command.Parameters.AddWithValue("$goal", goalCount);
        command.Parameters.AddWithValue("$baseline", baselineCompletedCount);
        command.Parameters.AddWithValue("$now", now);
        var profileId = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return Single("profile_id = $key", profileId, transaction)!;
    }

    /// <summary>Writes the goal and baseline of one profile.</summary>
    /// <param name="settings">New values; <see cref="AchievementSettings.ProfileId"/> names the profile.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void UpdateSettings(AchievementSettings settings, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transaction);
        var profileId = settings.ProfileId
            ?? throw new ArgumentException("The settings name no profile.", nameof(settings));

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE character_profiles SET goal_count = $goal, baseline_completed_count = $baseline, " +
            "baseline_effective_at = $effective, updated_at_utc = $updated WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$goal", settings.GoalCount);
        command.Parameters.AddWithValue("$baseline", settings.BaselineCompletedCount);
        command.Parameters.AddWithValue("$effective", UtcTimestamp.ToText(settings.BaselineEffectiveAt));
        command.Parameters.AddWithValue("$updated", UtcTimestamp.ToText(settings.UpdatedAtUtc));
        command.Parameters.AddWithValue("$id", profileId);
        command.ExecuteNonQuery();
    }

    /// <summary>Changes the name of a profile.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="displayName">New name.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Rename(long profileId, string displayName, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE character_profiles SET display_name = $name, updated_at_utc = $now WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)));
        command.Parameters.AddWithValue("$id", profileId);
        command.ExecuteNonQuery();
    }

    /// <summary>Makes a profile the current one of its region.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void SetCurrent(long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // Two statements: the partial unique index allows one current profile per region at every
        // moment, so the old one steps down before the new one steps up.
        using (var clear = _database.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText =
                "UPDATE character_profiles SET is_current = 0 WHERE is_current = 1 AND profile_id != $id " +
                "AND region = (SELECT region FROM character_profiles WHERE profile_id = $id);";
            clear.Parameters.AddWithValue("$id", profileId);
            clear.ExecuteNonQuery();
        }

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE character_profiles SET is_current = 1 WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId);
        command.ExecuteNonQuery();
    }

    private void ClearCurrent(Region region, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE character_profiles SET is_current = 0 WHERE region = $region AND is_current = 1;";
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.ExecuteNonQuery();
    }

    /// <summary>Removes a profile. The caller has made sure no run refers to it.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Delete(long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM character_profiles WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId);
        command.ExecuteNonQuery();
    }

    /// <summary>How many runs refer to a profile, the ones in the recycle bin included.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public long CountRuns(long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM mentor_runs WHERE profile_id = $id;";
        command.Parameters.AddWithValue("$id", profileId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static CharacterProfile Read(SqliteDataReader reader) => new()
    {
        ProfileId = reader.GetInt64(0),
        Region = EnumWire<Region>.Parse(reader.GetString(1)),
        DisplayName = reader.GetString(2),
        IsCurrent = reader.GetInt32(3) == 1,
        GoalCount = reader.GetInt32(4),
        BaselineCompletedCount = reader.GetInt32(5),
        BaselineEffectiveAt = UtcTimestamp.Parse(reader.GetString(6)),
        CreatedAtUtc = UtcTimestamp.Parse(reader.GetString(7)),
        UpdatedAtUtc = UtcTimestamp.Parse(reader.GetString(8)),
    };
}
