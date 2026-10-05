using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The counting decorator in front of the state machine of a profile that observes the CN clear
/// signal: how many clears were seen, how many of them completed a mentor run, and how long after
/// such a clear the duty was left. It only counts and reports; every event reaches the machine
/// unchanged and in order (docs/capture-diagnostics.md sections 5.2 and 8).
/// </summary>
public sealed class DutyClearTallyTests
{
    private const string SessionId = "30000000-0000-4000-8000-000000000021";
    private const string RunId = "00000000-0000-4000-8000-000000000031";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ItCountsSignalsAndOnlyTheOnesThatCompletedARun()
    {
        var machine = Machine();
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(new MachineSink(machine), machine, notes.Add);

        tally.Accept(Clear(10_000));
        tally.Accept(Pop(470_000));
        tally.Accept(Clear(471_000));
        tally.Accept(Zone(489_047));
        tally.Accept(Clear(2_046_833));
        // The same observation again (same key): one signal, not two.
        tally.Accept(Clear(2_046_833));
        // A result that is not the clear content passes through uncounted.
        tally.Accept(new DutyResult
        {
            Key = Key("DUTY_RESULT:outcome=7", 2_050_000),
            ObservedAtUtc = Start.AddMilliseconds(2_050_000),
            Mono = TimeSpan.FromMilliseconds(2_050_000),
            Victory = true,
        });

        Assert.Equal(3, tally.Signals);
        Assert.Equal(1, tally.Completions);
        Assert.Collection(
            notes,
            idle =>
            {
                Assert.Equal(DutyClearNoteKind.Signal, idle.Kind);
                Assert.Null(idle.RunId);
                Assert.Equal(RunState.Idle, idle.StateBefore);
                Assert.False(idle.Completed);
                Assert.Equal((1, 0), (idle.Signals, idle.Completions));
            },
            matched =>
            {
                Assert.Equal(RunId, matched.RunId);
                Assert.Equal(RunState.MentorMatched, matched.StateBefore);
                Assert.False(matched.Completed);
                Assert.Equal((2, 0), (matched.Signals, matched.Completions));
            },
            cleared =>
            {
                Assert.Equal(RunId, cleared.RunId);
                Assert.Equal(RunState.EnteredDuty, cleared.StateBefore);
                Assert.True(cleared.Completed);
                Assert.Null(cleared.ClearToExitMs);
                Assert.Equal((3, 1), (cleared.Signals, cleared.Completions));
            });
        Assert.Equal(1, machine.DuplicateCount);
    }

