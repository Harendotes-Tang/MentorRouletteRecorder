using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The run mutations - create, correct, soft delete, restore and undo: what each accepts and refuses,
/// what counts as a correction, and the two properties the audit chain rests on: idempotency and
/// append-only history. The error envelopes themselves are pinned by MutationErrorCompatibilityTests.
/// </summary>
public sealed class MutationTests
{
    private static RunChangeSet Change(string field, RunChangeSet value) =>
        value with { Specified = new HashSet<string>(StringComparer.Ordinal) { field } };

    private sealed class Fixture : RunMutationTestContext
    {
        public static string NewId() => Guid.NewGuid().ToString("D");

        public RunMutationOutcome CreateRun(
            RunResult result = RunResult.Completed,
            string reason = "补录 9 月 3 日的一次导随") =>
            Service.CreateManualRun(TestDatabase.ManualRun(result) with { Region = Region.Unknown, Reason = reason });

        /// <summary>
        /// A CN run of unknown outcome, entered now and ended a minute later, put into review the way
        /// the machine does it: the flag is set in place without spending a revision.
        /// </summary>
        public RunMutationOutcome CreatePendingReview(long? duration = null)
        {
            var created = Service.CreateManualRun(new CreateManualRunCommand
            {
                RequestId = NewId(), Reason = "回归记录", Result = RunResult.Unknown, Region = Region.Cn,
                EnteredAtUtc = Database.Clock.UtcNow, EndedAtUtc = Database.Clock.UtcNow.AddSeconds(60),
                DurationMs = duration,
            });
            Database.Database.RunInTransaction(tx =>
                Runs.Update(created.Run! with { PendingReview = true }, created.Run!.Revision, tx));
            return created;
        }
    }

    [Fact]
    public void CreateManualRun_StoresManualProvenanceAndFirstRevision()
    {
        using var fixture = new Fixture();

        var outcome = fixture.CreateRun();

        var run = fixture.Runs.Get(outcome.RunId)!;
        Assert.Equal(RunSource.Manual, run.Source);
        Assert.True(run.ManuallyCreated);
        Assert.False(run.ManuallyCorrected);
        Assert.Equal(DetectionConfidence.None, run.DetectionConfidence);
        Assert.Null(run.CaptureSessionId);
        Assert.Equal(1_800_000, run.DurationMs);
        Assert.Equal(1, run.Revision);

        var first = fixture.Revisions.GetFirst(outcome.RunId, null)!;
        Assert.Equal(ChangeKind.CreateManual, first.ChangeKind);
        Assert.Equal(RevisionActor.User, first.Actor);
        Assert.Contains(first.Changes, change => change.Field == "result");
    }

