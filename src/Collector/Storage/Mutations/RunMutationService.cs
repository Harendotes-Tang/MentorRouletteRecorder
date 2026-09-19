using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Application.Mutations;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// Every write a human can cause, in one transactional place.
///
/// Three properties hold for all of them:
/// <list type="number">
///   <item><description>the run row and its append-only revision row are written in the same
///   transaction, so the audit chain can never be missing a step;</description></item>
///   <item><description>the request is idempotent by <c>request_id</c>: a replay returns the
///   stored outcome without touching the database again;</description></item>
///   <item><description>nothing is ever deleted. Removal is a flag, and undo is a new
///   revision that restores old values (docs/manual-correction.md sections 1, 2 and 4).</description></item>
/// </list>
/// </summary>
public sealed class RunMutationService
{
    private readonly SqliteDatabase _database;
    private readonly RunRepository _runs;
    private readonly RunRevisionRepository _revisions;
    private readonly IdempotencyRepository _idempotency;
    private readonly SettingsRepository _settings;
    private readonly JobCatalog _jobs;
    private readonly DutyCatalog _duties;
    private readonly IClock _clock;

    /// <summary>Creates the service over an open database.</summary>
    /// <param name="database">Open database; the single writer.</param>
    /// <param name="settings">Settings repository, for the achievement baseline.</param>
    /// <param name="clock">Clock used to stamp rows.</param>
    /// <param name="jobs">Job reference table; the embedded one when omitted.</param>
    /// <param name="duties">Duty reference table; the embedded one when omitted.</param>
    public RunMutationService(
        SqliteDatabase database,
        SettingsRepository settings,
        IClock clock,
        JobCatalog? jobs = null,
        DutyCatalog? duties = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);

