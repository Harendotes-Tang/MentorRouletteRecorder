using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Where a calibrated profile gets "the match happened" from, in the order the draft tries:
/// the queue reply's state, a dedicated announcement at the template's offset, a dedicated
/// announcement at an offset the traffic named, and - only when none of those can be
/// identified - the player's own queue request standing in for it.
///
/// The last two exist because of the CN 2026.09.01 client: the queue reply emits exactly the
/// echoes each request earns and never speaks again, and every candidate at the template's
/// byte 16 tied at one duty entry each across four real-machine reports.
/// </summary>
public sealed class CalibrationMatchSourceTests
{
    private const ushort Request = 0xC001;
    private const ushort Reply = 0xC002;
    private const ushort Announce = 0xF00D;

    /// <summary>Session1 without its match message: a build whose announcement nobody has found.</summary>
    private static IEnumerable<DecodedMessage> WithoutTheMatch() =>
        CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == Reply && message.Mono == TimeSpan.FromMilliseconds(120_000)));

    /// <summary>A second roulette queued and echoed, so the reply opcode can be locked at all.</summary>
    private static IEnumerable<DecodedMessage> SecondQueue(byte roulette = 2, long at = 300_000)
    {
        yield return CalibrationObserverTests.Message(
            MessageDirection.Outbound, Request, CalibrationObserverTests.Bytes(24, (0, roulette)), at);
        yield return CalibrationObserverTests.Message(
            MessageDirection.Inbound, Reply, CalibrationObserverTests.Bytes(40, (9, 5), (16, roulette)), at + 120);
    }

    private static CalibrationDraft Derive(IEnumerable<DecodedMessage> traffic)
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        return CalibrationDraft.Derive(observer.Snapshot(), template);
    }

    /// <summary>
    /// The marker scan exists for this build: it announces the match on a message of its own
    /// and puts the roulette id at byte 8, where the template's pop has nothing. A scan fixed
    /// at byte 16 reports silence, which is indistinguishable from "the message never arrived".
    /// The position is named by following the queued roulette as it changes, which no unrelated
    /// byte can do.
    /// </summary>
    [Fact]
    public void AnAnnouncementWhoseRouletteIdMovedIsStillFound()
    {
        var draft = Derive(WithoutTheMatch()
            .Concat(SecondQueue())
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 120_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2)), 340_000),
            }));
        var pop = draft.Messages.FirstOrDefault(message => message.Name == "CONTENT_FINDER_POP");

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        Assert.NotNull(pop);
        Assert.Equal(Announce, pop!.Opcode);
        Assert.Equal(24, pop.ExpectedLength);
        Assert.Equal(PacketDirection.ServerToClient, pop.Direction);
        Assert.Equal(8, pop.Field("roulette_id")!.Offset);
        Assert.DoesNotContain(pop.Fields, field => field.Role == ProfileFieldRole.Selector);
        Assert.True(draft.Progress.PopSeen);
    }

    /// <summary>
    /// A roulette id is a number between 1 and 17, so a byte lands on one by accident roughly
    /// once in 256 messages. A position that carried the queued id on some of its shape's
    /// messages and not the rest is describing that accident, and must not be written into a
    /// profile however suggestive the hit looks.
    /// </summary>
    [Fact]
    public void APositionThatOnlySometimesCarriesTheQueuedIdIsNotTheAnnouncement()
    {
        var draft = Derive(WithoutTheMatch()
            .Concat(SecondQueue())
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 118_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 200)), 119_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2)), 340_000),
            }));

        Assert.NotEqual(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        Assert.DoesNotContain(draft.Messages,
            message => message.Name == "CONTENT_FINDER_POP" && message.Opcode == Announce);
    }

    /// <summary>
    /// One roulette proves nothing: a byte that happens to equal the id the player queued
    /// stays equal to it for as long as that queue stands, so a position sighted under a
    /// single roulette has not been tested at all.
    /// </summary>
    [Fact]
    public void OneRouletteIsNotEnoughToNameAPosition()
    {
        var draft = Derive(WithoutTheMatch()
            .Concat(SecondQueue())
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 120_000),
            }));

        Assert.NotEqual(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        Assert.DoesNotContain(draft.Messages,
            message => message.Name == "CONTENT_FINDER_POP" && message.Opcode == Announce);
    }

    /// <summary>
    /// This client answers one queue request with TWO messages on the same opcode, and also
    /// sends other 24-byte messages when the player presses the button, one of which can start
    /// with the same small number. The matched request must not be consumed by the first reply
    /// alone, and a reply opcode paired with two request opcodes must not be disqualified
    /// outright: the evidence is kept on disk, so one coincidence would poison every later
    /// session.
    /// </summary>
    [Fact]
    public void AStrayClientMessageCannotStealTheSecondReply()
    {
        var traffic = CalibrationObserverTests.Session1().Concat(new[]
        {
            // A second roulette, so the reply opcode can be locked, and a stray 24-byte client
            // message in the same second carrying the same id at the request's own offset.
            CalibrationObserverTests.Message(
                MessageDirection.Outbound, 0xBEEF, CalibrationObserverTests.Bytes(24, (0, 2)), 300_000),
            CalibrationObserverTests.Message(
                MessageDirection.Outbound, Request, CalibrationObserverTests.Bytes(24, (0, 2)), 300_010),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Reply, CalibrationObserverTests.Bytes(40, (9, 0), (16, 2)), 300_120),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Reply, CalibrationObserverTests.Bytes(40, (9, 7), (16, 2)), 300_260),
        });

        var draft = Derive(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(Request, draft.FinderRequestOpcode);
        Assert.Contains(draft.Messages, message => message.Name == "CONTENT_FINDER_POP");
    }

    /// <summary>
    /// The tolerance above applies only when one opcode is the clear winner. Two client opcodes
    /// that have each paired the same number of times are a real ambiguity, and calibration
    /// reports it rather than picking one.
    /// </summary>
    [Fact]
    public void TwoEquallySupportedRequestOpcodesStayAmbiguous()
    {
        Assert.Null(CalibrationDraft.DominantRequest(new[]
        {
            new FinderPairHit("tag", 0x0001, 0xC002, 1, 0, 100, DateTimeOffset.UnixEpoch),
            new FinderPairHit("tag", 0x0002, 0xC002, 2, 0, 100, DateTimeOffset.UnixEpoch.AddSeconds(5)),
        }));
        Assert.Equal((ushort)0x0001, CalibrationDraft.DominantRequest(new[]
        {
            new FinderPairHit("tag", 0x0001, 0xC002, 1, 0, 100, DateTimeOffset.UnixEpoch),
            new FinderPairHit("tag", 0x0001, 0xC002, 2, 0, 100, DateTimeOffset.UnixEpoch.AddSeconds(5)),
            new FinderPairHit("tag", 0x0002, 0xC002, 2, 0, 100, DateTimeOffset.UnixEpoch.AddSeconds(5)),
        }));
    }

    /// <summary>
    /// A shape that travels outside a load more often than the boundary can explain is still not
    /// the marker, and the player must not be told the build changed when the shape is sitting
    /// inside the bursts. The report says which of the two it is: marked/bursts+outside.
    /// </summary>
    [Fact]
    public void AShapeThatTravelsTooOftenIsRefusedWithoutBlamingTheBuild()
    {
        var strays = Enumerable.Range(0, CalibrationDraft.MaxZoneOutside + 3)
            .Select(i => CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xA107, CalibrationObserverTests.Bytes(456, (7, 9)), 190_000 + i * 1_000))
            .ToArray();
        var traffic = CalibrationObserverTests.Session1().Concat(strays).ToArray();
        var draft = Derive(traffic);

        // Not BLOCKED: blocking says "wait for a new version of the software", and the shape is
        // sitting right there inside the bursts.
        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Contains(draft.Blockers, text => text.Contains("换区报文对不上", StringComparison.Ordinal));
        Assert.DoesNotContain(draft.Blockers, text => text.Contains("需要新版本的软件", StringComparison.Ordinal));

        var evidence = CalibrationEvidenceSummary.From(Snapshot(traffic), CalibrationObserverTests.Template());
        Assert.Equal(1, evidence.ZoneOutside);
        Assert.Contains(evidence.ZoneShapes!, text => text.StartsWith("0xa107:456=", StringComparison.Ordinal));
    }

    private static CalibrationSnapshot Snapshot(IEnumerable<DecodedMessage> traffic)
    {
        var observer = new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        return observer.Snapshot();
    }

    /// <summary>
    /// Counts alone cannot tell "the player did not play" from "the observer stopped hearing",
    /// so the evidence also carries watched and quiet seconds and the age of every cluster and
    /// pair.
    /// </summary>
    [Fact]
    public void TheEvidenceSaysWhenItWasCollectedNotJustHowMuch()
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in CalibrationObserverTests.Session1().OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        // Session1 spans 5 s to 240 s of session time; the report is taken a minute after it.
        var asOf = snapshot.LastMessageAtUtc!.Value.AddMinutes(1);
        var evidence = CalibrationEvidenceSummary.From(snapshot, template, asOf);

        Assert.True(evidence.WatchedSeconds > 200);
        Assert.Equal(60, evidence.QuietSeconds);
        Assert.Equal(snapshot.Clusters.Count, evidence.ClustersAt!.Count);
        Assert.Equal(snapshot.Pairs.Count, evidence.PairsAt!.Count);
        // Newest last, and every one of them in the past.
        Assert.All(evidence.ClustersAt, seconds => Assert.True(seconds > 0));
        Assert.Equal(evidence.ClustersAt.OrderByDescending(seconds => seconds), evidence.ClustersAt);
    }

    /// <summary>
    /// One roulette, played from queue to exit, on a build whose match message nobody can find.
    /// Nothing can be written yet and the card has to say why: a single request/echo pair could
    /// be two unrelated messages that happened to share a byte, so the opcode pair is not named
    /// until a SECOND, different roulette pairs on the same two opcodes. Making the request is
    /// enough - the duty does not have to be played again.
    /// </summary>
    [Fact]
    public void OneRouletteAloneCannotNameTheRequestOpcodeYet()
    {
        var draft = Derive(WithoutTheMatch());

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Empty(draft.Messages);
        Assert.True(draft.Progress.FinderRequestSeen);
        Assert.True(draft.Progress.DutyZoneSeen);
        Assert.Contains(draft.Blockers, text => text.Contains("申请一个和刚才不一样的随机任务", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing announces the match anywhere in the traffic. Rather than record nothing at all,
    /// the player's own request stands in for it, and the profile says so by carrying a
    /// CONTENT_FINDER_POP that travels from the client to the server.
    /// </summary>
    [Fact]
    public void WithNoAnnouncementAnywhereTheQueueRequestStandsIn()
    {
        var draft = Derive(WithoutTheMatch().Concat(SecondQueue()));
        var pop = draft.Messages.FirstOrDefault(message => message.Name == "CONTENT_FINDER_POP");

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.NotNull(pop);
        Assert.Equal(Request, pop!.Opcode);
        Assert.Equal(PacketDirection.ClientToServer, pop.Direction);
        Assert.Equal(24, pop.ExpectedLength);
        Assert.Equal(0, pop.Field("roulette_id")!.Offset);
        Assert.DoesNotContain(pop.Fields, field => field.Role == ProfileFieldRole.Selector);
    }

    /// <summary>
    /// The same rule applied to a profile already sitting on disk. Refusing to write one that
    /// cannot enter a duty is not enough: a file already present still matches the build, so it
    /// stays in force and calibration stays idle unless the binding rejects it as unusable.
    /// </summary>
    [Fact]
    public void AQueueInferredProfileAlreadyOnDiskWithoutTheTerritoryIsNotUsable()
    {
        var template = CalibrationObserverTests.Template();
        var request = template.Calibration.FinderRequest;
        var queuePop = template.Pop with
        {
            Opcode = 0xC001,
            Direction = request.Direction,
            ExpectedLength = request.ExpectedLength,
            Fields = new[] { request.RouletteField },
        };

        Assert.False(Profile(queuePop, template.ZoneInitialization).ToBinding().IsUsable);
        Assert.True(Profile(queuePop, template.ZoneInitialization, template.ZoneTerritory!)
            .ToBinding().IsUsable);
        // A profile that reads the server's own announcement never needed the territory.
        Assert.True(Profile(template.Pop, template.ZoneInitialization).ToBinding().IsUsable);
    }

    private static ProtocolProfile Profile(params ProfileMessage[] messages) => new(
        "cn.local", Region.Cn, "2026.09.01.0000.0000", DateTimeOffset.UnixEpoch, 9,
        ProfileCompatibilityStatus.Verified, TimeSpan.FromSeconds(120), messages,
        Array.Empty<ProfileFixtureReference>(), "test", new string('0', 64), "", false);

    /// <summary>
    /// A queue-inferred profile tells a duty from a teleport by the territory it lands in, so
    /// without the territory message it can never enter one at all: the run starts when the
    /// player queues and sits at "matched" for ever, leaving one record with no duty, an
    /// unknown result and an overview stuck at 已匹配. Such a profile is not offered.
    /// </summary>
    [Fact]
    public void AQueueInferredProfileIsNotOfferedWithoutTheTerritoryMessage()
    {
        // The duty is still recognised - the observer reads the territory shape itself - but
        // a second shape of the same length carries it too, so no single opcode can be written
        // into the profile as THE territory message.
        var decoys = new[] { 5_000L, 125_000L, 215_000L }.Select(at => CalibrationObserverTests.Message(
            MessageDirection.Inbound, 0xA20A, CalibrationObserverTests.Bytes(136, (2, 15), (3, 4)), at + 10));
        var traffic = WithoutTheMatch().Concat(SecondQueue()).Concat(decoys);
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Blockers, text => text.Contains("哪个副本", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
    }

    /// <summary>
    /// The inferred match must not claim a popup the software never saw. The request is already
    /// on the timeline as 排本; repeating it as 匹配弹窗 would put two lines on one millisecond
    /// and tell the player something untrue about what was observed.
    /// </summary>
    [Fact]
    public void AnInferredMatchNeverShowsAPopupLineOnTheTimeline()
    {
        var draft = Derive(WithoutTheMatch().Concat(SecondQueue()));

        Assert.DoesNotContain(draft.Events, item => item.Kind == "pop");
        var entered = Assert.Single(draft.Events, item => item.Kind == "duty_enter");
        Assert.Contains("排本", entered.Label, StringComparison.Ordinal);
    }

    /// <summary>
    /// A queue that no recognised duty followed explains nothing, so there is nothing to infer
    /// from. The player is told to finish the duty rather than handed a profile built on a
    /// teleport.
    /// </summary>
    [Fact]
    public void AQueueWithNoKnownDutyBehindItInfersNothing()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(SecondQueue(1, 60_000))
            .Concat(SecondQueue(2, 90_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 5000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.ReplyState, draft.MatchSource);
        Assert.Empty(draft.Messages);
    }

    /// <summary>
    /// Cancelling may be invisible to a calibrated profile. A subsequent ordinary teleport
    /// must leave neither an active run nor an incomplete history row.
    /// </summary>
    [Fact]
    public void AnInferredProfileRefusesAZoneChangeThatIsNotAKnownDuty()
    {
        var machine = QueueMachine(territory => territory == 1039);

        Assert.Empty(machine.Handle(Pop(0)).Commands);
        Assert.Empty(machine.Handle(Territory(10_000, 5000)).Commands);
        Assert.Empty(machine.Handle(Zone(11)).Commands);
        Assert.Equal(RunState.Idle, machine.State);
        Assert.Null(machine.CurrentRunId);
    }

    /// <summary>The same machine enters as soon as the zone it lands in is a duty.</summary>
    [Fact]
    public void AnInferredProfileEntersWhenTheZoneIsAKnownDuty()
    {
        var machine = QueueMachine(territory => territory == 1039);

        Assert.Equal(RunState.Idle, machine.Handle(Pop(0)).ToState);
        Assert.Equal(RunState.Idle, machine.Handle(Territory(10_000, 1039)).ToState);
        var entered = machine.Handle(Zone(11));
        Assert.Equal(RunState.Idle, entered.FromState);
        Assert.Equal(RunState.EnteredDuty, entered.ToState);
        Assert.Single(entered.Commands.OfType<CreateRunCommand>());
        Assert.Single(entered.Commands.OfType<EnterDutyCommand>());
    }

    /// <summary>No duty table means the question cannot be answered, so no run is created.</summary>
    [Fact]
    public void AnInferredProfileWithNoDutyTableEntersNothing()
    {
        var machine = QueueMachine(null);

        Assert.Equal(RunState.Idle, machine.Handle(Pop(0)).ToState);
        Assert.Equal(RunState.Idle, machine.Handle(Territory(10_000, 1039)).ToState);
        Assert.Equal(RunState.Idle, machine.Handle(Zone(11)).ToState);
        Assert.Null(machine.CurrentRunId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void TeleportingWhileStillQueuedDoesNotLoseTheLaterDuty(bool? dutyFlag)
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Pop(0));
        machine.Handle(Territory(10_000, 5000));
        Assert.Empty(machine.Handle(Zone(11) with { IsDutyInstance = dutyFlag }).Commands);

        machine.Handle(Territory(1_800_000, 1039));
        var entered = machine.Handle(Zone(1801));
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
        machine.Handle(Pop(0));
        var key = Key(signal);
        SemanticEvent invalidation = signal switch
        {
            "cancel" => new MatchCancelled { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5) },
            "stop" => new CaptureStopped { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5) },
            "disconnect" => new ConnectionLost { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5) },
            "gap" => new EventSequenceGap { Key = key, ObservedAtUtc = Start.AddSeconds(5), Mono = TimeSpan.FromSeconds(5), DroppedCount = 1 },
            _ => Pop(5) with { RouletteId = 2 },
        };
        if (signal == "reset") machine.Reset();
        else Assert.Empty(machine.Handle(invalidation).Commands);

        machine.Handle(Territory(10_000, 1039));
        Assert.Empty(machine.Handle(Zone(11)).Commands);
        Assert.Null(machine.CurrentRunId);
        Assert.Equal(RunState.Idle, machine.State);
    }

    [Fact]
    public void AStaleQueueCannotBeRevivedByAMatchingContentId()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Pop(0) with { ContentId = 123 });
        Assert.Empty(machine.Handle(Zone(3601) with { ContentId = 123, TerritoryId = 1039 }).Commands);
        Assert.Null(machine.CurrentRunId);
    }

    [Fact]
    public void RequeueingUsesTheLatestRequestAndCreatesOnlyOneRunAtEntry()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Pop(0));
        Assert.Empty(machine.Handle(Pop(20)).Commands);
        machine.Handle(Territory(30_000, 1039));
        var entered = machine.Handle(Zone(31));
        Assert.Equal(Start.AddSeconds(20),
            Assert.Single(entered.Commands.OfType<CreateRunCommand>()).MatchedAtUtc);
        Assert.Empty(machine.Handle(Zone(31)).Commands);
    }

    [Fact]
    public void ANewQueueAfterAnEnteredDutyDoesNotCreateAnotherPendingRecord()
    {
        var machine = QueueMachine(territory => territory == 1039);
        machine.Handle(Pop(0));
        machine.Handle(Territory(10_000, 1039));
        machine.Handle(Zone(11));
        var next = machine.Handle(Pop(100));
        Assert.Single(next.Commands.OfType<FinishRunCommand>());
        Assert.Empty(next.Commands.OfType<CreateRunCommand>());
        Assert.Equal(RunState.Idle, next.ToState);
        Assert.Null(machine.CurrentRunId);
        machine.Handle(Territory(110_000, 1039));
        Assert.Single(machine.Handle(Zone(111)).Commands.OfType<CreateRunCommand>());
    }

    /// <summary>An ordinary profile is untouched by the duty-table rule.</summary>
    [Fact]
    public void AnObservedProfileStillEntersOnAnyZoneChangeInsideTheWindow()
    {
        var machine = new MentorRunStateMachine(
            ProfileBinding.Live("cn-test", Region.Cn, ProfileStatus.Verified, 9),
            new StateMachineOptions { MatchWindow = TimeSpan.FromSeconds(120) },
            () => "00000000-0000-4000-8000-000000000004");

        Assert.Equal(RunState.MentorMatched, machine.Handle(Pop(0)).ToState);
        Assert.Equal(RunState.EnteredDuty, machine.Handle(Zone(11)).ToState);
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static MentorRunStateMachine QueueMachine(Func<int, bool>? knownDuty) =>
        new(
            ProfileBinding.Live("cn.local", Region.Cn, ProfileStatus.Verified, 9, matchFromQueue: true),
            new StateMachineOptions { MatchWindow = TimeSpan.FromHours(1), IsKnownDuty = knownDuty },
            () => "00000000-0000-4000-8000-000000000003");

    private static ContentFinderPop Pop(int seconds) => new()
    {
        Key = Key("pop-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), RouletteId = 9,
    };

    private static TerritoryObserved Territory(int milliseconds, int territoryId) => new()
    {
        Key = Key("territory-" + milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), TerritoryId = territoryId,
    };

    private static ZoneInitialization Zone(int seconds) => new()
    {
        Key = Key("zone-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    private static EventKey Key(string semantic) =>
        new("00000000-0000-4000-8000-000000000020", PacketDirection.None, "SYNTHETIC", 0, null, semantic);
}
