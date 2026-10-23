using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Pipeline;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The reporting decorator in front of the state machine of a profile that observes the CN
/// duty-start content: it says which signal decided how the player got into the duty, and it
/// reports a commencement that arrives after the decision, the one shape that would contradict
/// the assumption about late joiners. It decides nothing and passes everything on in order.
/// </summary>
public sealed class DutyStartTallyTests
{
    private const string SessionId = "30000000-0000-4000-8000-000000000041";
    private const string RunId = "00000000-0000-4000-8000-000000000051";
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstSignalInADutyIsReportedAsTheDecision()
    {
        var machine = Machine();
        var notes = new List<DutyStartNote>();
        var tally = new DutyStartTally(new MachineSink(machine), machine, notes.Add);

        tally.Accept(Remaining(10_000, 5400));
        tally.Accept(Pop(470_000));
        tally.Accept(Commenced(471_000, 7200));
        tally.Accept(Zone(489_047));
        tally.Accept(Remaining(489_500, 5400));
        tally.Accept(Remaining(789_500, 5100));

        var note = Assert.Single(notes);
        Assert.False(note.Commenced);
        Assert.Equal(RunId, note.RunId);
        Assert.Equal(5400, note.Seconds);
        Assert.True(note.Decided);
        Assert.True(note.JoinedInProgress);
        Assert.True(machine.JoinedInProgress);
    }

    [Fact]
    public void ACommencementAfterTheDecisionIsReportedAsNotDecided()
    {
        var machine = Machine();
        var notes = new List<DutyStartNote>();
        var tally = new DutyStartTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));
        tally.Accept(Remaining(489_500, 5400));

        tally.Accept(Commenced(560_000, 7200));

        Assert.Equal(2, notes.Count);
        var late = notes[^1];
        Assert.True(late.Commenced);
        Assert.Equal(RunId, late.RunId);
        Assert.Equal(7200, late.Seconds);
        Assert.False(late.Decided);
        Assert.True(late.JoinedInProgress);
    }

    [Fact]
    public void ACommencementFirstIsTheDecisionAndTheReportsAfterItAreSilent()
    {
        var machine = Machine();
        var notes = new List<DutyStartNote>();
        var tally = new DutyStartTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));

        tally.Accept(Commenced(563_554, 7200));
        tally.Accept(Remaining(563_617, 7199));
        tally.Accept(Remaining(863_617, 6899));

        var note = Assert.Single(notes);
        Assert.True(note.Commenced);
        Assert.True(note.Decided);
        Assert.False(note.JoinedInProgress);
        Assert.False(machine.JoinedInProgress);
    }

    /// <summary>The same observation delivered again is absorbed by the machine; it is not the contradiction shape.</summary>
    [Fact]
    public void ARedeliveredCommencementIsNotReportedAgain()
    {
        var machine = Machine();
        var notes = new List<DutyStartNote>();
        var tally = new DutyStartTally(new MachineSink(machine), machine, notes.Add);
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));
        var commenced = Commenced(563_554, 7200);

        tally.Accept(commenced);
        tally.Accept(commenced);

        Assert.Single(notes);
        Assert.Equal(1, machine.DuplicateCount);
    }

    [Fact]
    public void OtherEventsPassThroughInOrderUntouched()
    {
        var machine = Machine();
        var seen = new List<SemanticEvent>();
        var tally = new DutyStartTally(new RecordingSink(machine, seen), machine, _ => { });
        var events = new SemanticEvent[]
        {
            Pop(470_000), Zone(489_047), Commenced(563_554, 7200), Remaining(563_617, 7199), Zone(2_060_556),
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

    [Fact]
    public void AThrowingSubscriberDoesNotEscapeTheTally()
    {
        var machine = Machine();
        var tally = new DutyStartTally(new MachineSink(machine), machine,
            _ => throw new InvalidOperationException("the log is broken"));
        tally.Accept(Pop(470_000));
        tally.Accept(Zone(489_047));

        var thrown = Record.Exception(() => tally.Accept(Commenced(563_554, 7200)));

        Assert.Null(thrown);
        Assert.False(machine.JoinedInProgress);
    }

    [Fact]
    public void TheLogLineCarriesOnlyItsFields()
    {
        var note = new DutyStartNote(false, RunId, 5400, true, true);

        Assert.Equal("duty_start_signal", note.LogEvent);
        var fields = note.LogFields();
        Assert.Equal(
            new[] { "decided", "joined_in_progress", "kind", "run_id", "seconds" },
            fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("time_remaining", fields["kind"]);
        Assert.Equal(RunId, fields["run_id"]);
        Assert.Equal(5400, fields["seconds"]);
        Assert.Equal(true, fields["decided"]);
        Assert.Equal(true, fields["joined_in_progress"]);
        Assert.Equal("commenced", new DutyStartNote(true, RunId, 7200, false, null).LogFields()["kind"]);
    }

    private static MentorRunStateMachine Machine() =>
        new(ProfileBinding.Synthetic("synthetic/v1", 42), null, () => RunId);

    private sealed class MachineSink(MentorRunStateMachine machine) : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent) => machine.Handle(semanticEvent);
    }

    private sealed class RecordingSink(MentorRunStateMachine machine, List<SemanticEvent> seen) : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent)
        {
            seen.Add(semanticEvent);
            machine.Handle(semanticEvent);
        }
    }

    private static ContentFinderPop Pop(long milliseconds) => new()
    {
        Key = Key("pop", milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), RouletteId = 42, ContentId = 900001,
    };

    private static ZoneInitialization Zone(long milliseconds) => new()
    {
        Key = Key("zone", milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), ContentId = 900001, TerritoryId = 800001,
        IsDutyInstance = true,
    };

    private static DutyCommenced Commenced(long milliseconds, int limit) => new()
    {
        Key = Key(DutyStartSignal.CommencedSemanticKey, milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), TimeLimitSeconds = limit,
    };

    private static DutyTimeRemaining Remaining(long milliseconds, int left) => new()
    {
        Key = Key(DutyStartSignal.TimeRemainingSemanticKey, milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), RemainingSeconds = left,
    };

    private static EventKey Key(string semantic, long epoch) =>
        new(SessionId, PacketDirection.ServerToClient, "61455", epoch, null, semantic);
}
