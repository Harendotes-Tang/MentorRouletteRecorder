using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What a queue-inferred profile does once it also knows when the popup appeared.
///
/// Such a profile creates the run only when the duty loads, because until then "the player
/// queued" is all it has. That is right for the record and wrong for the player, who wanted to be
/// told the moment the popup was on screen and got silence instead. An announcement recognised by
/// its timing carries no roulette, so it changes nothing about what is recorded: it only moves
/// MENTOR_MATCHED forward to the moment the server said so, and says that this one was observed
/// rather than inferred.
/// </summary>
public sealed class MatchAnnouncedStateMachineTests
{
    private const int Mentor = 42;
    private const int DutyTerritory = 800001;
    private static readonly DateTimeOffset Start = new(2026, 9, 19, 20, 0, 0, TimeSpan.Zero);

    /// <summary>A queue-inferred profile: the pop is the player's own request, entries need a known duty.</summary>
    private static MentorRunStateMachine Machine() => new(
        ProfileBinding.Live("cn-queue", Region.Cn, ProfileStatus.Verified, Mentor, matchFromQueue: true),
        new StateMachineOptions
        {
            MatchWindow = CalibrationDraftQueueWindow,
            IsKnownDuty = territory => territory == DutyTerritory,
        },
        () => "00000000-0000-4000-8000-000000000042");

    /// <summary>The window a queue-inferred profile declares: a queue, not an accept timer.</summary>
    private static readonly TimeSpan CalibrationDraftQueueWindow = TimeSpan.FromMinutes(60);

    private static EventKey Key(string semantic) =>
        new("00000000-0000-4000-8000-000000000010", PacketDirection.None, "SYNTHETIC", 0, null, semantic);

