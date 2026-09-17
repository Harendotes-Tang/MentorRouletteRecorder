using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>Reads and writes <c>capture_sessions</c>.</summary>
public sealed class CaptureSessionRepository
{
    private const string Columns =
        "capture_session_id, started_at_utc, ended_at_utc, collector_version, region, game_build, " +
        "protocol_profile_id, profile_status, adapter_id, packets_observed, packets_dropped, end_reason";

    private readonly SqliteDatabase _database;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public CaptureSessionRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Inserts a session row, ignoring a repeat of the same identifier.</summary>
    /// <param name="session">Session to insert.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    /// <returns>True when a new row was written; false when the identifier already existed.</returns>
    public bool Insert(CaptureSession session, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT OR IGNORE INTO capture_sessions ({Columns}) VALUES (" +
            "$id, $started, $ended, $version, $region, $build, $profile, $status, $adapter, " +
            "$observed, $dropped, $reason);";
        command.Parameters.AddWithValue("$id", session.CaptureSessionId);
        command.Parameters.AddWithValue("$started", UtcTimestamp.ToText(session.StartedAtUtc));
        command.Parameters.AddWithValue(
            "$ended", (object?)UtcTimestamp.ToTextOrNull(session.EndedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", session.CollectorVersion);
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(session.Region));
        command.Parameters.AddWithValue("$build", (object?)session.GameBuild ?? DBNull.Value);
        command.Parameters.AddWithValue("$profile", (object?)session.ProtocolProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", EnumWire<ProfileStatus>.Format(session.ProfileStatus));
        command.Parameters.AddWithValue("$adapter", (object?)session.AdapterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", session.PacketsObserved);
        command.Parameters.AddWithValue("$dropped", session.PacketsDropped);
        command.Parameters.AddWithValue(
            "$reason",
            session.EndReason is { } reason ? EnumWire<CaptureEndReason>.Format(reason) : (object)DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Rebinds an open session to a profile, for example after self-calibration hot-binds a
    /// newly confirmed local profile mid-session. Never touches a session that has already
    /// ended: a closed session's profile is history.
    /// </summary>
    /// <param name="captureSessionId">Session identifier.</param>
    /// <param name="profileId">Profile identifier to record, or null to clear it.</param>
    /// <param name="status">Profile status to record.</param>
    /// <param name="transaction">Enclosing transaction, or null to run outside one.</param>
    /// <returns>True when an open session was found and updated.</returns>
    public bool UpdateProfile(
        string captureSessionId,
        string? profileId,
        ProfileStatus status,
        SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE capture_sessions SET protocol_profile_id = $profile_id, profile_status = $status " +
            "WHERE capture_session_id = $id AND ended_at_utc IS NULL;";
        command.Parameters.AddWithValue("$profile_id", (object?)profileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", EnumWire<ProfileStatus>.Format(status));
        command.Parameters.AddWithValue("$id", captureSessionId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Closes a session that is still open.</summary>
    /// <param name="captureSessionId">Session identifier.</param>
    /// <param name="endedAtUtc">Time the session ended or was discovered to have ended.</param>
    /// <param name="reason">Why it ended.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Close(
        string captureSessionId,
        DateTimeOffset endedAtUtc,
        CaptureEndReason reason,
        SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE capture_sessions SET ended_at_utc = $ended, end_reason = $reason " +
            "WHERE capture_session_id = $id AND ended_at_utc IS NULL;";
        command.Parameters.AddWithValue("$ended", UtcTimestamp.ToText(endedAtUtc));
        command.Parameters.AddWithValue("$reason", EnumWire<CaptureEndReason>.Format(reason));
        command.Parameters.AddWithValue("$id", captureSessionId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Closes every session that is still open apart from <paramref name="exceptSessionId"/>,
    /// and returns how many rows that was. Used by crash recovery: a session left open is by
    /// definition one a previous process never got to close.
    /// </summary>
    /// <param name="exceptSessionId">Session to leave alone, typically this process's own.</param>
    /// <param name="endedAtUtc">Time the sessions are recorded as having ended.</param>
    /// <param name="reason">Why they ended.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public int CloseAllOpen(
        string? exceptSessionId,
        DateTimeOffset endedAtUtc,
        CaptureEndReason reason,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE capture_sessions SET ended_at_utc = $ended, end_reason = $reason " +
            "WHERE ended_at_utc IS NULL AND ($except IS NULL OR capture_session_id <> $except);";
        command.Parameters.AddWithValue("$ended", UtcTimestamp.ToText(endedAtUtc));
        command.Parameters.AddWithValue("$reason", EnumWire<CaptureEndReason>.Format(reason));
        command.Parameters.AddWithValue("$except", (object?)exceptSessionId ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    /// <summary>Lists sessions that were never closed, oldest first.</summary>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public IReadOnlyList<CaptureSession> ListOpen(SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM capture_sessions WHERE ended_at_utc IS NULL ORDER BY started_at_utc ASC;";
        using var reader = command.ExecuteReader();
        var sessions = new List<CaptureSession>();
        while (reader.Read())
        {
            sessions.Add(ReadRow(reader));
        }

        return sessions;
    }

    /// <summary>Reads one session, or null.</summary>
    /// <param name="captureSessionId">Session identifier.</param>
    public CaptureSession? Get(string captureSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);

        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText = $"SELECT {Columns} FROM capture_sessions WHERE capture_session_id = $id;";
            command.Parameters.AddWithValue("$id", captureSessionId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        });
    }

    private static CaptureSession ReadRow(SqliteDataReader reader) => new()
    {
        CaptureSessionId = reader.GetString(0),
        StartedAtUtc = UtcTimestamp.Parse(reader.GetString(1)),
        EndedAtUtc = reader.IsDBNull(2) ? null : UtcTimestamp.Parse(reader.GetString(2)),
        CollectorVersion = reader.GetString(3),
        Region = EnumWire<Region>.Parse(reader.GetString(4)),
        GameBuild = reader.IsDBNull(5) ? null : reader.GetString(5),
        ProtocolProfileId = reader.IsDBNull(6) ? null : reader.GetString(6),
        ProfileStatus = EnumWire<ProfileStatus>.Parse(reader.GetString(7)),
        AdapterId = reader.IsDBNull(8) ? null : reader.GetString(8),
        PacketsObserved = reader.GetInt64(9),
        PacketsDropped = reader.GetInt64(10),
        EndReason = reader.IsDBNull(11) ? null : EnumWire<CaptureEndReason>.Parse(reader.GetString(11)),
    };
}
