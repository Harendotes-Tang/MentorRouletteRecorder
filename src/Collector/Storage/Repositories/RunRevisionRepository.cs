using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Append-only access to <c>run_revisions</c>.
///
/// The class exposes <see cref="Append"/> and read methods and nothing else. There is no
/// update and no delete, and the database refuses both with a trigger, so the audit chain
/// cannot be rewritten even by a future mistake (docs/manual-correction.md section 2).
/// </summary>
public sealed class RunRevisionRepository
{
    private const string Columns =
        "revision_id, run_id, revision, changed_at_utc, change_kind, actor, reason, request_id, changes_json";

    private static readonly JsonSerializerOptions ChangeJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly SqliteDatabase _database;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public RunRevisionRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Appends one revision row.</summary>
    /// <param name="revision">Revision to append.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Append(RunRevision revision, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO run_revisions ({Columns}) VALUES (" +
            "$revision_id, $run_id, $revision, $changed_at_utc, $change_kind, $actor, $reason, " +
            "$request_id, $changes_json);";
        command.Parameters.AddWithValue("$revision_id", revision.RevisionId);
        command.Parameters.AddWithValue("$run_id", revision.RunId);
        command.Parameters.AddWithValue("$revision", revision.Revision);
        command.Parameters.AddWithValue("$changed_at_utc", UtcTimestamp.ToText(revision.ChangedAtUtc));
        command.Parameters.AddWithValue("$change_kind", EnumWire<ChangeKind>.Format(revision.ChangeKind));
        command.Parameters.AddWithValue("$actor", EnumWire<RevisionActor>.Format(revision.Actor));
        command.Parameters.AddWithValue("$reason", (object?)revision.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$request_id", (object?)revision.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$changes_json", SerializeChanges(revision.Changes));
        command.ExecuteNonQuery();
    }

    /// <summary>Reads the revision chain of a run, oldest first, paged.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size.</param>
    public Page<RunRevision> ListForRun(string runId, int page, int pageSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        if (page < 1 || pageSize < 1 || pageSize > Paging.MaxPageSize)
        {
            throw MentorRecorder.Collector.Contracts.Errors.CollectorException.BadRequest(
                "page 必须大于等于 1，page_size 必须在 1 到 200 之间。");
        }

        return _database.Read(_ =>
        {
            int total;
            using (var countCommand = _database.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM run_revisions WHERE run_id = $run_id;";
                countCommand.Parameters.AddWithValue("$run_id", runId);
                total = Convert.ToInt32(
                    countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            using var command = _database.CreateCommand();
            command.CommandText =
                $"SELECT {Columns} FROM run_revisions WHERE run_id = $run_id " +
                "ORDER BY revision ASC LIMIT $limit OFFSET $offset;";
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", ((long)page - 1) * pageSize);
            using var reader = command.ExecuteReader();

            var items = new List<RunRevision>();
            while (reader.Read())
            {
                items.Add(ReadRow(reader));
            }

            return new Page<RunRevision>(items, page, pageSize, total);
        });
    }

    /// <summary>Reads the first revision of a run, which holds the originally detected values.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public RunRevision? GetFirst(string runId, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM run_revisions WHERE run_id = $run_id ORDER BY revision ASC LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>Reads one specific revision of a run.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="revision">Revision number.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public RunRevision? GetAt(string runId, int revision, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM run_revisions WHERE run_id = $run_id AND revision = $revision;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$revision", revision);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static RunRevision ReadRow(SqliteDataReader reader) => new()
    {
        RevisionId = reader.GetString(0),
        RunId = reader.GetString(1),
        Revision = reader.GetInt32(2),
        ChangedAtUtc = UtcTimestamp.Parse(reader.GetString(3)),
        ChangeKind = EnumWire<ChangeKind>.Parse(reader.GetString(4)),
        Actor = EnumWire<RevisionActor>.Parse(reader.GetString(5)),
        Reason = reader.IsDBNull(6) ? null : reader.GetString(6),
        RequestId = reader.IsDBNull(7) ? null : reader.GetString(7),
        Changes = DeserializeChanges(reader.GetString(8)),
    };

    /// <summary>Serialises field changes into the stored JSON array form.</summary>
    /// <param name="changes">Changes to serialise.</param>
    public static string SerializeChanges(IReadOnlyList<RunFieldChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var rows = changes
            .Select(c => new ChangeRow(c.Field, c.OldValue, c.NewValue))
            .ToArray();
        return JsonSerializer.Serialize(rows, ChangeJsonOptions);
    }

    /// <summary>Reads field changes back from the stored JSON array form.</summary>
    /// <param name="json">Stored JSON.</param>
    public static IReadOnlyList<RunFieldChange> DeserializeChanges(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        var rows = JsonSerializer.Deserialize<ChangeRow[]>(json, ChangeJsonOptions) ?? Array.Empty<ChangeRow>();
        return rows
            .Select(r => new RunFieldChange(r.Field, Unwrap(r.OldValue), Unwrap(r.NewValue)))
            .ToArray();
    }

    private static object? Unwrap(object? value) => value is JsonElement element
        ? element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // Box before the conditional can promote both numeric branches to double.
            // Undo requires integer identities and must preserve Int64 durations exactly.
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            _ => element.ToString(),
        }
        : value;

    private sealed record ChangeRow(
        [property: System.Text.Json.Serialization.JsonPropertyName("field")] string Field,
        [property: System.Text.Json.Serialization.JsonPropertyName("old_value")] object? OldValue,
        [property: System.Text.Json.Serialization.JsonPropertyName("new_value")] object? NewValue);
}