    private static ContentFinderPop Queue(int seconds, int roulette = Mentor) => new()
    {
        Key = Key("queue-" + seconds + "-" + roulette), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), RouletteId = roulette,
    };

    private static MatchAnnounced Announced(int seconds) => new()
    {
        Key = Key("announced-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    private static ZoneInitialization Duty(int seconds) => new()
    {
        Key = Key("duty-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), TerritoryId = DutyTerritory, IsDutyInstance = true,
    };

    private static ZoneInitialization Town(int seconds) => new()
    {
        Key = Key("town-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), TerritoryId = 100, IsDutyInstance = null,
    };

    /// <summary>
    /// The point of the whole feature: the popup is on the player's screen and the software can
    /// say so, with the roulette taken from the request they made ten minutes earlier.
    /// </summary>
    [Fact]
    public void AnAnnouncementWithAMentorQueueStandingOpensTheRun()
    {
        var machine = Machine();
        machine.Handle(Queue(0));

        var result = machine.Handle(Announced(600));

        Assert.Equal(RunState.MentorMatched, result.ToState);
        Assert.True(result.Transitioned);
        Assert.True(machine.MatchObserved);
        var created = Assert.IsType<CreateRunCommand>(result.Commands[0]);
        Assert.Equal(Mentor, created.MentorRouletteId);
        // The match happened when the server said so, not when the player queued.
        Assert.Equal(Start.AddSeconds(600), created.MatchedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(600), machine.MatchedMono);
    }

    /// <summary>
    /// The run still has to end in a duty, and the announcement only moved the starting line: a
    /// known duty inside the window that follows enters as it always did.
    /// </summary>
    [Fact]
    public void TheDutyThatFollowsStillEntersNormally()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));

        var result = machine.Handle(Duty(640));

        Assert.Equal(RunState.EnteredDuty, result.ToState);
        Assert.IsType<EnterDutyCommand>(result.Commands[0]);
    }

    /// <summary>
    /// Nobody queued, so nobody can say what this announcement is about: another roulette, or a
    /// party member's queue the player was pulled into. Silence is the answer, and in particular
    /// not a parser error - the message is perfectly well understood, it is just not ours.
    /// </summary>
    [Fact]
    public void AnAnnouncementWithNoQueueOfOnesOwnIsIgnoredWithoutComplaint()
    {
        var machine = Machine();

        var result = machine.Handle(Announced(600));

        Assert.Equal(RunState.Idle, machine.State);
        Assert.False(result.Accepted);
        Assert.Empty(result.Commands);
        Assert.False(machine.MatchObserved);
    }

    /// <summary>The same, for a queue the player made for some other roulette.</summary>
    [Fact]
    public void AnAnnouncementAfterANonMentorQueueIsIgnored()
    {
        var machine = Machine();
        machine.Handle(Queue(0, roulette: 7));

        var result = machine.Handle(Announced(600));

        Assert.Equal(RunState.Idle, machine.State);
        Assert.False(result.Accepted);
    }

    /// <summary>
    /// Somebody declined and the finder re-formed the party, or the client simply sent the
    /// announcement three times in a row as this one does. Either way it is one run, and the
    /// window has to move with it or the entry that follows falls outside it.
    /// </summary>
    [Fact]
    public void ARepeatedAnnouncementRefreshesTheMatchRatherThanStartingAgain()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        var first = machine.Handle(Announced(600));

        var again = machine.Handle(Announced(660));

        Assert.Equal(RunState.MentorMatched, again.ToState);
        Assert.False(again.Transitioned);
        Assert.Equal(first.RunId, again.RunId);
        Assert.Equal(TimeSpan.FromSeconds(660), machine.MatchedMono);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(Duty(700)).ToState);
    }

    /// <summary>
    /// A new queue request while the announced match stands means the player let it go: they
    /// declined, or the timer ran out, and they asked the finder for something again. The old run
    /// never entered a duty, so it closes as cancelled before entry - observed plainly enough not
    /// to need the user's confirmation.
    /// </summary>
    [Fact]
    public void AFreshQueueRequestClosesTheAnnouncedMatchAndBecomesTheNextOne()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));

        var result = machine.Handle(Queue(700));

        // A queue-inferred profile holds the new request rather than opening a run for it, so
        // the machine lands back in IDLE; the run that closed says so through its command.
        Assert.Equal(RunState.Idle, result.ToState);
        Assert.True(result.Transitioned);
        var finish = Assert.IsType<FinishRunCommand>(result.Commands[0]);
        Assert.Equal(RunResult.CancelledBeforeEntry, finish.Result);
        Assert.Equal(DetectionConfidence.Medium, finish.Confidence);
        Assert.False(finish.PendingReview);

        // And the new request is the next queue: the duty it matches into is recorded.
        Assert.Equal(RunState.EnteredDuty, machine.Handle(Duty(800)).ToState);
        Assert.False(machine.MatchObserved);
    }

    /// <summary>
    /// After the announcement only the player's confirmation and the loading screen are left, so
    /// the entry window is the template's couple of minutes rather than the hour a queue may
    /// stand. Beyond it a zone change the profile cannot classify is the match having lapsed.
    /// </summary>
    [Fact]
    public void AnAnnouncedMatchUsesTheShorterEntryWindow()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));

        var result = machine.Handle(Town(600 + (int)StateMachineOptions.Default.AnnouncedWindow.TotalSeconds + 30));

        Assert.Equal(RunState.CancelledBeforeEntry, result.ToState);
        Assert.Equal(DetectionConfidence.Low, Assert.IsType<FinishRunCommand>(result.Commands[0]).Confidence);
    }

    /// <summary>
    /// Inside that window the same zone change proves nothing: the player teleported somewhere
    /// while deciding, and the duty may still be ahead of them.
    /// </summary>
    [Fact]
    public void AZoneChangeInsideTheShorterWindowDoesNotEndTheMatch()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));

        var result = machine.Handle(Town(630));

        Assert.Equal(RunState.MentorMatched, machine.State);
        Assert.False(result.Accepted);
    }

    /// <summary>
    /// The announcement is an addition, never a requirement. A match whose announcement was
    /// missed - a lossy capture, a client that skipped it - records exactly as it did before,
    /// with the run created and entered in one step when the duty loads.
    /// </summary>
    [Fact]
    public void AMissedAnnouncementStillRecordsTheRun()
    {
        var machine = Machine();
        machine.Handle(Queue(0));

        var result = machine.Handle(Duty(600));

        Assert.Equal(RunState.EnteredDuty, result.ToState);
        Assert.Equal(RunState.Idle, result.FromState);
        Assert.False(machine.MatchObserved);
    }

    /// <summary>
    /// A failed storage commit rolls the machine back, and "this match was announced" has to roll
    /// back with it: otherwise the next state published would claim the server said something it
    /// never said.
    /// </summary>
    [Fact]
    public void WhetherTheMatchWasAnnouncedSurvivesACheckpoint()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));
        var checkpoint = machine.Checkpoint();

        machine.Handle(Duty(640));
        machine.Reset();
        Assert.False(machine.MatchObserved);

        machine.Restore(checkpoint);

        Assert.True(machine.MatchObserved);
        Assert.Equal(RunState.MentorMatched, machine.State);
    }

    /// <summary>A profile that reads the server's own pop never sees one of these at all.</summary>
    [Fact]
    public void AProfileThatReadsTheServersOwnPopIgnoresTheAnnouncement()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("cn-pop", Region.Cn, ProfileStatus.Verified, Mentor),
            null,
            () => "00000000-0000-4000-8000-000000000043");

        var result = machine.Handle(Announced(600));

        Assert.Equal(RunState.Idle, machine.State);
        Assert.False(result.Accepted);
        Assert.Empty(result.Commands);
    }
}
