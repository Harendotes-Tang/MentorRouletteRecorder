using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Contracts.Errors;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MentorRecorder.Collector.Import;

/// <summary>Audited, idempotent compatibility maintenance for proven historical imports.</summary>
public static class ImportedHistoryMaintenance
{
    /// <summary>Stable audit reason used to recognize non-undoable import compatibility maintenance.</summary>
    public const string AuditReason = "依据导入来源审计维护历史身份；缺游戏时间保留待补充，不作为采集结果待复核。";
    /// <summary>
    /// Separates historical time gaps from outcome review before capture recovery. Only
    /// explicit source, metadata or a local IMPORT creation revision can select a row;
    /// missing entry alone never identifies an import or changes its result. A complete
    /// local audit chain can additionally undo proven capture restart changes to imported
    /// outcome/time facts, preserving subsequent human changes to unrelated fields.
    /// </summary>
    /// <param name="host">Open local host; this pass writes only its database.</param>
    /// <returns>Number of rows changed and given a system correction revision.</returns>
    public static int Run(CollectorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var changed = host.Database.RunInTransaction(transaction =>
        {
            var ids = new List<string>();
            using (var command = host.Database.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT run_id FROM mentor_runs WHERE " +
                    RunFilterSql.ImportedHistoryPredicate + " AND (source != 'IMPORT' OR pending_review = 1 " +
                    "OR result IN ('CANCELLED_BEFORE_ENTRY', 'INTERRUPTED'));";
                using var reader = command.ExecuteReader();
                while (reader.Read()) ids.Add(reader.GetString(0));
            }
            var count = 0;
            foreach (var id in ids)
            {
                var before = host.Runs.GetInternal(id, transaction)!;
                if (!HasAppendableAuditChain(host, before, transaction)) continue;
                var restored = RestoreProvenRestartChanges(host, before, transaction);
                var clearTimeReview = restored.PendingReview && (restored.Result == RunResult.Unknown
                    || (restored.Result != RunResult.CancelledBeforeEntry
                        && (restored.EnteredAtUtc is null || (restored.Result == RunResult.Completed && restored.EndedAtUtc is null))));
                var now = UtcTimestamp.Truncate(host.Clock.UtcNow);
                var after = restored with
                {
                    Source = RunSource.Import,
                    PendingReview = restored.PendingReview && !clearTimeReview,
                    Revision = before.Revision + 1,
                    UpdatedAtUtc = now,
                };
                var changes = RunMutationRules.Diff(before, after).ToList();
                if (before.Source != RunSource.Import)
                    changes.Insert(0, new("source", EnumWire<RunSource>.Format(before.Source), "IMPORT"));
                if (changes.Count == 0) continue;
                host.Runs.Update(after, before.Revision, transaction);
                host.Revisions.Append(new RunRevision
                {
                    RevisionId = Guid.NewGuid().ToString("D"), RunId = id, Revision = after.Revision,
                    ChangedAtUtc = now, ChangeKind = ChangeKind.Correct, Actor = RevisionActor.System,
                    Reason = AuditReason,
                    Changes = changes,
                }, transaction);
                count++;
            }
            return count;
        });
        if (changed > 0) host.LiveEvents.PublishStatsInvalidated("imported_history_maintenance");
        return changed;
    }

    private static readonly string[] RecoveryFacts =
    [RunFields.MatchedAtUtc, RunFields.EnteredAtUtc, RunFields.EndedAtUtc,
        RunFields.DurationMs, RunFields.Result, RunFields.PendingReview];

    private sealed record EvidenceRevision(int Number, string Kind, string Actor, string? Reason,
        string ChangedAtUtc, string ChangesJson);

