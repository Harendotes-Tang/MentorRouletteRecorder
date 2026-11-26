using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using static MentorRecorder.Collector.UnitTests.StateMachineEvents;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What the run state machine does for a queue-inferred profile, with and without knowing when the
/// popup appeared.
///
/// Such a profile creates the run only when the duty loads, because until then "the player
/// queued" is all it has. Without an announcement the zone it lands in must be a duty the duty
/// table knows, and a queue that was cancelled, stopped, lost or replaced creates nothing later.
///
/// That is right for the record and wrong for the player, who wanted to be told the moment the
/// popup was on screen and got silence instead. An announcement recognised by its timing carries
/// no roulette, so it changes nothing about what is recorded: it only moves MENTOR_MATCHED forward
/// to the moment the server said so, and says that this one was observed rather than inferred.
/// </summary>
public sealed class MatchAnnouncedStateMachineTests
{
    private const int Mentor = 42;

    /// <summary>The territory every <see cref="StateMachineEvents.Zone"/> enters.</summary>
    private const int DutyTerritory = 800001;

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

    /// <summary>The player's own queue request, which this profile reads as its pop.</summary>
    private static ContentFinderPop Queue(int seconds, int roulette = Mentor) => Pop(seconds, roulette);

    private static MatchAnnounced Announced(int seconds) => new()
    {
        Key = Key("announced-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    /// <summary>The known duty loading.</summary>
    private static ZoneInitialization Duty(int seconds) => Zone(seconds, contentId: null);

    private static ZoneInitialization Town(int seconds) => new()
    {
        Key = Key("town-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), TerritoryId = 100, IsDutyInstance = null,
    };

    /// <summary>A queue-inferred profile whose duty table is <paramref name="knownDuty"/> (none when null).</summary>
    private static MentorRunStateMachine QueueMachine(Func<int, bool>? knownDuty) =>
        new(
            ProfileBinding.Live("cn.local", Region.Cn, ProfileStatus.Verified, Mentor, matchFromQueue: true),
            new StateMachineOptions { MatchWindow = TimeSpan.FromHours(1), IsKnownDuty = knownDuty },
            () => "00000000-0000-4000-8000-000000000003");

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

    /// <summary>
    /// The rule this feature was let in under: it only ever adds. If the learned message is wrong
    /// and speaks early, the duty the player then enters is still the duty they queued for - the
    /// queue request stands behind it exactly as it did before there was any announcement.
    /// </summary>
    [Fact]
    public void ADutyEnteredLongAfterAnEarlyAnnouncementIsStillRecorded()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(10));

        var result = machine.Handle(Duty(10 + (int)StateMachineOptions.Default.AnnouncedWindow.TotalSeconds + 300));

        Assert.Equal(RunState.EnteredDuty, result.ToState);
    }

    /// <summary>
    /// An announced match that lapses closes its run, but the request behind it has not expired:
    /// the player who teleported after a false alarm and is matched ten minutes later must still
    /// get their record.
    /// </summary>
    [Fact]
    public void AnAnnouncedMatchThatLapsesLeavesTheQueueStanding()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(10));
        var lapsed = machine.Handle(Town(300));
        Assert.Equal(RunState.CancelledBeforeEntry, lapsed.ToState);

        var result = machine.Handle(Duty(900));

