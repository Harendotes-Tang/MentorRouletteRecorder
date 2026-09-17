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
    private const string SessionId = "30000000-0000-4000-8000-000000000001";
    private const int MentorRoulette = 42;

    /// <summary>Segment type the synthetic profile declares for every one of its messages.</summary>
    private const ushort SyntheticSegmentType = 61440;

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 4, 0, 0, TimeSpan.Zero);

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

    private static SemanticEventProcessor NewProcessor(TestDatabase fixture, out MentorRunStateMachine machine)
    {
        EnsureSession(fixture);
        var ordinal = 0;
        machine = new MentorRunStateMachine(
            ProfileBinding.Synthetic("pipeline-test", MentorRoulette),
            StateMachineOptions.Default,
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
