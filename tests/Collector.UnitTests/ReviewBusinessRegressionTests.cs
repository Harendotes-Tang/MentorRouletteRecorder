using System.Buffers.Binary;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Regression cases for the business-rule findings of the 2026-09-08 review.</summary>
public sealed class ReviewBusinessRegressionTests
{
    [Theory]
    [InlineData("note")]
    [InlineData("job_id")]
    [InlineData("contributes_to_goal")]
    [InlineData("matched_at_utc")]
    [InlineData("same_end_time")]
    public void UnrelatedCorrectionsPreserveTheRecordedDuration(string field)
    {
        using var db = new TestDatabase();
        var service = Service(db, out _);
        var created = CreateManual(service, db, duration: 30_000);
        var fields = new HashSet<string> { RunFields.Note };
        var changes = new RunChangeSet { Specified = fields, Note = "只补充记录说明" };
        switch (field)
        {
            case "job_id":
                fields.Add(RunFields.JobId);
                changes = changes with { JobId = 24 };
                break;
            case "contributes_to_goal":
                fields.Add(RunFields.ContributesToGoal);
                changes = changes with { ContributesToGoal = false };
                break;
            case "matched_at_utc":
                fields.Add(RunFields.MatchedAtUtc);
                changes = changes with { MatchedAtUtc = db.Clock.UtcNow.AddSeconds(-10) };
                break;
            case "same_end_time":
                fields.Add(RunFields.EndedAtUtc);
                changes = changes with { EndedAtUtc = created.Run!.EndedAtUtc };
                break;
        }

        var corrected = service.CorrectRun(new CorrectRunCommand(
            NewId(), created.RunId, created.Revision, "补充说明", changes));

        Assert.Equal(30_000L, corrected.Run!.DurationMs);
        var revision = new RunRevisionRepository(db.Database)
            .GetAt(created.RunId, corrected.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.DurationMs);
    }

    [Theory]
    [InlineData(false, 90_000L)]
    [InlineData(true, 12_000L)]
    public void ChangedDurationEndpointsRecomputeUnlessAnExplicitDurationWins(bool explicitDuration, long expected)
    {
        using var db = new TestDatabase();
        var service = Service(db, out _);
        var created = CreateManual(service, db, duration: 30_000);
        var fields = new HashSet<string> { RunFields.EndedAtUtc };
        if (explicitDuration) fields.Add(RunFields.DurationMs);
        var corrected = service.CorrectRun(new CorrectRunCommand(
            NewId(), created.RunId, created.Revision, "更正结束时间", new RunChangeSet
            {
                Specified = fields,
                EndedAtUtc = db.Clock.UtcNow.AddSeconds(90),
                DurationMs = 12_000,
            }));

        Assert.Equal(expected, corrected.Run!.DurationMs);
    }

    [Fact]
    public void EditingTheNoteOfAPendingRunDoesNotAcknowledgeItsResult()
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        var created = CreateManual(service, db, duration: 30_000, result: RunResult.Unknown);
        MarkPending(db, created.Run!);

