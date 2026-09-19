using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// Re-reads <c>mentor_runs.manually_corrected</c> against each run's own revision chain.
///
/// Until 1.3.1 every <c>CorrectRun</c> set the flag, including the player saying how a pending
/// run went - which, on a profile that cannot observe a duty's outcome, is every automatic run.
/// The history page therefore tagged all of them 已修正. The rule is now
/// <see cref="RunMutationRules.OverrulesTheRecord(bool, IReadOnlyList{RunFieldChange})"/>, and
/// rows written under the old one are brought in line here, at startup.
///
/// Not a migration on purpose: the flag is derived from <c>run_revisions</c>, nothing about the
/// schema changes, and a schema version bump would lock an older build out of the database for
/// the sake of a label. The pass is idempotent, touches only rows whose flag is set, spends no
/// revision and leaves <c>updated_at_utc</c> alone - the record did not change, only what the
/// software says about it.
/// </summary>
public static class CorrectedFlagMaintenance
{
    /// <summary>Clears the flag on every run no revision of which overruled the record.</summary>
    /// <param name="database">Open database.</param>
    /// <returns>How many rows were changed.</returns>
    public static int Run(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return database.RunInTransaction(transaction =>
        {
            var flagged = new List<string>();
            using (var select = database.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT run_id FROM mentor_runs WHERE manually_corrected = 1;";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    flagged.Add(reader.GetString(0));
                }
            }

            var changed = 0;
            foreach (var runId in flagged)
            {
                if (WasOverruled(database, runId, transaction))
                {
                    continue;
                }

                using var update = database.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE mentor_runs SET manually_corrected = 0 WHERE run_id = $run_id;";
                update.Parameters.AddWithValue("$run_id", runId);
                changed += update.ExecuteNonQuery();
            }

            return changed;
        });
    }

    /// <summary>
    /// True when some correction by the player overruled the record. A revision that cannot be
    /// read counts as one: the flag is only ever cleared on evidence.
    /// </summary>
    private static bool WasOverruled(
        SqliteDatabase database, string runId, Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT changes_json FROM run_revisions " +
            "WHERE run_id = $run_id AND actor = 'USER' AND change_kind = 'CORRECT' ORDER BY revision;";
        command.Parameters.AddWithValue("$run_id", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            IReadOnlyList<RunFieldChange> changes;
            try
            {
                changes = RunRevisionRepository.DeserializeChanges(reader.GetString(0));
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            {
                return true;
            }

            // Only the diff survives. A run that was pending review when its outcome was decided
            // shows it: acknowledging the review is part of the same revision.
            var wasPending = changes.Any(change =>
                change.Field == RunFields.PendingReview && Equals(change.OldValue, true));
            if (RunMutationRules.OverrulesTheRecord(wasPending, changes))
            {
                return true;
            }
        }

        return false;
    }
}
