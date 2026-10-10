using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Covers the pipeline shared by replay and live capture directly rather than through a fixture:
/// semantic events in, rows out, and the two lifecycle callbacks producing exactly the terminal
/// states docs/state-machine.md prescribes.
/// </summary>
public sealed class ProtocolPipelineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void PermanentlyDeletedActiveRunConsumesLateMessagesWithoutStorageFaultOrRecreation(bool completedBeforePurge, bool automaticExpiry)
    {
        using var fixture = new TestDatabase(); var processor = NewProcessor(fixture, out var machine);
        processor.Accept(Pop(0)); processor.Accept(Zone(1000));
        var runId = machine.CurrentRunId!;
        if (completedBeforePurge) processor.Accept(Result(2000, victory: true));
        var settings = new SettingsRepository(fixture.Database, fixture.Clock); settings.EnsureDefaults();
        var mutations = new Storage.Mutations.RunMutationService(fixture.Database, settings, fixture.Clock);
        var deleted = mutations.SoftDeleteRun(new RunReasonCommand(Guid.NewGuid().ToString("D"), runId, 1, "不保留当前记录")).Run!;
        if (automaticExpiry)
        {
            fixture.Clock.UtcNow += TimeSpan.FromDays(31);
            using var retention = new Storage.Mutations.HistoryRetentionService(fixture.Database, fixture.Clock);
            Assert.Equal(1, retention.CheckExpired(force: true));
        }
        else mutations.BatchMutateRuns(new BatchMutateRunsCommand(Guid.NewGuid().ToString("D"), "purge",
            new[] { new BatchRunTarget(runId, deleted.Revision) }, "永久删除"));
        processor.Accept(Job(3000)); processor.Accept(Result(4000, victory: true));
        processor.Accept(Pop(5000)); processor.Accept(Zone(6000)); processor.Accept(Result(7000, victory: true));
        Assert.Null(processor.LastStorageError); Assert.Null(new RunRepository(fixture.Database).Get(runId));
        var next = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.NotEqual(runId, next.RunId); Assert.Equal(RunResult.Completed, next.Result);
        using var trail = fixture.Database.CreateCommand(); trail.CommandText = "SELECT COUNT(*) FROM run_events WHERE run_id=$id;";
        trail.Parameters.AddWithValue("$id", runId); Assert.Equal(0L, trail.ExecuteScalar());
    }

    private const string SessionId = "30000000-0000-4000-8000-000000000001";
    private const int MentorRoulette = 42;

    /// <summary>Segment type the synthetic profile declares for every one of its messages.</summary>
    private const ushort SyntheticSegmentType = 61440;

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(2, 1037, "地下灵殿塔姆·塔拉墓园", "四人迷宫")]
    [InlineData(2, null, "地下灵殿塔姆·塔拉墓园", "四人迷宫")]
    [InlineData(42, 214, "完成集团战训练！", "行会令")]
    [InlineData(7_000_001, null, null, null)]
    public void RepeatedPopChangingDutyRefreshesTheStoredIdentity(
        int contentId, int? territoryId, string? dutyName, string? dutyCategory)
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor,
            Pop(0) with { ContentId = 4 },
            Pop(10_000) with { ContentId = contentId },
            Zone(20_000) with { ContentId = contentId, TerritoryId = territoryId });

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(contentId, run.ContentId);
        Assert.Equal(territoryId ?? (contentId == 2 ? 1037 : null), run.TerritoryId);
        Assert.Equal(dutyName, run.DutyName);
        Assert.Equal(dutyCategory, run.DutyCategory);
        Assert.Equal(DutySource.ContentId, run.DutySource);
        Assert.NotNull(run.EnteredAtUtc);
    }

    [Fact]
    public void RepeatedPopChangingDutyPreservesManualIdentityCorrection()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0) with { ContentId = 4 });
        CorrectTheDuty(fixture, Assert.Single(Runs(fixture, processor)));
        var corrected = Assert.Single(Runs(fixture, processor));

        Feed(fixture, processor,
            Pop(10_000) with { ContentId = 2 },
            Zone(20_000) with { ContentId = 2, TerritoryId = 1037 });

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(corrected.ContentId, run.ContentId);
        Assert.Equal(corrected.TerritoryId, run.TerritoryId);
        Assert.Equal(corrected.DutyName, run.DutyName);
        Assert.Equal(corrected.DutyCategory, run.DutyCategory);
        Assert.Equal(corrected.DutySource, run.DutySource);
        Assert.NotNull(run.EnteredAtUtc);
    }

    [Fact]
    public void SemanticEventsBecomeACompletedRun()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);

        Feed(fixture, processor,
            Pop(0),
            Zone(5_000),
            Job(6_000),
            Result(125_000, victory: true));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(120_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
        Assert.Equal(900_001, run.ContentId);
        Assert.Equal(RunSource.AutoNetwork, run.Source);
        Assert.Equal(4, processor.EventsAppended);
        Assert.Equal(1, processor.RevisionsAppended);
    }

    [Fact]
    public void CaptureStoppedInsideADutyProducesInterrupted()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0), Zone(5_000));

        processor.OnCaptureStopped(gameExited: false, Start.AddMilliseconds(60_000), TimeSpan.FromSeconds(60));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.Interrupted, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.Equal(RunState.Interrupted, processor.Machine.State);
        Assert.NotNull(run.EnteredAtUtc);
    }

    [Fact]
    public void ConnectionLostInsideADutyProducesDisconnectedAndNeverLeftOrAbandoned()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0), Zone(5_000));

        processor.OnConnectionLost(Start.AddMilliseconds(70_000), TimeSpan.FromSeconds(70));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.Disconnected, run.Result);
        Assert.NotEqual(RunResult.LeftOrAbandoned, run.Result);
        Assert.Equal(RunState.Disconnected, processor.Machine.State);
    }

    [Fact]
    public void CaptureStoppedBeforeEnteringProducesCancelledAndNoAttempt()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0));

        processor.OnCaptureStopped(gameExited: true, Start.AddMilliseconds(20_000), TimeSpan.FromSeconds(20));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.CancelledBeforeEntry, run.Result);
        Assert.Null(run.EnteredAtUtc);
    }

    [Fact]
    public void DroppedEventsCloseTheRunAsInterrupted()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0), Zone(5_000));

        processor.OnEventsDropped(17, Start.AddMilliseconds(30_000), TimeSpan.FromSeconds(30));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.Interrupted, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppedEventsBeforeEntryCloseTheMatchAndRequireFreshEvidence(bool fromQueue)
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out var machine, fromQueue);
        Feed(fixture, processor, BarePop(0));
        if (fromQueue)
        {
            Feed(fixture, processor, new MatchAnnounced
            {
                Key = Key("MATCH_ANNOUNCED", 1_000),
                ObservedAtUtc = Start.AddSeconds(1),
                Mono = TimeSpan.FromSeconds(1),
            });
        }
        Assert.Equal(RunState.MentorMatched, machine.State);
        processor.OnEventsDropped(2, Start.AddSeconds(12), TimeSpan.FromSeconds(12));

        var cancelled = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunState.CancelledBeforeEntry, machine.State);
        Assert.Equal(RunResult.CancelledBeforeEntry, cancelled.Result);
        Assert.Equal(DetectionConfidence.Low, cancelled.DetectionConfidence);
        Assert.True(cancelled.PendingReview);
        Assert.Null(cancelled.EnteredAtUtc);

        // A zone shortly after the gap and another past the announcement window must not
        // reuse either the old match or the longer-lived queue behind an announcement.
        Feed(fixture, processor,
            Zone(20_000) with { IsDutyInstance = true }, Result(30_000, true),
            BareZone(150_000), Zone(160_000) with { IsDutyInstance = true });
        Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.CancelledBeforeEntry, Runs(fixture, processor)[0].Result);

        Feed(fixture, processor, BarePop(180_000),
            Zone(185_000) with { IsDutyInstance = true }, Result(190_000, true));
        Assert.Equal(2, Runs(fixture, processor).Count);
        Assert.Single(Runs(fixture, processor), run => run.Result == RunResult.Completed);
    }

    /// <summary>
    /// Every game connection closing while matched is the same loss as a sequence gap: the
    /// server-side match does not survive it, so the record closes without an entry and the
    /// zone the player lands in after relogging is not this match's duty.
    /// </summary>
    [Fact]
    public void ConnectionLostBeforeEntryClosesTheMatchAndRequiresFreshEvidence()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out var machine);
        Feed(fixture, processor, BarePop(0));
        Assert.Equal(RunState.MentorMatched, machine.State);

        processor.OnConnectionLost(Start.AddSeconds(12), TimeSpan.FromSeconds(12));

        var cancelled = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunState.CancelledBeforeEntry, machine.State);
        Assert.Equal(RunResult.CancelledBeforeEntry, cancelled.Result);
        Assert.Equal(DetectionConfidence.Low, cancelled.DetectionConfidence);
        Assert.True(cancelled.PendingReview);
        Assert.Null(cancelled.EnteredAtUtc);

        Feed(fixture, processor, Zone(20_000) with { IsDutyInstance = true }, Result(30_000, true));
        Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.CancelledBeforeEntry, Runs(fixture, processor)[0].Result);
    }

    [Fact]
    public void LifecycleCallbacksAreDistinctObservationsAndBothGetRecorded()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, Pop(0), Zone(5_000), Result(60_000, victory: true));

        processor.OnConnectionLost(Start.AddMilliseconds(70_000), TimeSpan.FromSeconds(70));
        processor.OnConnectionLost(Start.AddMilliseconds(80_000), TimeSpan.FromSeconds(80));

        // The run had already completed, so neither drop may revive or reopen it.
        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(RunResult.Completed, run.Result);
    }

    /// <summary>
    /// The CN shape: a pop that names no content, an entry marker that names nothing at all,
    /// and a territory announcement just before it. The stored run must still carry a duty
    /// name (docs/state-machine.md 3.11).
    /// </summary>
    [Fact]
    public void ATerritoryAnnouncementNamesTheDutyOfARunThatEnteredBlind()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);

        Feed(fixture, processor, Job(0), BarePop(10_000), Territory(19_950, 1036), BareZone(20_000));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(1036, run.TerritoryId);
        Assert.Equal("天然要害沙斯塔夏溶洞", run.DutyName);
        Assert.Equal("四人迷宫", run.DutyCategory);
        Assert.Equal(19, run.JobId);
        Assert.Equal("骑士", run.JobName);

        // The name is a local display mapping; the content id behind it is not an observation
        // and must not reach the column duty statistics aggregate on (review finding M-5).
        // DutySource records how the duty was established.
        Assert.Null(run.ContentId);
        Assert.Equal(DutySource.Territory, run.DutySource);
    }

    /// <summary>
    /// Twenty-one CN territories host more than one duty. The record still gets a name, taken
    /// from the lowest content id on that territory, and confidence is not raised by it.
    /// </summary>
    [Fact]
    public void ATerritorySharedBySeveralDutiesResolvesToTheLowestContentId()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);

        Feed(fixture, processor, BarePop(10_000), BareZone(20_000), Territory(21_000, 792));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(792, run.TerritoryId);
        Assert.Null(run.ContentId);
        Assert.Equal("虚景跳跳乐大挑战", run.DutyName);
        Assert.Equal(DutySource.Territory, run.DutySource);
    }

    [Fact]
    public void ATerritoryTheReferenceFileDoesNotKnowLeavesTheIdAndNoName()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);

        Feed(fixture, processor, BarePop(10_000), Territory(19_950, 7_000_001), BareZone(20_000));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal(7_000_001, run.TerritoryId);
        Assert.Null(run.ContentId);
        Assert.Null(run.DutyName);
    }

    /// <summary>
    /// A user who has already named this duty outranks every automatic inference. The
    /// territory arrives after the correction and must not rewrite any of the duty fields
    /// the correction owns.
    /// </summary>
    [Fact]
    public void AManuallyCorrectedRunKeepsItsDutyFields()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);
        Feed(fixture, processor, BarePop(10_000), BareZone(20_000));
        var stored = Assert.Single(Runs(fixture, processor));
        CorrectTheDuty(fixture, stored);

        Feed(fixture, processor, Territory(21_000, 1036));

        var run = Assert.Single(Runs(fixture, processor));
        Assert.Equal("用户改过的副本", run.DutyName);
        Assert.Equal(12345, run.ContentId);
        Assert.Null(run.TerritoryId);
        Assert.Null(run.DutyCategory);
    }

    /// <summary>
    /// Records a user correction of the duty the way the mutation path does: the row is
    /// rewritten and a USER/CORRECT revision names the changed fields. The revision is what
    /// marks a field as user-owned; writing only the row would leave the automatic path free
    /// to overwrite it.
    /// </summary>
    /// <param name="fixture">Database under test.</param>
    /// <param name="stored">Run as the processor last wrote it.</param>
    private static void CorrectTheDuty(TestDatabase fixture, MentorRun stored)
    {
        var corrected = stored with
        {
            Revision = stored.Revision + 1,
            ManuallyCorrected = true,
            ContentId = 12345,
            DutyName = "用户改过的副本",
        };

        fixture.Database.RunInTransaction(transaction =>
        {
            new RunRepository(fixture.Database).Update(corrected, stored.Revision, transaction);
            new RunRevisionRepository(fixture.Database).Append(new RunRevision
            {
                RevisionId = SemanticEventProcessor.DeterministicId(stored.RunId + ":correction"),
                RunId = stored.RunId,
                Revision = corrected.Revision,
                ChangedAtUtc = Start.AddMilliseconds(20_500),
                ChangeKind = ChangeKind.Correct,
                Actor = RevisionActor.User,
                Reason = "手动填的副本名",
                Changes = new[]
                {
                    new RunFieldChange(RunFields.ContentId, stored.ContentId, corrected.ContentId),
                    new RunFieldChange(RunFields.DutyName, stored.DutyName, corrected.DutyName),
                },
            }, transaction);
        });
    }

    [Fact]
    public void AFailClosedProfileWritesNothingAndOnlyCountsRefusals()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var machine = new MentorRunStateMachine(ProfileBinding.FailClosed);
        var processor = new SemanticEventProcessor(
            fixture.Database,
            machine,
            new SemanticEventProcessorOptions(SessionId, Region.Cn, null, "failclosed"),
            fixture.Clock,
            JobCatalog.Default,
            DutyCatalog.Default);

        Feed(fixture, processor, Pop(0), Zone(5_000), Result(60_000, victory: true));

        Assert.Empty(processor.TouchedRunIds);
        Assert.Equal(0, processor.RunsCreated);
        Assert.Equal(3, machine.ParserErrorCount);
        Assert.Equal(3, new ParserErrorRepository(fixture.Database, fixture.Clock).Count());
    }

    [Fact]
    public void AcceptRollsBackMachineAndDedupState_WhenTheCommitFails()
    {
        using var fixture = new TestDatabase();
        var processor = new SemanticEventProcessor(
            fixture.Database,
            new MentorRunStateMachine(
                ProfileBinding.Synthetic("pipeline-test", MentorRoulette),
                StateMachineOptions.Default,
                () => "rollback-run"),
            new SemanticEventProcessorOptions(SessionId, Region.Cn, "pipeline-test", "rollback"),
            fixture.Clock,
            JobCatalog.Default,
            DutyCatalog.Default);

        processor.Accept(Pop(0));

        // The kind and the message both, so the capture fault text can say what went wrong.
        Assert.StartsWith("SqliteException: ", processor.LastStorageError);
        Assert.Equal(RunState.Idle, processor.Machine.State);
        Assert.Null(processor.Machine.CurrentRunId);
        Assert.Empty(processor.TouchedRunIds);
        Assert.Equal(0, processor.RunsCreated);
        Assert.Equal(0, processor.EventsAppended);
        Assert.Equal(0, processor.RevisionsAppended);

        EnsureSession(fixture);
        processor.Accept(Pop(0));

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal("rollback-run", run.RunId);
        Assert.Equal(RunState.MentorMatched, processor.Machine.State);
        Assert.Null(processor.LastStorageError);
    }

    /// <summary>
    /// A busy database is transient, and the event that hits it is usually the pop: dropping
    /// it means the whole run never exists. One short retry keeps the record.
    /// </summary>
    [Fact]
    public void ABusyCommitIsRetried_AndTheEventSurvives()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var attempts = 0;
        var processor = NewProcessor(fixture, "busy-run", work =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new CollectorException(
                    ErrorCodes.DbBusy, "数据库正忙，请稍后用相同的请求编号重试。", retryable: true);
            }

            fixture.Database.RunInTransaction(work);
        });

        processor.Accept(Pop(0));

        Assert.Equal(2, attempts);
        Assert.Null(processor.LastStorageError);
        Assert.Equal(RunState.MentorMatched, processor.Machine.State);
        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal("busy-run", run.RunId);
        Assert.Equal(1, processor.RunsCreated);
        Assert.Equal(1, processor.EventsAppended);
    }

    [Fact]
    public void APersistentlyBusyCommitIsGivenUpOn_AndTheErrorNamesItself()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var attempts = 0;
        var processor = NewProcessor(fixture, "never-run", _ =>
        {
            attempts++;
            throw new CollectorException(
                ErrorCodes.DbBusy, "数据库正忙，请稍后用相同的请求编号重试。", retryable: true);
        });

        processor.Accept(Pop(0));

        Assert.Equal(3, attempts);
        var error = processor.LastStorageError ?? string.Empty;
        Assert.Contains(ErrorCodes.DbBusy, error, StringComparison.Ordinal);
        Assert.Contains("数据库正忙", error, StringComparison.Ordinal);
        Assert.Equal(RunState.Idle, processor.Machine.State);
        Assert.Empty(processor.TouchedRunIds);
    }

    [Fact]
    public void ProcessingTheSameEventsTwiceWritesNothingTheSecondTime()
    {
        using var fixture = new TestDatabase();
        var first = NewProcessor(fixture, out _);
        Feed(fixture, first, Pop(0), Zone(5_000), Result(60_000, victory: true));

        var second = NewProcessor(fixture, out _);
        Feed(fixture, second, Pop(0), Zone(5_000), Result(60_000, victory: true));

        Assert.Equal(0, second.RunsCreated);
        Assert.Equal(0, second.EventsAppended);
        Assert.Equal(1, second.AlreadyPersistedRuns);
        var run = Assert.Single(Runs(fixture, second));
        Assert.Equal(RunResult.Completed, run.Result);
    }

    /// <summary>
    /// One pop closes the run in flight and opens the next one, so the same observation
    /// belongs to two runs. <c>run_events.event_key</c> is globally unique, so the key must be
    /// run-scoped; an unscoped one drops the second run's opening event and empties its trail.
    /// </summary>
    [Fact]
    public void APopThatRestartsARun_KeepsTheOpeningEventOnBothRuns()
    {
        using var fixture = new TestDatabase();
        var processor = NewProcessor(fixture, out _);

        Feed(fixture, processor, Pop(0), Zone(5_000), Pop(600_000));

        var runs = Runs(fixture, processor);
        Assert.Equal(2, runs.Count);
        Assert.Equal(0, processor.EventsDeduped);

        var events = new RunEventRepository(fixture.Database);
        foreach (var run in runs)
        {
            Assert.Contains(events.ListForRun(run.RunId), item => item.EventType == "CONTENT_FINDER_POP");
        }

        // Scoping is a suffix, so the observation identity the wire reads from the key --
        // direction, opcode, payload digest -- is still the leading part of both rows.
        var keys = runs.SelectMany(run => events.ListForRun(run.RunId))
            .Where(item => item.EventType == "CONTENT_FINDER_POP")
            .Select(item => item.EventKey!)
            .ToArray();
        Assert.Equal(3, keys.Length);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.StartsWith(SessionId + "|ServerToClient|CONTENT_FINDER_POP|", key));
        Assert.All(keys, key => Assert.EndsWith("|run:" + RunIdOf(key, runs), key, StringComparison.Ordinal));

        // The restarting pop is one observation stored twice, so the two rows are the same
        // string up to the scope: nothing but the run may differ.
        var restarting = keys.Where(key => key.Contains("|run:", StringComparison.Ordinal))
            .Select(key => key[..key.LastIndexOf("|run:", StringComparison.Ordinal)])
            .GroupBy(prefix => prefix, StringComparer.Ordinal)
            .Single(group => group.Count() == 2);
        Assert.Equal(2, restarting.Count());

        // The identity the wire reports stays readable off the scoped key: the extra trailing
        // field must not push the leading fields out of the places Parse reads.
        Assert.All(keys, key => Assert.Equal("S2C", EventIdentity.Parse(key).Direction));
    }

    /// <summary>Run whose trail holds the event with this key.</summary>
    /// <param name="key">Stored, run-scoped event key.</param>
    /// <param name="runs">Runs the test produced.</param>
    private static string RunIdOf(string key, IReadOnlyList<MentorRun> runs) =>
        runs.Single(run => key.EndsWith("|run:" + run.RunId, StringComparison.Ordinal)).RunId;

    [Fact]
    public void DeterministicIdIsStableAndUuidShaped()
    {
        var first = SemanticEventProcessor.DeterministicId("seed");
        var second = SemanticEventProcessor.DeterministicId("seed");

        Assert.Equal(first, second);
        Assert.True(Guid.TryParseExact(first, "D", out _));
        Assert.NotEqual(first, SemanticEventProcessor.DeterministicId("other seed"));
    }

    /// <summary>
    /// The live bridge stamps its lifecycle events with a monotonic reading, and the duration
    /// of a run is that reading minus the entry reading. Both must come off the same clock:
    /// the pipeline's own session timer starts later than the capture source's, so mixing
    /// them shortens an interrupted duty, usually all the way to zero.
    /// </summary>
    [Fact]
    public void LiveLifecycleEventsUseTheSameMonotonicClockAsTheMessages()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var selection = ProfileSelector.SelectExplicit(SyntheticProfilePath, allowSynthetic: true);
        Assert.True(selection.IsUsable);

        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => selection);
        pipeline.OnCaptureStarted(SessionId);

        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));
        pipeline.Accept(JobMessage(900_000));

        // The stop is observed a long time after the last message in wall-clock terms, but
        // the duty lasted from the entry to the last thing the game actually said.
        fixture.Clock.UtcNow = Start.AddMilliseconds(950_000);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Interrupted, run.Result);
        Assert.Equal(885_000, run.DurationMs);
    }

    /// <summary>
    /// Every <c>run_state_changed</c> the live bridge publishes carries the run it is about.
    ///
    /// A client announcing "进入 {duty}" reads the name off this event and de-duplicates by
    /// <c>run_id</c>. Without them it falls back to the snapshot taken when the pop arrived --
    /// which on the CN client names no duty at all -- so every entry is announced as
    /// "进入 未知副本" and every reconnect replays the announcement (review finding H-3).
    /// </summary>
    [Fact]
    public async Task EveryStateChangeCarriesTheRunItIsAbout()
    {
        using var fixture = new TestDatabase();
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = NewPipeline(fixture, bus);
        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));

        var stateChanges = (await DrainAsync(subscription))
            .Where(payload => payload["kind"]!.GetValue<string>() == "run_state_changed")
            .ToArray();

        Assert.NotEmpty(stateChanges);
        Assert.All(stateChanges, payload => Assert.NotNull(payload["run"]));

        var runId = new RunRepository(fixture.Database).Query(null, null, 1, 50).Items.Single().RunId;
        var entered = stateChanges.Single(
            payload => payload["state"]!.GetValue<string>() == "ENTERED_DUTY");
        Assert.Equal(runId, entered["run"]!["run_id"]!.GetValue<string>());
        Assert.Equal(900_001, entered["run"]!["content_id"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchSourceTravelsWithLiveAndReplayedStateEvents(bool fromQueue)
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var selection = MatchSourceSelection(fromQueue);
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus, _ => selection);
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000) with
        {
            Direction = fromQueue ? MessageDirection.Outbound : MessageDirection.Inbound,
        });
        if (fromQueue)
        {
            Assert.Empty(await DrainAsync(live));
            pipeline.Accept(KnownDutyZoneMessage(15_000));
        }

        var matched = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed" &&
            payload["state"]!.GetValue<string>() == (fromQueue ? "ENTERED_DUTY" : "MENTOR_MATCHED"));
        Assert.Equal(fromQueue, matched["match_from_queue"]!.GetValue<bool>());
        Assert.NotNull(matched["run"]);

        using var replay = bus.Subscribe(Guid.NewGuid().ToString("D"));
        var replayed = Assert.Single(await DrainAsync(replay), payload =>
            payload["event_id"]!.GetValue<string>() == matched["event_id"]!.GetValue<string>());
        Assert.Equal(fromQueue, replayed["match_from_queue"]!.GetValue<bool>());
    }

    /// <summary>
    /// Reported from a real evening: the match popped and was announced, somebody withdrew, the
    /// finder put the party back in the queue by itself, and when the match popped again nothing
    /// was said. The second pop is the same run - the machine refreshes it and the state does not
    /// change - so no state event went out and the desktop had nothing to speak for. A pop that
    /// arrives well after the last one is a new popup on the player's screen, and says so; the
    /// three or four copies the client sends within a second of each other are still one.
    /// </summary>
    [Fact]
    public async Task AMatchOfferedAgainAfterSomeoneWithdrewIsAnnouncedAgain()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var selection = MatchSourceSelection(fromQueue: false);
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus, _ => selection);
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        pipeline.OnCaptureStarted(SessionId);

        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(PopMessage(10_300));
        var first = (await DrainAsync(live)).Where(payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed").ToArray();
        Assert.Single(first);
        Assert.Equal(1, first[0]["match_offer"]!.GetValue<int>());

        // Inside the match window: the same run, offered a second time.
        pipeline.Accept(PopMessage(40_000));
        var second = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed");
        Assert.Equal("MENTOR_MATCHED", second["state"]!.GetValue<string>());
        Assert.Equal(2, second["match_offer"]!.GetValue<int>());
        Assert.Equal(first[0]["run"]!["run_id"]!.GetValue<string>(), second["run"]!["run_id"]!.GetValue<string>());
        Assert.False(second["match_from_queue"]!.GetValue<bool>());

        // Long after it: the machine closes the lapsed run and opens another, and the state is
        // MENTOR_MATCHED before and after - which used to mean no event, and no voice, at all.
        pipeline.Accept(PopMessage(400_000));
        var third = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_state_changed");
        Assert.Equal("MENTOR_MATCHED", third["state"]!.GetValue<string>());
        Assert.Equal(1, third["match_offer"]!.GetValue<int>());
        Assert.NotEqual(second["run"]!["run_id"]!.GetValue<string>(), third["run"]!["run_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnobservedCancellationThenTeleportLeavesNoHistoryOrCurrentRun()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus,
            _ => MatchSourceSelection(fromQueue: true));
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000) with { Direction = MessageDirection.Outbound });
        // This profile has no cancellation opcode or duty flag, so only the ordinary zone
        // change after the user cancels is observable.
        pipeline.Accept(ZoneMessage(15_000, 5000));

        var current = pipeline.GetCurrentRun();
        Assert.Equal(RunState.Idle, current.State);
        Assert.Null(current.Run);
        Assert.Empty(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Empty(await DrainAsync(live));

        pipeline.Accept(PopMessage(30_000) with { Direction = MessageDirection.Outbound });
        pipeline.Accept(KnownDutyZoneMessage(35_000));
        var entered = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(Start.AddSeconds(30), entered.MatchedAtUtc);
        Assert.Equal(Start.AddSeconds(35), entered.EnteredAtUtc);
        Assert.Equal(1039, entered.TerritoryId);
        Assert.Equal(entered.RunId, pipeline.GetCurrentRun().Run!.RunId);
        var events = await DrainAsync(live);
        Assert.Single(events, item => item["kind"]!.GetValue<string>() == "run_created");
        Assert.DoesNotContain(events, item => item["state"]?.GetValue<string>() == "MENTOR_MATCHED");

        pipeline.Accept(Message(61443, 95_000, new byte[] { 1, 0, 0, 0 }));
        var finished = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Completed, finished.Result);
        Assert.Equal(60_000, finished.DurationMs);
    }

    [Fact]
    public void DeferredQueueCreationSurvivesFailedEntryCommitAndKeepsOriginalTimeline()
    {
        using var fixture = new TestDatabase();
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("queue-test", Region.Cn, ProfileStatus.Verified, MentorRoulette,
                matchFromQueue: true),
            new StateMachineOptions { MatchWindow = TimeSpan.FromHours(1), IsKnownDuty = id => id == 1039 },
            () => "deferred-run");
        var processor = new SemanticEventProcessor(fixture.Database, machine,
            new SemanticEventProcessorOptions(SessionId, Region.Cn, "queue-test", "deferred"), fixture.Clock);
        processor.Accept(BarePop(10_000));
        processor.Accept(Job(15_000));
        processor.Accept(Territory(19_950, 1039));
        Assert.Null(processor.LastStorageError);
        Assert.Equal(0, processor.RunsCreated);

        // The missing capture-session foreign key fails after the pending queue is consumed.
        processor.Accept(BareZone(20_000));
        Assert.NotNull(processor.LastStorageError);
        Assert.Equal(RunState.Idle, machine.State);
        Assert.Empty(processor.TouchedRunIds);
        EnsureSession(fixture);
        processor.Accept(BareZone(20_000));
        Assert.Null(processor.LastStorageError);
        processor.Accept(Result(80_000, victory: true));

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(Start.AddSeconds(10), run.MatchedAtUtc);
        Assert.Equal(Start.AddSeconds(20), run.EnteredAtUtc);
        Assert.Equal(60_000, run.DurationMs);
        Assert.Equal(19, run.JobId);
        Assert.Equal(1039, run.TerritoryId);
        Assert.NotNull(run.DutyName);
        Assert.Equal(1, processor.RevisionsAppended);
        var trail = new RunEventRepository(fixture.Database).ListForRun(run.RunId);
        Assert.Equal(0, Assert.Single(trail, item => item.EventType == "CONTENT_FINDER_POP").MonotonicOffsetMs);
        Assert.Equal(10_000, Assert.Single(trail, item => item.EventType == "ZONE_INITIALIZATION").MonotonicOffsetMs);
        Assert.Equal(70_000, Assert.Single(trail, item => item.EventType == "DUTY_RESULT").MonotonicOffsetMs);
    }

    private static ProfileSelection MatchSourceSelection(bool fromQueue)
    {
        var original = ProfileSelector.SelectExplicit(SyntheticProfilePath, allowSynthetic: true);
        var profile = original.Profile! with
        {
            MatchWindow = fromQueue ? TimeSpan.FromHours(1) : original.Profile!.MatchWindow,
            Messages = original.Profile!.Messages.Select(message => message.Name switch
            {
                "CONTENT_FINDER_POP" => message with
                {
                    Direction = fromQueue ? PacketDirection.ClientToServer : PacketDirection.ServerToClient,
                },
                "ZONE_INITIALIZATION" when fromQueue => message with
                {
                    Fields = message.Fields.Where(field => field.Name != "is_duty_instance").ToArray(),
                },
                _ => message,
            }).ToArray(),
        };
        return original with
        {
            Region = Region.Cn,
            Profile = profile,
            Binding = original.Binding with { MatchFromQueue = profile.MatchFromQueue },
        };
    }

    private static DecodedMessage KnownDutyZoneMessage(long monoMs)
    {
        var zone = ZoneMessage(monoMs);
        var payload = zone.Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 1039);
        return zone with { Payload = payload };
    }

    /// <summary>
    /// One pop straight after another publishes exactly one <c>run_finished</c> for the run
    /// that was displaced and exactly one <c>run_created</c> for the one that replaced it.
    ///
    /// The observable state machine cannot show this case: RestartOn writes the terminal state
    /// and clears it inside a single step (review finding L-14).
    /// </summary>
    [Fact]
    public async Task APopThatDisplacesARunPublishesOneRunFinishedAndOneRunCreated()
    {
        using var fixture = new TestDatabase();
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = NewPipeline(fixture, bus);
        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(0));
        pipeline.Accept(ZoneMessage(5_000));
        pipeline.Accept(PopMessage(600_000));

        var events = await DrainAsync(subscription);
        var kinds = events.Select(payload => payload["kind"]!.GetValue<string>()).ToArray();

        Assert.Equal(1, kinds.Count(kind => kind == "run_finished"));
        Assert.Equal(2, kinds.Count(kind => kind == "run_created"));

        var runs = new RunRepository(fixture.Database).Query(null, null, 1, 50).Items;
        Assert.Equal(2, runs.Count);

        var finished = events.Single(payload => payload["kind"]!.GetValue<string>() == "run_finished");
        Assert.NotNull(finished["run"]);
        Assert.NotNull(finished["state"]);
    }

    /// <summary>
    /// A lost game connection inside a duty ends the run as DISCONNECTED, driven through the
    /// bridge the capture layer actually calls rather than by poking the processor.
    ///
    /// Calling <c>SemanticEventProcessor.OnConnectionLost</c> directly only proves the
    /// processor reacts; it leaves the terminal state without a producer on the live path
    /// (review finding H-6).
    /// </summary>
    [Fact]
    public void AConnectionLostReportedToTheBridgeEndsTheRunAsDisconnected()
    {
        using var fixture = new TestDatabase();
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = NewPipeline(fixture, bus);

        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));

        // Reported by capture session id, exactly as CaptureController reports it.
        fixture.Clock.UtcNow = Start.AddMilliseconds(70_000);
        pipeline.OnConnectionLost(SessionId);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Disconnected, run.Result);
        Assert.NotEqual(RunResult.LeftOrAbandoned, run.Result);
        Assert.Equal(RunState.Disconnected, pipeline.RunState);
    }

    /// <summary>A report naming another capture session changes nothing.</summary>
    [Fact]
    public void AConnectionLostFromAnotherSessionIsIgnored()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));

        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));

        pipeline.OnConnectionLost("40000000-0000-4000-8000-000000000009");

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Null(run.EndedAtUtc);
    }

    /// <summary>
    /// The read path collapses a terminal state to IDLE and must publish that collapse.
    /// Collapsing silently leaves a client that polls between the terminal state and the next
    /// declared message without <c>run_state_changed(IDLE)</c>: by the time a message arrives,
    /// the before-state the publication compares against is already IDLE (findings L-9, R-9).
    /// </summary>
    [Fact]
    public async Task ReadingAFinishedRunPublishesTheCollapseToIdleExactlyOnce()
    {
        using var fixture = new TestDatabase();
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = NewPipeline(fixture, bus);

        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));
        pipeline.OnConnectionLost(SessionId);
        Assert.Equal(RunState.Disconnected, pipeline.RunState);

        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        // Two polls, exactly as a Desktop that refreshes on a timer makes.
        Assert.Equal(RunState.Idle, pipeline.GetCurrentRun().State);
        Assert.Equal(RunState.Idle, pipeline.GetCurrentRun().State);

        var idle = (await DrainAsync(subscription))
            .Where(payload => payload["kind"]!.GetValue<string>() == "run_state_changed")
            .Where(payload => payload["state"]!.GetValue<string>() == "IDLE")
            .ToArray();

        Assert.Single(idle);
    }

    /// <summary>
    /// Audit 2026-10-03 ODp-2, CS3a-X1. A direction of the connection that carried the match, given up
    /// by the capture after a gap it could not fill, delivers nothing more, so the match in flight can
    /// no longer be followed to its end - the same loss as a queue overflow (docs/state-machine.md 3.3
    /// rule 3). It used to reach calibration only: the run sat in MENTOR_MATCHED until the game closed
    /// and was then written off as a confident cancellation nobody was asked to check.
    /// </summary>
    [Fact]
    public void ADamagedGameDirectionClosesAMatchedRunPendingReview()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.OnCaptureHealth(Health());
        pipeline.Accept(PopMessage(10_000));
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);

        pipeline.OnCaptureHealth(Health(damagedDirections: 1));
        pipeline.OnDirectionDamaged(SessionId, "synthetic-connection", MessageDirection.Inbound);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.CancelledBeforeEntry, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.True(run.PendingReview);
        Assert.Null(run.EnteredAtUtc);

        // The session-wide count is calibration's reading, not another loss: the next match stands.
        pipeline.Accept(PopMessage(400_000));
        pipeline.OnCaptureHealth(Health(damagedDirections: 1));
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Audit 2026-10-03, CS3a-X1. The client keeps several connections open, and only the one the
    /// profile's messages arrive on carries the run. A direction given up on another one - the chat
    /// server's, say - loses nothing the run is followed by: the match stands and the duty is
    /// entered. The session-wide count rose all the same, and it ends no run.
    /// </summary>
    [Fact]
    public void ADamagedDirectionOfAConnectionThatCarriedNoProfileMessageLeavesTheRunAlone()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.OnCaptureHealth(Health());
        pipeline.Accept(PopMessage(10_000));

        pipeline.OnCaptureHealth(Health(damagedDirections: 1));
        pipeline.OnDirectionDamaged(SessionId, "chat-connection", MessageDirection.Inbound);
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);

        pipeline.Accept(ZoneMessage(15_000));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.NotNull(run.EnteredAtUtc);
    }

    /// <summary>A marker for another capture session changes nothing in this one.</summary>
    [Fact]
    public void ADamagedDirectionOfAnotherSessionIsIgnored()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));

        pipeline.OnDirectionDamaged(Guid.NewGuid().ToString("D"), "synthetic-connection", MessageDirection.Inbound);

        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Inside a duty the damage ends the run when it is noticed, as INTERRUPTED at LOW, rather than
    /// hours later when the game closes with a duration stretched to whatever the other connections
    /// were still delivering.
    /// </summary>
    [Fact]
    public void ADamagedGameDirectionInterruptsADutyWhenItIsNoticed()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));
        pipeline.Accept(JobMessage(20_000));
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        pipeline.OnDirectionDamaged(SessionId, "synthetic-connection", MessageDirection.Inbound);
        pipeline.Accept(JobMessage(3_600_000));
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.ProcessExit);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Interrupted, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.Equal(5_000, run.DurationMs);
    }

    /// <summary>
    /// Audit 2026-10-03, V2-1. Damage is judged by direction, not only by connection. The match, the
    /// entry and the result all arrive on the zone connection's inbound direction; its outbound
    /// direction delivered nothing this profile parses. Giving that one up - Npcap missed a single
    /// client segment - loses nothing the duty is followed by, so the duty stands and the result that
    /// still arrives on the intact inbound direction completes it. It used to close the run as
    /// INTERRUPTED at LOW, and the result was then ignored.
    /// </summary>
    [Fact]
    public void AGivenUpOutboundDirectionOfTheZoneConnectionLeavesTheDutyToItsResult()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));
        pipeline.OnDirectionDamaged(SessionId, "synthetic-connection", MessageDirection.Outbound);
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);

        pipeline.Accept(ZoneMessage(15_000));
        pipeline.OnDirectionDamaged(SessionId, "synthetic-connection", MessageDirection.Outbound);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        pipeline.Accept(Message(61443, 75_000, new byte[] { 1, 0, 0, 0 }));

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.False(run.PendingReview);
        Assert.Equal(60_000, run.DurationMs);
    }

    /// <summary>
    /// On a profile that stands the player's request in for the match, the request travels outbound:
    /// that direction did carry a message the profile parses, so giving it up is a gap and the
    /// parked request is let go, exactly as a queue overflow lets it go.
    /// </summary>
    [Fact]
    public void AGivenUpOutboundDirectionThatCarriedTheQueueRequestStillCountsAsAGap()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock),
            _ => MatchSourceSelection(fromQueue: true));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000) with { Direction = MessageDirection.Outbound });

        pipeline.OnDirectionDamaged(SessionId, "synthetic-connection", MessageDirection.Outbound);
        pipeline.Accept(KnownDutyZoneMessage(15_000));

        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Empty(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
    }

    /// <summary>
    /// The adapter's drop counter covers every program's traffic on the address. A drop that hit a
    /// stream the game decodes leaves a gap that stream cannot fill and is reported as a damaged
    /// direction; on its own the counter says nothing about the run.
    /// </summary>
    [Fact]
    public void AnAdapterDropOnItsOwnLeavesTheRunAlone()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));

        pipeline.OnCaptureHealth(Health() with { AdapterDropped = 40 });

        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// A capture stopped by an error stopped watching while the player could still enter the duty:
    /// a run that had not entered yet is cancelled at LOW and waits for the player, like a lost
    /// connection. A stop the player asked for stays the confident cancellation it always was.
    /// </summary>
    [Theory]
    [InlineData(CaptureEndReason.Error, true)]
    [InlineData(CaptureEndReason.UserStop, false)]
    public void ACaptureStoppedByAnErrorWhileMatchedEndsPendingReview(CaptureEndReason reason, bool pending)
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        pipeline.OnCaptureStarted(SessionId);
        pipeline.Accept(PopMessage(10_000));

        pipeline.OnCaptureStopped(SessionId, reason);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.CancelledBeforeEntry, run.Result);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.Equal(pending, run.PendingReview);
    }

    // ------------------------------------------------ the CN clear signal on the live path ----

    /// <summary>
    /// The observed CN timeline through the live bridge: login job, pop, territory, entry, the clear
    /// on an opcode the profile never declares, the exit 13.7 s later. The clear completes the run
    /// at once, with no confirmation, and the desktop is told so (duty-result brief, decision 1).
    /// </summary>
    [Fact]
    public async Task ALiveClearCompletesTheRunAndPublishesItAsCompleted()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var bus = new LiveEventBus(fixture.Clock);
        var pipeline = new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus, _ => CnSelection());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        using var live = bus.Subscribe(Guid.NewGuid().ToString("D"));
        pipeline.OnCaptureStarted(SessionId);

        FeedCnDuty(pipeline, clear: true);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.False(run.PendingReview);
        Assert.Equal(DetectionConfidence.High, run.DetectionConfidence);
        Assert.Equal(Start.AddMilliseconds(2_046_833), run.EndedAtUtc);
        Assert.Equal(2_046_833 - 489_047, run.DurationMs);

        var finished = Assert.Single(await DrainAsync(live), payload =>
            payload["kind"]!.GetValue<string>() == "run_finished");
        Assert.Equal("COMPLETED", finished["state"]!.GetValue<string>());

        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
        Assert.Collection(
            notes,
            signal =>
            {
                Assert.Equal(DutyClearNoteKind.Signal, signal.Kind);
                Assert.Equal(run.RunId, signal.RunId);
                Assert.Equal(RunState.EnteredDuty, signal.StateBefore);
                Assert.True(signal.Completed);
            },
            exit =>
            {
                Assert.Equal(DutyClearNoteKind.Exit, exit.Kind);
                Assert.Equal(run.RunId, exit.RunId);
                Assert.Equal(2_060_556 - 2_046_833, exit.ClearToExitMs);
            });
    }

    /// <summary>Duty-result brief, decision 2: an exit without the clear proves nothing.</summary>
    [Fact]
    public void ALiveExitWithoutTheClearIsStillUnknownPendingReview()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => CnSelection());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.OnCaptureStarted(SessionId);

        FeedCnDuty(pipeline, clear: false);

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Unknown, run.Result);
        Assert.True(run.PendingReview);
        Assert.Equal(DetectionConfidence.Low, run.DetectionConfidence);
        Assert.Equal(0, pipeline.DutyClearSignalCount);
        Assert.Equal(0, pipeline.DutyClearCompletionCount);
        Assert.Empty(notes);
    }

    /// <summary>
    /// The clear of a duty no mentor run is in - another roulette, or an entry that was never
    /// recorded - is counted and completes nothing, which is what lets a report tell the two apart.
    /// </summary>
    [Fact]
    public void TheClearOfAnotherDutyIsCountedButCompletesNothing()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => CnSelection());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.OnCaptureStarted(SessionId);

        pipeline.Accept(CnMessage(0xA002, 489_047, new byte[8]));
        pipeline.Accept(ClearMessage(2_046_833));
        pipeline.Accept(CnMessage(0xA002, 2_060_556, new byte[8]));

        Assert.Empty(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(0, pipeline.DutyClearCompletionCount);
        var note = Assert.Single(notes);
        Assert.Equal(RunState.Idle, note.StateBefore);
        Assert.Null(note.RunId);
        Assert.False(note.Completed);
    }

    /// <summary>
    /// A loss between the clear and the exit - an overflow, the game connection closing, a direction
    /// of the zone connection given up - means the next zone change is no longer known to be the
    /// exit: it may be the re-login, or the next duty's entry, much later. No exit line is written
    /// for it. A direction that delivered nothing the profile parses loses nothing, and the exit that
    /// follows is still reported. The counts are the session's either way.
    /// </summary>
    [Theory]
    [InlineData("overflow", false)]
    [InlineData("connection", false)]
    [InlineData("zone-connection", false)]
    [InlineData("chat", true)]
    public void ALossBetweenTheClearAndTheExitDropsTheExitLine(string loss, bool exitReported)
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => CnSelection());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.OnCaptureStarted(SessionId);

        FeedCnDuty(pipeline, clear: true, betweenClearAndExit: () =>
        {
            switch (loss)
            {
                case "overflow":
                    pipeline.OnEventsDropped(SessionId, 3);
                    break;
                case "connection":
                    pipeline.OnConnectionLost(SessionId);
                    break;
                default:
                    pipeline.OnDirectionDamaged(SessionId, loss, MessageDirection.Inbound);
                    break;
            }
        });

        var run = Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
        Assert.Equal(RunResult.Completed, run.Result);
        Assert.Equal(exitReported, notes.Exists(note => note.Kind == DutyClearNoteKind.Exit));
        Assert.Equal(1, pipeline.DutyClearSignalCount);
        Assert.Equal(1, pipeline.DutyClearCompletionCount);
    }

    /// <summary>
    /// The capture stops between the clear and the exit: nothing more of that session reaches the
    /// parser, and the next session binds a parser of its own, so its first zone change is never
    /// reported as the exit from the cleared duty.
    /// </summary>
    [Fact]
    public void AClearWhoseCaptureStoppedBeforeTheExitReportsNoExit()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => CnSelection());
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.OnCaptureStarted(SessionId);

        FeedCnDuty(pipeline, clear: true,
            betweenClearAndExit: () => pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop));
        const string nextSession = "30000000-0000-4000-8000-000000000004";
        pipeline.OnCaptureStarted(nextSession);
        pipeline.Accept(CnMessage(0xA002, 3_600_000, new byte[8]) with { CaptureSessionId = nextSession });

        Assert.Equal(RunResult.Completed, Assert.Single(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items).Result);
        Assert.DoesNotContain(notes, note => note.Kind == DutyClearNoteKind.Exit);
    }

    /// <summary>
    /// The counters belong to the capture session: they read 0 before any session, still read the
    /// session's counts once it has stopped (the closing log line reads them then), and start again
    /// from 0 when the next session starts.
    /// </summary>
    [Fact]
    public void TheCountersResetWhenTheNextCaptureSessionStarts()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock), _ => CnSelection());
        Capture.IParserStats stats = pipeline;
        Assert.Equal(0, stats.DutyClearSignalCount);

        pipeline.OnCaptureStarted(SessionId);
        FeedCnDuty(pipeline, clear: true);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
        Assert.Equal(1, stats.DutyClearSignalCount);
        Assert.Equal(1, stats.DutyClearCompletionCount);

        pipeline.OnCaptureStarted("30000000-0000-4000-8000-000000000002");
        Assert.Equal(0, stats.DutyClearSignalCount);
        Assert.Equal(0, stats.DutyClearCompletionCount);
    }

    /// <summary>A next session that binds no parser - the client updated, nothing matches - reads 0 too.</summary>
    [Fact]
    public void TheCountersReadZeroWhenTheNextSessionBindsNothing()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var usable = true;
        var pipeline = new LiveProtocolPipeline(
            fixture.Database, fixture.Clock, new LiveEventBus(fixture.Clock),
            game => usable
                ? CnSelection()
                : new ProfileSelection(ProfileCompatibilityStatus.Unsupported, ProfileBinding.FailClosed, null,
                    Region.Cn, game.GameBuild, ProfileSelector.NoProfileMatchesReason));
        pipeline.OnCaptureStarted(SessionId);
        FeedCnDuty(pipeline, clear: true);
        pipeline.OnCaptureStopped(SessionId, CaptureEndReason.UserStop);
        Assert.Equal(1, pipeline.DutyClearSignalCount);

        usable = false;
        pipeline.Refresh(Capture.GameProcessDetection.NotRunning);
        pipeline.OnCaptureStarted("30000000-0000-4000-8000-000000000003");

        Assert.Equal(0, pipeline.DutyClearSignalCount);
        Assert.Equal(0, pipeline.DutyClearCompletionCount);
    }

    /// <summary>A profile that declares DUTY_RESULT is not watched: nothing is counted, nothing reported.</summary>
    [Fact]
    public void AProfileDeclaringDutyResultHasNoClearCounters()
    {
        using var fixture = new TestDatabase();
        var pipeline = NewPipeline(fixture, new LiveEventBus(fixture.Clock));
        var notes = new List<DutyClearNote>();
        pipeline.DutyClearObserved += notes.Add;
        pipeline.OnCaptureStarted(SessionId);

        pipeline.Accept(PopMessage(10_000));
        pipeline.Accept(ZoneMessage(15_000));
        pipeline.Accept(ClearMessage(60_000));

        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.Equal(0, pipeline.DutyClearSignalCount);
        Assert.Empty(notes);
    }

    /// <summary>
    /// A VERIFIED CN profile in memory, in the shape of the shipped one and selected the way the live
    /// catalogue selects it: a pop, an entry marker with no field, the territory and the job, and no
    /// DUTY_RESULT. The opcodes are invented.
    /// </summary>
    private static ProfileSelection CnSelection()
    {
        static ProfileField Field(string name, int offset, ProfileFieldType type, long? min = null, long? max = null) =>
            new(name, offset, type, 0, ProfileEndian.Little, new ProfileFieldConstraints(min, max, null));

        var profile = new ProtocolProfile(
            "cn-pipeline-test", Region.Cn, "2026.09.15.0000.0000", Start, MentorRoulette,
            ProfileCompatibilityStatus.Verified, TimeSpan.FromSeconds(45),
            new[]
            {
                new ProfileMessage("CONTENT_FINDER_POP", 0xA001, PacketDirection.ServerToClient, null, 8, null, null,
                    Array.Empty<long>(), new[] { Field("roulette_id", 0, ProfileFieldType.U16, min: 1) }),
                new ProfileMessage("ZONE_INITIALIZATION", 0xA002, PacketDirection.ServerToClient, null, 8, null, null,
                    Array.Empty<long>(), Array.Empty<ProfileField>()),
                new ProfileMessage("ZONE_TERRITORY", 0xA003, PacketDirection.ServerToClient, null, 136, null, null,
                    Array.Empty<long>(), new[] { Field("territory_id", 2, ProfileFieldType.U16, min: 1) }),
                new ProfileMessage("PLAYER_JOB", 0xA004, PacketDirection.ServerToClient, null, 16, null, null,
                    Array.Empty<long>(), new[] { Field("job_id", 0, ProfileFieldType.U8, min: 1, max: 43) }),
            },
            Array.Empty<ProfileFixtureReference>(), "in-memory CN test profile", new string('0', 64), "", false);
        return new ProfileSelection(
            ProfileCompatibilityStatus.Verified, profile.ToBinding(), profile, Region.Cn, profile.GameBuild,
            "in-memory CN test profile");
    }

    /// <summary>The observed timeline: job at login, pop, territory, entry, optionally the clear, the exit.</summary>
    /// <param name="pipeline">Pipeline to feed.</param>
    /// <param name="clear">Whether the clear arrives before the exit.</param>
    /// <param name="betweenClearAndExit">What else happens between the clear and the exit, if anything.</param>
    private static void FeedCnDuty(LiveProtocolPipeline pipeline, bool clear, Action? betweenClearAndExit = null)
    {
        var job = new byte[16];
        job[0] = 19;
        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, MentorRoulette);
        var territory = new byte[136];
        BinaryPrimitives.WriteUInt16LittleEndian(territory.AsSpan(2), 1036);

        pipeline.Accept(CnMessage(0xA004, 45_900, job));
        pipeline.Accept(CnMessage(0xA001, 470_000, pop));
        pipeline.Accept(CnMessage(0xA003, 488_997, territory));
        pipeline.Accept(CnMessage(0xA002, 489_047, new byte[8]));
        if (clear)
        {
            pipeline.Accept(ClearMessage(2_046_833));
        }

        betweenClearAndExit?.Invoke();
        pipeline.Accept(CnMessage(0xA002, 2_060_556, new byte[8]));
    }

    /// <summary>The clear of the invented duty 0xF00D, on an opcode no test profile declares.</summary>
    private static DecodedMessage ClearMessage(long monoMs)
    {
        var body = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0x006D);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 0xF00D);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0x8003);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0x40000003);
        return CnMessage(0x0204, monoMs, body);
    }

    private static DecodedMessage CnMessage(ushort opcode, long monoMs, byte[] payload) =>
        new(
            SessionId,
            MessageDirection.Inbound,
            Start.AddMilliseconds(monoMs),
            TimeSpan.FromMilliseconds(monoMs),
            monoMs,
            3,
            opcode,
            payload,
            "zone-connection");

    private static Protocol.Calibration.CaptureSessionHealth Health(long damagedDirections = 0) =>
        new(SessionId, Capture.CaptureSilentReason.None, 0, 0, DamagedGameDirections: damagedDirections);

    /// <summary>A live bridge over the checked-in synthetic profile, writing to a real database.</summary>
    /// <param name="fixture">Database under test.</param>
    /// <param name="bus">Event bus the bridge publishes to.</param>
    private static LiveProtocolPipeline NewPipeline(TestDatabase fixture, LiveEventBus bus)
    {
        EnsureSession(fixture);
        var selection = ProfileSelector.SelectExplicit(SyntheticProfilePath, allowSynthetic: true);
        Assert.True(selection.IsUsable);
        return new LiveProtocolPipeline(fixture.Database, fixture.Clock, bus, _ => selection);
    }

    /// <summary>Reads everything a subscription holds, stopping at the first quiet interval.</summary>
    /// <param name="subscription">Subscription to drain.</param>
    private static async Task<IReadOnlyList<System.Text.Json.Nodes.JsonObject>> DrainAsync(
        LiveEventSubscription subscription)
    {
        var events = new List<System.Text.Json.Nodes.JsonObject>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None)
            is { } payload)
        {
            events.Add(payload);
        }

        return events;
    }

    private static string SyntheticProfilePath => Path.Combine(
        AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");

    /// <summary>A decoded message for the synthetic profile, which is all these tests parse.</summary>
    /// <param name="opcode">Opcode declared by the synthetic profile.</param>
    /// <param name="monoMs">Monotonic reading of the observation.</param>
    /// <param name="payload">Payload after the IPC header.</param>
    private static DecodedMessage Message(ushort opcode, long monoMs, byte[] payload) =>
        new(
            SessionId,
            MessageDirection.Inbound,
            Start.AddMilliseconds(monoMs),
            TimeSpan.FromMilliseconds(monoMs),
            monoMs,
            SyntheticSegmentType,
            opcode,
            payload,
            "synthetic-connection");

    private static DecodedMessage PopMessage(long monoMs)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0), MentorRoulette);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 900_001);
        return Message(61441, monoMs, payload);
    }

    private static DecodedMessage ZoneMessage(long monoMs, uint territoryId = 800_001)
    {
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), territoryId);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 900_001);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), 1);
        payload[12] = 1;
        return Message(61442, monoMs, payload);
    }

    private static DecodedMessage JobMessage(long monoMs) =>
        Message(61444, monoMs, new byte[] { 19, 0, 0, 0 });

    /// <summary>
    /// Runs carry a foreign key to their capture session, so the session row has to exist
    /// before the processor writes anything. Live capture (Phase 2) opens it the same way.
    /// </summary>
    private static void EnsureSession(TestDatabase fixture)
    {
        var sessions = new CaptureSessionRepository(fixture.Database);
        if (sessions.Get(SessionId) is not null)
        {
            return;
        }

        fixture.Database.RunInTransaction(transaction => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = SessionId,
            StartedAtUtc = Start,
            CollectorVersion = Program.Version,
            Region = Region.Cn,
            ProtocolProfileId = "pipeline-test",
            ProfileStatus = ProfileStatus.Unverified,
        }, transaction));
    }

    /// <summary>A processor whose commits go through <paramref name="runInTransaction"/>.</summary>
    /// <param name="fixture">Database under test.</param>
    /// <param name="runId">Identifier the machine assigns to the run it opens.</param>
    /// <param name="runInTransaction">Transaction runner standing in for the database.</param>
    private static SemanticEventProcessor NewProcessor(
        TestDatabase fixture, string runId, Action<Action<SqliteTransaction>> runInTransaction) =>
        new(
            fixture.Database,
            new MentorRunStateMachine(
                ProfileBinding.Synthetic("pipeline-test", MentorRoulette),
                StateMachineOptions.Default,
                () => runId),
            new SemanticEventProcessorOptions(SessionId, Region.Cn, "pipeline-test", "retry"),
            fixture.Clock,
            JobCatalog.Default,
            DutyCatalog.Default,
            runInTransaction: runInTransaction);

    private static SemanticEventProcessor NewProcessor(
        TestDatabase fixture, out MentorRunStateMachine machine, bool fromQueue = false)
    {
        EnsureSession(fixture);
        var ordinal = 0;
        machine = new MentorRunStateMachine(
            ProfileBinding.Synthetic("pipeline-test", MentorRoulette) with { MatchFromQueue = fromQueue },
            fromQueue ? StateMachineOptions.Default with
            {
                MatchWindow = TimeSpan.FromHours(1),
                IsKnownDuty = territory => territory == 800_001,
            } : StateMachineOptions.Default,
            () => SemanticEventProcessor.DeterministicId("pipeline:run:" + ordinal++));
        return new SemanticEventProcessor(
            fixture.Database,
            machine,
            new SemanticEventProcessorOptions(SessionId, Region.Cn, "pipeline-test", "pipeline"),
            fixture.Clock,
            JobCatalog.Default,
            DutyCatalog.Default,
            semanticEvent =>
            {
                fixture.Clock.UtcNow = semanticEvent.ObservedAtUtc;
                fixture.Clock.Elapsed = semanticEvent.Mono;
            });
    }

    private static void Feed(
        TestDatabase fixture, SemanticEventProcessor processor, params SemanticEvent[] events)
    {
        fixture.Database.RunInTransaction(transaction =>
        {
            foreach (var semanticEvent in events)
            {
                processor.Process(semanticEvent, transaction);
            }

            processor.AppendPendingRevisions(transaction);
        });
    }

    private static IReadOnlyList<MentorRun> Runs(TestDatabase fixture, SemanticEventProcessor processor)
    {
        var repository = new RunRepository(fixture.Database);
        return processor.TouchedRunIds
            .Select(id => repository.Get(id)!)
            .OrderBy(run => run.CreatedAtUtc)
            .ToArray();
    }

    private static EventKey Key(string kind, long monoMs) =>
        new(SessionId, PacketDirection.ServerToClient, kind, monoMs, null, kind + ":" + monoMs);

    private static ContentFinderPop Pop(long monoMs) => new()
    {
        Key = Key("CONTENT_FINDER_POP", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        RouletteId = MentorRoulette,
        ContentId = 900_001,
    };

    private static ZoneInitialization Zone(long monoMs) => new()
    {
        Key = Key("ZONE_INITIALIZATION", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        ContentId = 900_001,
        TerritoryId = 800_001,
    };

    /// <summary>A pop that carries no content id, the way the CN one does.</summary>
    /// <param name="monoMs">Monotonic reading of the observation.</param>
    private static ContentFinderPop BarePop(long monoMs) => new()
    {
        Key = Key("CONTENT_FINDER_POP", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        RouletteId = MentorRoulette,
    };

    /// <summary>An entry marker with no readable field, the way the CN one is.</summary>
    /// <param name="monoMs">Monotonic reading of the observation.</param>
    private static ZoneInitialization BareZone(long monoMs) => new()
    {
        Key = Key("ZONE_INITIALIZATION", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
    };

    private static TerritoryObserved Territory(long monoMs, int territoryId) => new()
    {
        Key = Key("ZONE_TERRITORY", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        TerritoryId = territoryId,
    };

    private static PlayerJob Job(long monoMs) => new()
    {
        Key = Key("PLAYER_JOB", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        JobId = 19,
    };

    private static DutyResult Result(long monoMs, bool victory) => new()
    {
        Key = Key("DUTY_RESULT", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        Victory = victory,
    };
}
