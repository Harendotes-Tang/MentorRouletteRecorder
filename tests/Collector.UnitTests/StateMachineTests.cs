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

    /// <summary>
    /// Audit 2026-10-03 OG-5. A profile that becomes unusable before the duty was entered ends a
    /// run that never entered: it is cancelled before entry, and nobody saw how, so it is low and
    /// pending review. UNKNOWN with no entry time is a row the correction rules refuse outright.
    /// </summary>
    [Fact]
    public void ProfileLostBeforeEntry_IsCancelledBeforeEntryPendingReview()
    {
        var machine = Machine();
        machine.Handle(Pop(0, 42));

        var result = machine.Handle(new ProfileLost
        {
            Key = Key("lost-10"), ObservedAtUtc = Start.AddSeconds(10), Mono = TimeSpan.FromSeconds(10),
        });

        Assert.Equal(RunState.CancelledBeforeEntry, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.CancelledBeforeEntry, finish.Result);
        Assert.Equal(DetectionConfidence.Low, finish.Confidence);
        Assert.True(finish.PendingReview);
        Assert.Null(finish.DurationMs);
        Assert.False(machine.IsUsable);
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

    /// <summary>
    /// A CN binding that sees the clear signal but declares no DUTY_RESULT: the parser may
    /// produce a victory, and an exit without one is still not an observed non-victory.
    /// </summary>
    private static MentorRunStateMachine MachineObservingTheClear(StateMachineOptions? options = null) =>
        new(ProfileBinding.Live("cn-test", Region.Cn, ProfileStatus.Verified, 42,
                canDetectDutyResult: false, observesDutyClear: true),
            options, () => "00000000-0000-4000-8000-000000000003");

    /// <summary>Pop, territory, entry marker that names nothing, and the job seen at login.</summary>
    private static MentorRunStateMachine EnteredWithTheClearObservable(bool withJob = true)
    {
        var machine = MachineObservingTheClear();
        if (withJob)
        {
            machine.Handle(Job(0, 19));
        }

        machine.Handle(Pop(470, 42));
        machine.Handle(Territory(488_950, 1036));
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(489)).ToState);
        return machine;
    }

    [Fact]
    public void TheClearInTheDutyCompletesWithHighConfidence()
    {
        Assert.True(MachineObservingTheClear().IsUsable);
        var machine = EnteredWithTheClearObservable();

        var result = machine.Handle(Clear(2_046));

        Assert.Equal(RunState.Completed, result.ToState);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.Completed, finish.Result);
        Assert.Equal(DetectionConfidence.High, finish.Confidence);
        Assert.False(finish.PendingReview);
        Assert.Equal((2_046 - 489) * 1000L, finish.DurationMs);
        var trail = Assert.IsType<AppendEventCommand>(result.Commands[1]);
        Assert.Equal(RunState.EnteredDuty, trail.FromState);
        Assert.Equal(RunState.Completed, trail.ToState);
    }

    [Fact]
    public void TheClearWithoutAJobCompletesAtMedium()
    {
        var machine = EnteredWithTheClearObservable(withJob: false);

        var result = machine.Handle(Clear(2_046));

        Assert.Equal(RunState.Completed, result.ToState);
        Assert.Equal(DetectionConfidence.Medium, Assert.IsType<FinishRunCommand>(result.Commands[0]).Confidence);
    }

    /// <summary>
    /// A clear before the entry was recorded creates nothing and rewrites nothing: a COMPLETED
    /// without an entry time is a row the statistics repair to UNKNOWN anyway.
    /// </summary>
    [Fact]
    public void TheClearBeforeEntryIsIgnored()
    {
        var machine = MachineObservingTheClear();

        var idle = machine.Handle(Clear(1));
        Assert.Equal(RunState.Idle, idle.ToState);
        Assert.False(idle.Accepted);
        Assert.Empty(idle.Commands);

        machine.Handle(Pop(10, 42));
        var matched = machine.Handle(Clear(12));
        Assert.Equal(RunState.MentorMatched, matched.ToState);
        Assert.False(matched.Accepted);
        Assert.Empty(matched.Commands);

        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(20)).ToState);
        var exit = machine.Handle(ZoneUnknown(1_500));
        Assert.Equal(RunState.UnknownFinalState, exit.ToState);
        var finish = Assert.IsType<FinishRunCommand>(exit.Commands[0]);
        Assert.Equal(RunResult.Unknown, finish.Result);
        Assert.True(finish.PendingReview);
    }

    [Fact]
    public void TheClearDoesNotForgetAParkedQueueRequest()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("cn-queue", Region.Cn, ProfileStatus.Verified, 42,
                canDetectDutyResult: false, matchFromQueue: true, observesDutyClear: true),
            new StateMachineOptions { MatchWindow = TimeSpan.FromHours(1), IsKnownDuty = id => id == 1036 },
            () => "00000000-0000-4000-8000-000000000004");

        machine.Handle(Pop(0, 42));
        var clear = machine.Handle(Clear(30));
        Assert.False(clear.Accepted);
        Assert.Empty(clear.Commands);
        Assert.True(machine.HasParkedQueue(TimeSpan.FromSeconds(30)));

        machine.Handle(Territory(59_950, 1036));
        var entered = machine.Handle(ZoneUnknown(60));

        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Single(entered.Commands.OfType<CreateRunCommand>());
    }

    /// <summary>
    /// The usual shape of a CN local or shared calibration: the match is inferred from the queue
    /// request. The clear completes the run; the exit fourteen seconds later meets a machine with no
    /// run and no parked request, and creates and closes nothing; the next request and its duty
    /// open the next run as before (review of the duty-clear feature, L5).
    /// </summary>
    [Fact]
    public void AQueueInferredRunTheClearCompletedLeavesTheExitInertAndTheNextRequestOpensARun()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("cn-queue", Region.Cn, ProfileStatus.Verified, 42,
                canDetectDutyResult: false, matchFromQueue: true, observesDutyClear: true),
            new StateMachineOptions { MatchWindow = TimeSpan.FromHours(1), IsKnownDuty = id => id == 1036 },
            () => "00000000-0000-4000-8000-000000000005");
        machine.Handle(Job(0, 19));
        machine.Handle(Pop(10, 42));
        machine.Handle(Territory(59_950, 1036));
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(60)).ToState);

        var cleared = machine.Handle(Clear(2_046));
        Assert.Equal(RunState.Completed, cleared.ToState);
        var finish = Assert.IsType<FinishRunCommand>(cleared.Commands[0]);
        Assert.Equal(RunResult.Completed, finish.Result);
        Assert.False(finish.PendingReview);

        var town = machine.Handle(Territory(2_059_950, 129));
        var exit = machine.Handle(ZoneUnknown(2_060));
        Assert.Empty(town.Commands);
        Assert.False(exit.Accepted);
        Assert.Empty(exit.Commands);
        Assert.Equal(RunState.Idle, machine.State);
        Assert.Null(machine.CurrentRunId);
        Assert.False(machine.HasParkedQueue(TimeSpan.FromSeconds(2_060)));

        Assert.Empty(machine.Handle(Pop(2_500, 42)).Commands);
        machine.Handle(Territory(2_559_950, 1036));
        var next = machine.Handle(ZoneUnknown(2_560));
        Assert.Equal(RunState.EnteredDuty, next.ToState);
        Assert.Single(next.Commands.OfType<CreateRunCommand>());
        Assert.Empty(next.Commands.OfType<FinishRunCommand>());
    }

    [Fact]
    public void TheClearAfterTheExitChangesNothing()
    {
        var machine = EnteredWithTheClearObservable();
        Assert.Equal(RunState.UnknownFinalState, machine.Handle(ZoneUnknown(2_060)).ToState);

        var late = machine.Handle(Clear(2_070));

        Assert.False(late.Accepted);
        Assert.Empty(late.Commands);
        Assert.Equal(RunState.Idle, machine.State);
    }

    [Fact]
    public void TheClearTwiceCompletesOnce()
    {
        var machine = EnteredWithTheClearObservable();
        Assert.Equal(RunState.Completed, machine.Handle(Clear(2_046)).ToState);

        var copy = machine.Handle(Clear(2_046));
        Assert.True(copy.Duplicate);
        Assert.Empty(copy.Commands);
        Assert.Equal(1, machine.DuplicateCount);

        var resent = machine.Handle(Clear(2_046, epoch: 2_047_000));
        Assert.False(resent.Duplicate);
        Assert.False(resent.Accepted);
        Assert.Empty(resent.Commands);
    }

    /// <summary>Duty-result brief, decision 2: absence proves nothing.</summary>
    [Fact]
    public void AnExitWithoutTheClearStaysUnknownPendingReview()
    {
        var machine = EnteredWithTheClearObservable();

        var exit = machine.Handle(ZoneUnknown(2_060));

        Assert.Equal(RunState.UnknownFinalState, exit.ToState);
        var finish = Assert.IsType<FinishRunCommand>(exit.Commands[0]);
        Assert.Equal(RunResult.Unknown, finish.Result);
        Assert.Equal(DetectionConfidence.Low, finish.Confidence);
        Assert.True(finish.PendingReview);
    }

    [Fact]
    public void APopInTheDutyBeforeTheClearStillRestartsAsUnknown()
    {
        var machine = EnteredWithTheClearObservable();

        var pop = machine.Handle(Pop(1_900, 42));
        var finish = Assert.IsType<FinishRunCommand>(pop.Commands[0]);
        Assert.Equal(RunResult.Unknown, finish.Result);
        Assert.True(finish.PendingReview);
        Assert.Equal(RunState.MentorMatched, machine.State);

        var clear = machine.Handle(Clear(2_046));
        Assert.False(clear.Accepted);
        Assert.Empty(clear.Commands);
        Assert.Equal(RunState.MentorMatched, machine.State);
    }

    [Fact]
    public void APopAfterTheClearOpensTheNextRun()
    {
        var machine = EnteredWithTheClearObservable();
        Assert.Equal(RunState.Completed, machine.Handle(Clear(2_046)).ToState);

        var next = machine.Handle(Pop(2_500, 42));

        Assert.Equal(RunState.MentorMatched, next.ToState);
        Assert.IsType<CreateRunCommand>(next.Commands[0]);
        Assert.Empty(next.Commands.OfType<FinishRunCommand>());
    }

    [Theory]
    [InlineData("gap", RunState.Interrupted)]
    [InlineData("connection", RunState.Disconnected)]
    [InlineData("capture", RunState.Interrupted)]
    public void ALossBeforeTheClearWinsAndALossAfterItChangesNothing(string kind, RunState lossState)
    {
        SemanticEvent Loss(int seconds) => kind switch
        {
            "gap" => new EventSequenceGap
            {
                Key = Key("gap-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
                Mono = TimeSpan.FromSeconds(seconds), DroppedCount = 3,
            },
            "connection" => Connection(seconds),
            _ => Capture(seconds),
        };

        var before = EnteredWithTheClearObservable();
        Assert.Equal(lossState, before.Handle(Loss(2_000)).ToState);
        var ignored = before.Handle(Clear(2_046));
        Assert.False(ignored.Accepted);
        Assert.Empty(ignored.Commands);

        var after = EnteredWithTheClearObservable();
        Assert.Equal(RunState.Completed, after.Handle(Clear(2_046)).ToState);
        var loss = after.Handle(Loss(2_050));
        Assert.Empty(loss.Commands.OfType<FinishRunCommand>());
        Assert.Equal(RunState.Idle, after.State);
    }

    /// <summary>
    /// The victory the parser makes of the clear signal: keyed by the message's own opcode,
    /// epoch and payload hash, and by the fixed semantic key.
    /// </summary>
    private static DutyResult Clear(int seconds, long epoch = 0) => new()
    {
        Key = new EventKey(
            "00000000-0000-4000-8000-000000000010", PacketDirection.ServerToClient, "61455",
            epoch == 0 ? seconds * 1000L : epoch,
            "1b7b8dd132a57e397bdaf964397fb17e4ea20f5a6a62f4bebc9c0f1ab0bf8566", "DUTY_RESULT:clear_signal"),
        ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
        Victory = true,
    };

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
