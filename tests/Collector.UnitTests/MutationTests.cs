using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Every refusal path of the mutation layer, one test per contract error code, plus the two
/// properties the audit chain rests on: idempotency and append-only history.
/// </summary>
public sealed class MutationTests
{
    private static RunChangeSet Change(string field, Action<RunChangeSetBuilder> configure)
    {
        var builder = new RunChangeSetBuilder();
        configure(builder);
        builder.Fields.Add(field);
        return builder.Build();
    }

    private sealed class RunChangeSetBuilder
    {
        public HashSet<string> Fields { get; } = new(StringComparer.Ordinal);

        public RunChangeSet Value { get; set; } = new();

        public RunChangeSet Build() => Value with { Specified = Fields };
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Database = new TestDatabase();
            Settings = new SettingsRepository(Database.Database, Database.Clock);
            Settings.EnsureDefaults();
            Runs = new RunRepository(Database.Database);
            Revisions = new RunRevisionRepository(Database.Database);
            Service = new RunMutationService(Database.Database, Settings, Database.Clock);
        }

        public TestDatabase Database { get; }

        public SettingsRepository Settings { get; }

        public RunRepository Runs { get; }

        public RunRevisionRepository Revisions { get; }

        public RunMutationService Service { get; }

        public static string NewId() => Guid.NewGuid().ToString("D");