    /// <summary>
    /// A clear the machine meets in a finished run's terminal state is met as IDLE, which is how the
    /// machine itself handles it, and it is about no run.
    /// </summary>
    [Fact]
    public void AClearAfterACompletionIsMetInIdleAndIsAboutNoRun()
    {
        var machine = Machine();
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));
        tally.Accept(Clear(2_046_833));

        tally.Accept(Clear(2_046_900));

        var late = notes[^1];
        Assert.Equal(RunState.Idle, late.StateBefore);
        Assert.Null(late.RunId);
        Assert.False(late.Completed);
        Assert.Equal((2, 1), (tally.Signals, tally.Completions));
    }

    [Fact]
    public void ItReportsTheGapToTheNextZoneChangeOnce()
    {
        var machine = Machine();
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));
        tally.Accept(Clear(2_046_833));

        tally.Accept(Zone(2_060_556));
        tally.Accept(Zone(2_100_000));

        var exit = Assert.Single(notes, note => note.Kind == DutyClearNoteKind.Exit);
        Assert.Equal(RunId, exit.RunId);
        Assert.Equal(13_723, exit.ClearToExitMs);
        Assert.Equal((1, 1), (exit.Signals, exit.Completions));
        Assert.Equal(DutyClearNoteKind.Exit, notes[^1].Kind);
    }

    /// <summary>
    /// A loss the machine is told of between the clear and the next zone change - a gap, the game
    /// connection closing - means that zone change is no longer known to be the exit: it may be the
    /// re-login or the next duty's entry, much later. The pipeline tells the tally to forget the
    /// exit, and no clear-to-exit time is reported. The counts stay.
    /// </summary>
    [Fact]
    public void AForgottenExitIsNotReported()
    {
        var machine = Machine();
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));
        tally.Accept(Clear(2_046_833));

        tally.ForgetExit();
        tally.Accept(Zone(3_600_000));

        Assert.DoesNotContain(notes, note => note.Kind == DutyClearNoteKind.Exit);
        Assert.Equal((1, 1), (tally.Signals, tally.Completions));
    }

    /// <summary>
    /// The counts belong to the capture session, not to one parser: a rebind, a swap or a
    /// withdrawal in the session gives the next parser a new machine and a new tally, and that
    /// tally adds to the same counts. Its notes carry the session's totals.
    /// </summary>
    [Fact]
    public void TalliesOfOneSessionAddToItsCounts()
    {
        var counts = new DutyClearCounts();
        var notes = new List<DutyClearNote>();
        var first = Machine();
        var firstTally = new DutyClearTally(new MachineSink(first), first, notes.Add, counts);
        firstTally.Accept(Pop(470_000));
        firstTally.Accept(Zone(489_047));
        firstTally.Accept(Clear(2_046_833));

        var second = Machine();
        var secondTally = new DutyClearTally(new MachineSink(second), second, notes.Add, counts);
        secondTally.Accept(Clear(3_000_000));

        Assert.Equal((2, 1), (counts.Signals, counts.Completions));
        Assert.Equal((2, 1), (firstTally.Signals, firstTally.Completions));
        Assert.Equal((2, 1), (secondTally.Signals, secondTally.Completions));
        Assert.Equal((2, 1), (notes[^1].Signals, notes[^1].Completions));
        Assert.False(notes[^1].Completed);
    }

    /// <summary>A clear that completed nothing has no exit to report.</summary>
    [Fact]
    public void AClearThatCompletedNothingReportsNoExit()
    {
        var machine = Machine();
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(new MachineSink(machine), machine, notes.Add);

        tally.Accept(Clear(10_000));
        tally.Accept(Zone(20_000));

        Assert.DoesNotContain(notes, note => note.Kind == DutyClearNoteKind.Exit);
    }

    [Fact]
    public void OtherEventsPassThroughInOrderUntouched()
    {
        var machine = Machine();
        var seen = new List<SemanticEvent>();
        var tally = new DutyClearTally(new RecordingSink(machine, seen), machine, _ => { });
        var events = new SemanticEvent[]
        {
            Job(1_000), Pop(470_000), Zone(489_047), Clear(2_046_833), Zone(2_060_556), Pop(2_500_000),
        };

        foreach (var semanticEvent in events)
        {
            tally.Accept(semanticEvent);
        }

        Assert.Equal(events.Length, seen.Count);
        for (var index = 0; index < events.Length; index++)
        {
            Assert.Same(events[index], seen[index]);
        }
    }

    /// <summary>
    /// The store refused the clear: the processor puts the machine back, so the run is still in the
    /// duty and nothing was completed. The signal was seen all the same.
    /// </summary>
    [Fact]
    public void ARunTheStoreRefusedIsNotCountedAsCompleted()
    {
        using var fixture = new TestDatabase();
        EnsureSession(fixture);
        var machine = Machine();
        var refuseResults = false;
        var processor = new SemanticEventProcessor(
            fixture.Database,
            machine,
            new SemanticEventProcessorOptions(SessionId, Region.Cn, "cn-test", "tally"),
            fixture.Clock,
            runInTransaction: work =>
            {
                if (refuseResults)
                {
                    throw new InvalidOperationException("the store refused the write");
                }

                fixture.Database.RunInTransaction(work);
            });
        var notes = new List<DutyClearNote>();
        var tally = new DutyClearTally(processor, machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));

        refuseResults = true;
        tally.Accept(Clear(2_046_833));

        Assert.NotNull(processor.LastStorageError);
        Assert.Equal(RunState.EnteredDuty, machine.State);
        Assert.Equal(1, tally.Signals);
        Assert.Equal(0, tally.Completions);
        var note = Assert.Single(notes);
        Assert.False(note.Completed);
        Assert.Equal(RunState.EnteredDuty, note.StateBefore);
        Assert.Null(new RunRepository(fixture.Database).Get(RunId)!.EndedAtUtc);
    }

    /// <summary>A subscriber that throws never reaches the parser that is handing the event over.</summary>
    [Fact]
    public void AThrowingSubscriberDoesNotEscapeTheTally()
    {
        var machine = Machine();
        var tally = new DutyClearTally(new MachineSink(machine), machine,
            _ => throw new InvalidOperationException("the log is broken"));
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));

        var thrown = Record.Exception(() => tally.Accept(Clear(2_046_833)));

        Assert.Null(thrown);
        Assert.Equal(RunState.Completed, machine.State);
        Assert.Equal(1, tally.Completions);
    }

    // The log lines: only what the design lists (run id, state met, completed, the two counts,
    // the clear-to-exit gap). No payload, no hash, no opcode and no duty number.

    [Fact]
    public void TheSignalLogLineCarriesOnlyItsFields()
    {
        var note = new DutyClearNote(DutyClearNoteKind.Signal, RunId, RunState.EnteredDuty, true, null, 3, 1);

        Assert.Equal("duty_clear_signal", note.LogEvent);
        var fields = note.LogFields();
        Assert.Equal(
            new[] { "completed", "completions", "run_id", "signals", "state_before" },
            fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(RunId, fields["run_id"]);
        Assert.Equal("ENTERED_DUTY", fields["state_before"]);
        Assert.Equal(true, fields["completed"]);
        Assert.Equal(3L, fields["signals"]);
        Assert.Equal(1L, fields["completions"]);
    }

    [Fact]
    public void TheExitLogLineCarriesOnlyItsFields()
    {
        var note = new DutyClearNote(DutyClearNoteKind.Exit, RunId, RunState.Idle, true, 13_723, 1, 1);

        Assert.Equal("duty_clear_exit", note.LogEvent);
        var fields = note.LogFields();
        Assert.Equal(new[] { "clear_to_exit_ms", "run_id" }, fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(RunId, fields["run_id"]);
        Assert.Equal(13_723L, fields["clear_to_exit_ms"]);
    }

    private static MentorRunStateMachine Machine() =>
        new(ProfileBinding.Live("cn-test", Region.Cn, ProfileStatus.Verified, 42,
                canDetectDutyResult: false, observesDutyClear: true),
            null, () => RunId);

    private static EventKey Key(string semantic, long monoMs) =>
        new(SessionId, PacketDirection.ServerToClient, "61455", monoMs, null, semantic + ":" + monoMs);

    /// <summary>The victory the parser makes of the clear, keyed the way the parser keys it.</summary>
    private static DutyResult Clear(long monoMs) => new()
    {
        Key = new EventKey(
            SessionId, PacketDirection.ServerToClient, "61455", monoMs,
            "1b7b8dd132a57e397bdaf964397fb17e4ea20f5a6a62f4bebc9c0f1ab0bf8566", DutyClearSignal.SemanticKey),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        Victory = true,
    };

    private static ContentFinderPop Pop(long monoMs) => new()
    {
        Key = Key("CONTENT_FINDER_POP", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        RouletteId = 42,
    };

    private static ZoneInitialization Zone(long monoMs) => new()
    {
        Key = Key("ZONE_INITIALIZATION", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
    };

    private static PlayerJob Job(long monoMs) => new()
    {
        Key = Key("PLAYER_JOB", monoMs),
        ObservedAtUtc = Start.AddMilliseconds(monoMs),
        Mono = TimeSpan.FromMilliseconds(monoMs),
        JobId = 19,
    };

    private static void EnsureSession(TestDatabase fixture)
    {
        var sessions = new CaptureSessionRepository(fixture.Database);
        fixture.Database.RunInTransaction(transaction => sessions.Insert(new CaptureSession
        {
            CaptureSessionId = SessionId,
            StartedAtUtc = Start,
            CollectorVersion = Program.Version,
            Region = Region.Cn,
            ProtocolProfileId = "cn-test",
            ProfileStatus = ProfileStatus.Verified,
        }, transaction));
    }

    /// <summary>The state machine with no storage behind it.</summary>
    private sealed class MachineSink(MentorRunStateMachine machine) : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent) => machine.Handle(semanticEvent);
    }

    /// <summary>The state machine, recording what reached it.</summary>
    private sealed class RecordingSink(MentorRunStateMachine machine, List<SemanticEvent> seen) : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent)
        {
            seen.Add(semanticEvent);
            machine.Handle(semanticEvent);
        }
    }
}
