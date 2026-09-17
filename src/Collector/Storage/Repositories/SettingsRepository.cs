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
    /// <summary>Setting key holding the achievement baseline change history.</summary>
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
    }

    /// <summary>
    /// Creates the single achievement row and the default application settings when they are
    /// missing. Safe to call on every startup.
    /// </summary>
    public void EnsureDefaults()
    {
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        _database.RunInTransaction(tx =>
        {
            using (var command = _database.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText =
                    "INSERT OR IGNORE INTO achievement_settings " +
                    "(id, goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc) " +
                    "VALUES (1, $goal, 0, $now, $now);";
                command.Parameters.AddWithValue("$goal", AchievementSettings.DefaultGoalCount);
                command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(now));
                command.ExecuteNonQuery();
            }

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
    /// Reads the achievement settings row.
    ///
    /// The transaction-less form gates itself: every repository shares one SQLite connection
    /// and <see cref="SqliteDatabase"/> serialises access to it, so a read issued outside the
    /// gate while another transaction is open is refused by ADO.NET (review finding H2).
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public AchievementSettings GetAchievementSettings(SqliteTransaction? transaction = null) =>
        transaction is null
            ? _database.Read(_ => ReadAchievementSettings(transaction: null))
            : ReadAchievementSettings(transaction);

    private AchievementSettings ReadAchievementSettings(SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc " +
            "FROM achievement_settings WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            var now = UtcTimestamp.Truncate(_clock.UtcNow);
            return new AchievementSettings
            {
                GoalCount = AchievementSettings.DefaultGoalCount,
                BaselineCompletedCount = 0,
                BaselineEffectiveAt = now,
                UpdatedAtUtc = now,
            };
        }

        return new AchievementSettings
        {
            GoalCount = reader.GetInt32(0),
            BaselineCompletedCount = reader.GetInt32(1),
            BaselineEffectiveAt = UtcTimestamp.Parse(reader.GetString(2)),
            UpdatedAtUtc = UtcTimestamp.Parse(reader.GetString(3)),
        };
    }

    /// <summary>Writes the achievement settings row.</summary>
    /// <param name="settings">New values.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void UpdateAchievementSettings(AchievementSettings settings, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO achievement_settings " +
            "(id, goal_count, baseline_completed_count, baseline_effective_at, updated_at_utc) " +
            "VALUES (1, $goal, $baseline, $effective, $updated) " +
            "ON CONFLICT(id) DO UPDATE SET goal_count = $goal, baseline_completed_count = $baseline, " +
            "baseline_effective_at = $effective, updated_at_utc = $updated;";
        command.Parameters.AddWithValue("$goal", settings.GoalCount);
        command.Parameters.AddWithValue("$baseline", settings.BaselineCompletedCount);
        command.Parameters.AddWithValue("$effective", UtcTimestamp.ToText(settings.BaselineEffectiveAt));
        command.Parameters.AddWithValue("$updated", UtcTimestamp.ToText(settings.UpdatedAtUtc));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Appends one entry to the baseline change history kept in
    /// <c>application_settings["achievement.baseline_history"]</c>.
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

        var history = ReadBaselineAudit(transaction).ToList();
        history.Add(new BaselineAuditEntry(
            auditEventId,
            requestId,
            settings.GoalCount,
            settings.BaselineCompletedCount,
            UtcTimestamp.ToText(settings.BaselineEffectiveAt),
            UtcTimestamp.ToText(settings.UpdatedAtUtc),
            reason));

        var trimmed = history.Count > MaxBaselineAuditEntries
            ? history.GetRange(history.Count - MaxBaselineAuditEntries, MaxBaselineAuditEntries)
            : history;

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO application_settings (key, value_json, updated_at_utc) " +
            "VALUES ($key, $value, $now) " +
            "ON CONFLICT(key) DO UPDATE SET value_json = $value, updated_at_utc = $now;";
        command.Parameters.AddWithValue("$key", BaselineHistoryKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(trimmed));
        command.Parameters.AddWithValue("$now", UtcTimestamp.ToText(settings.UpdatedAtUtc));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads the baseline change history, oldest first. Self-gating when no transaction is
    /// supplied, for the same reason as <see cref="GetAchievementSettings"/>.
    /// </summary>
    /// <param name="transaction">Enclosing transaction, or null for a standalone read.</param>
    public IReadOnlyList<BaselineAuditEntry> ReadBaselineAudit(SqliteTransaction? transaction = null) =>
        transaction is null
            ? _database.Read(_ => ReadBaselineAuditRow(transaction: null))
            : ReadBaselineAuditRow(transaction);

    private IReadOnlyList<BaselineAuditEntry> ReadBaselineAuditRow(SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value_json FROM application_settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", BaselineHistoryKey);
        if (command.ExecuteScalar() is not string json)
        {
            return Array.Empty<BaselineAuditEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<BaselineAuditEntry[]>(json) ?? Array.Empty<BaselineAuditEntry>();
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
/// <param name="BaselineEffectiveAt">Effective moment, as stored text.</param>
/// <param name="ChangedAtUtc">Change time, as stored text.</param>
/// <param name="Reason">Mandatory explanation.</param>
public sealed record BaselineAuditEntry(
    [property: JsonPropertyName("audit_event_id")] string AuditEventId,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("goal_count")] int GoalCount,
    [property: JsonPropertyName("baseline_completed_count")] int BaselineCompletedCount,
    [property: JsonPropertyName("baseline_effective_at")] string BaselineEffectiveAt,
    [property: JsonPropertyName("changed_at_utc")] string ChangedAtUtc,
    [property: JsonPropertyName("reason")] string Reason);
