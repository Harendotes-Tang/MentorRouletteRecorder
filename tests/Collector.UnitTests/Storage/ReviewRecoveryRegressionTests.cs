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

    /// <summary>
    /// Audit 2026-10-03 OG-4. Undoing the system revision crash recovery wrote used to put the run
    /// back to UNKNOWN, open and not pending review: the "still in flight" shape that every
    /// statistic and the review list leave out, and that no later restart closes again because the
    /// restart marker already exists. The run vanished for good. The undo is refused and nothing is
    /// written; the player still settles the run with an ordinary correction. The same holds for a
    /// run that never entered a duty, which the undo would turn into a row the rules refuse.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecoveryRevisionCannotBeUndoneIntoARunNobodyWouldSeeAgain(bool entered)
    {
        using var fixture = new Fixture(entered);
        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        var revisions = host.Revisions.ListForRun(fixture.RunId, 1, 50).Total;

        var error = Assert.Throws<CollectorException>(() => host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, recovered.Revision, "撤销自动恢复的判断")));

        Assert.Equal(ErrorCodes.UndoNotAllowed, error.Code);
        Assert.Equal("expected_revision", error.Field);
        Assert.Equal(recovered, host.Runs.Get(fixture.RunId));
        Assert.Equal(revisions, host.Revisions.ListForRun(fixture.RunId, 1, 50).Total);
        Assert.Equal(1, host.Statistics.GetDashboard().UnfinishedPendingReview);

        var confirmed = host.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, recovered.Revision,
            "确认无误", new RunChangeSet { Specified = new HashSet<string> { RunFields.PendingReview }, PendingReview = false })).Run!;
        Assert.False(confirmed.PendingReview);
    }

    /// <summary>
    /// A recovery revision that only flagged the run - its end time was the player's and stayed
    /// open - is refused too: undoing it clears the flag and leaves the same in-flight shape.
    /// </summary>
    [Fact]
    public void ARecoveryRevisionThatOnlyFlaggedTheRunCannotBeUndoneEither()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EnteredAtUtc },
            EnteredAtUtc = fixture.Start.AddSeconds(120),
        });
        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        Assert.Null(recovered.EndedAtUtc);

        var error = Assert.Throws<CollectorException>(() => host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, recovered.Revision, "撤销自动恢复的判断")));

        Assert.Equal(ErrorCodes.UndoNotAllowed, error.Code);
        Assert.Equal(recovered, host.Runs.Get(fixture.RunId));
    }

    /// <summary>
    /// The one-off repair of a row an earlier version stored is a system revision as well; undoing
    /// it would restore the shape the repair exists to remove, so it is refused under the undo code
    /// rather than as a malformed request.
    /// </summary>
    [Fact]
    public void TheRepairOfAnEarlierVersionsRowCannotBeUndone()
    {
        using var fixture = new Fixture(entered: false);
        fixture.WriteAsAnEarlierVersionRecovered();
        using var host = fixture.Restart(120);
        var repaired = host.Runs.Get(fixture.RunId)!;

        var error = Assert.Throws<CollectorException>(() => host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, repaired.Revision, "撤销修复")));

        Assert.Equal(ErrorCodes.UndoNotAllowed, error.Code);
        Assert.Equal(repaired, host.Runs.Get(fixture.RunId));
    }

    /// <summary>
    /// Audit 2026-10-03 CS5-X2. A correction that sets a closed automatic run back to 未知 with no end
    /// time used to produce the same "still in flight" shape an undo of recovery is refused for: left
    /// out of every statistic, off the review list, and never closed again. It is refused with a reason
    /// the player can act on, and nothing is written.
    /// </summary>
    [Fact]
    public void ACorrectionCannotPutAClosedAutomaticRunBackInFlight()
    {
        using var fixture = new Fixture();
        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        var revisions = host.Revisions.ListForRun(fixture.RunId, 1, 50).Total;

        var error = Assert.Throws<CollectorException>(() => host.Mutations.CorrectRun(new CorrectRunCommand(
            NewId(), fixture.RunId, recovered.Revision, "结局其实不清楚", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Result, RunFields.EndedAtUtc },
                Result = RunResult.Unknown, EndedAtUtc = null,
            })));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("ended_at_utc", error.Field);
        Assert.Contains("结束时间", error.Message, StringComparison.Ordinal);
        Assert.Equal(recovered, host.Runs.Get(fixture.RunId));
        Assert.Equal(revisions, host.Revisions.ListForRun(fixture.RunId, 1, 50).Total);
        Assert.Equal(1, host.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    /// <summary>
    /// Confirming a pending 未知 run that has no end time would take it off the review list into the same
    /// shape, so it is refused until the run has an end time or a result; with an end time it confirms.
    /// </summary>
    [Fact]
    public void ConfirmingAnUnknownRunWithNoEndTimeWaitsForAnEndTime()
    {
        using var fixture = new Fixture();
        fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EnteredAtUtc },
            EnteredAtUtc = fixture.Start.AddSeconds(120),
        });
        using var host = fixture.Restart(60);
        var pending = host.Runs.Get(fixture.RunId)!;
        Assert.Equal(RunResult.Unknown, pending.Result);
        Assert.Null(pending.EndedAtUtc);
        Assert.True(pending.PendingReview);

        var error = Assert.Throws<CollectorException>(() => host.Mutations.CorrectRun(new CorrectRunCommand(
            NewId(), fixture.RunId, pending.Revision, "确认无误",
            new RunChangeSet { Specified = new HashSet<string> { RunFields.PendingReview }, PendingReview = false })));
        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal(pending, host.Runs.Get(fixture.RunId));

        var confirmed = host.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, pending.Revision,
            "补上结束时间", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.PendingReview, RunFields.EndedAtUtc },
                PendingReview = false, EndedAtUtc = fixture.Start.AddSeconds(300),
            })).Run!;
        Assert.False(confirmed.PendingReview);
        Assert.Equal(RunResult.Unknown, confirmed.Result);
        Assert.NotNull(confirmed.EndedAtUtc);
    }

    /// <summary>
    /// The run the state machine is still following has that shape already; correcting something else on
    /// it - here its entry time - is not refused.
    /// </summary>
    [Fact]
    public void TheRunStillInFlightCanStillBeCorrected()
    {
        using var fixture = new Fixture();
        var corrected = fixture.Correct(new RunChangeSet
        {
            Specified = new HashSet<string> { RunFields.EnteredAtUtc },
            EnteredAtUtc = fixture.Start.AddSeconds(2),
        });

        Assert.Equal(RunResult.Unknown, corrected.Result);
        Assert.Null(corrected.EndedAtUtc);
        Assert.False(corrected.PendingReview);
    }

    /// <summary>
    /// A player's own correction of a recovered run stays undoable: undo goes back to what recovery
    /// wrote, which is closed and on the review list.
    /// </summary>
    [Fact]
    public void APlayersCorrectionOfARecoveredRunCanStillBeUndone()
    {
        using var fixture = new Fixture();
        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        var corrected = host.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, recovered.Revision,
            "其实打完了", new RunChangeSet { Specified = new HashSet<string> { RunFields.Result }, Result = RunResult.Completed })).Run!;

        var undone = host.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, corrected.Revision, "撤销误判")).Run!;

        Assert.Equal(RunResult.Interrupted, undone.Result);
        Assert.Equal(recovered.EndedAtUtc, undone.EndedAtUtc);
    }

    /// <summary>
    /// Audit 2026-10-03 ODp-3 / OG-5. A process that died while a match was still waiting to be
    /// entered left a run that never entered a duty. Closing it as INTERRUPTED with no entry time
    /// produced a row the correction rules refuse outright, so the player could neither confirm
    /// it, nor annotate it, from the review list it was put on. It never entered, so it is
    /// cancelled before entry - at low confidence and pending review, because nobody saw how.
    /// </summary>
    [Fact]
    public void ANeverEnteredRunLeftOpenIsRecoveredAsCancelledBeforeEntryAndCanBeResolved()
    {
        using var fixture = new Fixture(entered: false);

        using var host = fixture.Restart(60);
        var recovered = host.Runs.Get(fixture.RunId)!;
        Assert.Equal(RunResult.CancelledBeforeEntry, recovered.Result);
        Assert.Equal(DetectionConfidence.Low, recovered.DetectionConfidence);
        Assert.True(recovered.PendingReview);
        Assert.Null(recovered.EnteredAtUtc);
        Assert.Equal(fixture.Start.AddSeconds(60), recovered.EndedAtUtc);
        var marker = Assert.Single(host.Events.ListForRun(fixture.RunId),
            item => item.EventType == CrashRecoveryService.EventType);
        Assert.Equal(RunState.CancelledBeforeEntry, marker.ToState);
        Assert.Equal(1, host.Statistics.GetDashboard().UnfinishedPendingReview);

        var annotated = host.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, recovered.Revision,
            "补一句说明", new RunChangeSet { Specified = new HashSet<string> { RunFields.Note }, Note = "排到了但没进" })).Run!;
        var confirmed = host.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, annotated.Revision,
            "确认无误", new RunChangeSet { Specified = new HashSet<string> { RunFields.PendingReview }, PendingReview = false })).Run!;
        Assert.False(confirmed.PendingReview);
        Assert.Equal("排到了但没进", confirmed.Note);
        Assert.Equal(0, host.Statistics.GetDashboard().UnfinishedPendingReview);
    }

    /// <summary>
    /// The rows earlier versions already wrote in that shape - closed, INTERRUPTED, never entered - are
    /// put right once, through a system revision like every other automatic change, and can then be
    /// confirmed like any other record.
    /// </summary>
    [Fact]
    public void ANeverEnteredRunAnEarlierVersionRecoveredAsInterruptedIsRepairedOnce()
    {
        using var fixture = new Fixture(entered: false);
        fixture.WriteAsAnEarlierVersionRecovered();

        MentorRun repaired;
        using (var first = fixture.Restart(120))
        {
            repaired = first.Runs.Get(fixture.RunId)!;
            Assert.Equal(RunResult.CancelledBeforeEntry, repaired.Result);
            Assert.Equal(DetectionConfidence.Low, repaired.DetectionConfidence);
            Assert.True(repaired.PendingReview);
            Assert.Equal(fixture.Start.AddSeconds(60), repaired.EndedAtUtc);
            Assert.Equal(3, repaired.Revision);
            var revision = first.Revisions.GetAt(fixture.RunId, repaired.Revision, null)!;
            Assert.Equal(RevisionActor.System, revision.Actor);
            Assert.False(string.IsNullOrWhiteSpace(revision.Reason));
            Assert.Contains(revision.Changes, change => change.Field == RunFields.Result
                && Equals(change.OldValue, "INTERRUPTED") && Equals(change.NewValue, "CANCELLED_BEFORE_ENTRY"));
        }

        using var second = fixture.Restart(180);
        Assert.Equal(repaired, second.Runs.Get(fixture.RunId));
        var confirmed = second.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, repaired.Revision,
            "确认无误", new RunChangeSet { Specified = new HashSet<string> { RunFields.PendingReview }, PendingReview = false })).Run!;
        Assert.False(confirmed.PendingReview);
    }

    /// <summary>
    /// Audit 2026-10-03, V3-2. 1.5.0 still let the player undo the revision crash recovery wrote. That put an entered
    /// run back to UNKNOWN, open and not pending review - the "still in flight" shape every statistic and the review
    /// list leave out - and no later restart closed it, because its restart marker exists. Refusing the undo (OG-4)
    /// stops new ones; the rows already in that shape are put back on the review list once, through a system
    /// revision that changes nothing else, so the player can settle them. A second start finds nothing to do.
    /// </summary>
    [Fact]
    public void ARunAnEarlierVersionLeftInFlightByAnUndoneRecoveryIsPutBackOnTheReviewListOnce()
    {
        using var fixture = new Fixture();
        using (var first = fixture.Restart(60))
        {
            fixture.UndoRecoveryAsAnEarlierVersionDid(first);
            Assert.True(RunMutationRules.ReadsAsInFlight(first.Runs.Get(fixture.RunId)!));
            Assert.Equal(0, first.Statistics.GetDashboard().UnfinishedPendingReview);
        }

        MentorRun repaired;
        int revisions;
        using (var second = fixture.Restart(120))
        {
            repaired = second.Runs.Get(fixture.RunId)!;
            Assert.True(repaired.PendingReview);
            Assert.Equal(RunResult.Unknown, repaired.Result);
            Assert.Null(repaired.EndedAtUtc);
            Assert.NotNull(repaired.EnteredAtUtc);
            var revision = second.Revisions.GetAt(fixture.RunId, repaired.Revision, null)!;
            Assert.Equal(RevisionActor.System, revision.Actor);
            Assert.Equal(ChangeKind.Correct, revision.ChangeKind);
            Assert.Null(revision.RequestId);
            Assert.False(string.IsNullOrWhiteSpace(revision.Reason));
            var change = Assert.Single(revision.Changes);
            Assert.Equal(RunFields.PendingReview, change.Field);
            Assert.True(Equals(change.OldValue, false) && Equals(change.NewValue, true));
            Assert.Equal(1, second.Statistics.GetDashboard().UnfinishedPendingReview);
            Assert.Single(second.Events.ListForRun(fixture.RunId), item => item.EventType == CrashRecoveryService.EventType);
            revisions = second.Revisions.ListForRun(fixture.RunId, 1, 50).Total;
        }

        using var third = fixture.Restart(180);
        Assert.Equal(repaired, third.Runs.Get(fixture.RunId));
        Assert.Equal(revisions, third.Revisions.ListForRun(fixture.RunId, 1, 50).Total);

        // Taking the flag back off would lose the run again, so the flag is no more undoable than recovery.
        var undo = Assert.Throws<CollectorException>(() => third.Mutations.UndoRevision(new RunReasonCommand(
            NewId(), fixture.RunId, repaired.Revision, "撤销待复核标记")));
        Assert.Equal(ErrorCodes.UndoNotAllowed, undo.Code);

        var settled = third.Mutations.CorrectRun(new CorrectRunCommand(NewId(), fixture.RunId, repaired.Revision,
            "确认是中途掉线", new RunChangeSet
            {
                Specified = new HashSet<string> { RunFields.Result, RunFields.EndedAtUtc, RunFields.PendingReview },
                Result = RunResult.Disconnected, EndedAtUtc = fixture.Start.AddSeconds(90), PendingReview = false,
            })).Run!;
        Assert.False(settled.PendingReview);
    }

    /// <summary>
    /// The same shape on a row the player deleted is no longer anybody's to review, and stays as it is.
    /// </summary>
    [Fact]
    public void ADeletedRunLeftInFlightByAnUndoneRecoveryIsLeftAlone()
    {
        using var fixture = new Fixture();
        MentorRun deleted;
        using (var first = fixture.Restart(60))
        {
            fixture.UndoRecoveryAsAnEarlierVersionDid(first);
            var open = first.Runs.Get(fixture.RunId)!;
            deleted = first.Mutations.SoftDeleteRun(
                new RunReasonCommand(NewId(), fixture.RunId, open.Revision, "这条不要了")).Run!;
        }

        using var second = fixture.Restart(120);
        var after = second.Runs.Get(fixture.RunId)!;
        Assert.True(after.SoftDeleted);
        Assert.False(after.PendingReview);
        Assert.Equal(deleted.Revision, after.Revision);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabase _database = new();
        public DateTimeOffset Start { get; }
        public string RunId { get; }
        public RunMutationService Service { get; }
        public MentorRun Run => new RunRepository(_database.Database).Get(RunId)!;

        public Fixture(bool entered = true)
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
            if (entered)
            {
                processor.Accept(new ZoneInitialization
                {
                    Key = new EventKey(session, PacketDirection.ServerToClient, "entry", 1, null, "entry"),
                    ObservedAtUtc = Start.AddSeconds(1), Mono = TimeSpan.FromSeconds(1), IsDutyInstance = true,
                });
            }

            Assert.Null(processor.LastStorageError);
            RunId = machine.CurrentRunId!;
            Assert.NotNull(RunId);
        }

        /// <summary>What crash recovery before audit 2026-10-03 wrote for a never-entered open run.</summary>
        public void WriteAsAnEarlierVersionRecovered()
        {
            var runs = new RunRepository(_database.Database);
            var revisions = new RunRevisionRepository(_database.Database);
            var events = new RunEventRepository(_database.Database);
            var at = Start.AddSeconds(60);
            _database.Database.RunInTransaction(tx =>
            {
                var open = runs.GetInternal(RunId, tx)!;
                var closed = open with
                {
                    Revision = open.Revision + 1, Result = RunResult.Interrupted,
                    DetectionConfidence = DetectionConfidence.Low, PendingReview = true,
                    EndedAtUtc = at, UpdatedAtUtc = at,
                };
                runs.Update(closed, open.Revision, tx);
                revisions.Append(new RunRevision
                {
                    RevisionId = NewId(), RunId = RunId, Revision = closed.Revision, ChangedAtUtc = at,
                    ChangeKind = ChangeKind.Correct, Actor = RevisionActor.System,
                    Reason = "程序重启时发现未完结记录，已置为 INTERRUPTED 并标记待复核。",
                    Changes = RunMutationRules.Diff(open, closed),
                }, tx);
                events.Append(new RunEvent
                {
                    EventId = NewId(), RunId = RunId, Sequence = events.NextSequence(RunId, tx),
                    OccurredAtUtc = at, MonotonicOffsetMs = 0, EventType = CrashRecoveryService.EventType,
                    ToState = RunState.InterruptedPendingReview, Confidence = DetectionConfidence.Low,
                    EventKey = "earlier-version-restart:" + RunId,
                }, tx);
            });
        }

        /// <summary>
        /// What 1.5.0's undo of the recovery revision wrote: the values the revision replaced, put back by a
        /// correction in the player's name. The undo is refused now (OG-4), so it is written here directly.
        /// </summary>
        public void UndoRecoveryAsAnEarlierVersionDid(CollectorHost host)
        {
            host.Database.RunInTransaction(tx =>
            {
                var recovered = host.Runs.GetInternal(RunId, tx)!;
                var target = host.Revisions.GetAt(RunId, recovered.Revision, tx)!;
                Assert.Equal(RevisionActor.System, target.Actor);
                var restored = recovered;
                foreach (var change in target.Changes)
                {
                    restored = RunFieldWriter.Apply(restored, change.Field, change.OldValue);
                }

                restored = restored with { Revision = recovered.Revision + 1 };
                host.Runs.Update(restored, recovered.Revision, tx);
                host.Revisions.Append(new RunRevision
                {
                    RevisionId = NewId(), RunId = RunId, Revision = restored.Revision, ChangedAtUtc = host.Clock.UtcNow,
                    ChangeKind = ChangeKind.Correct, Actor = RevisionActor.User, Reason = "撤销自动恢复的判断",
                    RequestId = NewId(), Changes = RunMutationRules.Diff(recovered, restored),
                }, tx);
            });
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