        _database = database;
        _settings = settings;
        _clock = clock;
        _runs = new RunRepository(database);
        _revisions = new RunRevisionRepository(database);
        _idempotency = new IdempotencyRepository(database, clock);
        _jobs = jobs ?? JobCatalog.Default;
        _duties = duties ?? DutyCatalog.Default;
    }

    /// <summary>Creates a run by hand.</summary>
    /// <param name="command">Validated request.</param>
    public RunMutationOutcome CreateManualRun(CreateManualRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = RunMutationValidation.RequireReason(command.Reason);
        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "CreateManualRun",
            EnumWire<RunResult>.Format(command.Result),
            command.ContentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.JobId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UtcTimestamp.ToTextOrNull(command.MatchedAtUtc),
            UtcTimestamp.ToTextOrNull(command.EnteredAtUtc),
            UtcTimestamp.ToTextOrNull(command.EndedAtUtc),
            command.DurationMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? (command.DurationSpecified ? "null" : null),
            command.ContributesToGoal ? "1" : "0",
            command.DutyName,
            command.DutyCategory,
            command.Note,
            reason);

        return Apply(command.RequestId, "CreateManualRun", fingerprint, tx =>
        {
            var now = UtcTimestamp.Truncate(_clock.UtcNow);
            var job = _jobs.Find(command.JobId);
            var duty = _duties.Find(command.ContentId, Region.Unknown);
            var candidate = new MentorRun
            {
                RunId = Guid.NewGuid().ToString("D"),
                Revision = 1,
                CaptureSessionId = null,
                Region = Region.Unknown,
                GameBuild = null,
                ProtocolProfileId = null,
                MentorRouletteId = null,
                ContentId = command.ContentId,
                TerritoryId = duty?.TerritoryId,
                DutyName = command.DutyName ?? duty?.LocalizedName,
                DutyCategory = command.DutyCategory ?? duty?.DutyCategory,
                DutySource = command.ContentId is null && duty?.TerritoryId is null
                    ? null
                    : DutySource.Manual,
                JobId = command.JobId,
                JobName = command.JobId is null ? JobCatalog.UnknownJobName : job?.NameZh ?? JobCatalog.UnknownJobName,
                Role = job?.Role ?? Role.Unknown,
                MatchedAtUtc = MutationText.Normalize(command.MatchedAtUtc),
                EnteredAtUtc = MutationText.Normalize(command.EnteredAtUtc),
                EndedAtUtc = MutationText.Normalize(command.EndedAtUtc),
                DurationMs = command.DurationMs,
                Result = command.Result,
                DetectionConfidence = DetectionConfidence.None,
                Source = RunSource.Manual,
                ContributesToGoal = command.ContributesToGoal,
                ManuallyCreated = true,
                ManuallyCorrected = false,
                SoftDeleted = false,
                PendingReview = false,
                Note = command.Note,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };

            var run = RunMutationValidation.ValidateFinalValue(
                candidate, command.DurationSpecified || command.DurationMs is not null);
            _runs.Insert(run, tx);

            var revisionId = Guid.NewGuid().ToString("D");
            _revisions.Append(
                new RunRevision
                {
                    RevisionId = revisionId,
                    RunId = run.RunId,
                    Revision = 1,
                    ChangedAtUtc = now,
                    ChangeKind = ChangeKind.CreateManual,
                    Actor = RevisionActor.User,
                    Reason = reason,
                    RequestId = command.RequestId,
                    Changes = InitialChanges(run),
                },
                tx);

            return new MutationSnapshot(fingerprint, run.RunId, 1, revisionId, run, null);
        });
    }

    /// <summary>Corrects an existing run.</summary>
    /// <param name="command">Validated request.</param>
    public RunMutationOutcome CorrectRun(CorrectRunCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = RunMutationValidation.RequireReason(command.Reason);
        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "CorrectRun",
            command.RunId,
            command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MutationSnapshotCodec.Canonical(command.Changes),
            reason);

        return Apply(command.RequestId, "CorrectRun", fingerprint, tx =>
        {
            var before = Load(command.RunId, tx);
            RequireRevision(before, command.ExpectedRevision);

            if (command.Changes.IsEmpty)
            {
                throw new CollectorException(
                    ErrorCodes.NoChanges, "未做任何修改，不会写入修订记录。", field: "changes");
            }

            var patched = ApplyChangeSet(before, command.Changes);
            var durationEndpointsChanged = patched.EnteredAtUtc != before.EnteredAtUtc
                || patched.EndedAtUtc != before.EndedAtUtc;
            var candidate = RunMutationValidation.ValidateFinalValue(
                patched, command.Changes.Has(RunFields.DurationMs), durationEndpointsChanged);

            // Only an outcome decision or an explicit acknowledgement resolves review; a
            // note/job/time correction leaves an unknown outcome to be reviewed. Confirming
            // the existing outcome changes pending_review and earns its own audit revision.
            var acknowledgesReview = command.Changes.Has(RunFields.Result)
                || (command.Changes.Has(RunFields.PendingReview) && command.Changes.PendingReview == false);
            if (!before.PendingReview || !acknowledgesReview)
            {
                RunMutationValidation.RequireChanges(RunMutationRules.Diff(before, candidate));
            }

            // Saying how a pending run went, or filling a field the software left blank, corrects
            // nothing (RunMutationRules.OverrulesTheRecord). The revision is written either way.
            return Commit(
                before, candidate, ChangeKind.Correct, reason, command.RequestId, fingerprint, tx,
                markCorrected: RunMutationRules.OverrulesTheRecord(before, RunMutationRules.Diff(before, candidate)),
                clearPendingReview: acknowledgesReview);
        });
    }

    /// <summary>Marks a run as deleted without removing anything.</summary>
    /// <param name="command">Validated request.</param>
    public RunMutationOutcome SoftDeleteRun(RunReasonCommand command) =>
        SetDeleted(command, deleted: true, ChangeKind.SoftDelete, "SoftDeleteRun");

    /// <summary>Restores a soft-deleted run.</summary>
    /// <param name="command">Validated request.</param>
    public RunMutationOutcome RestoreRun(RunReasonCommand command) =>
        SetDeleted(command, deleted: false, ChangeKind.Restore, "RestoreRun");

    /// <summary>
    /// Undoes the newest revision by appending a new one that restores the previous values.
    /// The undone revision stays in the chain: history is never rewritten.
    /// </summary>
    /// <param name="command">Validated request; the expected revision is the one being undone.</param>
    public RunMutationOutcome UndoRevision(RunReasonCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = RunMutationValidation.RequireReason(command.Reason);
        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "UndoRevision",
            command.RunId,
            command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason);

        return Apply(command.RequestId, "UndoRevision", fingerprint, tx =>
        {
            var before = Load(command.RunId, tx);
            RequireRevision(before, command.ExpectedRevision);

            if (before.Revision <= 1)
            {
                throw new CollectorException(
                    ErrorCodes.UndoNotAllowed,
                    "第 1 条修订是创建记录本身，无法撤销；如需移除请使用软删除。",
                    new Dictionary<string, object?> { ["run_id"] = before.RunId },
                    field: "expected_revision");
            }

            var target = _revisions.GetAt(command.RunId, before.Revision, tx)
                ?? throw CollectorException.NotFound(command.RunId);

            var restored = before;
            foreach (var change in target.Changes)
            {
                restored = RunFieldWriter.Apply(restored, change.Field, change.OldValue);
            }

            var candidate = RunMutationValidation.ValidateFinalValue(restored, durationExplicit: true);
            // A system revision may contain only confidence, which is intentionally
            // absent from the client-editable field diff but is still a real undo.
            if (before.DetectionConfidence == candidate.DetectionConfidence)
            {
                RunMutationValidation.RequireChanges(RunMutationRules.Diff(before, candidate));
            }

            return Commit(
                before, candidate, ChangeKind.Correct, reason, command.RequestId, fingerprint, tx,
                markCorrected: false, clearPendingReview: false);
        });
    }

    /// <summary>Changes the achievement goal and baseline.</summary>
    /// <param name="command">Validated request.</param>
    public BaselineMutationOutcome UpdateAchievementBaseline(UpdateAchievementBaselineCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = RunMutationValidation.RequireReason(command.Reason);
        if (command.GoalCount < 1)
        {
            throw CollectorException.BadRequest("goal_count 必须大于等于 1。", "goal_count");
        }

        if (command.BaselineCompletedCount < 0)
        {
            throw CollectorException.BadRequest(
                "baseline_completed_count 不能为负数。", "baseline_completed_count");
        }

        var fingerprint = MutationSnapshotCodec.Fingerprint(
            "UpdateAchievementBaseline",
            command.GoalCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.BaselineCompletedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UtcTimestamp.ToText(command.BaselineEffectiveAt),
            reason);

        var snapshot = ApplySnapshot(command.RequestId, "UpdateAchievementBaseline", fingerprint, tx =>
        {
            var completed = new StatisticsRepository(_database, _settings, _jobs, _duties, _clock)
                .CountContributingCompleted(tx);
            if (completed > int.MaxValue - (long)command.BaselineCompletedCount)
            {
                throw CollectorException.BadRequest(
                    "已有基数与已记录完成次数的合计超过支持上限，请减小已有基数。",
                    "baseline_completed_count");
            }

            var now = UtcTimestamp.Truncate(_clock.UtcNow);
            var settings = new AchievementSettings
            {
                GoalCount = command.GoalCount,
                BaselineCompletedCount = command.BaselineCompletedCount,
                BaselineEffectiveAt = UtcTimestamp.Truncate(command.BaselineEffectiveAt),
                UpdatedAtUtc = now,
            };
            _settings.UpdateAchievementSettings(settings, tx);

            var auditEventId = Guid.NewGuid().ToString("D");
            _settings.AppendBaselineAudit(auditEventId, settings, reason, command.RequestId, tx);
            return new MutationSnapshot(fingerprint, null, 0, auditEventId, null, settings);
        });

        // The fallback read happens after the transaction has committed, so it needs the
        // database gate of its own; SettingsRepository.GetAchievementSettings takes it when no
        // transaction is supplied (review finding H2).
        return new BaselineMutationOutcome(
            snapshot.Snapshot.Settings ?? _settings.GetAchievementSettings(),
            snapshot.Snapshot.AuditEventId,
            snapshot.Replayed);
    }

    private RunMutationOutcome SetDeleted(
        RunReasonCommand command, bool deleted, ChangeKind kind, string messageType)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reason = RunMutationValidation.RequireReason(command.Reason);
        var fingerprint = MutationSnapshotCodec.Fingerprint(
            messageType,
            command.RunId,
            command.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reason);

        return Apply(command.RequestId, messageType, fingerprint, tx =>
        {
            var before = Load(command.RunId, tx);
            RequireRevision(before, command.ExpectedRevision);

            if (deleted && before.SoftDeleted)
            {
                throw new CollectorException(
                    ErrorCodes.AlreadyDeleted,
                    "该记录已经处于删除状态。",
                    new Dictionary<string, object?> { ["run_id"] = before.RunId });
            }

            if (!deleted && !before.SoftDeleted)
            {
                throw new CollectorException(
                    ErrorCodes.NotDeleted,
                    "该记录并未被删除，无需恢复。",
                    new Dictionary<string, object?> { ["run_id"] = before.RunId });
            }

            var candidate = before with { SoftDeleted = deleted };
            return Commit(
                before, candidate, kind, reason, command.RequestId, fingerprint, tx,
                markCorrected: false, clearPendingReview: deleted);
        });
    }

    private MutationSnapshot Commit(
        MentorRun before,
        MentorRun candidate,
        ChangeKind kind,
        string reason,
        string requestId,
        string fingerprint,
        SqliteTransaction transaction,
        bool markCorrected,
        bool clearPendingReview)
    {
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var final = candidate with
        {
            Revision = before.Revision + 1,
            // This is historical provenance, including after undo restores the values
            // from before the first correction. It must never erase existing history.
            ManuallyCorrected = before.ManuallyCorrected || markCorrected || candidate.ManuallyCorrected,
            PendingReview = !clearPendingReview && candidate.PendingReview,
            UpdatedAtUtc = now,
        };

        var changes = RunMutationRules.Diff(before, final).ToList();
        if (before.DetectionConfidence != final.DetectionConfidence)
        {
            // Undo may restore this system-maintained value from a recovery revision.
            // Keep that actual difference so undoing the undo can replay it as well.
            changes.Add(new RunFieldChange("detection_confidence",
                EnumWire<DetectionConfidence>.Format(before.DetectionConfidence),
                EnumWire<DetectionConfidence>.Format(final.DetectionConfidence)));
        }
        _runs.Update(final, before.Revision, transaction);

        var revisionId = Guid.NewGuid().ToString("D");
        _revisions.Append(
            new RunRevision
            {
                RevisionId = revisionId,
                RunId = final.RunId,
                Revision = final.Revision,
                ChangedAtUtc = now,
                ChangeKind = kind,
                Actor = RevisionActor.User,
                Reason = reason,
                RequestId = requestId,
                Changes = changes,
            },
            transaction);

        return new MutationSnapshot(fingerprint, final.RunId, final.Revision, revisionId, final, null);
    }

    private MentorRun ApplyChangeSet(MentorRun run, RunChangeSet changes)
    {
        var next = run;
        if (changes.Has(RunFields.ContentId))
        {
            var duty = _duties.Find(changes.ContentId, next.Region);
            next = next with
            {
                ContentId = changes.ContentId,
                TerritoryId = duty?.TerritoryId ?? next.TerritoryId,
                DutyName = duty?.LocalizedName,
                DutyCategory = duty?.DutyCategory,
                DutySource = DutySource.Manual,
            };
        }

        if (changes.Has(RunFields.DutyName))
        {
            next = next with { DutyName = changes.DutyName };
        }

        if (changes.Has(RunFields.DutyCategory))
        {
            next = next with { DutyCategory = changes.DutyCategory };
        }

        if (changes.Has(RunFields.JobId))
        {
            var job = _jobs.Find(changes.JobId);
            next = next with
            {
                JobId = changes.JobId,
                JobName = job?.NameZh ?? JobCatalog.UnknownJobName,
                Role = job?.Role ?? Role.Unknown,
            };
        }

        if (changes.Has(RunFields.MatchedAtUtc))
        {
            next = next with { MatchedAtUtc = MutationText.Normalize(changes.MatchedAtUtc) };
        }

        if (changes.Has(RunFields.EnteredAtUtc))
        {
            next = next with { EnteredAtUtc = MutationText.Normalize(changes.EnteredAtUtc) };
        }

        if (changes.Has(RunFields.EndedAtUtc))
        {
            next = next with { EndedAtUtc = MutationText.Normalize(changes.EndedAtUtc) };
        }

        if (changes.Has(RunFields.DurationMs))
        {
            next = next with { DurationMs = changes.DurationMs };
        }

        if (changes.Has(RunFields.Result) && changes.Result is { } result)
        {
            next = next with { Result = result };
        }

        if (changes.Has(RunFields.ContributesToGoal) && changes.ContributesToGoal is { } contributes)
        {
            next = next with { ContributesToGoal = contributes };
        }

        if (changes.Has(RunFields.Note))
        {
            next = next with { Note = changes.Note };
        }

        return next;
    }

    private MentorRun Load(string runId, SqliteTransaction transaction) =>
        _runs.GetInternal(runId, transaction) ?? throw CollectorException.NotFound(runId);

    private static void RequireRevision(MentorRun run, int expectedRevision)
    {
        if (run.Revision == expectedRevision)
        {
            return;
        }

        throw new CollectorException(
            ErrorCodes.RevisionConflict,
            $"该记录已被修改（当前 revision = {run.Revision}），请刷新后重试。",
            new Dictionary<string, object?>
            {
                ["run_id"] = run.RunId,
                ["current_revision"] = run.Revision,
                ["expected_revision"] = expectedRevision,
            });
    }

    private RunMutationOutcome Apply(
        string requestId, string messageType, string fingerprint, Func<SqliteTransaction, MutationSnapshot> work)
    {
        var applied = ApplySnapshot(requestId, messageType, fingerprint, work);
        return new RunMutationOutcome(
            applied.Snapshot.RunId ?? string.Empty,
            applied.Snapshot.Revision,
            applied.Snapshot.AuditEventId,
            applied.Replayed,
            applied.Snapshot.Run);
    }

    private (MutationSnapshot Snapshot, bool Replayed) ApplySnapshot(
        string requestId, string messageType, string fingerprint, Func<SqliteTransaction, MutationSnapshot> work)
    {
        if (!Guid.TryParseExact(requestId, "D", out _))
        {
            throw CollectorException.BadRequest("request_id 必须是标准 UUID。", "request_id");
        }

        return _database.RunInTransaction(tx =>
        {
            if (_idempotency.TryGetResponse(requestId, tx) is { } stored)
            {
                var snapshot = MutationSnapshotCodec.Deserialize(stored)
                    ?? throw new CollectorException(
                        ErrorCodes.Internal, "幂等记录已损坏，无法安全重放该请求。");

                if (!string.Equals(snapshot.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    // Refusing is the only safe answer: replaying the stored result would tell
                    // the client its *new* change was applied. See contracts/error-codes.md and
                    // docs/manual-correction.md section 4. details.conflict is kept for clients
                    // written against the older ERR_BAD_REQUEST spelling.
                    throw new CollectorException(
                        ErrorCodes.IdempotencyConflict,
                        "同一个 request_id 被用于内容不同的请求，已拒绝以免产生歧义的结果。",
                        new Dictionary<string, object?> { ["conflict"] = "idempotency" },
                        field: "request_id");
                }

                return (snapshot, true);
            }

            // No stored response, but the append-only audit chain may still remember this
            // request id from before the 24 h retention sweep. Applying it again would hit
            // UNIQUE(request_id) and surface as ERR_INTERNAL; the contract code says what
            // actually happened (review finding M-7).
            if (_idempotency.WasAppliedBeforePrune(requestId, tx))
            {
                throw ExpiredReplay(requestId);
            }

            try
            {
                var result = work(tx);
                _idempotency.Store(requestId, messageType, MutationSnapshotCodec.Serialize(result), tx);
                return (result, false);
            }
            catch (SqliteException ex) when (IsRequestIdConflict(ex))
            {
                // Belt and braces: a concurrent writer, or a row this process cannot see,
                // still ends as the contract code rather than as an internal error.
                throw ExpiredReplay(requestId);
            }
        });
    }

    /// <summary>True for a UNIQUE violation on a <c>request_id</c> column.</summary>
    /// <param name="ex">Failure raised by the write.</param>
    private static bool IsRequestIdConflict(SqliteException ex) =>
        ex.SqliteErrorCode == 19 &&
        ex.Message.Contains("request_id", StringComparison.OrdinalIgnoreCase);

    /// <summary>The answer to a replay whose stored response has already been pruned.</summary>
    /// <param name="requestId">Client-generated request id.</param>
    private static CollectorException ExpiredReplay(string requestId) => new(
        ErrorCodes.IdempotencyConflict,
        "该请求编号已经执行过，但可重放的结果已超出保留期，请用新的请求编号重试。",
        new Dictionary<string, object?>
        {
            ["conflict"] = "idempotency",
            ["request_id"] = requestId,
            ["reason"] = "RESPONSE_EXPIRED",
        },
        field: "request_id");

    /// <summary>
    /// A create revision records the full initial value set, not a difference
    /// (docs/manual-correction.md section 2, invariant 6), keeping the originally entered
    /// values reachable through revision 1.
    /// </summary>
    private static IReadOnlyList<RunFieldChange> InitialChanges(MentorRun run) =>
        new RunFieldChange[]
        {
            new(RunFields.ContentId, null, run.ContentId),
            new(RunFields.DutyName, null, run.DutyName),
            new(RunFields.DutyCategory, null, run.DutyCategory),
            new(RunFields.JobId, null, run.JobId),
            new(RunFields.JobName, null, run.JobName),
            new(RunFields.Role, null, EnumWire<Role>.Format(run.Role)),
            new(RunFields.MatchedAtUtc, null, UtcTimestamp.ToTextOrNull(run.MatchedAtUtc)),
            new(RunFields.EnteredAtUtc, null, UtcTimestamp.ToTextOrNull(run.EnteredAtUtc)),
            new(RunFields.EndedAtUtc, null, UtcTimestamp.ToTextOrNull(run.EndedAtUtc)),
            new(RunFields.DurationMs, null, run.DurationMs),
            new(RunFields.Result, null, EnumWire<RunResult>.Format(run.Result)),
            new(RunFields.ContributesToGoal, null, run.ContributesToGoal),
            new(RunFields.Note, null, run.Note),
            new(RunFields.SoftDeleted, null, run.SoftDeleted),
            new(RunFields.PendingReview, null, run.PendingReview),
            new(RunFields.ManuallyCorrected, null, run.ManuallyCorrected),
        };
}
