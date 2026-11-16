using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Reads and writes the achievement baseline and the key/value application settings.
///
/// Nothing in these tables is a credential, a token or a piece of personally identifying
/// information, and nothing here is ever sent anywhere (docs/data-model.md section 6).
/// </summary>
public sealed class SettingsRepository
{
    /// <summary>
    /// Prefix of the setting keys holding the baseline change history, one list per profile:
    /// <c>achievement.baseline_history.profile.&lt;profile_id&gt;</c>.
    /// </summary>
    public const string BaselineHistoryKey = "achievement.baseline_history";

    /// <summary>Longest retained baseline history.</summary>
    public const int MaxBaselineAuditEntries = 100;

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to stamp writes.</param>
    public SettingsRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
        Profiles = new CharacterProfileRepository(database, clock);
        Identities = new CharacterIdentityRepository(database, clock);
    }

    /// <summary>
    /// The character profiles the goals and baselines are stored in since schema 14. The
    /// region-addressed members of this class mean the region's current profile, which is the only
    /// one a player with one character ever has.
    /// </summary>
    public CharacterProfileRepository Profiles { get; }

    /// <summary>
    /// The characters the Collector has told apart since schema 15, and the profile each one is.
    /// </summary>
    public CharacterIdentityRepository Identities { get; }

    /// <summary>
    /// Creates the CN/TC profiles and the default application settings when they are
    /// missing. Safe to call on every startup.
    /// </summary>
    public void EnsureDefaults()
    {
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        _database.RunInTransaction(tx =>
        {
            Profiles.EnsureCurrent(Region.Cn, tx);
            Profiles.EnsureCurrent(Region.Tc, tx);

            SetDefault(tx, "ui.language", "\"zh-Hans\"", now);
            SetDefault(tx, "tts.enabled", "false", now);
            SetDefault(tx, "capture.autostart", "false", now);
            SetDefault(tx, "capture.queue_capacity", "4096", now);
        });
    }

    private void SetDefault(SqliteTransaction tx, string key, string valueJson, DateTimeOffset now)
    {
        using var command = _database.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "INSERT OR IGNORE INTO application_settings (key, value_json, updated_at_utc) " +
            "VALUES ($key, $value, $now);";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", valueJson);
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(now));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the goal and baseline of a region's current profile; the defaults, naming no profile,
    /// when the region has none yet.
    ///
    /// The transaction-less form gates itself: every repository shares one SQLite connection
    /// and <see cref="SqliteDatabase"/> serialises access to it, so a read issued outside the
    /// gate while another transaction is open is refused by ADO.NET (review finding H2).
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public AchievementSettings GetAchievementSettings(SqliteTransaction? transaction = null, Region region = Region.Cn) =>
        transaction is null
            ? _database.Read(_ => ReadAchievementSettings(null, region))
            : ReadAchievementSettings(transaction, region);

    private AchievementSettings ReadAchievementSettings(SqliteTransaction? transaction, Region region)
    {
        if (Profiles.FindCurrent(region, transaction) is { } profile)
        {
            return profile.ToSettings();
        }

        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        return new AchievementSettings
        {
            Region = region,
            GoalCount = AchievementSettings.DefaultGoalCount,
            BaselineCompletedCount = 0,
            BaselineEffectiveAt = now,
            UpdatedAtUtc = now,
        };
    }

    /// <summary>
    /// Writes the goal and baseline of the profile <paramref name="settings"/> names, or of its
    /// region's current profile when it names none, and returns them as stored.
    /// </summary>
    /// <param name="settings">New values.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public AchievementSettings UpdateAchievementSettings(AchievementSettings settings, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transaction);

        var addressed = settings.ProfileId is null
            ? settings with { ProfileId = Profiles.EnsureCurrent(settings.Region, transaction).ProfileId }
            : settings;
        Profiles.UpdateSettings(addressed, transaction);
        return addressed;
    }

    /// <summary>
    /// Appends one entry to the baseline change history of the profile <paramref name="settings"/>
    /// names, kept in <c>application_settings</c> under
    /// <c>achievement.baseline_history.profile.&lt;profile_id&gt;</c>.
    ///
    /// The achievement baseline has no run to hang a <c>run_revisions</c> row on, so this is
    /// its audit trail: who changed the goal, to what, when and why. The list is capped so a
    /// user who keeps adjusting the baseline cannot grow the row without bound.
    /// </summary>
    /// <param name="auditEventId">Identifier returned to the client.</param>
    /// <param name="settings">Settings as stored by this change.</param>
    /// <param name="reason">Mandatory explanation.</param>
    /// <param name="requestId">Request that caused the change.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void AppendBaselineAudit(
        string auditEventId,
        AchievementSettings settings,
        string reason,
        string requestId,
        SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(auditEventId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(reason);
        ArgumentNullException.ThrowIfNull(transaction);

        // Settings that name no profile mean the region's current one, as in UpdateAchievementSettings.
        var profileId = settings.ProfileId ?? Profiles.EnsureCurrent(settings.Region, transaction).ProfileId;
        var history = ReadBaselineAuditRow(transaction, profileId, settings.Region).ToList();
        history.Add(new BaselineAuditEntry(
            auditEventId,
            requestId,
            settings.GoalCount,
            settings.BaselineCompletedCount,
            UtcTimestamp.ToText(settings.BaselineEffectiveAt),
            UtcTimestamp.ToText(settings.UpdatedAtUtc),
            reason, settings.Region));

        var trimmed = history.Count > MaxBaselineAuditEntries
            ? history.GetRange(history.Count - MaxBaselineAuditEntries, MaxBaselineAuditEntries)
            : history;

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO application_settings (key, value_json, updated_at_utc) " +
            "VALUES ($key, $value, $now) " +
            "ON CONFLICT(key) DO UPDATE SET value_json = $value, updated_at_utc = $now;";
        command.Parameters.AddWithValue("$key", AuditKey(profileId));
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(trimmed));
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(settings.UpdatedAtUtc));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the baseline change history of a region's current profile, oldest first. Self-gating
    /// when no transaction is supplied, for the same reason as <see cref="GetAchievementSettings"/>.
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public IReadOnlyList<BaselineAuditEntry> ReadBaselineAudit(SqliteTransaction? transaction = null, Region region = Region.Cn) =>
        transaction is null
            ? _database.Read(_ => ReadCurrentBaselineAudit(null, region))
            : ReadCurrentBaselineAudit(transaction, region);

    /// <summary>Reads the baseline change history of one profile, oldest first.</summary>
    /// <param name="profile">Profile whose history is read.</param>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public IReadOnlyList<BaselineAuditEntry> ReadBaselineAudit(CharacterProfile profile, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return transaction is null
            ? _database.Read(_ => ReadBaselineAuditRow(null, profile.ProfileId, profile.Region))
            : ReadBaselineAuditRow(transaction, profile.ProfileId, profile.Region);
    }

    private IReadOnlyList<BaselineAuditEntry> ReadCurrentBaselineAudit(SqliteTransaction? transaction, Region region) =>
        Profiles.FindCurrent(region, transaction) is { } profile
            ? ReadBaselineAuditRow(transaction, profile.ProfileId, region)
            : Array.Empty<BaselineAuditEntry>();

    private static string AuditKey(long profileId) =>
        BaselineHistoryKey + ".profile." + profileId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every surviving baseline audit entry, of every profile.</summary>
    internal IReadOnlyList<BaselineAuditEntry> ReadAllBaselineAudit(SqliteTransaction? transaction = null) =>
        Profiles.List(transaction).SelectMany(profile => ReadBaselineAudit(profile, transaction)).ToArray();

    /// <summary>Drops the baseline change history of a profile that is being removed.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    internal void DeleteBaselineAudit(long profileId, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM application_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", AuditKey(profileId));
        command.ExecuteNonQuery();
    }

    /// <summary>Regions with records/profiles plus the always-visible CN and TC defaults.</summary>
    public IReadOnlyList<Region> GetAchievementRegions() => _database.Read(_ =>
    {
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT region FROM character_profiles UNION SELECT region FROM mentor_runs;";
        using var reader = command.ExecuteReader();
        var regions = new HashSet<Region> { Region.Cn, Region.Tc };
        while (reader.Read()) regions.Add(EnumWire<Region>.Parse(reader.GetString(0)));
        return (IReadOnlyList<Region>)regions.OrderBy(value => value == Region.Cn ? 0 : value == Region.Tc ? 1 : 2)
            .ThenBy(value => EnumWire<Region>.Format(value), StringComparer.Ordinal).ToArray();
    });

    private IReadOnlyList<BaselineAuditEntry> ReadBaselineAuditRow(
        SqliteTransaction? transaction, long profileId, Region region)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value_json FROM application_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", AuditKey(profileId));
        if (command.ExecuteScalar() is not string json)
        {
            return Array.Empty<BaselineAuditEntry>();
        }

        try
        {
            return (JsonSerializer.Deserialize<BaselineAuditEntry[]>(json) ?? Array.Empty<BaselineAuditEntry>())
                .Select(entry => entry with { Region = region }).ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<BaselineAuditEntry>();
        }
    }

    /// <summary>Reads one application setting, or null when unset.</summary>
    /// <param name="key">Setting key.</param>
    /// <param name="transaction">Enclosing settings transaction, or null for a standalone read.</param>
    public string? GetSetting(string key, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT value_json FROM application_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        });
    }

    /// <summary>Writes one application setting.</summary>
    /// <param name="key">Setting key.</param>
    /// <param name="valueJson">JSON-encoded value.</param>
    public void SetSetting(string key, string valueJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(valueJson);

        _database.RunInTransaction(tx => WriteSetting(key, valueJson, tx));
    }

    /// <summary>
    /// Atomically writes the supplied keys and reads their resulting projection before
    /// committing. A write or projection failure rolls back every supplied key; omitted
    /// keys remain unchanged. The callback must use the supplied transaction for reads.
    /// </summary>
    /// <typeparam name="T">Projection returned after the transaction commits.</typeparam>
    /// <param name="values">Setting keys mapped to JSON-encoded values.</param>
    /// <param name="readResult">Synchronous projection read inside the same transaction.</param>
    public T SetSettings<T>(IReadOnlyDictionary<string, string> values, Func<SqliteTransaction, T> readResult)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(readResult);
        return _database.RunInTransaction(tx =>
        {
            foreach (var (key, value) in values)
            {
                ArgumentException.ThrowIfNullOrEmpty(key);
                ArgumentNullException.ThrowIfNull(value);
                WriteSetting(key, value, tx);
            }
            return readResult(tx);
        });
    }

    private void WriteSetting(string key, string valueJson, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO application_settings (key, value_json, updated_at_utc) " +
            "VALUES ($key, $value, $now) " +
            "ON CONFLICT(key) DO UPDATE SET value_json = $value, updated_at_utc = $now;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", valueJson);
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)));
        command.ExecuteNonQuery();
    }
}

/// <summary>One recorded change of the achievement baseline.</summary>
/// <param name="AuditEventId">Identifier returned to the client.</param>
/// <param name="RequestId">Request that caused the change.</param>
/// <param name="GoalCount">Goal after the change.</param>
/// <param name="BaselineCompletedCount">Baseline after the change.</param>
/// <param name="BaselineEffectiveAt">When the baseline count last changed, as stored text.</param>
/// <param name="ChangedAtUtc">Change time, as stored text.</param>
/// <param name="Reason">Mandatory explanation.</param>
public sealed record BaselineAuditEntry(
    [property: JsonPropertyName("audit_event_id")] string AuditEventId,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("goal_count")] int GoalCount,
    [property: JsonPropertyName("baseline_completed_count")] int BaselineCompletedCount,
    [property: JsonPropertyName("baseline_effective_at")] string BaselineEffectiveAt,
    [property: JsonPropertyName("changed_at_utc")] string ChangedAtUtc,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("region")] Region Region = Region.Cn);
