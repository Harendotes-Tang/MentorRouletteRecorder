using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// What <c>GetReflectionSummary</c> needs from storage, as identifiers only.
///
/// The runs themselves are loaded through <see cref="RunRepository"/> so that they are
/// hydrated exactly like every other run a client sees; this record never builds its own.
/// </summary>
/// <param name="ReflectionCount">Reflections on non-deleted runs.</param>
/// <param name="PendingCompletedCount">Non-deleted COMPLETED runs without a reflection.</param>
/// <param name="RecentRunIds">Run ids ordered by reflection updated_at_utc descending.</param>
/// <param name="NextPendingRunId">The 补录心得 target, or null when there is none.</param>
public sealed record ReflectionSummary(
    int ReflectionCount,
    int PendingCompletedCount,
    IReadOnlyList<string> RecentRunIds,
    string? NextPendingRunId);

/// <summary>
/// Reads and writes <c>run_reflections</c> (schema v3).
///
/// The table holds the user's own diary entries, one per run at most. Writing one is not a
/// correction of anything the capture layer observed, so nothing here touches
/// <c>mentor_runs.revision</c> or <c>run_revisions</c>; the only audit trail a reflection
/// has is its own <c>created_at_utc</c> / <c>updated_at_utc</c> pair.
/// </summary>
public sealed class RunReflectionRepository
{
    private const string Columns = "mood, text, created_at_utc, updated_at_utc";

    private readonly SqliteDatabase _database;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public RunReflectionRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads the reflection of one run, or null when it has none.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public RunReflection? Get(string runId, SqliteTransaction? transaction = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM run_reflections WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Reads the reflections of a batch of runs, keyed by run id. Runs without one are simply
    /// absent from the result; this is the lookup a page of runs is hydrated with.
    /// </summary>
    /// <param name="runIds">Run identifiers; an empty list yields an empty map.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public IReadOnlyDictionary<string, RunReflection> GetMany(
        IReadOnlyList<string> runIds,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(runIds);

        var found = new Dictionary<string, RunReflection>(runIds.Count, StringComparer.Ordinal);
        if (runIds.Count == 0)
        {
            return found;
        }

        var names = new List<string>(runIds.Count);
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        for (var i = 0; i < runIds.Count; i++)
        {
            var name = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            command.Parameters.AddWithValue(name, runIds[i]);
        }

        command.CommandText =
            $"SELECT run_id, {Columns} FROM run_reflections WHERE run_id IN ({string.Join(", ", names)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found[reader.GetString(0)] = Read(reader, offset: 1);
        }

        return found;
    }

    /// <summary>
    /// Inserts or replaces the reflection of a run, preserving the original
    /// <c>created_at_utc</c> when one is already there.
    /// </summary>
    /// <param name="runId">Run identifier; the run must exist.</param>
    /// <param name="mood">Mood the user picked.</param>
    /// <param name="text">Trimmed, non-empty text.</param>
    /// <param name="now">Timestamp to stamp the write with.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public RunReflection Upsert(
        string runId,
        ReflectionMood mood,
        string text,
        DateTimeOffset now,
        SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(transaction);

        var stamped = UtcTimestamp.Truncate(now);
        var created = Get(runId, transaction)?.CreatedAtUtc ?? stamped;

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT INTO run_reflections (run_id, mood, text, created_at_utc, updated_at_utc) " +
            "VALUES ($run_id, $mood, $text, $created, $updated) " +
            "ON CONFLICT(run_id) DO UPDATE SET mood = $mood, text = $text, updated_at_utc = $updated;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$mood", ReflectionText.Format(mood));
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$created", UtcTimestamp.ToText(created));
        command.Parameters.AddWithValue("$updated", UtcTimestamp.ToText(stamped));
        command.ExecuteNonQuery();

        return new RunReflection(mood, text, created, stamped);
    }

    /// <summary>Removes the reflection of a run. Returns true when a row was actually removed.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public bool Delete(string runId, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM run_reflections WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Aggregates the dashboard summary. Soft-deleted runs are excluded from every number
    /// here, including the counts.
    /// </summary>
    /// <param name="recentLimit">How many recent entries to name; 0 asks for none.</param>
    public ReflectionSummary GetSummary(int recentLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentLimit);

        return _database.Read(_ => new ReflectionSummary(
            ScalarCount(
                "SELECT COUNT(*) FROM run_reflections f " +
                "JOIN mentor_runs r ON r.run_id = f.run_id WHERE r.soft_deleted = 0;"),
            ScalarCount("SELECT COUNT(*) FROM mentor_runs r WHERE " + PendingWhere + ";"),
            RecentRunIds(recentLimit),
            ScalarText(
                "SELECT r.run_id FROM mentor_runs r WHERE " + PendingWhere + " " +
                "ORDER BY (r.matched_at_utc IS NULL) ASC, r.matched_at_utc DESC, r.run_id ASC LIMIT 1;")));
    }

    /// <summary>A non-deleted COMPLETED run that nobody has written about yet.</summary>
    private const string PendingWhere =
        "r.soft_deleted = 0 AND r.result = 'COMPLETED' " +
        "AND NOT EXISTS (SELECT 1 FROM run_reflections f WHERE f.run_id = r.run_id)";

    private IReadOnlyList<string> RecentRunIds(int limit)
    {
        if (limit == 0)
        {
            return Array.Empty<string>();
        }

        using var command = _database.CreateCommand();
        command.CommandText =
            "SELECT r.run_id FROM run_reflections f JOIN mentor_runs r ON r.run_id = f.run_id " +
            "WHERE r.soft_deleted = 0 " +
            "ORDER BY f.updated_at_utc DESC, r.run_id ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var ids = new List<string>(limit);
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private int ScalarCount(string sql)
    {
        using var command = _database.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string? ScalarText(string sql)
    {
        using var command = _database.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    private static RunReflection Read(SqliteDataReader reader, int offset = 0) => new(
        ReflectionText.Parse(reader.GetString(offset)),
        reader.GetString(offset + 1),
        UtcTimestamp.Parse(reader.GetString(offset + 2)),
        UtcTimestamp.Parse(reader.GetString(offset + 3)));
}
