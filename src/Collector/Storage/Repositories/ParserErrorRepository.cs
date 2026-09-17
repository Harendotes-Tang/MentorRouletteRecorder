using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Bounded diagnostics for refusals: a fail-closed profile, a message that failed a
/// robustness check, a session that lost its profile.
///
/// Rows carry a short kind and a non-sensitive detail string only. Packet bytes, chat text,
/// character names and addresses are never written here (docs/privacy-boundary.md section 5).
/// The table is trimmed to <see cref="MaxRows"/> so that a broken profile cannot grow the
/// database without limit.
/// </summary>
public sealed class ParserErrorRepository
{
    /// <summary>Maximum number of retained rows.</summary>
    public const int MaxRows = 1000;

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    public ParserErrorRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>Records one refusal.</summary>
    /// <param name="kind">Short machine-readable reason.</param>
    /// <param name="detail">Non-sensitive explanatory detail.</param>
    /// <param name="captureSessionId">Session the refusal belongs to, if any.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Record(string kind, string? detail, string? captureSessionId, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentNullException.ThrowIfNull(transaction);

        using (var command = _database.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO parser_errors (error_id, occurred_at_utc, capture_session_id, kind, detail) " +
                "VALUES ($id, $occurred, $session, $kind, $detail);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue(
                "$occurred", UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)));
            command.Parameters.AddWithValue("$session", (object?)captureSessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        Trim(transaction);
    }

    /// <summary>Number of stored refusals.</summary>
    public int Count() => _database.Read(_ =>
    {
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM parser_errors;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    });

    private void Trim(SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM parser_errors WHERE error_id IN (" +
            "SELECT error_id FROM parser_errors ORDER BY occurred_at_utc DESC, error_id DESC " +
            "LIMIT -1 OFFSET $keep);";
        command.Parameters.AddWithValue("$keep", MaxRows);
        command.ExecuteNonQuery();
    }
}
