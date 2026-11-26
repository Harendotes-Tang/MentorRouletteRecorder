using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
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
                CollectorTexts.HistoryRevisionsPaging());
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

    /// <summary>Reads the newest revision of one kind of a run, or null when it has none.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="kind">Change kind to look for.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    internal RunRevision? GetLatestOfKind(string runId, ChangeKind kind, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM run_revisions WHERE run_id = $run_id AND change_kind = $kind " +
            "ORDER BY revision DESC LIMIT 1;";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$kind", EnumWire<ChangeKind>.Format(kind));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    /// <summary>
    /// The runs among <paramref name="runIds"/> whose review the player confirmed
    /// (<see cref="MentorRun.ReviewConfirmed"/>): one query over their revisions, rule in
    /// <see cref="IsReviewConfirmed"/>. Every revision is read, not only the user's corrections, because an
    /// undo is recognised by the revision it reverses, which may be the software's. A revision that cannot be
    /// read leaves its run unconfirmed: the mark is only ever shown on evidence.
    /// </summary>
    /// <param name="runIds">Runs to classify.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    internal IReadOnlySet<string> FindReviewConfirmed(IReadOnlyList<string> runIds, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(runIds);

        var revisions = new Dictionary<string, List<(int Revision, bool UserCorrection, IReadOnlyList<RunFieldChange> Changes)>>(
            StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        if (runIds.Count > 0)
        {
            using var command = _database.CreateCommand();
            command.Transaction = transaction;
            var names = new string[runIds.Count];
            for (var i = 0; i < runIds.Count; i++)
            {
                names[i] = "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.AddWithValue(names[i], runIds[i]);
            }

            command.CommandText =
                "SELECT run_id, revision, changes_json, (actor = 'USER' AND change_kind = 'CORRECT') " +
                $"FROM run_revisions WHERE run_id IN ({string.Join(", ", names)}) " +
                "ORDER BY run_id, revision;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var runId = reader.GetString(0);
                IReadOnlyList<RunFieldChange> changes;
                try
                {
                    changes = DeserializeChanges(reader.GetString(2));
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                {
                    unreadable.Add(runId);
                    continue;
                }

                if (!revisions.TryGetValue(runId, out var list))
                    revisions.Add(runId, list = []);
                list.Add((reader.GetInt32(1), reader.GetInt64(3) != 0, changes));
            }
        }

        return revisions
            .Where(pair => !unreadable.Contains(pair.Key) && IsReviewConfirmed(pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The 已确认 rule over one run's revisions, oldest first (audit 2026-11-26 K3-1, RB-1).
    /// <para>
    /// An undo is recognised by structure: a user correction (actor USER, kind CORRECT) that exactly reverses
    /// the revision right before it, whoever wrote that one (<see cref="IsUndoOf"/>). An undo is never itself a
    /// confirming revision. It switches the revision it ultimately takes back off, and an undo of that undo
    /// switches it on again - so undoing a confirmation clears the mark, undoing that undo restores it because
    /// the confirmation it brings back counts again, and undoing a system revision or the user's blanking of a
    /// field never sets it.
    /// </para>
    /// <para>
    /// Every other user correction is a confirming revision when it cleared <c>pending_review</c> (true → false)
    /// or filled a blank job, duty or entry kind or an UNKNOWN result, and overruled nothing the software
    /// recorded (<see cref="RunMutationRules.OverrulesTheRecord(bool, IReadOnlyList{RunFieldChange})"/>). The
    /// latest confirming revision decides: the run is confirmed when it is in force (not undone). Character and
    /// region moves change only <c>profile_id</c> / <c>region</c> and confirm nothing; deletion and restore have
    /// kinds of their own and system revisions another actor, so neither confirms.
    /// </para>
    /// </summary>
    /// <param name="revisions">The run's revisions, oldest first; <c>UserCorrection</c> marks actor USER, kind CORRECT.</param>
    internal static bool IsReviewConfirmed(
        IReadOnlyList<(int Revision, bool UserCorrection, IReadOnlyList<RunFieldChange> Changes)> revisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);

        // takesBack[i]: the revision an undo chain through i ends in (i itself for one that is no undo);
        // inForce[j]: whether revision j currently stands.
        var takesBack = new int[revisions.Count];
        var inForce = new bool[revisions.Count];
        var latestConfirming = -1;
        for (var i = 0; i < revisions.Count; i++)
        {
            var (revision, userCorrection, changes) = revisions[i];
            if (i > 0 && userCorrection && revisions[i - 1].Revision == revision - 1 &&
                IsUndoOf(changes, revisions[i - 1].Changes))
            {
                takesBack[i] = takesBack[i - 1];
                inForce[takesBack[i]] = !inForce[takesBack[i]];
                continue;
            }

            takesBack[i] = i;
            inForce[i] = true;
            if (userCorrection && IsConfirming(changes))
                latestConfirming = i;
        }

        return latestConfirming >= 0 && inForce[latestConfirming];
    }

    private static bool IsConfirming(IReadOnlyList<RunFieldChange> changes)
    {
        var wasPending = changes.Any(change =>
            change.Field == RunFields.PendingReview && Equals(change.OldValue, true));
        return changes.Any(IsConfirmingChange) && !RunMutationRules.OverrulesTheRecord(wasPending, changes);
    }

    /// <summary>
    /// Whether <paramref name="undo"/> exactly reverses <paramref name="target"/>, as
    /// <c>RunMutationService.UndoRevision</c> writes it: every change of the target put back, and nothing else
    /// changed - except what undo itself adds or leaves out: <c>manually_corrected</c> is history and never
    /// reverted, and a character profile removed since is reset to none.
    /// </summary>
    private static bool IsUndoOf(IReadOnlyList<RunFieldChange> undo, IReadOnlyList<RunFieldChange> target)
    {
        var reversible = target.Where(change => change.Field != RunFields.ManuallyCorrected).ToArray();
        if (reversible.Length == 0)
            return false;

        static bool ProfileReset(RunFieldChange change) =>
            change.Field == RunFields.ProfileId && change.NewValue is null;

        return reversible.All(change => undo.Any(back =>
                back.Field == change.Field && Equals(back.OldValue, change.NewValue) &&
                (Equals(back.NewValue, change.OldValue) || ProfileReset(back)))) &&
            undo.All(back => back.Field == RunFields.ManuallyCorrected || ProfileReset(back) ||
                reversible.Any(change => change.Field == back.Field));
    }

    private static bool IsConfirmingChange(RunFieldChange change) => change.Field switch
    {
        RunFields.PendingReview => Equals(change.OldValue, true) && Equals(change.NewValue, false),
        RunFields.Result => Equals(change.OldValue, "UNKNOWN") && change.NewValue is string result && result != "UNKNOWN",
        RunFields.JobId or RunFields.ContentId or RunFields.DutyName or RunFields.JoinedInProgress =>
            IsBlank(change.OldValue) && !IsBlank(change.NewValue),
        _ => false,
    };

    // The blank values RunMutationRules.OverrulesTheRecord treats as "the software had none".
    private static bool IsBlank(object? value) =>
        value is null or "未知" or "UNKNOWN" || (value is string text && text.Length == 0);

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

    /// <summary>Whether this record's exact import provenance has been moved to another region.</summary>
    internal bool HasRegionMigration(string runId, SqliteTransaction? transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM run_revisions rev,
                json_each(CASE WHEN json_valid(rev.changes_json) THEN rev.changes_json ELSE '[]' END) field
                WHERE rev.run_id = $run_id AND rev.revision > 1
                    AND field.value ->> '$.field' = $field
                    AND field.value ->> '$.old_value' IS NOT NULL
                    AND field.value ->> '$.new_value' IS NOT NULL
            );
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$field", RunAuditFields.Region);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>Reads field changes back from the stored JSON array form.</summary>
    /// <param name="json">Stored JSON.</param>
    public static IReadOnlyList<RunFieldChange> DeserializeChanges(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);

        var rows = JsonSerializer.Deserialize<ChangeRow[]>(json, ChangeJsonOptions) ?? Array.Empty<ChangeRow>();
        if (rows.Any(row => row is null || string.IsNullOrEmpty(row.Field)))
            throw new JsonException("Audit changes must contain non-null rows with a field name.");
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