        Assert.Equal(RunState.EnteredDuty, result.ToState);
    }

    /// <summary>
    /// Audit 2026-11-26 D1-1: an announced match accepts the shape the IDLE path accepts. The
    /// entry marker carries no territory and none was announced before it; the duty's territory
    /// follows it inside the territory window, and the run enters at the marker and completes.
    /// </summary>
    [Fact]
    public void AnAnnouncedMatchEntersWhenTheTerritoryFollowsTheMarker()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        Assert.Equal(RunState.MentorMatched, machine.Handle(Announced(600)).ToState);
        var marker = ZoneUnknown(630);

        var held = machine.Handle(marker);
        Assert.Empty(held.Commands);
        Assert.Equal(RunState.MentorMatched, machine.State);
        var beforeTerritory = machine.Checkpoint();
        var territory = Territory(630_050, 1039);

        var entered = machine.Handle(territory);

        Assert.Equal(RunState.MentorMatched, entered.FromState);
        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal(marker.ObservedAtUtc, Assert.Single(entered.Commands.OfType<EnterDutyCommand>()).EnteredAtUtc);
        Assert.Equal(1039, Assert.Single(entered.Commands.OfType<SetDutyCommand>()).TerritoryId);
        Assert.Equal(marker.Mono, machine.EnteredMono);

        // A failed commit rolls back to the held marker, and the same territory enters again.
        machine.Restore(beforeTerritory);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(territory).ToState);

        var completed = machine.Handle(Result(1500, victory: true));
        var finish = Assert.Single(completed.Commands.OfType<FinishRunCommand>());
        Assert.Equal(RunResult.Completed, finish.Result);
        Assert.Equal(870_000L, finish.DurationMs);
    }

    /// <summary>
    /// What the IDLE path drops, the announced path drops: a territory after the window, a town's
    /// territory, or a later marker leaves the held marker unentered and the match standing.
    /// </summary>
    [Theory]
    [InlineData("late")]
    [InlineData("town")]
    [InlineData("marker")]
    [InlineData("leave")]
    public void AnAnnouncedMatchDoesNotEnterOnATerritoryTheIdlePathWouldDrop(string shape)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        machine.Handle(Announced(600));
        Assert.Empty(machine.Handle(ZoneUnknown(630)).Commands);

        switch (shape)
        {
            case "late":
                Assert.Empty(machine.Handle(Territory(640_001, 1039)).Commands);
                break;
            case "town":
                Assert.Empty(machine.Handle(Territory(630_050, 5000)).Commands);
                Assert.Empty(machine.Handle(Territory(630_100, 1039)).Commands);
                break;
            case "marker":
                Assert.Empty(machine.Handle(ZoneUnknown(631) with { TerritoryId = 5000 }).Commands);
                Assert.Empty(machine.Handle(Territory(631_050, 1039)).Commands);
                break;
            default:
                Assert.Empty(machine.Handle(Leave(631)).Commands);
                Assert.Empty(machine.Handle(Territory(631_050, 1039)).Commands);
                break;
        }

        Assert.Equal(RunState.MentorMatched, machine.State);
        Assert.Null(machine.EnteredMono);
    }

    /// <summary>
    /// Past the announcement's own window the request still stands behind a known duty, so a held
    /// marker whose territory names the duty enters the announced run; one whose territory names a
    /// town is the lapsed match, closed at the marker, with the request left standing.
    /// </summary>
    [Theory]
    [InlineData(1039, true)]
    [InlineData(5000, false)]
    public void AHeldMarkerPastTheAnnouncedWindowIsJudgedByItsTerritory(int territoryId, bool enters)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        var opened = machine.Handle(Announced(10));
        var marker = ZoneUnknown(300);
        Assert.Empty(machine.Handle(marker).Commands);

        var result = machine.Handle(Territory(300_050, territoryId));

        Assert.Equal(enters ? RunState.EnteredDuty : RunState.CancelledBeforeEntry, result.ToState);
        Assert.Equal(opened.RunId, result.RunId);
        if (enters)
        {
            Assert.Equal(marker.ObservedAtUtc, Assert.Single(result.Commands.OfType<EnterDutyCommand>()).EnteredAtUtc);
            return;
        }

        var finish = Assert.Single(result.Commands.OfType<FinishRunCommand>());
        Assert.Equal(marker.ObservedAtUtc, finish.EndedAtUtc);
        Assert.Equal(DetectionConfidence.Low, finish.Confidence);
        machine.Handle(Territory(900_000, 1039));
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(901)).ToState);
    }

    /// <summary>
    /// A learned message that turns out to be chatty must not write a trail row per arrival for
    /// as long as the match stands.
    /// </summary>
    [Fact]
    public void AnAnnouncementRepeatedWithoutEndStopsRefreshingTheMatch()
    {
        var machine = Machine();
        machine.Handle(Queue(0));
        machine.Handle(Announced(10));
        var written = 0;
        for (var i = 1; i <= 60; i++)
        {
            written += machine.Handle(Announced(10 + i)).Commands.Count;
        }

        Assert.True(written <= MentorRunStateMachine.MaxAnnouncedRefreshes,
            $"{written} trail rows for one match");
    }

    // ---- no announcement: the duty table decides, and only a standing queue counts ----

    /// <summary>
    /// Cancelling may be invisible to a calibrated profile. A subsequent ordinary teleport
    /// must leave neither an active run nor an incomplete history row.
    /// </summary>
    [Fact]
    public void AnInferredProfileRefusesAZoneChangeThatIsNotAKnownDuty()
    {
        var machine = QueueMachine(territory => territory == 1039);

        Assert.Empty(machine.Handle(Queue(0)).Commands);
        Assert.Empty(machine.Handle(Territory(10_000, 5000)).Commands);
        Assert.Empty(machine.Handle(ZoneUnknown(11)).Commands);
        Assert.Equal(RunState.Idle, machine.State);
        Assert.Null(machine.CurrentRunId);
    }

    /// <summary>The same machine enters as soon as the zone it lands in is a duty.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnInferredProfileEntersWhenTheZoneIsAKnownDuty(bool territoryArrivesAfterEntry)
    {
        var machine = QueueMachine(territory => territory == 1039);
        var zone = ZoneUnknown(11);
        var territory = Territory(territoryArrivesAfterEntry ? 11_050 : 10_950, 1039);

        Assert.Equal(RunState.Idle, machine.Handle(Queue(0)).ToState);
        var pending = machine.Handle(territoryArrivesAfterEntry ? zone : territory);
        Assert.Equal(RunState.Idle, pending.ToState);
        Assert.Empty(pending.Commands);
        Assert.Null(machine.CurrentRunId);

        var entered = machine.Handle(territoryArrivesAfterEntry ? territory : zone);
        Assert.Equal(RunState.Idle, entered.FromState);
        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal(Start, Assert.Single(entered.Commands.OfType<CreateRunCommand>()).MatchedAtUtc);
        Assert.Equal(zone.ObservedAtUtc, Assert.Single(entered.Commands.OfType<EnterDutyCommand>()).EnteredAtUtc);
        Assert.Equal(1039, Assert.Single(entered.Commands.OfType<SetDutyCommand>()).TerritoryId);
        Assert.Equal(new[] { "CONTENT_FINDER_POP", zone.EventType, territory.EventType },
            entered.Commands.OfType<AppendEventCommand>().Select(command => command.Event.EventType));
        Assert.Equal(zone.Mono, machine.EnteredMono);
        Assert.False(machine.MatchObserved);

        var completed = machine.Handle(Result(15, victory: true));
        var finish = Assert.Single(completed.Commands.OfType<FinishRunCommand>());
        Assert.Equal(RunResult.Completed, finish.Result);
        Assert.Equal(4000L, finish.DurationMs);
    }

    [Theory]
    [InlineData(11, 21_000, true)]
    [InlineData(11, 21_001, false)]
    [InlineData(3600, 3_600_050, false)]
    public void ADelayedTerritoryMustArriveWithinBothWindows(int entrySeconds, int territoryMilliseconds, bool enters)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        Assert.Empty(machine.Handle(ZoneUnknown(entrySeconds)).Commands);

        var result = machine.Handle(Territory(territoryMilliseconds, 1039));

        Assert.Equal(enters ? RunState.EnteredDuty : RunState.Idle, result.ToState);
        Assert.Equal(enters, result.Commands.Any());
    }

    [Fact]
    public void ADelayedTownTerritoryConsumesTheMarkerButKeepsTheQueue()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        Assert.Empty(machine.Handle(ZoneUnknown(11)).Commands);
        Assert.Empty(machine.Handle(Territory(11_050, 5000)).Commands);
        Assert.Empty(machine.Handle(Territory(11_100, 1039)).Commands);

        var entered = machine.Handle(ZoneUnknown(12));
        Assert.Equal(Start, Assert.Single(entered.Commands.OfType<CreateRunCommand>()).MatchedAtUtc);
        Assert.Equal(RunState.EnteredDuty, entered.ToState);
    }

    [Fact]
    public void ACandidateEntryCanBeReplayedAfterCheckpointRestore()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        var beforeMarker = machine.Checkpoint();
        Assert.Empty(machine.Handle(ZoneUnknown(11)).Commands);
        var beforeTerritory = machine.Checkpoint();
        var territory = Territory(11_050, 1039);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(territory).ToState);

        machine.Restore(beforeTerritory);
        var replayed = machine.Handle(territory);
        Assert.Equal(RunState.EnteredDuty, replayed.ToState);
        Assert.Single(replayed.Commands.OfType<CreateRunCommand>());

        machine.Restore(beforeMarker);
        Assert.Empty(machine.Handle(territory).Commands);
        Assert.Equal(RunState.Idle, machine.State);
    }

    /// <summary>No duty table means the question cannot be answered, so no run is created.</summary>
    [Fact]
    public void AnInferredProfileWithNoDutyTableEntersNothing()
    {
        var machine = QueueMachine(null);

        Assert.Equal(RunState.Idle, machine.Handle(Queue(0)).ToState);
        Assert.Equal(RunState.Idle, machine.Handle(Territory(10_000, 1039)).ToState);
        Assert.Equal(RunState.Idle, machine.Handle(ZoneUnknown(11)).ToState);
        Assert.Null(machine.CurrentRunId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void TeleportingWhileStillQueuedDoesNotLoseTheLaterDuty(bool? dutyFlag)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        machine.Handle(Territory(10_000, 5000));
        Assert.Empty(machine.Handle(ZoneUnknown(11) with { IsDutyInstance = dutyFlag }).Commands);
        Assert.Empty(machine.Handle(Territory(11_050, 1039)).Commands);

        machine.Handle(Territory(1_800_000, 1039));
        var entered = machine.Handle(ZoneUnknown(1801));
        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Equal(Start, Assert.Single(entered.Commands.OfType<CreateRunCommand>()).MatchedAtUtc);
        Assert.Equal(Start.AddSeconds(1801),
            Assert.Single(entered.Commands.OfType<EnterDutyCommand>()).EnteredAtUtc);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("stop")]
    [InlineData("disconnect")]
    [InlineData("gap")]
    [InlineData("other_roulette")]
    [InlineData("reset")]
    public void AnInvalidatedQueueCannotCreateALaterRun(string signal)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        Assert.Empty(machine.Handle(ZoneUnknown(4)).Commands);
        var key = Key(signal);
        SemanticEvent invalidation = signal switch
        {
            "cancel" => new MatchCancelled { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5) },
            "stop" => CaptureStop(5),
            "disconnect" => Connection(5),
            "gap" => new EventSequenceGap { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5), DroppedCount = 1 },
            _ => Queue(5, roulette: 2),
        };
        if (signal == "reset") machine.Reset();
        else Assert.Empty(machine.Handle(invalidation).Commands);

        Assert.Empty(machine.Handle(Territory(10_000, 1039)).Commands);
        Assert.Empty(machine.Handle(ZoneUnknown(11)).Commands);
        Assert.Null(machine.CurrentRunId);
        Assert.Equal(RunState.Idle, machine.State);
    }

    [Fact]
    public void AStaleQueueCannotBeRevivedByAMatchingContentId()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0) with { ContentId = 123 });
        Assert.Empty(machine.Handle(ZoneUnknown(3601) with { ContentId = 123, TerritoryId = 1039 }).Commands);
        Assert.Null(machine.CurrentRunId);
    }

    [Fact]
    public void RequeueingUsesTheLatestRequestAndCreatesOnlyOneRunAtEntry()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        Assert.Empty(machine.Handle(ZoneUnknown(19)).Commands);
        Assert.Empty(machine.Handle(Queue(20)).Commands);
        Assert.Empty(machine.Handle(Territory(20_050, 1039)).Commands);
        machine.Handle(Territory(30_000, 1039));
        var entered = machine.Handle(ZoneUnknown(31));
        Assert.Equal(Start.AddSeconds(20),
            Assert.Single(entered.Commands.OfType<CreateRunCommand>()).MatchedAtUtc);
        Assert.Empty(machine.Handle(ZoneUnknown(31)).Commands);
    }

    [Fact]
    public void ANewQueueAfterAnEnteredDutyDoesNotCreateAnotherPendingRecord()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Queue(0));
        machine.Handle(Territory(10_000, 1039));
        machine.Handle(ZoneUnknown(11));
        var next = machine.Handle(Queue(100));
        Assert.Single(next.Commands.OfType<FinishRunCommand>());
        Assert.Empty(next.Commands.OfType<CreateRunCommand>());
        Assert.Equal(RunState.Idle, next.ToState);
        Assert.Null(machine.CurrentRunId);
        machine.Handle(Territory(110_000, 1039));
        Assert.Single(machine.Handle(ZoneUnknown(111)).Commands.OfType<CreateRunCommand>());
    }

    /// <summary>An ordinary profile is untouched by the duty-table rule.</summary>
    [Fact]
    public void AnObservedProfileStillEntersOnAnyZoneChangeInsideTheWindow()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("cn-test", Region.Cn, ProfileStatus.Verified, Mentor),
            new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) },
            () => "00000000-0000-4000-8000-000000000004");

        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0, Mentor)).ToState);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(ZoneUnknown(11)).ToState);
    }
}
