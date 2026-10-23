using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Persisted integer audit values must remain exact and usable by undo.</summary>
public sealed class ReviewRevisionRegressionTests
{
    [Theory]
    [InlineData(2_147_483_647L)]
    [InlineData(9_007_199_254_740_993L)]
    [InlineData(long.MaxValue)]
    public void IntegerAuditValuesRoundTripWithoutFloatingPointPromotion(long value)
    {
        var json = RunRevisionRepository.SerializeChanges(new[]
        {
            new RunFieldChange(RunFields.DurationMs, value, 1L),
        });
        var change = Assert.Single(RunRevisionRepository.DeserializeChanges(json));

        Assert.Equal(value, Assert.IsType<long>(change.OldValue));
        Assert.Equal(value, RunFieldWriter.Apply(TestDatabase.Run(), change.Field, change.OldValue).DurationMs);
    }

    [Fact]
    public void FractionalAuditValuesRemainInvalidForIntegerFields()
    {
        var change = Assert.Single(RunRevisionRepository.DeserializeChanges(
            "[{\"field\":\"job_id\",\"old_value\":19.5,\"new_value\":24}]"));

        Assert.Equal(19.5, Assert.IsType<double>(change.OldValue));
        Assert.Throws<CollectorException>(() =>
            RunFieldWriter.Apply(TestDatabase.Run(), change.Field, change.OldValue));
    }

    [Fact]
    public void UndoRestoresPersistedIntegerFieldsWithoutErasingCorrectionHistory()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(db.Database, settings, db.Clock);
        var created = service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = NewId(), Reason = "创建回归记录", Result = RunResult.Completed,
            EnteredAtUtc = db.Clock.UtcNow, EndedAtUtc = db.Clock.UtcNow.AddSeconds(60),
            JobId = 19, DurationMs = 9_007_199_254_740_993L,
        });
        var corrected = service.CorrectRun(new CorrectRunCommand(
            NewId(), created.RunId, created.Revision, "更正职业和时长", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.JobId, RunFields.DurationMs },
                JobId = 24, DurationMs = 30_000,
            }));
        var undone = service.UndoRevision(new RunReasonCommand(
            NewId(), created.RunId, corrected.Revision, "恢复原先的职业和时长"));

        Assert.Equal(19, undone.Run!.JobId);
        Assert.Equal(created.Run!.JobName, undone.Run.JobName);
        Assert.Equal(created.Run.Role, undone.Run.Role);
        Assert.Equal(9_007_199_254_740_993L, undone.Run.DurationMs);
        Assert.True(undone.Run.ManuallyCorrected);
        var revision = new RunRevisionRepository(db.Database).GetAt(created.RunId, undone.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.ManuallyCorrected);
    }

    [Fact]
    public void UndoAcceptsAConfidenceOnlySystemRevisionAndAuditsTheRestoredValue()
    {
        using var db = new TestDatabase();
        var settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        var service = new RunMutationService(db.Database, settings, db.Clock);
        var created = service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = NewId(), Reason = "历史修订夹具", Result = RunResult.Completed,
            EnteredAtUtc = db.Clock.UtcNow, EndedAtUtc = db.Clock.UtcNow.AddSeconds(60),
        });
        db.Database.RunInTransaction(tx =>
        {
            new RunRepository(db.Database).Update(created.Run! with
            {
                Revision = 2, DetectionConfidence = DetectionConfidence.Low,
            }, 1, tx);
            new RunRevisionRepository(db.Database).Append(new RunRevision
            {
                RevisionId = NewId(), RunId = created.RunId, Revision = 2,
                ChangedAtUtc = db.Clock.UtcNow, ChangeKind = ChangeKind.Correct,
                Actor = RevisionActor.System, Reason = "系统仅更新置信度",
                Changes = new[] { new RunFieldChange("detection_confidence", "NONE", "LOW") },
            }, tx);
        });

        var undone = service.UndoRevision(new RunReasonCommand(
            NewId(), created.RunId, 2, "恢复先前置信度"));

        Assert.Equal(DetectionConfidence.None, undone.Run!.DetectionConfidence);
        var revision = new RunRevisionRepository(db.Database).GetAt(created.RunId, undone.Revision, null)!;
        var change = Assert.Single(revision.Changes);
        Assert.Equal("detection_confidence", change.Field);
        Assert.Equal("LOW", change.OldValue);
        Assert.Equal("NONE", change.NewValue);
    }

    private static string NewId() => Guid.NewGuid().ToString("D");
}