        public RunMutationOutcome CreateRun(
            RunResult result = RunResult.Completed,
            string reason = "补录 9 月 3 日的一次导随") =>
            Service.CreateManualRun(new CreateManualRunCommand
            {
                RequestId = NewId(),
                Reason = reason,
                Result = result,
                ContentId = 900001,
                JobId = 19,
                MatchedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
                EnteredAtUtc = result == RunResult.CancelledBeforeEntry
                    ? null
                    : new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
                EndedAtUtc = result == RunResult.CancelledBeforeEntry
                    ? null
                    : new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero),
            });

        public void Dispose() => Database.Dispose();
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
            Change(RunFields.ContentId, b => b.Value = b.Value with { ContentId = duty.ContentId })));
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
    public void CreateManualRun_WithReversedTimes_IsRefused()
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CreateManualRun(
            new CreateManualRunCommand
            {
                RequestId = Fixture.NewId(),
                Reason = "时间填反了",
                Result = RunResult.Completed,
                EnteredAtUtc = new DateTimeOffset(2026, 9, 3, 11, 0, 0, TimeSpan.Zero),
                EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            }));

        Assert.Equal(ErrorCodes.TimeOrder, error.Code);
    }

    [Fact]
    public void CreateManualRun_WithNegativeDuration_IsRefused()
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CreateManualRun(
            new CreateManualRunCommand
            {
                RequestId = Fixture.NewId(),
                Reason = "时长填成负数",
                Result = RunResult.Completed,
                EnteredAtUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
                EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 30, 0, TimeSpan.Zero),
                DurationMs = -1,
            }));

        Assert.Equal(ErrorCodes.NegativeDuration, error.Code);
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
                Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Completed }))));

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
                Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Disconnected }))));

        Assert.Equal(ErrorCodes.RevisionConflict, error.Code);
        Assert.Equal(1, error.Details!["current_revision"]);
    }

    [Fact]
    public void CorrectRun_WithIdenticalValues_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(),
                run.RunId,
                1,
                "没有实际改动",
                Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Completed }))));

        Assert.Equal(ErrorCodes.NoChanges, error.Code);
    }

    [Fact]
    public void CorrectRun_AcknowledgingReview_IsAChangeOnItsOwn()
    {
        using var fixture = new Fixture();
        var created = fixture.CreateRun(RunResult.Unknown);
        var stored = fixture.Runs.Get(created.RunId)!;
        // Put the run into review the way the machine does: flip the flag in place without
        // spending a revision, so the audit chain (contiguous from 1) stays intact.
        fixture.Database.Database.RunInTransaction(tx =>
            fixture.Runs.Update(stored with { PendingReview = true }, stored.Revision, tx));

        var outcome = fixture.Service.CorrectRun(new CorrectRunCommand(
            Fixture.NewId(), created.RunId, stored.Revision, "已确认复核",
            Change(RunFields.PendingReview, b => b.Value = b.Value with { PendingReview = false })));

        var after = fixture.Runs.Get(created.RunId)!;
        Assert.False(after.PendingReview);
        Assert.True(after.ManuallyCorrected);
        Assert.Equal(RunResult.Unknown, after.Result);
        Assert.Equal(stored.Revision + 1, outcome.Revision);
        var revision = fixture.Revisions.ListForRun(created.RunId, 1, 50).Items[^1];
        Assert.Contains(revision.Changes, change => change.Field == RunFields.PendingReview);
    }

    [Fact]
    public void CorrectRun_AcknowledgingAnAlreadyReviewedRun_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                Fixture.NewId(), run.RunId, 1, "重复确认",
                Change(RunFields.PendingReview, b => b.Value = b.Value with { PendingReview = false }))));

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
                    b => b.Value = b.Value with
                    {
                        EndedAtUtc = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
                    }))));

        Assert.Equal(ErrorCodes.TimeOrder, error.Code);
    }

    [Fact]
    public void CorrectRun_ToCompletedWithoutEndTime_IsRefused()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun(RunResult.CancelledBeforeEntry);

        var builder = new RunChangeSetBuilder();
        builder.Value = builder.Value with { Result = RunResult.Completed };
        builder.Fields.Add(RunFields.Result);

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(Fixture.NewId(), run.RunId, 1, "改成已完成", builder.Build())));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("entered_at_utc", error.Field);
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
                b => b.Value = b.Value with
                {
                    EndedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 41, 0, TimeSpan.Zero),
                })));

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
            Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Disconnected })));

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
            Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Disconnected })));

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

    [Fact]
    public void SameRequestId_ReplaysTheStoredResponseWithoutASecondWrite()
    {
        using var fixture = new Fixture();
        var run = fixture.CreateRun();
        var requestId = Fixture.NewId();
        var changes = Change(
            RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Disconnected });

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
            Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Disconnected })));

        var error = Assert.Throws<CollectorException>(() => fixture.Service.CorrectRun(
            new CorrectRunCommand(
                requestId,
                run.RunId,
                1,
                "换了个理由和内容",
                Change(RunFields.Result, b => b.Value = b.Value with { Result = RunResult.Interrupted }))));

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

    [Fact]
    public void UpdateAchievementBaseline_StoresGoalBaselineAndAudit()
    {
        using var fixture = new Fixture();

        var outcome = fixture.Service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Fixture.NewId(),
            2000,
            1500,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "开始使用本软件前已完成 1500 次"));

        Assert.Equal(1500, outcome.Settings.BaselineCompletedCount);
        Assert.Equal(2000, outcome.Settings.GoalCount);
        var audit = fixture.Settings.ReadBaselineAudit();
        Assert.Equal(outcome.AuditEventId, Assert.Single(audit).AuditEventId);
    }

    [Theory]
    [InlineData(0, 0, ErrorCodes.BadRequest)]
    [InlineData(2000, -1, ErrorCodes.BadRequest)]
    public void UpdateAchievementBaseline_RejectsOutOfRangeValues(int goal, int baseline, string expected)
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(
                Fixture.NewId(), goal, baseline, DateTimeOffset.UnixEpoch, "越界")));

        Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void UpdateAchievementBaseline_WithoutReason_IsRefused()
    {
        using var fixture = new Fixture();

        var error = Assert.Throws<CollectorException>(() => fixture.Service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(
                Fixture.NewId(), 2000, 10, DateTimeOffset.UnixEpoch, "  ")));

        Assert.Equal(ErrorCodes.ReasonRequired, error.Code);
    }
}
