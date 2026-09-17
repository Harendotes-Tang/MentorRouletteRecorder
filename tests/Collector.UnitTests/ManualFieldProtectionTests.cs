using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>人工修订之后继续处理真实语义事件，验证字段保护与待复核之间的完整写入链。</summary>
public sealed class ManualFieldProtectionTests
{
    [Fact]
    public void ManualResultAndTimesSurviveAutomaticExitWithoutNewAuditRevision()
    {
        using var fixture = new Fixture();
        var manualEnd = fixture.Start.AddSeconds(30);
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.Result, RunFields.EndedAtUtc },
            Result = RunResult.Completed, EndedAtUtc = manualEnd,
        });

        fixture.Exit();

        var stored = fixture.Run;
        Assert.Equal(RunResult.Completed, stored.Result);
        Assert.Equal(manualEnd, stored.EndedAtUtc);
        Assert.Equal(corrected.DurationMs, stored.DurationMs);
        Assert.False(stored.PendingReview);
        Assert.Equal(corrected.Revision, stored.Revision);
        Assert.Equal(stored.Revision, fixture.Revisions.ListForRun(stored.RunId, 1, 50).Total);
    }

    [Fact]
    public void NoteOnlyCorrectionAllowsAutomaticEndAndRemainsVisibleForReview()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.Note }, Note = "保留我写的备注",
        });
        fixture.Exit();

        Assert.Equal("保留我写的备注", fixture.Run.Note);
        Assert.Equal(fixture.Start.AddSeconds(60), fixture.Run.EndedAtUtc);
        Assert.Equal(59_000, fixture.Run.DurationMs);
        Assert.True(fixture.Run.PendingReview);
        Assert.True(fixture.Run.ManuallyCorrected);
        Assert.Equal(1, fixture.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    [Fact]
    public void ManualJobAndDerivedRoleSurviveLaterPlayerJobObservation()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = 24,
        });
        fixture.Job(21, 5);
        fixture.Exit();

        Assert.Equal(24, fixture.Run.JobId);
        Assert.Equal(corrected.JobName, fixture.Run.JobName);
        Assert.Equal(corrected.Role, fixture.Run.Role);
        Assert.NotNull(fixture.Run.EndedAtUtc);
        Assert.True(fixture.Run.PendingReview);
    }

    [Fact]
    public void ANewProtectionInstanceHonorsPersistentCorrectionsAndUndoValues()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = 24,
        });
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = 21,
        });
        var beforeUndo = fixture.Run;
        fixture.Service.UndoRevision(new RunReasonCommand(Guid.NewGuid().ToString("D"),
            beforeUndo.RunId, beforeUndo.Revision, "恢复上一次职业选择"));

        var current = fixture.Run;
        var protection = new ManualRunFieldProtection(fixture.Database.Database);
        var merged = fixture.Database.Database.RunInTransaction(tx =>
            protection.Merge(current, current with { JobId = 19, JobName = "自动值", Role = Role.Tank }, tx));

        Assert.Equal(24, merged.JobId);
        Assert.Equal(current.JobName, merged.JobName);
        Assert.Equal(current.Role, merged.Role);
    }

    /// <summary>
    /// The reference for "this correction was withdrawn" is what the field held immediately
    /// before the first user correction, not what the record was created with. Capture fills
    /// the job in seconds after the pop, so the created value is null while the pre-correction
    /// value is the real job; measured against the created value, an undone job correction
    /// freezes the field for good and no later class change is ever recorded
    /// (review finding R-8).
    /// </summary>
    [Fact]
    public void AnUndoneJobCorrectionReleasesProtectionSoALaterClassChangeStillApplies()
    {
        using var fixture = new Fixture();

        // Capture already filled the job in (19) before the user touched it.
        Assert.Equal(19, fixture.Run.JobId);

        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = 24,
        });
        var current = fixture.Run;
        Assert.Equal(24, current.JobId);

        fixture.Service.UndoRevision(new RunReasonCommand(Guid.NewGuid().ToString("D"),
            current.RunId, current.Revision, "保留更正前的职业"));
        Assert.Equal(19, fixture.Run.JobId);

        // The player really does change class now. The withdrawn edit must not block it.
        fixture.Job(21, 5);
        Assert.Equal(21, fixture.Run.JobId);

        // Withdrawing the edit does not erase that it happened.
        Assert.True(fixture.Run.ManuallyCorrected);
    }

    [Theory]
    [InlineData(30, 30_000L)]
    [InlineData(120, null)]
    public void CorrectedEntryTimeCannotMakeAnAutomaticExitFailStorage(int enteredSeconds, long? duration)
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EnteredAtUtc },
            EnteredAtUtc = fixture.Start.AddSeconds(enteredSeconds),
        });
        fixture.Exit();
        Assert.Equal(fixture.Start.AddSeconds(enteredSeconds), fixture.Run.EnteredAtUtc);
        Assert.Equal(duration, fixture.Run.DurationMs);
        if (enteredSeconds > 60) Assert.Null(fixture.Run.EndedAtUtc);
        else Assert.Equal(fixture.Start.AddSeconds(60), fixture.Run.EndedAtUtc);
        Assert.Equal(RunResult.Unknown, fixture.Run.Result);
        Assert.True(fixture.Run.PendingReview);
    }

    /// <summary>
    /// Undoing a correction back to the value the record was created with is the user
    /// withdrawing their edit, so the field goes back to being filled in automatically. Were it
    /// to stay frozen, a mistyped end time that was undone would stop the duty result from ever
    /// closing that run again (review finding L-19).
    /// </summary>
    [Fact]
    public void AnUndoneEndTimeReleasesProtectionSoALaterVictoryStillFinishesTheRun()
    {
        using var fixture = new Fixture(canDetectResult: true);
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EndedAtUtc },
            EndedAtUtc = fixture.Start.AddSeconds(30),
        });
        fixture.Service.UndoRevision(new RunReasonCommand(Guid.NewGuid().ToString("D"),
            corrected.RunId, corrected.Revision, "撤销误填的结束时间"));
        Assert.Null(fixture.Run.EndedAtUtc);

        fixture.Victory();

        Assert.Equal(fixture.Start.AddSeconds(60), fixture.Run.EndedAtUtc);
        Assert.Equal(RunResult.Completed, fixture.Run.Result);
        Assert.False(fixture.Run.PendingReview);

        // Withdrawing the edit does not erase that it happened.
        Assert.True(fixture.Run.ManuallyCorrected);
    }

    /// <summary>A correction that was never undone still keeps the automatic exit out.</summary>
    [Fact]
    public void AStandingEndTimeCorrectionStillSurvivesALaterVictory()
    {
        using var fixture = new Fixture(canDetectResult: true);
        var manualEnd = fixture.Start.AddSeconds(30);
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EndedAtUtc }, EndedAtUtc = manualEnd,
        });

        fixture.Victory();

        Assert.Equal(manualEnd, fixture.Run.EndedAtUtc);
    }

    /// <summary>
    /// The time-consistency rewrite exists to protect a human decision. Applied to a purely
    /// automatic write it turns a legitimate finish into an open record with a null end time,
    /// and because the machine considers the run closed nothing would ever finish it again
    /// (review finding H-8).
    /// </summary>
    [Fact]
    public void AnAutomaticExitStampedBeforeTheEntryStillClosesTheRun()
    {
        using var fixture = new Fixture();

        // The wall clock runs 30 s behind the stamp the entry was recorded with.
        fixture.ExitAt(-30);

        var stored = fixture.Run;

        // The end stamp is pulled up to the entry rather than thrown away: the record is
        // closed, and the database's own ordering constraint is satisfied.
        Assert.Equal(stored.EnteredAtUtc, stored.EndedAtUtc);
        Assert.NotNull(stored.EndedAtUtc);

        // The duration keeps coming from the monotonic reading, which the skew cannot touch.
        Assert.Equal(59_000, stored.DurationMs);
        Assert.Equal(RunResult.Unknown, stored.Result);
        Assert.True(stored.PendingReview);

        // Nothing about this is a manual decision, so no revision claims one.
        Assert.False(stored.ManuallyCorrected);
    }

    /// <summary>
    /// "COMPLETED with a missing endpoint" is one of the contradictions the time-consistency
    /// check names, and the clamp that replaced the roll-back for finding H-8 only orders
    /// endpoints that exist. Without a rule of its own, a completed run with no entry stamp is
    /// written through unchanged, leaving nothing but the storage latch between it and the
    /// database (review finding R-13).
    /// </summary>
    [Fact]
    public void AnAutomaticCompletedRunWithAMissingEndpointFallsBackToUnknownForReview()
    {
        using var fixture = new Fixture();
        var current = fixture.Run;
        var protection = new ManualRunFieldProtection(fixture.Database.Database);

        var merged = fixture.Database.Database.RunInTransaction(tx => protection.Merge(
            current,
            current with
            {
                Result = RunResult.Completed,
                DetectionConfidence = DetectionConfidence.High,
                PendingReview = false,
                EnteredAtUtc = null,
                EndedAtUtc = null,
            },
            tx));

        Assert.Equal(RunResult.Unknown, merged.Result);
        Assert.Equal(DetectionConfidence.Low, merged.DetectionConfidence);
        Assert.True(merged.PendingReview);
    }

    /// <summary>An automatic run with both endpoints keeps its COMPLETED result untouched.</summary>
    [Fact]
    public void AnAutomaticCompletedRunWithBothEndpointsIsLeftAlone()
    {
        using var fixture = new Fixture();
        var current = fixture.Run;
        var protection = new ManualRunFieldProtection(fixture.Database.Database);

        var merged = fixture.Database.Database.RunInTransaction(tx => protection.Merge(
            current,
            current with
            {
                Result = RunResult.Completed,
                DetectionConfidence = DetectionConfidence.High,
                PendingReview = false,
                EndedAtUtc = fixture.Start.AddSeconds(60),
            },
            tx));

        Assert.Equal(RunResult.Completed, merged.Result);
        Assert.Equal(DetectionConfidence.High, merged.DetectionConfidence);
        Assert.False(merged.PendingReview);
    }

    [Fact]
    public void OlderUndoRowsWithAResetHistoryFlagStillUseTheirPersistentRevisionTrail()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = 24,
        });
        var current = fixture.Run with { ManuallyCorrected = false };
        var protection = new ManualRunFieldProtection(fixture.Database.Database);
        var merged = fixture.Database.Database.RunInTransaction(tx =>
            protection.Merge(current, current with { JobId = 21 }, tx));
        Assert.Equal(24, merged.JobId);
    }

    [Fact]
    public void CorrectedDutyIdentityAndDisplayCannotBeReplacedByTerritoryInference()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.ContentId, RunFields.DutyName, RunFields.DutyCategory },
            ContentId = 900002, DutyName = "人工副本", DutyCategory = "人工分类",
        });
        var protection = new ManualRunFieldProtection(fixture.Database.Database);
        var merged = fixture.Database.Database.RunInTransaction(tx =>
            protection.Merge(corrected, corrected with
            {
                ContentId = 900003, TerritoryId = 800003, DutyName = "自动副本", DutyCategory = "自动分类",
                EndedAtUtc = fixture.Start.AddSeconds(60),
            }, tx));

        Assert.Equal(corrected.ContentId, merged.ContentId);
        Assert.Equal(corrected.TerritoryId, merged.TerritoryId);
        Assert.Equal(corrected.DutyName, merged.DutyName);
        Assert.Equal(corrected.DutyCategory, merged.DutyCategory);
        Assert.NotNull(merged.EndedAtUtc);
    }

    /// <summary>
    /// The CN <c>ZONE_TERRITORY</c> offset has not been confirmed against an in-duty payload,
    /// and a wrong offset lands inside the mapped territory range about half the time. A
    /// content id back-inferred from it would file the attempt under a duty the player never
    /// entered, in the column every statistic aggregates on and indistinguishably from an
    /// observed one (review finding M-5).
    /// </summary>
    [Fact]
    public void ATerritoryOnlyEntryNamesTheDutyWithoutInventingAContentId()
    {
        using var fixture = new Fixture(withContentId: false);

        fixture.Territory(1036, 2);

        var stored = fixture.Run;
        Assert.Equal(1036, stored.TerritoryId);
        Assert.Null(stored.ContentId);
        Assert.Equal("天然要害沙斯塔夏溶洞", stored.DutyName);
        Assert.Equal(DutySource.Territory, stored.DutySource);
    }

    /// <summary>A content id that was actually observed is recorded as observed.</summary>
    [Fact]
    public void AnObservedContentIdIsRecordedWithItsProvenance()
    {
        using var fixture = new Fixture();

        Assert.Equal(900001, fixture.Run.ContentId);
        Assert.Equal(DutySource.ContentId, fixture.Run.DutySource);
    }

    [Fact]
    public void SoftDeletedActiveRunDoesNotBecomeAPendingNotificationOnExit()
    {
        using var fixture = new Fixture();
        var current = fixture.Run;
        fixture.Service.SoftDeleteRun(new RunReasonCommand(Guid.NewGuid().ToString("D"),
            current.RunId, current.Revision, "这条记录无需保留在统计中"));
        fixture.Exit();
        var stored = fixture.Database.Database.RunInTransaction(tx =>
            fixture.Runs.GetInternal(current.RunId, tx))!;
        Assert.True(stored.SoftDeleted);
        Assert.False(stored.PendingReview);
        Assert.Equal(0, fixture.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _session = Guid.NewGuid().ToString("D");
        private readonly SemanticEventProcessor _processor;
        private readonly string _runId;
        public TestDatabase Database { get; } = new();
        public DateTimeOffset Start { get; }
        public RunRepository Runs { get; }
        public RunRevisionRepository Revisions { get; }
        public RunMutationService Service { get; }
        public StatisticsRepository Statistics { get; }
        public MentorRun Run => Runs.Get(_runId)!;

        public Fixture(bool canDetectResult = false, bool withContentId = true)
        {
            Start = Database.Clock.UtcNow;
            Runs = new RunRepository(Database.Database);
            Revisions = new RunRevisionRepository(Database.Database);
            var settings = new SettingsRepository(Database.Database, Database.Clock);
            settings.EnsureDefaults();
            Service = new RunMutationService(Database.Database, settings, Database.Clock);
            Statistics = new StatisticsRepository(Database.Database, settings);
            Database.Database.RunInTransaction(tx => new CaptureSessionRepository(Database.Database).Insert(
                new CaptureSession
                {
                    CaptureSessionId = _session, StartedAtUtc = Start, CollectorVersion = "review",
                    Region = Region.Cn, ProfileStatus = ProfileStatus.Verified,
                }, tx));
            _processor = new SemanticEventProcessor(Database.Database,
                new MentorRunStateMachine(ProfileBinding.Live("review", Region.Cn, ProfileStatus.Verified, 42, canDetectResult)),
                new SemanticEventProcessorOptions(_session, Region.Cn, "review", _session), Database.Clock);
            Accept(new ContentFinderPop { Key = Key("pop", 0), ObservedAtUtc = Start, Mono = TimeSpan.Zero,
                RouletteId = 42, ContentId = withContentId ? 900001 : null });
            _runId = _processor.Machine.CurrentRunId!;
            Accept(new ZoneInitialization { Key = Key("enter", 1), ObservedAtUtc = Start.AddSeconds(1),
                Mono = TimeSpan.FromSeconds(1),
                ContentId = withContentId ? 900001 : null,
                TerritoryId = withContentId ? 800001 : null,
                IsDutyInstance = withContentId ? null : true });
            if (withContentId)
            {
                Job(19, 2);
            }
        }

        public MentorRun Correct(RunChangeSet changes) => Service.CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"), Run.RunId, Run.Revision, "人工核对后更正", changes)).Run!;
        /// <summary>A territory announcement observed inside the duty.</summary>
        public void Territory(int territoryId, int seconds) => Accept(new TerritoryObserved
        {
            Key = Key("territory", seconds), ObservedAtUtc = Start.AddSeconds(seconds),
            Mono = TimeSpan.FromSeconds(seconds), TerritoryId = territoryId,
        });
        public void Job(int job, int seconds) => Accept(new PlayerJob
        {
            Key = Key("job", seconds), ObservedAtUtc = Start.AddSeconds(seconds),
            Mono = TimeSpan.FromSeconds(seconds), JobId = job,
        });
        public void Exit() => Accept(new ZoneLeft
        {
            Key = Key("exit", 60), ObservedAtUtc = Start.AddSeconds(60), Mono = TimeSpan.FromSeconds(60),
        });
        /// <summary>An exit whose wall-clock stamp is <paramref name="seconds"/> from the start.</summary>
        public void ExitAt(int seconds) => Accept(new ZoneLeft
        {
            Key = Key("exit", seconds), ObservedAtUtc = Start.AddSeconds(seconds),
            Mono = TimeSpan.FromSeconds(60),
        });
        public void Victory() => Accept(new DutyResult
        {
            Key = Key("victory", 60), ObservedAtUtc = Start.AddSeconds(60),
            Mono = TimeSpan.FromSeconds(60), Victory = true,
        });
        private EventKey Key(string kind, int seconds) =>
            new(_session, PacketDirection.ServerToClient, kind, seconds, null, kind + seconds);
        private void Accept(SemanticEvent value)
        {
            Database.Clock.UtcNow = value.ObservedAtUtc;
            Database.Clock.Elapsed = value.Mono;
            _processor.Accept(value);
            Assert.Null(_processor.LastStorageError);
        }
        public void Dispose() => Database.Dispose();
    }
}
