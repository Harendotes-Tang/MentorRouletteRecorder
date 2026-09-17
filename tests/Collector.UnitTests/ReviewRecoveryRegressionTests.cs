using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Recovery;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Restart recovery shares field protection and processes protected open rows once.</summary>
public sealed class ReviewRecoveryRegressionTests
{
    /// <summary>
    /// Review finding L-19. Undoing a correction back to the values the record was created
    /// with withdraws the decision, so restart recovery may close the run as it closes any
    /// other one -- and, having closed it, must not find it again.
    /// </summary>
    [Fact]
    public void RestartClosesARunWhoseCorrectionWasUndone_AndNeverFindsItTwice()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.Result, RunFields.EndedAtUtc },
            Result = RunResult.Completed, EndedAtUtc = fixture.Start.AddSeconds(30),
        });
        var undone = fixture.Service.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, corrected.Revision, "撤销误填的结局与结束时间")).Run!;
        Assert.True(undone.ManuallyCorrected);
        Assert.Equal(RunResult.Unknown, undone.Result);
        Assert.Null(undone.EndedAtUtc);

        MentorRun recovered;
        using (var first = fixture.Restart(60))
        {
            Assert.Equal(1, first.Recovery.RecoveredCount);
            recovered = first.Runs.Get(fixture.RunId)!;
            Assert.Equal(RunResult.Interrupted, recovered.Result);
            Assert.Equal(fixture.Start.AddSeconds(60), recovered.EndedAtUtc);
            Assert.True(recovered.PendingReview);
            var marker = Assert.Single(first.Events.ListForRun(fixture.RunId),
                item => item.EventType == CrashRecoveryService.EventType);
            Assert.Equal(RunState.InterruptedPendingReview, marker.ToState);
        }

        using var second = fixture.Restart(180);
        Assert.Equal(0, second.Recovery.RecoveredCount);
        Assert.Equal(recovered, second.Runs.Get(fixture.RunId));
        Assert.Single(second.Events.ListForRun(fixture.RunId), item => item.EventType == CrashRecoveryService.EventType);
    }

    /// <summary>
    /// Review finding H-8. A run left open by a process that died, with no human decision on
    /// it at all, is closed with an end time on the first pass and is not seen again. The
    /// time-consistency rewrite must not apply to this purely automatic write: under a
    /// backwards clock it nulls <c>ended_at_utc</c>, and every later restart rediscovers the
    /// same record.
    /// </summary>
    [Fact]
    public void RestartClosesAnUntouchedOpenRunOnceEvenWhenTheClockRanBackwards()
    {
        using var fixture = new Fixture();

        MentorRun recovered;
        using (var first = fixture.Restart(-90))
        {
            Assert.Equal(1, first.Recovery.RecoveredCount);
            recovered = first.Runs.Get(fixture.RunId)!;

            // The end stamp is clamped up to the entry rather than nulled out, so the row is
            // storable and the run is closed for good.
            Assert.Equal(recovered.EnteredAtUtc, recovered.EndedAtUtc);
            Assert.NotNull(recovered.EndedAtUtc);
            Assert.Equal(RunResult.Interrupted, recovered.Result);
            Assert.True(recovered.PendingReview);
            Assert.False(recovered.ManuallyCorrected);

            // The reason must not claim a human decision that was never made.
            var revision = first.Revisions.GetAt(fixture.RunId, recovered.Revision, null)!;
            Assert.DoesNotContain("人工", revision.Reason ?? string.Empty, StringComparison.Ordinal);
        }

        using var second = fixture.Restart(240);
        Assert.Equal(0, second.Recovery.RecoveredCount);
        Assert.Equal(recovered, second.Runs.Get(fixture.RunId));
        Assert.Single(second.Events.ListForRun(fixture.RunId), item => item.EventType == CrashRecoveryService.EventType);
    }

    [Fact]
    public void RestartWithAManualFutureEntryKeepsLegalTimesAndDoesNotRecoverAgain()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EnteredAtUtc },
            EnteredAtUtc = fixture.Start.AddSeconds(120),
        });

        MentorRun recovered;
        using (var first = fixture.Restart(60))
        {
            Assert.Equal(1, first.Recovery.RecoveredCount);
            recovered = first.Runs.Get(fixture.RunId)!;
            Assert.Equal(corrected.EnteredAtUtc, recovered.EnteredAtUtc);
            Assert.Null(recovered.EndedAtUtc);
            Assert.Null(recovered.DurationMs);
            Assert.Equal(RunResult.Unknown, recovered.Result);
            Assert.True(recovered.PendingReview);
            Assert.Equal(DetectionConfidence.Low, recovered.DetectionConfidence);
            var revision = first.Revisions.GetAt(fixture.RunId, recovered.Revision, null)!;
            Assert.DoesNotContain(revision.Changes, change => change.Field is RunFields.Result
                or RunFields.EnteredAtUtc or RunFields.EndedAtUtc or RunFields.DurationMs);
            Assert.Contains(revision.Changes, change => change.Field == RunFields.PendingReview
                && Equals(change.OldValue, false) && Equals(change.NewValue, true));
            Assert.Equal(1, first.Statistics.GetDashboard().UnfinishedPendingReview);
        }

        using var second = fixture.Restart(180);
        Assert.Equal(0, second.Recovery.RecoveredCount);
        Assert.Equal(recovered, second.Runs.Get(fixture.RunId));
        Assert.Single(second.Events.ListForRun(fixture.RunId), item => item.EventType == CrashRecoveryService.EventType);
    }

    [Fact]
    public void AManualNoteSurvivesRecoveryWhileAutomaticOutcomeAndReviewStillUpdate()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.Note }, Note = "只记录说明，结局没有确认",
        });

        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        Assert.Equal("只记录说明，结局没有确认", recovered.Note);
        Assert.Equal(RunResult.Interrupted, recovered.Result);
        Assert.True(recovered.PendingReview);
        Assert.Equal(fixture.Start.AddSeconds(60), recovered.EndedAtUtc);
        Assert.Equal(1, host.Statistics.GetDashboard().UnfinishedPendingReview);
        var revision = host.Revisions.GetAt(fixture.RunId, recovered.Revision, null)!;
        Assert.DoesNotContain(revision.Changes, change => change.Field == RunFields.Note);
        Assert.Contains(revision.Changes, change => change.Field == RunFields.Result
            && Equals(change.OldValue, "UNKNOWN") && Equals(change.NewValue, "INTERRUPTED"));
    }

    [Fact]
    public void ARecoveryRevisionCanBeUndoneAndReappliedIncludingItsConfidence()
    {
        using var fixture = new Fixture();
        var original = fixture.Run;
        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;

        var undone = host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, recovered.Revision, "撤销自动恢复的判断")).Run!;

        Assert.Equal(original.Result, undone.Result);
        Assert.Equal(original.EndedAtUtc, undone.EndedAtUtc);
        Assert.Equal(original.PendingReview, undone.PendingReview);
        Assert.Equal(original.DetectionConfidence, undone.DetectionConfidence);
        var revision = host.Revisions.GetAt(fixture.RunId, undone.Revision, null)!;
        Assert.Contains(revision.Changes, change => change.Field == "detection_confidence"
            && Equals(change.OldValue, "LOW")
            && Equals(change.NewValue, EnumWire<DetectionConfidence>.Format(original.DetectionConfidence)));

        var reapplied = host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, undone.Revision, "恢复先前的自动恢复判断")).Run!;
        Assert.Equal(recovered.Result, reapplied.Result);
        Assert.Equal(recovered.EndedAtUtc, reapplied.EndedAtUtc);
        Assert.Equal(recovered.PendingReview, reapplied.PendingReview);
        Assert.Equal(recovered.DetectionConfidence, reapplied.DetectionConfidence);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabase _database = new();
        public DateTimeOffset Start { get; }
        public string RunId { get; }
        public RunMutationService Service { get; }
        public MentorRun Run => new RunRepository(_database.Database).Get(RunId)!;

        public Fixture()
        {
            Start = _database.Clock.UtcNow;
            var settings = new SettingsRepository(_database.Database, _database.Clock);
            settings.EnsureDefaults();
            Service = new RunMutationService(_database.Database, settings, _database.Clock);
            var session = NewId();
            _database.Database.RunInTransaction(tx => new CaptureSessionRepository(_database.Database).Insert(
                new CaptureSession
                {
                    CaptureSessionId = session, StartedAtUtc = Start, CollectorVersion = "review",
                    Region = Region.Cn, ProfileStatus = ProfileStatus.Verified,
                }, tx));
            var machine = new MentorRunStateMachine(
                ProfileBinding.Live("review", Region.Cn, ProfileStatus.Verified, 42, false));
            var processor = new SemanticEventProcessor(_database.Database, machine,
                new SemanticEventProcessorOptions(session, Region.Cn, "review", session), _database.Clock);
            processor.Accept(new ContentFinderPop
            {
                Key = new EventKey(session, PacketDirection.ServerToClient, "pop", 0, null, "pop"),
                ObservedAtUtc = Start, Mono = TimeSpan.Zero, RouletteId = 42,
            });
            processor.Accept(new ZoneInitialization
            {
                Key = new EventKey(session, PacketDirection.ServerToClient, "entry", 1, null, "entry"),
                ObservedAtUtc = Start.AddSeconds(1), Mono = TimeSpan.FromSeconds(1), IsDutyInstance = true,
            });
            Assert.Null(processor.LastStorageError);
            RunId = machine.CurrentRunId!;
            Assert.NotNull(RunId);
        }

        public MentorRun Correct(RunChangeSet changes)
        {
            var current = new RunRepository(_database.Database).Get(RunId)!;
            return Service.CorrectRun(new CorrectRunCommand(
                NewId(), RunId, current.Revision, "人工核对后更正", changes)).Run!;
        }

        public CollectorHost Restart(int seconds)
        {
            _database.Database.Dispose();
            _database.Clock.UtcNow = Start.AddSeconds(seconds);
            return CollectorHost.Open(_database.Path, _database.Clock, new CaptureServices());
        }

        public void Dispose() => _database.Dispose();
    }

    private static string NewId() => Guid.NewGuid().ToString("D");
}