        var corrected = service.CorrectRun(new CorrectRunCommand(
            NewId(), created.RunId, created.Revision, "仅补备注", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Note }, Note = "结局尚未核实",
            }));

        Assert.True(corrected.Run!.ManuallyCorrected);
        Assert.True(corrected.Run.PendingReview);
        Assert.Equal(30_000L, corrected.Run.DurationMs);
        Assert.Equal(1, new StatisticsRepository(db.Database, settings).GetDashboard().UnfinishedPendingReview);
        var revision = new RunRevisionRepository(db.Database).GetAt(created.RunId, corrected.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.PendingReview);
    }

    [Theory]
    [InlineData("result")]
    [InlineData("unchanged_result")]
    [InlineData("pending_review")]
    public void AnExplicitResultDecisionOrAcknowledgementResolvesPendingReview(string decision)
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        var created = CreateManual(service, db, result: RunResult.Unknown);
        MarkPending(db, created.Run!);
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

        var corrected = service.CorrectRun(new CorrectRunCommand(
            NewId(), created.RunId, created.Revision, "明确确认本次结局", changes));

        Assert.False(corrected.Run!.PendingReview);
        Assert.True(corrected.Run.ManuallyCorrected);
        Assert.Equal(created.Revision + 1, corrected.Revision);
        Assert.Equal(0, new StatisticsRepository(db.Database, settings).GetDashboard().UnfinishedPendingReview);
        var revision = new RunRevisionRepository(db.Database).GetAt(created.RunId, corrected.Revision, null)!;
        Assert.Contains(revision.Changes, change => change.Field == RunFields.PendingReview
            && Equals(change.OldValue, true) && Equals(change.NewValue, false));
    }

    [Theory]
    [InlineData("zone", false)]
    [InlineData("instance", false)]
    [InlineData("nonduty", false)]
    [InlineData("zone", true)]
    [InlineData("instance", true)]
    [InlineData("nonduty", true)]
    public void ExplicitExitsRespectTheProfilesResultCapability(string exitKind, bool canDetectResult)
    {
        var start = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("review", Region.Cn, ProfileStatus.Verified, 42, canDetectResult));
        machine.Handle(Pop(start));
        machine.Handle(Enter(start));
        var key = Key(exitKind, 2);
        var time = start.AddSeconds(2);
        var mono = TimeSpan.FromSeconds(2);
        SemanticEvent exit = exitKind switch
        {
            "zone" => new ZoneLeft { Key = key, ObservedAtUtc = time, Mono = mono },
            "instance" => new InstanceLeft { Key = key, ObservedAtUtc = time, Mono = mono },
            _ => new ZoneInitialization { Key = key, ObservedAtUtc = time, Mono = mono, IsDutyInstance = false },
        };

        var finished = Assert.Single(machine.Handle(exit).Commands.OfType<FinishRunCommand>());

        Assert.Equal(canDetectResult ? RunResult.LeftOrAbandoned : RunResult.Unknown, finished.Result);
        Assert.Equal(!canDetectResult, finished.PendingReview);
        if (!canDetectResult) Assert.Equal(DetectionConfidence.Low, finished.Confidence);
    }

    [Fact]
    public async Task AutomaticChangesInOneMillisecondStillPublishUpdatedRowsAndStatistics()
    {
        using var db = new TestDatabase();
        _ = Service(db, out _);
        var sessionId = NewId();
        EnsureSession(db, sessionId);
        var selection = ProfileSelector.SelectExplicit(Path.Combine(
            AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json"), allowSynthetic: true);
        var bus = new LiveEventBus(db.Clock);
        var pipeline = new LiveProtocolPipeline(db.Database, db.Clock, bus, _ => selection);
        pipeline.OnCaptureStarted(sessionId);
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 900_001);
        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 800_001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 900_001);
        zone[12] = 1;
        DecodedMessage Message(ushort opcode, long epoch, byte[] payload) => new(
            sessionId, MessageDirection.Inbound, db.Clock.UtcNow, TimeSpan.FromMilliseconds(epoch),
            epoch, 61440, opcode, payload, "review-connection");
        pipeline.Accept(Message(61441, 0, pop));
        pipeline.Accept(Message(61442, 1, zone));
        var job = Message(61444, 2, new byte[] { 19, 0, 0, 0 });
        pipeline.Accept(job);
        pipeline.Accept(job); // An unchanged duplicate must not emit another row update.
        pipeline.Accept(Message(61443, 3, new byte[] { 1, 0, 0, 0 }));

        using var subscription = bus.Subscribe(NewId());
        var kinds = new List<string?>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(10), CancellationToken.None) is { } row)
            kinds.Add(row["kind"]?.GetValue<string>());

        Assert.Equal(3, kinds.Count(kind => kind == "run_updated"));
        Assert.Equal(4, kinds.Count(kind => kind == "stats_invalidated"));
        Assert.Equal(1, kinds.Count(kind => kind == "run_finished"));
        var stored = Assert.Single(new RunRepository(db.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(1, stored.Revision);
        Assert.Equal(db.Clock.UtcNow, stored.UpdatedAtUtc);
        Assert.Equal(RunResult.Completed, stored.Result);
    }

    [Fact]
    public void BaselineThatWouldOverflowTheCurrentProgressIsRejectedWithoutChangingSettings()
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        CreateManual(service, db);
        var before = settings.GetAchievementSettings();

        var error = Assert.Throws<CollectorException>(() => service.UpdateAchievementBaseline(
            new UpdateAchievementBaselineCommand(NewId(), 2000, int.MaxValue, db.Clock.UtcNow, "极值校验")));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("baseline_completed_count", error.Field);
        Assert.Equal(before, settings.GetAchievementSettings());
        Assert.Empty(settings.ReadBaselineAudit());
    }

    [Fact]
    public void ACompletionAfterTheMaximumBaselineDoesNotBreakTheDashboard()
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, int.MaxValue, db.Clock.UtcNow, "校验后续自动完成"));
        RecordAutomaticCompletion(db);

        var dashboard = new StatisticsRepository(db.Database, settings).GetDashboard();

        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(int.MaxValue, dashboard.AchievementProgress);
        Assert.Equal(0, dashboard.Remaining);
        Assert.Equal(int.MaxValue, settings.GetAchievementSettings().BaselineCompletedCount);
    }

    [Fact]
    public void PreviouslySavedOversizedProgressRemainsReadable()
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        CreateManual(service, db);
        db.Database.RunInTransaction(tx => settings.UpdateAchievementSettings(
            settings.GetAchievementSettings(tx) with { BaselineCompletedCount = int.MaxValue }, tx));

        Assert.Equal(int.MaxValue, new StatisticsRepository(db.Database, settings).GetDashboard().AchievementProgress);
    }

    [Fact]
    public void BaselineValidationUsesTheSameConfirmedAndContributingPopulationAsStatistics()
    {
        using var db = new TestDatabase();
        var service = Service(db, out var settings);
        CreateManual(service, db, contributes: false);
        var deleted = CreateManual(service, db);
        service.SoftDeleteRun(new RunReasonCommand(NewId(), deleted.RunId, deleted.Revision, "排除已删除记录"));
        db.Database.RunInTransaction(tx => new RunRepository(db.Database).Insert(
            TestDatabase.Run(source: RunSource.Import) with { MentorRouletteId = null }, tx));

        service.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            NewId(), 2000, int.MaxValue, db.Clock.UtcNow, "排除不计入目标的记录"));

        Assert.Equal(int.MaxValue, new StatisticsRepository(db.Database, settings).GetDashboard().AchievementProgress);
    }

    private static RunMutationService Service(TestDatabase db, out SettingsRepository settings)
    {
        settings = new SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        return new RunMutationService(db.Database, settings, db.Clock);
    }

    private static RunMutationOutcome CreateManual(
        RunMutationService service, TestDatabase db, long? duration = null, bool contributes = true,
        RunResult result = RunResult.Completed) =>
        service.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = NewId(), Reason = "回归记录", Result = result,
            EnteredAtUtc = db.Clock.UtcNow, EndedAtUtc = db.Clock.UtcNow.AddSeconds(60),
            DurationMs = duration, ContributesToGoal = contributes,
        });

    private static void MarkPending(TestDatabase db, MentorRun run) =>
        db.Database.RunInTransaction(tx => new RunRepository(db.Database)
            .Update(run with { PendingReview = true }, run.Revision, tx));

    private static void RecordAutomaticCompletion(TestDatabase db)
    {
        var sessionId = NewId();
        EnsureSession(db, sessionId);
        var machine = new MentorRunStateMachine(ProfileBinding.Synthetic("review", 42));
        var processor = new SemanticEventProcessor(db.Database, machine,
            new SemanticEventProcessorOptions(sessionId, Region.Cn, "review", sessionId), db.Clock);
        processor.Accept(Pop(db.Clock.UtcNow));
        processor.Accept(Enter(db.Clock.UtcNow));
        processor.Accept(new DutyResult
        {
            Key = Key("victory", 2), ObservedAtUtc = db.Clock.UtcNow.AddSeconds(2),
            Mono = TimeSpan.FromSeconds(2), Victory = true,
        });
        Assert.Null(processor.LastStorageError);
        Assert.Equal(RunState.Completed, machine.State);
    }

    private static void EnsureSession(TestDatabase db, string sessionId) =>
        db.Database.RunInTransaction(tx => new CaptureSessionRepository(db.Database).Insert(new CaptureSession
        {
            CaptureSessionId = sessionId, StartedAtUtc = db.Clock.UtcNow, CollectorVersion = "review",
            Region = Region.Cn, ProfileStatus = ProfileStatus.Unverified,
        }, tx));

    private static ContentFinderPop Pop(DateTimeOffset start) => new()
    {
        Key = Key("pop", 0), ObservedAtUtc = start, Mono = TimeSpan.Zero, RouletteId = 42,
    };

    private static ZoneInitialization Enter(DateTimeOffset start) => new()
    {
        Key = Key("entry", 1), ObservedAtUtc = start.AddSeconds(1),
        Mono = TimeSpan.FromSeconds(1), IsDutyInstance = true,
    };

    private static EventKey Key(string kind, long epoch) =>
        new("review", PacketDirection.ServerToClient, kind, epoch, null, kind + epoch);

    private static string NewId() => Guid.NewGuid().ToString("D");
}