    [Fact]
    public void CorrectRun_SelectingDutyResolvesItsNameAndCategory()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun();
        var duty = DutyCatalog.Default.Find(9, Region.Unknown)!;
        Assert.NotNull(duty);
        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, 1, "手动匹配副本",
            Change(RunFields.ContentId, new() { ContentId = duty.ContentId })));
        var matched = fixture.Runs.Get(created.RunId)!;
        Assert.Equal(duty.LocalizedName, matched.DutyName);
        Assert.Equal(duty.DutyCategory, matched.DutyCategory);
        Assert.Equal(duty.TerritoryId, matched.TerritoryId);

    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateManualRun_WithoutReason_IsRefused(string reason)
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CreateManualRun(
            new CreateManualRunCommand
            {
                RequestId = Fixture.NewId(),
                Reason = reason,
                Result = RunResult.Completed,
            }));

        Assert.Equal(ErrorCodes.ReasonRequired, error.Code);
    }

    [Fact]
    public void CorrectRun_OnMissingRun_IsNotFound()
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(),
                Fixture.NewId(),
                1,
                "改一条不存在的记录",
                Change(RunFields.Result, new() { Result = RunResult.Completed }))));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Fact]
    public void CorrectRun_WithStaleRevision_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(),
                run.RunId,
                7,
                "并发修改",
                Change(RunFields.Result, new() { Result = RunResult.Disconnected }))));

        Assert.Equal(ErrorCodes.RevisionConflict, error.Code);
        Assert.Equal(1, error.Details!["current_revision"]);
    }

    /// <summary>
    /// The player asked why every automatic record was tagged 已修正. The shipping profile cannot
    /// observe whether a duty was cleared, so every run ends pending review and the player says
    /// how it went in 本次导随结果 - and that answer was filed as a correction. Supplying the
    /// outcome the software could not see, or filling a field it left blank, corrects nothing.
    /// </summary>
    [Fact]
    public void CorrectRun_ConfirmingTheOutcomeAndFillingABlankIsNotACorrection()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun(RunResult.Unknown);
        var stored = fixture.Runs.Get(created.RunId)!;
        fixture.Database.Database.RunInTransaction(tx => fixture.Runs.Update(
            stored with { PendingReview = true, JobId = null, JobName = "未知", Role = Role.Unknown },
            stored.Revision, tx));

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, stored.Revision, "确认通关并补录职业",
            new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Result, RunFields.JobId },
                Result = RunResult.Completed,
                JobId = 19,
            }));

        var after = fixture.Runs.Get(created.RunId)!;
        Assert.Equal(RunResult.Completed, after.Result);
        Assert.Equal(19, after.JobId);
        Assert.False(after.PendingReview);
        Assert.False(after.ManuallyCorrected);
    }

    [Fact]
    public void EditingTheNoteOfAPendingRunDoesNotAcknowledgeItsResult()
    {
        using var fixture = new Fixture();
        var created = fixture.CreatePendingReview(duration: 30_000);

        var corrected = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, created.Revision, "仅补备注", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Note }, Note = "结局尚未核实",
            }));

        // A note corrects nothing the software recorded (RunMutationRules.OverrulesTheRecord).
        Assert.False(corrected.Run!.ManuallyCorrected);
        Assert.True(corrected.Run.PendingReview);
        Assert.Equal(30_000L, corrected.Run.DurationMs);
        Assert.Equal(1, new StatisticsRepository(fixture.Database.Database, fixture.Settings).GetDashboard().UnfinishedPendingReview);
        var revision = fixture.Revisions.GetAt(created.RunId, corrected.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.PendingReview);
    }

    /// <summary>
    /// Deciding the result, keeping it as unknown on purpose, or acknowledging the review on its own
    /// each resolve the review, and none of them is a correction.
    /// </summary>
    [Theory]
    [InlineData("result")]
    [InlineData("unchanged_result")]
    [InlineData("pending_review")]
    public void AnExplicitResultDecisionOrAcknowledgementResolvesPendingReview(string decision)
    {
        using var fixture = new Fixture();
        var created = fixture.CreatePendingReview();
        var changes = decision == "pending_review"
            ? new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.PendingReview }, PendingReview = false,
            }
            : new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Result },
                Result = decision == "result" ? RunResult.Completed : RunResult.Unknown,
            };

        var corrected = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, created.Revision, "明确确认本次结局", changes));

        Assert.False(corrected.Run!.PendingReview);
        // Answering an open question is a confirmation, not a correction: the record said
        // "unknown, please review" and the player reviewed it.
        Assert.False(corrected.Run.ManuallyCorrected);
        Assert.Equal(decision == "result" ? RunResult.Completed : RunResult.Unknown, corrected.Run.Result);
        Assert.Equal(created.Revision + 1, corrected.Revision);
        Assert.Equal(0, new StatisticsRepository(fixture.Database.Database, fixture.Settings).GetDashboard().UnfinishedPendingReview);
        var revision = fixture.Revisions.GetAt(created.RunId, corrected.Revision, null)!;
        Assert.Contains(revision.Changes, change => change.Field == RunFields.PendingReview
            && Equals(change.OldValue, true) && Equals(change.NewValue, false));
    }

    /// <summary>Changing something the software DID record is a correction, outcome included.</summary>
    [Fact]
    public void CorrectRun_ChangingARecordedValueIsACorrection()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun(RunResult.Completed);
        var stored = fixture.Runs.Get(created.RunId)!;

        // Not pending review: the outcome was settled, so changing it overrules the record.
        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, stored.Revision, "其实是中途退出",
            Change(RunFields.Result, new() { Result = RunResult.LeftOrAbandoned })));
        Assert.True(fixture.Runs.Get(created.RunId)!.ManuallyCorrected);

        var second = fixture.CreateRun(RunResult.Completed);
        var other = fixture.Runs.Get(second.RunId)!;
        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), second.RunId, other.Revision, "职业记错了",
            Change(RunFields.JobId, new() { JobId = 21 })));
        Assert.True(fixture.Runs.Get(second.RunId)!.ManuallyCorrected);
    }

    /// <summary>
    /// How the duty was entered: filling it in where capture saw no director signal supplies what
    /// the software could not know; changing what it recorded overrules the record
    /// (docs/manual-correction.md section 7). Undo restores the earlier value like any other.
    /// </summary>
    [Fact]
    public void CorrectRun_FillingHowTheDutyWasEnteredIsNotACorrectionButChangingItIs()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun(RunResult.Completed);
        var stored = fixture.Runs.Get(created.RunId)!;
        Assert.Null(stored.JoinedInProgress);

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, stored.Revision, "这一场是中途加入的",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = true })));
        var filled = fixture.Runs.Get(created.RunId)!;
        Assert.True(filled.JoinedInProgress);
        Assert.False(filled.ManuallyCorrected);

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, filled.Revision, "记错了，是开局进的",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = false })));
        var overruled = fixture.Runs.Get(created.RunId)!;
        Assert.False(overruled.JoinedInProgress);
        Assert.True(overruled.ManuallyCorrected);

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, overruled.Revision, "其实不记得了",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = null })));
        var cleared = fixture.Runs.Get(created.RunId)!;
        Assert.Null(cleared.JoinedInProgress);

        fixture.Service.UndoRevision(new RunReasonCommand(Fixture.NewId(), created.RunId, cleared.Revision, "撤销"));
        Assert.False(fixture.Runs.Get(created.RunId)!.JoinedInProgress);

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, fixture.Runs.Get(created.RunId)!.Revision, "同一个值",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = false }))));
        Assert.Equal(ErrorCodes.NoChanges, error.Code);
    }

    /// <summary>The idempotency fingerprint sees the value: the same request id with another value is a conflict.</summary>
    [Fact]
    public void CorrectRun_SameRequestIdWithAnotherEntryValue_IsRefused()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun(RunResult.Completed);
        var requestId = Fixture.NewId();

        fixture.Service.CorrectRun(new CorrectRunCommand(
            requestId, created.RunId, 1, "中途加入",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = true })));
        var conflict = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(new CorrectRunCommand(
            requestId, created.RunId, 1, "中途加入",
            Change(RunFields.JoinedInProgress, new() { JoinedInProgress = false }))));

        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.Code);
        Assert.True(fixture.Runs.Get(created.RunId)!.JoinedInProgress);
    }

    [Fact]
    public void CreateManualRun_StoresHowTheDutyWasEnteredOnlyWhenThePlayerSaysSo()
    {
        using var fixture = new Fixture();

        var said = fixture.Service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Fixture.NewId(),
            Reason = "补录一次中途加入的导随",
            Result = RunResult.Completed,
            EnteredAtUtc = new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 20, 0, TimeSpan.Zero),
            JoinedInProgress = true,
        });
        var unsaid = fixture.CreateRun();

        Assert.True(fixture.Runs.Get(said.RunId)!.JoinedInProgress);
        Assert.Null(fixture.Runs.Get(unsaid.RunId)!.JoinedInProgress);
        var first = fixture.Revisions.GetFirst(said.RunId, null)!;
        Assert.Contains(first.Changes, change => change.Field == RunFields.JoinedInProgress);
    }

    [Fact]
    public void CorrectRun_AcknowledgingAnAlreadyReviewedRun_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(), run.RunId, 1, "重复确认",
                Change(RunFields.PendingReview, new() { PendingReview = false }))));

        Assert.Equal(ErrorCodes.NoChanges, error.Code);
    }

    [Fact]
    public void CorrectRun_WithEmptyChangeSet_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(Fixture.NewId(), run.RunId, 1, "空改动", new RunChangeSet())));

        Assert.Equal(ErrorCodes.NoChanges, error.Code);
    }

    [Fact]
    public void CorrectRun_ThatWouldBreakTimeOrder_IsRefusedAgainstTheStoredRow()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(),
                run.RunId,
                1,
                "只改结束时间，但会早于已存的进入时间",
                Change(
                    RunFields.EndedAtUtc,
                    new() { EndedAtUtc = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero) }))));

        Assert.Equal(ErrorCodes.TimeOrder, error.Code);
    }

    [Fact]
    public void CorrectRun_ToCompletedWithoutTimes_IsAcceptedWithAnUnknownDuration()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(RunResult.CancelledBeforeEntry);

        var outcome = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), run.RunId, 1, "改成已完成",
            Change(RunFields.Result, new() { Result = RunResult.Completed })));

        var corrected = fixture.Runs.Get(run.RunId)!;
        Assert.Equal(2, outcome.Revision);
        Assert.Equal(RunResult.Completed, corrected.Result);
        Assert.Null(corrected.EnteredAtUtc);
        Assert.Null(corrected.EndedAtUtc);
        Assert.Null(corrected.DurationMs);
    }

    [Fact]
    public void CorrectRun_RecomputesDurationFromCorrectedTimes()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var outcome = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(),
            run.RunId,
            1,
            "结束时间记错了 10 分钟",
            Change(
                RunFields.EndedAtUtc,
                new() { EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 41, 0, TimeSpan.Zero) })));

        var corrected = fixture.Runs.Get(run.RunId)!;
        Assert.Equal(2, outcome.Revision);
        Assert.Equal(2_400_000, corrected.DurationMs);
        Assert.True(corrected.ManuallyCorrected);
    }

    [Fact]
    public void CorrectRun_KeepsTheOriginalValuesReachableThroughRevisionOne()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(),
            run.RunId,
            1,
            "其实是掉线了",
            Change(RunFields.Result, new() { Result = RunResult.Disconnected })));

        var first = fixture.Revisions.GetFirst(run.RunId, null)!;
        var originalResult = first.Changes.Single(change => change.Field == "result");
        Assert.Equal("COMPLETED", originalResult.NewValue);
        Assert.Equal(RunResult.Disconnected, fixture.Runs.Get(run.RunId)!.Result);
    }

    [Fact]
    public void SoftDeleteRun_TwiceIsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        fixture.Service.SoftDeleteRun(new RunReasonCommand(Fixture.NewId(), run.RunId, 1, "记错了"));
        var error = Assert.Throws<CollectorException>(() => fixture.Service.SoftDeleteRun(
            new RunReasonCommand(Fixture.NewId(), run.RunId, 2, "再删一次")));

        Assert.Equal(ErrorCodes.AlreadyDeleted, error.Code);
    }

    [Fact]
    public void RestoreRun_OnALiveRunIsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.RestoreRun(
            new RunReasonCommand(Fixture.NewId(), run.RunId, 1, "恢复一条没删的")));

        Assert.Equal(ErrorCodes.NotDeleted, error.Code);
    }

    [Fact]
    public void SoftDeleteThenRestore_KeepsTheWholeRevisionChain()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        fixture.Service.SoftDeleteRun(new RunReasonCommand(Fixture.NewId(), run.RunId, 1, "删掉"));
        var restored = fixture.Service.RestoreRun(new RunReasonCommand(Fixture.NewId(), run.RunId, 2, "恢复"));

        Assert.Equal(3, restored.Revision);
        var chain = fixture.Revisions.ListForRun(run.RunId, 1, 50);
        Assert.Equal(3, chain.Total);
        Assert.Equal(
            new[] { ChangeKind.CreateManual, ChangeKind.SoftDelete, ChangeKind.Restore },
            chain.Items.Select(item => item.ChangeKind));
        Assert.False(fixture.Runs.Get(run.RunId)!.SoftDeleted);
    }

    [Fact]
    public void UndoRevision_AppendsANewRevisionRestoringThePreviousValues()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(),
            run.RunId,
            1,
            "先改成掉线",
            Change(RunFields.Result, new() { Result = RunResult.Disconnected })));

        var undone = fixture.Service.UndoRevision(
            new RunReasonCommand(Fixture.NewId(), run.RunId, 2, "改错了，撤销上一次修改"));

        Assert.Equal(3, undone.Revision);
        Assert.Equal(RunResult.Completed, fixture.Runs.Get(run.RunId)!.Result);

        // The undone revision is still there: undo appends, it never rewrites history.
        var chain = fixture.Revisions.ListForRun(run.RunId, 1, 50);
        Assert.Equal(3, chain.Total);
        Assert.Equal(ChangeKind.Correct, chain.Items[1].ChangeKind);
    }

    [Fact]
    public void UndoRevision_CannotUndoTheCreateRevision()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.UndoRevision(
            new RunReasonCommand(Fixture.NewId(), run.RunId, 1, "撤销创建")));

        Assert.Equal(ErrorCodes.UndoNotAllowed, error.Code);
        Assert.Equal("expected_revision", error.Field);
    }

    /// <summary>Persisted integer audit values stay exact, so undo restores them as they were.</summary>
    [Fact]
    public void UndoRestoresPersistedIntegerFieldsWithoutErasingCorrectionHistory()
    {
        using var fixture = new Fixture();
        var now = fixture.Database.Clock.UtcNow;
        var created = fixture.Service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Fixture.NewId(), Reason = "创建回归记录", Result = RunResult.Completed,
            EnteredAtUtc = now, EndedAtUtc = now.AddSeconds(60),
            JobId = 19, DurationMs = 9_007_199_254_740_993L,
        });
        var corrected = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, created.Revision, "更正职业和时长", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.JobId, RunFields.DurationMs },
                JobId = 24, DurationMs = 30_000,
            }));
        var undone = fixture.Service.UndoRevision(new RunReasonCommand(
            Fixture.NewId(), created.RunId, corrected.Revision, "恢复原先的职业和时长"));

        Assert.Equal(19, undone.Run!.JobId);
        Assert.Equal(created.Run!.JobName, undone.Run.JobName);
        Assert.Equal(created.Run.Role, undone.Run.Role);
        Assert.Equal(9_007_199_254_740_993L, undone.Run.DurationMs);
        Assert.True(undone.Run.ManuallyCorrected);
        var revision = fixture.Revisions.GetAt(created.RunId, undone.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.ManuallyCorrected);
    }

    [Fact]
    public void UndoAcceptsAConfidenceOnlySystemRevisionAndAuditsTheRestoredValue()
    {
        using var fixture = new Fixture();
        var now = fixture.Database.Clock.UtcNow;
        var created = fixture.Service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Fixture.NewId(), Reason = "历史修订夹具", Result = RunResult.Completed,
            EnteredAtUtc = now, EndedAtUtc = now.AddSeconds(60),
        });
        fixture.Database.Database.RunInTransaction(tx =>
        {
            fixture.Runs.Update(created.Run! with
            {
                Revision = 2, DetectionConfidence = DetectionConfidence.Low,
            }, 1, tx);
            fixture.Revisions.Append(new RunRevision
            {
                RevisionId = Fixture.NewId(), RunId = created.RunId, Revision = 2,
                ChangedAtUtc = now, ChangeKind = ChangeKind.Correct,
                Actor = RevisionActor.System, Reason = "系统仅更新置信度",
                Changes = new[] { new RunFieldChange("detection_confidence", "NONE", "LOW") },
            }, tx);
        });

        var undone = fixture.Service.UndoRevision(new RunReasonCommand(
            Fixture.NewId(), created.RunId, 2, "恢复先前置信度"));

        Assert.Equal(DetectionConfidence.None, undone.Run!.DetectionConfidence);
        var revision = fixture.Revisions.GetAt(created.RunId, undone.Revision, null)!;
        var change = Assert.Single(revision.Changes);
        Assert.Equal("detection_confidence", change.Field);
        Assert.Equal("LOW", change.OldValue);
        Assert.Equal("NONE", change.NewValue);
    }

    [Fact]
    public void SameRequestId_ReplaysTheStoredResponseWithoutASecondWrite()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();
        var requestId = Fixture.NewId();
        var changes = Change(RunFields.Result, new() { Result = RunResult.Disconnected });

        var first = fixture.Service.CorrectRun(
            new CorrectRunCommand(requestId, run.RunId, 1, "掉线了", changes));
        var replay = fixture.Service.CorrectRun(
            new CorrectRunCommand(requestId, run.RunId, 1, "掉线了", changes));

        Assert.False(first.IdempotentReplay);
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.AuditEventId, replay.AuditEventId);
        Assert.Equal(2, fixture.Runs.Get(run.RunId)!.Revision);
        Assert.Equal(2, fixture.Revisions.ListForRun(run.RunId, 1, 50).Total);
    }

    [Fact]
    public void SameRequestIdWithADifferentBody_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();
        var requestId = Fixture.NewId();

        fixture.Service.CorrectRun(new CorrectRunCommand(
            requestId,
            run.RunId,
            1,
            "掉线了",
            Change(RunFields.Result, new() { Result = RunResult.Disconnected })));

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                requestId,
                run.RunId,
                1,
                "换了个理由和内容",
                Change(RunFields.Result, new() { Result = RunResult.Interrupted }))));

        Assert.Equal(ErrorCodes.IdempotencyConflict, error.Code);
        Assert.Equal("idempotency", error.Details!["conflict"]);
    }

    [Fact]
    public void RunRevisions_RefuseUpdateAndDelete()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var update = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            fixture.Database.Database.RunInTransaction(tx =>
            {
                using var command = fixture.Database.Database.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "UPDATE run_revisions SET reason = 'tampered' WHERE run_id = $id;";
                command.Parameters.AddWithValue("$id", run.RunId);
                command.ExecuteNonQuery();
            }));

        var delete = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            fixture.Database.Database.RunInTransaction(tx =>
            {
                using var command = fixture.Database.Database.CreateCommand();
                command.Transaction = tx;
                command.CommandText = "DELETE FROM run_revisions WHERE run_id = $id;";
                command.Parameters.AddWithValue("$id", run.RunId);
                command.ExecuteNonQuery();
            }));

        Assert.Contains("append-only", update.Message, StringComparison.Ordinal);
        Assert.Contains("append-only", delete.Message, StringComparison.Ordinal);
    }
}