    private static bool HasAppendableAuditChain(CollectorHost host, MentorRun run, SqliteTransaction transaction)
    {
        using var command = host.Database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*), COALESCE(MAX(revision), 0) FROM run_revisions WHERE run_id = $id;";
        command.Parameters.AddWithValue("$id", run.RunId);
        using var reader = command.ExecuteReader();
        return reader.Read() && reader.GetInt32(0) == run.Revision && reader.GetInt32(1) == run.Revision;
    }

    /// <summary>
    /// Replays only locally recorded facts. No missing audit value is inferred from the
    /// current row, and a corrupt or incomplete audit record cannot prevent startup.
    /// </summary>
    private static MentorRun RestoreProvenRestartChanges(CollectorHost host, MentorRun current, SqliteTransaction transaction)
    {
        if (current.Result is not (RunResult.CancelledBeforeEntry or RunResult.Interrupted)) return current;
        try
        {
            var chain = new List<EvidenceRevision>();
            using (var command = host.Database.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT revision, change_kind, actor, reason, changed_at_utc, changes_json " +
                    "FROM run_revisions WHERE run_id = $id ORDER BY revision;";
                command.Parameters.AddWithValue("$id", current.RunId);
                using var reader = command.ExecuteReader();
                while (reader.Read()) chain.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
            if (chain.Count < 2 || chain.Count != current.Revision || chain[0] is not { Number: 1, Kind: "IMPORT", Actor: "USER" })
                return current;
            var first = ReadChanges(chain[0].ChangesJson);
            if (first.Any(change => change.OldValue is not null) || first.Select(change => change.Field).Distinct().Count() != first.Count)
                return current;
            var snapshot = first.ToDictionary(change => change.Field, change => change.NewValue, StringComparer.Ordinal);
            if (!snapshot.TryGetValue("run_id", out var initialId) || !Equals(initialId, current.RunId)
                || !snapshot.TryGetValue("revision", out var initialRevision) || !Equals(initialRevision, 1L)
                || !snapshot.TryGetValue("source", out var initialSource) || initialSource is not string sourceText
                || !EnumWire<RunSource>.TryParse(sourceText, out _)
                || !snapshot.TryGetValue("capture_session_id", out var initialSession) || !Equals(initialSession, current.CaptureSessionId)
                || RecoveryFacts.Any(field => !snapshot.ContainsKey(field))) return current;

            var original = current with { Source = RunSource.Import };
            foreach (var field in RecoveryFacts) original = RunFieldWriter.Apply(original, field, snapshot[field]);
            if (original.Result is RunResult.Unknown or RunResult.CancelledBeforeEntry || original.EndedAtUtc is not null)
                return current;
            _ = RunMutationRules.ValidateFinalValue(original, durationExplicit: true, recalculateDuration: false);
            var facts = RecoveryFacts.ToDictionary(field => field, field => snapshot[field], StringComparer.Ordinal);
            var hadRestart = false;
            var expectedRevision = 2;
            foreach (var revision in chain.Skip(1))
            {
                if (revision.Number != expectedRevision++) return current;
                var changes = ReadChanges(revision.ChangesJson);
                if (changes.Select(change => change.Field).Distinct().Count() != changes.Count) return current;
                var affected = changes.Where(change => facts.ContainsKey(change.Field)).ToArray();
                foreach (var change in affected)
                    if (!Equals(change.OldValue, facts[change.Field])) return current;

                if (revision.Actor == "USER")
                {
                    // An acknowledgement is an outcome decision too; never take it back.
                    if (affected.Length != 0) return current;
                }
                else if (revision.Actor == "SYSTEM" && revision.Kind == "CORRECT" && revision.Reason == AuditReason)
                {
                    // An earlier identity/time-review maintenance is safe to replay, but
                    // our own outcome restoration makes a second restoration ineligible.
                    if (changes.Any(change => change.Field is not ("source" or "pending_review"))) return current;
                }
                else
                {
                    if (revision.Actor != "SYSTEM" || revision.Kind != "CORRECT"
                        || revision.Reason is not ("程序重启时发现未完结记录，已记为中断并标记待复核。"
                            or "程序重启时发现未完结记录，它尚未进入副本，已记为进本前取消并标记待复核。")
                        || changes.Any(change => !facts.ContainsKey(change.Field) && change.Field != "detection_confidence")
                        || facts[RunFields.EndedAtUtc] is not null) return current;
                    var afterFacts = new Dictionary<string, object?>(facts, StringComparer.Ordinal);
                    foreach (var change in affected) afterFacts[change.Field] = change.NewValue;
                    var expectedResult = facts[RunFields.EnteredAtUtc] is null ? "CANCELLED_BEFORE_ENTRY" : "INTERRUPTED";
                    if (!Equals(afterFacts[RunFields.Result], expectedResult)
                        || !Equals(afterFacts[RunFields.PendingReview], true)
                        || afterFacts[RunFields.EndedAtUtc] is not string endText || !UtcTimestamp.TryParse(endText, out _)
                        || !HasRestartEvent(host, current, revision.ChangedAtUtc, expectedResult, transaction)) return current;
                    hadRestart = true;
                }
                foreach (var change in affected) facts[change.Field] = change.NewValue;
            }
            if (!hadRestart) return current;
            var currentWire = Wire.Run(current);
            foreach (var field in RecoveryFacts)
            {
                var value = currentWire[field];
                object? plain = value is null ? null : value is System.Text.Json.Nodes.JsonValue scalar
                    ? scalar.TryGetValue<string>(out var text) ? text : scalar.TryGetValue<bool>(out var flag) ? flag
                        : scalar.GetValue<long>() : null;
                if (!Equals(plain, facts[field])) return current;
            }
            return original with { PendingReview = false };
        }
        catch (Exception failure) when (failure is JsonException or CollectorException or RunRuleViolationException
            or FormatException or ArgumentException or InvalidOperationException)
        {
            return current;
        }
    }

    private static IReadOnlyList<RunFieldChange> ReadChanges(string json)
    {
        // The general audit reader accepts historical JSON value types. Here the
        // container shape must also be complete before any evidence is replayed.
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Audit changes are not an array.");
        foreach (var row in document.RootElement.EnumerateArray())
            if (row.ValueKind != JsonValueKind.Object
                || row.EnumerateObject().Select(property => property.Name).Distinct().Count() != row.EnumerateObject().Count()
                || !row.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString())
                || !row.TryGetProperty("old_value", out var oldValue) || !row.TryGetProperty("new_value", out var newValue)
                || !ValidEvidenceValue(field.GetString()!, oldValue) || !ValidEvidenceValue(field.GetString()!, newValue))
                throw new JsonException("Audit change is incomplete.");
        return RunRevisionRepository.DeserializeChanges(json);
    }

    private static bool ValidEvidenceValue(string field, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return true; // Creation old values and unknown endpoints.
        return field switch
        {
            RunFields.MatchedAtUtc or RunFields.EnteredAtUtc or RunFields.EndedAtUtc =>
                value.ValueKind == JsonValueKind.String && UtcTimestamp.TryParse(value.GetString(), out _),
            RunFields.DurationMs => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var duration) && duration >= 0,
            RunFields.PendingReview => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            RunFields.Result => value.ValueKind == JsonValueKind.String && EnumWire<RunResult>.TryParse(value.GetString(), out _),
            "revision" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var revision) && revision >= 1,
            _ => true,
        };
    }

    private static bool HasRestartEvent(CollectorHost host, MentorRun run, string occurredAtUtc, string result, SqliteTransaction transaction)
    {
        using var command = host.Database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM run_events WHERE run_id = $id AND event_type = 'PROCESS_RESTART' " +
            "AND occurred_at_utc = $at AND confidence = 'LOW' AND from_state IS NULL " +
            "AND to_state = $state AND event_key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$id", run.RunId);
        command.Parameters.AddWithValue("$at", occurredAtUtc);
        command.Parameters.AddWithValue("$state", result == "CANCELLED_BEFORE_ENTRY" ? result : "INTERRUPTED_PENDING_REVIEW");
        command.Parameters.AddWithValue("$key", new EventKey(run.CaptureSessionId ?? run.RunId,
            PacketDirection.None, "PROCESS_RESTART", 0, null, "restart:" + run.RunId).ToCanonicalString());
        return command.ExecuteScalar() is not null;
    }
}
