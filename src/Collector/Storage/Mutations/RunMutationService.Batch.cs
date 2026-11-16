using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Application.Mutations;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <content>The mutations that act on several history rows at once.</content>
public sealed partial class RunMutationService
{
    /// <summary>Batch action that files the selected runs under one character profile, or under none.</summary>
    public const string AssignProfileAction = "assign_profile";

    /// <summary>Checks every selected revision and deletion state before changing any row.</summary>
    public BatchRunMutationOutcome BatchMutateRuns(BatchMutateRunsCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Guid.TryParseExact(command.RequestId, "D", out _))
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotUuid("request_id"), "request_id");
        if (command.Action is not ("soft_delete" or "restore" or "purge" or AssignProfileAction))
            throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldOnlyValues("action", "soft_delete / restore / purge / assign_profile"), "action");
        var reason = RunMutationValidation.RequireReason(command.Reason);
        HistoryRetentionService.ValidateIds(command.Runs.Select(run => run.RunId).ToArray());
        if (command.Runs.Any(run => run.ExpectedRevision < 1))
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldAtLeast("expected_revision", 1), "runs");
        var sorted = command.Runs.OrderBy(run => run.RunId, StringComparer.Ordinal).ToArray();
        var fingerprint = MutationSnapshotCodec.Fingerprint("BatchMutateRuns", command.Action, reason,
            string.Join(',', sorted.Select(run => run.RunId + ":" + run.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        // The three older actions keep the fingerprint their stored receipts were written with.
        if (command.Action == AssignProfileAction)
            fingerprint = MutationSnapshotCodec.Fingerprint(fingerprint,
                command.ProfileId?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var committed = _database.RunInTransaction(tx =>
        {
            var stored = _idempotency.TryGetResponse(command.RequestId, tx);
            if (stored is not null)
            {
                var receipt = BatchMutationReceiptCodec.Deserialize(stored);
                if (receipt is null || receipt.Fingerprint != fingerprint)
                    throw new CollectorException(ErrorCodes.IdempotencyConflict, CollectorTexts.HistoryBatchRequestReused(), field: "request_id");
                return receipt.Outcome with { IdempotentReplay = true };
            }
            if (_idempotency.WasAppliedBeforePrune(command.RequestId, tx))
                throw CollectorException.ExpiredIdempotencyResponse(command.RequestId);
            var runs = sorted.Select(target =>
            {
                var run = Load(target.RunId, tx); RequireRevision(run, target.ExpectedRevision);
                // A run keeps its owner in the recycle bin, so both live and deleted rows may be moved.
                if (command.Action == AssignProfileAction) return run;
                if (command.Action == "soft_delete" && run.SoftDeleted)
                    throw new CollectorException(ErrorCodes.AlreadyDeleted, CollectorTexts.HistoryBatchAlreadyDeleted(),
                        new Dictionary<string, object?> { ["run_id"] = run.RunId });
                if (command.Action != "soft_delete" && !run.SoftDeleted)
                    throw new CollectorException(ErrorCodes.NotDeleted, CollectorTexts.HistoryBatchNotDeleted(),
                        new Dictionary<string, object?> { ["run_id"] = run.RunId });
                return run;
            }).ToArray();
            if (command.Action == "purge")
            {
                using var retention = new HistoryRetentionService(_database, _clock); retention.Purge(runs, tx);
            }
            else if (command.Action == AssignProfileAction)
            {
                runs = AssignProfile(runs, command.ProfileId, reason, command.RequestId, fingerprint, tx);
            }
            else
            {
                var deleted = command.Action == "soft_delete";
                foreach (var before in runs)
                {
                    Commit(before, before with { SoftDeleted = deleted }, deleted ? ChangeKind.SoftDelete : ChangeKind.Restore,
                        reason, AuditRequestId(command.RequestId, before), fingerprint, tx, markCorrected: false, clearPendingReview: deleted);
                }
            }
            var outcome = new BatchRunMutationOutcome(
                runs.Length, runs.Select(run => run.RunId).ToArray(), command.Action, IdempotentReplay: false);
            _idempotency.Store(command.RequestId, "BatchMutateRuns",
                BatchMutationReceiptCodec.Serialize(fingerprint, outcome), tx);
            return outcome;
        });
        if (committed.Action == "purge") _onPurged?.Invoke(committed.RunIds);
        return committed;
    }

    /// <summary>
    /// Files <paramref name="runs"/> under <paramref name="profileId"/>, or under no profile when it
    /// is null, and returns the runs that changed owner. A run already there is left as it is: no
    /// revision says nothing happened. Changing the owner corrects no recorded fact, so no run is
    /// marked 已修正, and no baseline moves with the runs - the player sets each character's own.
    /// </summary>
    private MentorRun[] AssignProfile(
        MentorRun[] runs, long? profileId, string reason, string requestId, string fingerprint, SqliteTransaction transaction)
    {
        if (profileId is { } target)
        {
            var profile = _settings.Profiles.Find(target, transaction) ?? throw new CollectorException(
                ErrorCodes.NotFound, CollectorTexts.ProfileNotFound(),
                new Dictionary<string, object?> { ["profile_id"] = target }, field: "profile_id");
            if (runs.FirstOrDefault(run => run.Region != profile.Region) is { } foreign)
                throw new CollectorException(ErrorCodes.BadRequest, CollectorTexts.ProfileRegionMismatch(),
                    new Dictionary<string, object?> { ["run_id"] = foreign.RunId }, field: "profile_id");
        }

        var moved = runs.Where(run => run.ProfileId != profileId).ToArray();
        if (moved.Length == 0)
            throw new CollectorException(ErrorCodes.NoChanges, CollectorTexts.ProfileAssignUnchanged(), field: "profile_id");
        foreach (var before in moved)
        {
            Commit(before, before with { ProfileId = profileId }, ChangeKind.Correct, reason,
                AuditRequestId(requestId, before), fingerprint, transaction, markCorrected: false, clearPendingReview: false);
        }

        return moved;
    }

    // UNIQUE audit request IDs remain per revision; the parent batch receipt is atomic.
    private static string AuditRequestId(string requestId, MentorRun run) =>
        new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(requestId + ":" + run.RunId)).AsSpan(0, 16)).ToString("D");
}
