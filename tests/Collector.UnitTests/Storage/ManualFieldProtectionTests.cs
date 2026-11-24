using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
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

        fixture.Live.Exit();

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
        fixture.Live.Exit();

        Assert.Equal("保留我写的备注", fixture.Run.Note);
        Assert.Equal(fixture.Start.AddSeconds(60), fixture.Run.EndedAtUtc);
        Assert.Equal(59_000, fixture.Run.DurationMs);
        Assert.True(fixture.Run.PendingReview);
        // A note was never the software's to record, so writing one corrects nothing (1.3.1).
        Assert.False(fixture.Run.ManuallyCorrected);
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
        fixture.Live.Job(21, 5);
        fixture.Live.Exit();

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
        var merged = fixture.Merge(current, current with { JobId = 19, JobName = "自动值", Role = Role.Tank });

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
        fixture.Live.Job(21, 5);
        Assert.Equal(21, fixture.Run.JobId);

        // Withdrawing the edit does not erase that it happened.
        Assert.True(fixture.Run.ManuallyCorrected);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(24)]
    public void EachCorrectionCycleUsesItsOwnBaselineAndCanReleaseProtection(int secondJob)
    {
        using var fixture = new Fixture();
        void CorrectJob(int job) => fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.JobId }, JobId = job,
        });
        void Undo() => fixture.Service.UndoRevision(new RunReasonCommand(Guid.NewGuid().ToString("D"),
            fixture.Run.RunId, fixture.Run.Revision, "撤销本轮职业更正"));

        CorrectJob(24);
        Undo();
        fixture.Live.Job(21, 5);
        Assert.Equal(21, fixture.Run.JobId);
        CorrectJob(secondJob);
        fixture.Live.Job(32, 6);
        Assert.Equal(secondJob, fixture.Run.JobId);

        var current = fixture.Run;
        var merged = fixture.Merge(current, current with { JobId = 32 });
        Assert.Equal(secondJob, merged.JobId);

        Undo();
        Assert.Equal(21, fixture.Run.JobId);
        current = fixture.Run;
        merged = fixture.Merge(current, current with { JobId = 32 });
        Assert.Equal(32, merged.JobId);
        fixture.Live.Job(33, 7);
        Assert.Equal(33, fixture.Run.JobId);
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
        fixture.Live.Exit();
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

        fixture.Live.Victory();

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

        fixture.Live.Victory();

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
        fixture.Live.ExitAt(-30);

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

        var merged = fixture.Merge(
            current,
            current with
            {
                Result = RunResult.Completed,
                DetectionConfidence = DetectionConfidence.High,
                PendingReview = false,
                EnteredAtUtc = null,
                EndedAtUtc = null,
            });

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

        var merged = fixture.Merge(
            current,
            current with
            {
                Result = RunResult.Completed,
                DetectionConfidence = DetectionConfidence.High,
                PendingReview = false,
                EndedAtUtc = fixture.Start.AddSeconds(60),
            });

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
        var merged = fixture.Merge(current, current with { JobId = 21 });
        Assert.Equal(24, merged.JobId);
    }

    /// <summary>
    /// Until 1.3.1 every CorrectRun set <c>manually_corrected</c>, including the player saying how a
    /// pending run went, so every automatic record on a machine already carries the flag. The rule
    /// changed (<see cref="RunMutationRules.OverrulesTheRecord(bool, IReadOnlyList{RunFieldChange})"/>);
    /// the rows written under the old one are re-read against their own revision chains at startup.
    /// </summary>
    [Fact]
    public void RowsThatWereOnlyConfirmedLoseTheFlagAndRealCorrectionsKeepIt()
    {
        using var db = new TestDatabase();
        var runs = new RunRepository(db.Database);
        var service = db.Mutations();

        MentorRun Pending()
        {
            var created = service.CreateManualRun(TestDatabase.ManualRun(RunResult.Unknown) with { Region = Region.Unknown });
            var stored = runs.Get(created.RunId)!;
            // Into review the way the machine does it: in place, without spending a revision.
            db.Database.RunInTransaction(tx => runs.Update(stored with { PendingReview = true }, stored.Revision, tx));
            return runs.Get(created.RunId)!;
        }

        var confirmed = Pending();
        service.CorrectRun(new CorrectRunCommand(Guid.NewGuid().ToString("D"), confirmed.RunId, confirmed.Revision, "确认通关",
            new RunChangeSet { Specified = new HashSet<string> { RunFields.Result }, Result = RunResult.Completed }));
        var corrected = Pending();
        service.CorrectRun(new CorrectRunCommand(Guid.NewGuid().ToString("D"), corrected.RunId, corrected.Revision, "职业记错了",
            new RunChangeSet { Specified = new HashSet<string> { RunFields.JobId }, JobId = 21 }));

        // What a database written by 1.3.0 and earlier looks like: both rows flagged.
        using (var command = db.Database.CreateCommand())
        {
            command.CommandText = "UPDATE mentor_runs SET manually_corrected = 1;";
            command.ExecuteNonQuery();
        }

        var changed = CorrectedFlagMaintenance.Run(db.Database);

        Assert.Equal(1, changed);
        Assert.False(runs.Get(confirmed.RunId)!.ManuallyCorrected);
        Assert.True(runs.Get(corrected.RunId)!.ManuallyCorrected);
        // Idempotent, and it spends no revision: the flag is derived, the audit chain is not touched.
        Assert.Equal(0, CorrectedFlagMaintenance.Run(db.Database));
        Assert.Equal(confirmed.Revision + 1, runs.Get(confirmed.RunId)!.Revision);
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
        var merged = fixture.Merge(corrected, corrected with
        {
            ContentId = 900003, TerritoryId = 800003, DutyName = "自动副本", DutyCategory = "自动分类",
            EndedAtUtc = fixture.Start.AddSeconds(60),
        });

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

        fixture.Live.Territory(1036, 2);

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
        fixture.Live.Exit();
        var stored = fixture.Database.Database.RunInTransaction(tx =>
            fixture.Runs.GetInternal(current.RunId, tx))!;
        Assert.True(stored.SoftDeleted);
        Assert.False(stored.PendingReview);
        Assert.Equal(0, fixture.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    /// <summary>
    /// A run capture has matched and entered: with a content id the entry names the duty and the
    /// job follows a second later; without one only the duty flag is known.
    /// </summary>
    private sealed class Fixture : RunMutationTestContext
    {
        private readonly string _runId;

        public Fixture(bool canDetectResult = false, bool withContentId = true)
        {
            Statistics = new StatisticsRepository(Database.Database, Settings);
            Live = new LiveRunTestBed(Database,
                ProfileBinding.Live("review", Region.Cn, ProfileStatus.Verified, 42, canDetectResult));
            _runId = Live.Pop(withContentId ? 900001 : null);
            if (withContentId)
            {
                Live.Enter();
                Live.Job(19, 2);
            }
            else
            {
                Live.Enter(contentId: null, territoryId: null, isDutyInstance: true);
            }
        }

        public LiveRunTestBed Live { get; }

        public DateTimeOffset Start => Live.Start;

        public StatisticsRepository Statistics { get; }

        public MentorRun Run => Runs.Get(_runId)!;

        public MentorRun Correct(RunChangeSet changes) => Service.CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"), Run.RunId, Run.Revision, "人工核对后更正", changes)).Run!;

        /// <summary>What a new field protection makes of an automatic write over <paramref name="current"/>.</summary>
        public MentorRun Merge(MentorRun current, MentorRun proposed) => Database.Database.RunInTransaction(tx =>
            new ManualRunFieldProtection(Database.Database).Merge(current, proposed, tx));
    }
}
