using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Appends to and reads the per-run event trail.
///
/// Inserts are deduplicated by <c>event_key</c>: a UNIQUE index makes a repeated observation
/// a no-op instead of a second row, which is what makes replaying a fixture twice, or
/// restarting the Collector mid-run, safe.
///
/// That index is <em>global</em>, so a caller whose observation can belong to more than one
/// run must scope the key it stores by the run -- see
/// <c>Protocol.Pipeline.SemanticEventProcessor.ScopedEventKey</c> and
/// docs/state-machine.md section 7.3. The pop that closes one run is the pop that opens the
/// next, so an unscoped key silently drops the second row and the new run starts empty.
/// </summary>
public sealed class RunEventRepository
{
    private const string Columns =
        "event_id, run_id, sequence, occurred_at_utc, monotonic_offset_ms, event_type, " +
        "from_state, to_state, confidence, event_key, detail_json";

    private readonly SqliteDatabase _database;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public RunEventRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Appends one event. Returns false when an event with the same key already exists, in
    /// which case nothing is written.
    /// </summary>
    /// <param name="runEvent">Event to append.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public bool Append(RunEvent runEvent, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO run_events ({Columns}) VALUES (" +
            "$event_id, $run_id, $sequence, $occurred_at_utc, $monotonic_offset_ms, $event_type, " +
            "$from_state, $to_state, $confidence, $event_key, $detail_json) " +
            "ON CONFLICT(event_key) WHERE event_key IS NOT NULL DO NOTHING;";
        command.Parameters.AddWithValue("$event_id", runEvent.EventId);
        command.Parameters.AddWithValue("$run_id", runEvent.RunId);
        command.Parameters.AddWithValue("$sequence", runEvent.Sequence);
        command.Parameters.AddWithValue("$occurred_at_utc", UtcTimestamp.ToText(runEvent.OccurredAtUtc));
        command.Parameters.AddWithValue("$monotonic_offset_ms", Math.Max(0, runEvent.MonotonicOffsetMs));
        command.Parameters.AddWithValue("$event_type", runEvent.EventType);
        command.Parameters.AddWithValue(
            "$from_state",
            runEvent.FromState is { } from ? EnumWire<RunState>.Format(from) : (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$to_state",
            runEvent.ToState is { } to ? EnumWire<RunState>.Format(to) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$confidence", EnumWire<DetectionConfidence>.Format(runEvent.Confidence));
        command.Parameters.AddWithValue("$event_key", (object?)runEvent.EventKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail_json", (object?)runEvent.DetailJson ?? DBNull.Value);

        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// True when an event with this deduplication key is already stored. The key has to be
    /// exactly what was written, run scope and all; a bare canonical key never matches a row
    /// the semantic processor stored.
    /// </summary>
    /// <param name="eventKey">Stored deduplication key, scoped as the writer scoped it.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public bool ExistsByKey(string eventKey, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventKey);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM run_events WHERE event_key = $event_key LIMIT 1;";
        command.Parameters.AddWithValue("$event_key", eventKey);
        return command.ExecuteScalar() is not null;
    }

    /// <summary>Next free sequence number for a run.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public int NextSequence(string runId, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT IFNULL(MAX(sequence), -1) + 1 FROM run_events WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Lists the events of a run in order.</summary>
    /// <param name="runId">Run identifier.</param>
    public IReadOnlyList<RunEvent> ListForRun(string runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                $"SELECT {Columns} FROM run_events WHERE run_id = $run_id ORDER BY sequence ASC;";
            command.Parameters.AddWithValue("$run_id", runId);
            using var reader = command.ExecuteReader();
            var events = new List<RunEvent>();
            while (reader.Read())
            {
                events.Add(new RunEvent
                {
                    EventId = reader.GetString(0),
                    RunId = reader.GetString(1),
                    Sequence = reader.GetInt32(2),
                    OccurredAtUtc = UtcTimestamp.Parse(reader.GetString(3)),
                    MonotonicOffsetMs = reader.GetInt64(4),
                    EventType = reader.GetString(5),
                    FromState = reader.IsDBNull(6) ? null : EnumWire<RunState>.Parse(reader.GetString(6)),
                    ToState = reader.IsDBNull(7) ? null : EnumWire<RunState>.Parse(reader.GetString(7)),
                    Confidence = EnumWire<DetectionConfidence>.Parse(reader.GetString(8)),
                    EventKey = reader.IsDBNull(9) ? null : reader.GetString(9),
                    DetailJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                });
            }

            return (IReadOnlyList<RunEvent>)events;
        });
    }
}
