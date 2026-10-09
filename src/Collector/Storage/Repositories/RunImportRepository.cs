using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>Import provenance and exact retry identities on the local database connection.</summary>
public sealed class RunImportRepository(SqliteDatabase database)
{
    public RunImportMetadata? Get(string runId, SqliteTransaction? transaction = null) =>
        GetMany(new[] { runId }, transaction).GetValueOrDefault(runId);

    /// <summary>Hydrates a page with one bounded parameterised query.</summary>
    public IReadOnlyDictionary<string, RunImportMetadata> GetMany(
        IReadOnlyList<string> runIds, SqliteTransaction? transaction = null)
    {
        var found = new Dictionary<string, RunImportMetadata>(StringComparer.Ordinal);
        if (runIds.Count == 0) return found;
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        var names = runIds.Select((id, index) =>
        {
            var name = "$id" + index;
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        command.CommandText = "SELECT run_id, source_kind, source_name, source_recorded_at, " +
            "source_recorded_at_utc, imported_at_utc, source_fingerprint, mentor_confirmed " +
            $"FROM run_import_metadata WHERE run_id IN ({string.Join(',', names)});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            found[reader.GetString(0)] = new RunImportMetadata(reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : UtcTimestamp.Parse(reader.GetString(4)),
                UtcTimestamp.Parse(reader.GetString(5)), reader.GetString(6), reader.GetInt32(7) == 1);
        }
        return found;
    }

    public void Insert(string runId, RunImportMetadata metadata, SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO run_import_metadata VALUES " +
            "($id,$kind,$name,$source_time,$source_utc,$imported,$fingerprint,$mentor);";
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$kind", metadata.SourceKind);
        command.Parameters.AddWithValue("$name", (object?)metadata.SourceName ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_time", (object?)metadata.SourceRecordedAt ?? DBNull.Value);
        command.Parameters.AddWithValue("$source_utc", (object?)UtcTimestamp.ToTextOrNull(metadata.SourceRecordedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$imported", UtcTimestamp.ToText(metadata.ImportedAtUtc));
        command.Parameters.AddWithValue("$fingerprint", metadata.SourceFingerprint);
        command.Parameters.AddWithValue("$mentor", metadata.MentorConfirmed ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public string? FindFingerprint(string fingerprint, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id FROM run_import_metadata WHERE source_fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        return command.ExecuteScalar() as string;
    }
}
