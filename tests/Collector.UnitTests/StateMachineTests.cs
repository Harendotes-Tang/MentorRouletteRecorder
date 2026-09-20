using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Events;
using Xunit;

namespace MentorRecorder.Collector.UnitTests;

public sealed class StateMachineTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 4, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Handle_FailsClosed_WhenProfileIsNotVerified()
    {
        var machine = new MentorRunStateMachine(ProfileBinding.FailClosed);

        var result = machine.Handle(Pop(0, 42));

        Assert.Equal(RunState.Idle, machine.State);
        Assert.False(result.Accepted);
        Assert.IsType<RecordParserErrorCommand>(Assert.Single(result.Commands));
    }

    [Fact]
    public void Victory_CompletesUsingMonotonicDuration()
    {
        var machine = Machine();

        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0, 42, 900001)).ToState);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(Zone(5, 900001)).ToState);
        machine.Handle(Job(6, 19));
        var result = machine.Handle(Result(125, victory: true));

        Assert.Equal(RunState.Completed, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(120_000, finish.DurationMs);
        Assert.Equal(RunResult.Completed, finish.Result);
        Assert.Equal(DetectionConfidence.High, finish.Confidence);
    }

    /// <summary>
    /// The CN client names the duty by its territory and never sends a content id. A run whose
    /// match, entry, end, job and duty were all read off the wire is as certain as a run gets;
    /// holding it at MEDIUM for a field this client does not have made HIGH unreachable.
    /// </summary>
    [Fact]
    public void ADutyKnownByItsTerritoryAloneStillCompletesWithHighConfidence()
    {
        var machine = Machine();

        machine.Handle(Pop(0, 42));
        machine.Handle(Zone(5, contentId: null) with { TerritoryId = null });
        machine.Handle(Territory(5_050, 1036));
        machine.Handle(Job(6, 19));
        var result = machine.Handle(Result(125, victory: true));

        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(DetectionConfidence.High, finish.Confidence);
    }

    [Fact]
    public void ARunWhoseDutyWasNeverNamedStaysAtMediumConfidence()
    {
        var machine = Machine();

        machine.Handle(Pop(0, 42));
        machine.Handle(Zone(5, contentId: null) with { TerritoryId = null });
        machine.Handle(Job(6, 19));
        var result = machine.Handle(Result(125, victory: true));

        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(DetectionConfidence.Medium, finish.Confidence);
    }

    [Fact]
    public void NonVictory_NeverCompletes()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42, 900001));
        machine.Handle(Zone(5, 900001));

        var result = machine.Handle(Result(10, victory: false));

        Assert.Equal(RunState.LeftOrAbandoned, result.ToState);
        Assert.DoesNotContain(result.Commands.OfType<FinishRunCommand>(), c => c.Result == RunResult.Completed);
    }

    [Fact]
    public void EnterDuty_IgnoresMismatchedContent()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42, 900001));

        var result = machine.Handle(Zone(5, 900002));

        Assert.Equal(RunState.MentorMatched, machine.State);
        Assert.False(result.Accepted);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void EnterDuty_RequiresMatchWindow_WhenNoCrossCheckFieldExists()
    {
        var machine = Machine(new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(45) });
        machine.Handle(Pop(0, 42, contentId: null));

        var result = machine.Handle(Zone(46, contentId: null));

        Assert.Equal(RunState.MentorMatched, machine.State);
        Assert.False(result.Accepted);
    }

    [Theory]
    [InlineData("connection", RunState.Disconnected, RunResult.Disconnected)]
    [InlineData("capture", RunState.Interrupted, RunResult.Interrupted)]
    [InlineData("leave", RunState.LeftOrAbandoned, RunResult.LeftOrAbandoned)]
    public void EnteredDuty_FinalOutcomesRemainDistinct(string kind, RunState state, RunResult runResult)
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42, 900001));
        machine.Handle(Zone(5, 900001));
        SemanticEvent ev = kind switch
        {
            "connection" => Connection(10),
            "capture" => Capture(10),
            _ => Leave(10),
        };

        var transition = machine.Handle(ev);

        Assert.Equal(state, transition.ToState);
        Assert.Equal(runResult, Assert.IsType<FinishRunCommand>(transition.Commands[0]).Result);
    }

    [Fact]
    public void Handle_DeduplicatesObservationAndBoundsMemory()
    {
        var machine = Machine(new StateMachineOptions { DedupCapacity = 2 });
        var first = Pop(0, 7);
        machine.Handle(first);
        var duplicate = machine.Handle(first);
        machine.Handle(Pop(1, 8));
        machine.Handle(Pop(2, 9));

        Assert.True(duplicate.Duplicate);
        Assert.Equal(2, machine.DedupSetCount);
    }

    private static MentorRunStateMachine Machine(StateMachineOptions? options = null) =>
        new(ProfileBinding.Synthetic("synthetic/v1", 42), options, () => "00000000-0000-4000-8000-000000000001");

    /// <summary>A live VERIFIED profile that observes pops and zone changes but no duty result.</summary>
    private static MentorRunStateMachine MachineWithoutDutyResult(StateMachineOptions? options = null) =>
        new(ProfileBinding.Live("cn-test", Region.Cn, ProfileStatus.Verified, 42, canDetectDutyResult: false),
            options, () => "00000000-0000-4000-8000-000000000002");

    private static ZoneInitialization ZoneUnknown(int seconds) => new()
    {
        Key = Key("zone-unknown-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), IsDutyInstance = null,
    };

    [Fact]
    public void UnknownDutyFlag_EntersWithinWindow_ThenClosesUnknownPendingReview()
    {
        var machine = MachineWithoutDutyResult();

        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0, 42)).ToState);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(20)).ToState);
        var result = machine.Handle(ZoneUnknown(1500));

        Assert.Equal(RunState.UnknownFinalState, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.Unknown, finish.Result);
        Assert.Equal(DetectionConfidence.Low, finish.Confidence);
        Assert.True(finish.PendingReview);
        Assert.Equal(1_480_000, finish.DurationMs);
        Assert.Equal(RunState.UnknownFinalState, machine.State);
        // The terminal state is left behind by the next event: a new pop opens a new run.
        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(2000, 42)).ToState);
    }

    [Fact]
    public void UnknownDutyFlag_AfterTheMatchWindow_IsALapsedMatch()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));

        var result = machine.Handle(ZoneUnknown(200));

        Assert.Equal(RunState.CancelledBeforeEntry, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.CancelledBeforeEntry, finish.Result);
        Assert.False(finish.PendingReview);
        Assert.Null(finish.DurationMs);
    }

    [Fact]
    public void UnknownDutyFlag_WithADutyResultCapableProfile_LeavesAsAbandoned()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42));
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(5)).ToState);

        var result = machine.Handle(ZoneUnknown(100));

        Assert.Equal(RunState.LeftOrAbandoned, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.LeftOrAbandoned, finish.Result);
        Assert.False(finish.PendingReview);
    }

    [Fact]
    public void UnknownDutyFlag_VictoryStillCompletesBeforeTheExit()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(5));

        Assert.Equal(RunState.Completed, machine.Handle(Result(600, victory: true)).ToState);
        Assert.False(machine.Handle(ZoneUnknown(650)).Accepted);
    }

    [Fact]
    public void RepeatedPopWithinTheWindow_IsOneRun()
    {
        var machine = MachineWithoutDutyResult(new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) });

        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0, 42)).ToState);
        Assert.True(machine.Handle(Pop(97, 42)).Accepted);
        var entered = machine.Handle(ZoneUnknown(102));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal("00000000-0000-4000-8000-000000000002", entered.RunId);
    }

    /// <summary>
    /// A declined match pops again 97 seconds later and the player enters 33 seconds after
    /// that. Measured from the first pop the entry falls outside the window and the duty is
    /// lost; measured from the pop that formed the party it is a normal entry on the same run.
    /// </summary>
    [Fact]
    public void RepeatedPopWithinTheWindow_RefreshesTheMatchAnchor_AndStillEntersTheDuty()
    {
        var machine = MachineWithoutDutyResult(new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) });
        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0, 42)).ToState);

        var repeat = machine.Handle(Pop(97, 42));

        // The re-pop is not a new run and not a silent no-op: it refreshes the anchor and
        // leaves a trail entry, so the audit shows why the entry was accepted so late.
        Assert.True(repeat.Accepted);
        Assert.False(repeat.Transitioned);
        Assert.Equal("00000000-0000-4000-8000-000000000002", repeat.RunId);
        var appended = Assert.IsType<AppendEventCommand>(Assert.Single(repeat.Commands));
        Assert.Equal(RunState.MentorMatched, appended.FromState);
        Assert.Equal(RunState.MentorMatched, appended.ToState);
        Assert.Equal(TimeSpan.FromSeconds(97), machine.MatchedMono);

        var entered = machine.Handle(ZoneUnknown(130));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal("00000000-0000-4000-8000-000000000002", entered.RunId);
    }

    [Fact]
    public void RepeatedPopWithinTheWindow_MeasuresTheWindowFromTheNewestPop()
    {
        var machine = MachineWithoutDutyResult(new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) });
        machine.Handle(Pop(0, 42));
        machine.Handle(Pop(97, 42));

        // One second past the window of the second pop: the match lapsed, the player went
        // somewhere else, and nothing may be inferred about a duty.
        var result = machine.Handle(ZoneUnknown(97 + 121));

        Assert.Equal(RunState.CancelledBeforeEntry, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.CancelledBeforeEntry, finish.Result);
        Assert.Null(finish.DurationMs);
    }

    [Fact]
    public void UnknownDutyFlag_NextPopWithoutDutyResult_ClosesUnknownPendingReview()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(5));

        var result = machine.Handle(Pop(900, 42));

        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.Unknown, finish.Result);
        Assert.True(finish.PendingReview);
        Assert.Equal(RunState.MentorMatched, machine.State);
    }

    /// <summary>
    /// The job is announced at login, which for most runs is the only time it is ever seen.
    /// A machine that only listened while a run existed would store every mentor run with an
    /// unknown job (docs/state-machine.md section 3.11).
    /// </summary>
    [Fact]
    public void JobObservedWhileIdle_IsStampedOnTheNextRun()
    {
        var machine = MachineWithoutDutyResult();

        var ignored = machine.Handle(Job(0, 19));
        var started = machine.Handle(Pop(600, 42));

        // Nothing happens when it is observed -- there is no run to write it to yet.
        Assert.Equal(RunState.Idle, ignored.ToState);
        Assert.Empty(ignored.Commands);

        Assert.IsType<CreateRunCommand>(started.Commands[0]);
        var job = Assert.IsType<SetJobCommand>(started.Commands[1]);
        Assert.Equal(19, job.JobId);
        Assert.Equal(started.RunId, job.RunId);
    }

    [Fact]
    public void NoJobEverObserved_LeavesTheRunWithoutAJobCommand()
    {
        var machine = MachineWithoutDutyResult();

        var started = machine.Handle(Pop(0, 42));

        Assert.Empty(started.Commands.OfType<SetJobCommand>());
    }

    /// <summary>
    /// The CN territory announcement arrives about 50 ms before the entry marker, and the
    /// entry marker itself names nothing. Without borrowing the announcement the run would be
    /// stored with no duty at all.
    /// </summary>
    [Fact]
    public void TerritoryAnnouncedJustBeforeEntry_IdentifiesTheDuty()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Handle(Territory(19_950, 1036));

        var entered = machine.Handle(ZoneUnknown(20));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal(1036, Assert.Single(entered.Commands.OfType<SetDutyCommand>()).TerritoryId);
    }

    [Fact]
    public void TerritoryAnnouncedAfterEntry_StillIdentifiesTheDuty()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(20));

        var late = machine.Handle(Territory(25_000, 1036));

        Assert.False(late.Transitioned);
        Assert.Equal(RunState.EnteredDuty, late.ToState);
        Assert.Equal(1036, Assert.Single(late.Commands.OfType<SetDutyCommand>()).TerritoryId);

        // The run now knows where it is, so the next announcement is the zone the player is
        // leaving for and must not overwrite the duty.
        Assert.Empty(machine.Handle(Territory(30_000, 129)).Commands.OfType<SetDutyCommand>());
    }

    [Fact]
    public void TerritoryOlderThanTheMemoryWindow_IsNotBorrowed()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Handle(Territory(1_000, 1036));

        // 19 seconds elapsed: far outside the 10-second window, so this announcement is about
        // some earlier zone change and says nothing about the duty being entered now.
        var entered = machine.Handle(ZoneUnknown(20));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Empty(entered.Commands.OfType<SetDutyCommand>());
    }

    [Fact]
    public void AnEntryMarkerThatNamesItsOwnZone_NeverBorrowsTheAnnouncement()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42, 900001));
        machine.Handle(Territory(4_950, 1036));

        var entered = machine.Handle(Zone(5, 900001));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Empty(entered.Commands.OfType<SetDutyCommand>());
        Assert.Equal(800001, Assert.IsType<EnterDutyCommand>(entered.Commands[0]).TerritoryId);
    }

    /// <summary>
    /// A verified pop for another roulette, inside the window, is the direct evidence that the
    /// mentor match is over. Ignoring it leaves the run open, so a level-roulette entry seconds
    /// later is recorded as a mentor entry and counted as an attempt (findings M-3 and R-7).
    ///
    /// It closes the run, but not confidently: "another roulette" rests entirely on the
    /// roulette id decoded at the profile's offset, and no mentor sample has ever been
    /// captured on the CN client to check that offset against. Until one exists the record is
    /// LOW and waits for the user, so a re-sent pop cannot silently rewrite a real mentor run
    /// as cancelled.
    /// </summary>
    [Fact]
    public void ForeignRoulettePopInsideTheWindow_CancelsTheMentorMatchPendingReview()
    {
        var machine = MachineWithoutDutyResult(new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) });
        machine.Handle(Pop(0, 42));

        var result = machine.Handle(Pop(20, 7));

        Assert.Equal(RunState.CancelledBeforeEntry, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.CancelledBeforeEntry, finish.Result);
        Assert.Equal(DetectionConfidence.Low, finish.Confidence);
        Assert.True(finish.PendingReview);

        // The zone change that follows belongs to the other roulette and starts nothing.
        Assert.False(machine.Handle(ZoneUnknown(25)).Accepted);
        Assert.Equal(RunState.Idle, machine.State);
    }

    /// <summary>
    /// Territory memory must not travel with the seed: its age is measured against the capture
    /// source's stopwatch, which every session restarts from zero, so a city observed three
    /// seconds into a faulted session compares as fresh in the session that replaces it and is
    /// recorded as the duty the player has just entered (review finding R-6).
    /// </summary>
    [Fact]
    public void SeededMemory_DoesNotCarryTheTerritoryAcrossSessions()
    {
        var first = MachineWithoutDutyResult();
        first.Handle(Job(0, 19));
        first.Handle(Territory(3_000, 129));
        Assert.NotNull(first.LastTerritory);

        var retried = MachineWithoutDutyResult();
        retried.Seed(first.Memory);

        Assert.Null(retried.LastTerritory);
        Assert.Equal(19, retried.LastKnownJobId);

        // A pop and an entry with no fresh announcement must not adopt the previous session's
        // zone, however recent its stale reading looks.
        retried.Handle(Pop(7, 42));
        var entered = retried.Handle(ZoneUnknown(8));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Null(Assert.IsType<EnterDutyCommand>(entered.Commands[0]).TerritoryId);
    }

    /// <summary>
    /// The CN profile binds PLAYER_JOB to a packet the client also sends on every experience
    /// gain, so an unchanged job arrives dozens of times per duty (review finding M-4).
    /// </summary>
    [Fact]
    public void RepeatedJobObservationWithTheSameJob_WritesNothing()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        var first = machine.Handle(Job(1, 19));

        var repeat = machine.Handle(Job(2, 19));

        Assert.True(first.Accepted);
        Assert.False(repeat.Accepted);
        Assert.False(repeat.Duplicate);
        Assert.Empty(repeat.Commands);

        // A real class change is still recorded.
        Assert.True(machine.Handle(Job(3, 24)).Accepted);
    }

    /// <summary>
    /// The territory announcement precedes the entry marker by about 50 ms; one that arrives
    /// just before the exit is the destination the player is leaving for, and adopting it
    /// writes the wrong duty onto the record (review finding L-6).
    /// </summary>
    [Fact]
    public void TerritoryLongAfterEntry_IsNotAdoptedAsTheDuty()
    {
        var machine = MachineWithoutDutyResult(
            new StateMachineOptions { TerritoryMemory = TimeSpan.FromSeconds(10) });
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(1));

        // The exit is at 60 s; this announcement is 50 ms before it and 59 s after the entry.
        var late = machine.Handle(Territory(59_950, 800042));

        Assert.False(late.Accepted);
        Assert.Empty(late.Commands.OfType<SetDutyCommand>());
    }

    /// <summary>Within the memory window the same announcement still names the duty entered.</summary>
    [Fact]
    public void TerritoryShortlyAfterEntry_IsStillAdoptedAsTheDuty()
    {
        var machine = MachineWithoutDutyResult(
            new StateMachineOptions { TerritoryMemory = TimeSpan.FromSeconds(10) });
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(1));

        var soon = machine.Handle(Territory(1_050, 800042));

        Assert.True(soon.Accepted);
        Assert.Equal(800042, Assert.IsType<SetDutyCommand>(soon.Commands[0]).TerritoryId);
    }

    /// <summary>
    /// A capture session that faulted and was retried builds a new machine; without the seed
    /// the next run is recorded job-less until the player changes class, because the job is
    /// only announced at login and on class changes (review finding L-8).
    /// </summary>
    [Fact]
    public void SeededMemory_StampsTheLastKnownJobOnTheNextRun()
    {
        var first = MachineWithoutDutyResult();
        first.Handle(Job(0, 19));
        Assert.Equal(19, first.LastKnownJobId);

        var retried = MachineWithoutDutyResult();
        retried.Seed(first.Memory);

        var started = retried.Handle(Pop(600, 42));

        Assert.Equal(19, Assert.IsType<SetJobCommand>(started.Commands[1]).JobId);
        Assert.Equal(19, retried.LastKnownJobId);
    }

    /// <summary>A machine already tracking a run refuses a seed rather than rewriting itself.</summary>
    [Fact]
    public void SeededMemory_IsRefusedWhileARunIsInFlight()
    {
        var source = MachineWithoutDutyResult();
        source.Handle(Job(0, 24));

        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Seed(source.Memory);

        Assert.Null(machine.LastKnownJobId);
    }

    /// <summary>
    /// A terminal state must not survive until the next profile-declared message: a player who
    /// finished a duty and idles in a city would read UNKNOWN_FINAL_STATE for as long as they
    /// stay there (review finding L-9).
    /// </summary>
    [Fact]
    public void NormalizeIfTerminal_LeavesAFinishedRunReadingIdle()
    {
        var machine = MachineWithoutDutyResult();
        machine.Handle(Pop(0, 42));
        machine.Handle(ZoneUnknown(5));
        machine.Handle(ZoneUnknown(100));
        Assert.Equal(RunState.UnknownFinalState, machine.State);

        machine.NormalizeIfTerminal();
        Assert.Equal(RunState.Idle, machine.State);
        Assert.Null(machine.CurrentRunId);

        // Idempotent, and it never disturbs a run that is still in flight.
        machine.NormalizeIfTerminal();
        Assert.Equal(RunState.Idle, machine.State);
        machine.Handle(Pop(200, 42));
        machine.NormalizeIfTerminal();
        Assert.Equal(RunState.MentorMatched, machine.State);
        Assert.NotNull(machine.CurrentRunId);
    }

    private static ContentFinderPop Pop(int seconds, int rouletteId, int? contentId = null) => new()
    {
        Key = Key("pop-" + seconds + "-" + rouletteId), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), RouletteId = rouletteId, ContentId = contentId,
    };

    private static ZoneInitialization Zone(int seconds, int? contentId) => new()
    {
        Key = Key("zone-" + seconds + "-" + contentId), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), ContentId = contentId, TerritoryId = 800001,
        IsDutyInstance = true,
    };

    private static TerritoryObserved Territory(int milliseconds, int territoryId) => new()
    {
        Key = Key("territory-" + milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), TerritoryId = territoryId,
    };

    private static PlayerJob Job(int seconds, int jobId) => new()
    {
        Key = Key("job-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), JobId = jobId,
    };

    private static DutyResult Result(int seconds, bool victory) => new()
    {
        Key = Key("result-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), Victory = victory,
    };

    private static ConnectionLost Connection(int seconds) => new()
    {
        Key = Key("connection-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    private static CaptureStopped Capture(int seconds) => new()
    {
        Key = Key("capture-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    private static ZoneLeft Leave(int seconds) => new()
    {
        Key = Key("leave-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    private static EventKey Key(string semantic) =>
        new("00000000-0000-4000-8000-000000000010", PacketDirection.None, "SYNTHETIC", 0, null, semantic);
}
