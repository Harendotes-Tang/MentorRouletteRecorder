using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Calibration on a build nobody has a profile for: the observer collects shapes and timing,
/// the draft turns them into opcodes. Traffic here is synthetic, shaped like the CN client
/// but with every opcode changed, which is exactly what a patch does.
/// </summary>
public sealed class CalibrationObserverTests
{
    private const string Session = "calibration-session";
    private static readonly DateTimeOffset Start = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    // New-build opcodes. None of them appears in the template.
    private const ushort Anchor = 0xA100;
    private const ushort ZoneInit = 0xA107;
    private const ushort Territory = 0xA108;
    internal const ushort JobOpcode = 0xA109;
    private const ushort Request = 0xC001;
    private const ushort Reply = 0xC002;
    private const ushort Movement = 0xD001;
    private const int DutyTerritory = 1039; // Sastasha in the CN duty table.
    private const int TownTerritory = 5000; // Not a duty.

    internal static CalibrationTemplate Template()
    {
        var profile = new ProtocolProfile(
            "cn.template", Region.Cn, "2026.08.05.0000.0000", Start, 9, ProfileCompatibilityStatus.Verified,
            TimeSpan.FromSeconds(120),
            new[]
            {
                new ProfileMessage("CONTENT_FINDER_POP", 0x0323, PacketDirection.ServerToClient, null, 40, null, null,
                    Array.Empty<long>(), new[]
                    {
                        new ProfileField("finder_state", 9, ProfileFieldType.U8, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(null, null, new long[] { 3 }), ProfileFieldRole.Selector),
                        new ProfileField("roulette_id", 16, ProfileFieldType.U8, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(1, 255, null)),
                    }),
                new ProfileMessage("ZONE_INITIALIZATION", 0x014a, PacketDirection.ServerToClient, null, 456, null, null,
                    Array.Empty<long>(), Array.Empty<ProfileField>()),
                new ProfileMessage("ZONE_TERRITORY", 0x028d, PacketDirection.ServerToClient, null, 136, null, null,
                    Array.Empty<long>(), new[]
                    {
                        new ProfileField("territory_id", 2, ProfileFieldType.U16, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(1, null, null)),
                    }),
                new ProfileMessage("PLAYER_JOB", 0x0350, PacketDirection.ServerToClient, null, 16, null, null,
                    Array.Empty<long>(), new[]
                    {
                        new ProfileField("job_id", 0, ProfileFieldType.U8, 0, ProfileEndian.Little,
                            new ProfileFieldConstraints(1, 43, null)),
                    }),
            },
            Array.Empty<ProfileFixtureReference>(), "template", new string('0', 64), "", false)
        {
            Calibration = new ProfileCalibration(
                new CalibrationFinderRequest(
                    PacketDirection.ClientToServer, 24,
                    new ProfileField("roulette_id", 0, ProfileFieldType.U8, 0, ProfileEndian.Little,
                        new ProfileFieldConstraints(1, 255, null))),
                TimeSpan.FromSeconds(1)),
        };
        return CalibrationTemplate.From(profile)!;
    }

    internal static DecodedMessage Message(
        MessageDirection direction, ushort opcode, byte[] payload, long t, string connection = "zone")
        => new(Session, direction, Start + TimeSpan.FromMilliseconds(t), TimeSpan.FromMilliseconds(t), 0, 3,
            opcode, payload, connection);

    internal static byte[] Bytes(int length, params (int Offset, byte Value)[] writes)
    {
        var payload = new byte[length];
        foreach (var (offset, value) in writes)
        {
            payload[offset] = value;
        }

        return payload;
    }

    private static byte[] TerritoryPayload(int territory) =>
        Bytes(136, (2, (byte)(territory & 0xff)), (3, (byte)(territory >> 8)));

    /// <summary>One zone-load burst: anchor, five large shapes, and the three template-shaped members.</summary>
    internal static IEnumerable<DecodedMessage> Cluster(long t, int territory, byte job = 21, string connection = "zone",
        bool includeZoneInit = true, int zoneInitLength = 456, bool includeJob = true)
    {
        yield return Message(MessageDirection.Inbound, Territory, TerritoryPayload(territory), t, connection);
        yield return Message(MessageDirection.Inbound, Anchor, new byte[3000], t + 100, connection);
        for (var i = 1; i <= 5; i++)
        {
            yield return Message(MessageDirection.Inbound, (ushort)(Anchor + i), new byte[300 + i * 100], t + 100 + i * 50, connection);
        }

        if (includeZoneInit)
        {
            yield return Message(MessageDirection.Inbound, ZoneInit, new byte[zoneInitLength], t + 400, connection);
        }

        if (includeJob)
        {
            yield return Message(MessageDirection.Inbound, JobOpcode, Bytes(16, (0, job)), t + 450, connection);
            yield return Message(MessageDirection.Inbound, JobOpcode, Bytes(16, (0, job)), t + 460, connection); // twice, like 0x0350 at login
        }
        yield return Message(MessageDirection.Outbound, Movement, new byte[24], t + 800, connection);
    }

    internal static IEnumerable<DecodedMessage> Noise(long from, long to, string connection = "zone")
    {
        for (var t = from; t < to; t += 500)
        {
            // Movement is 24 bytes C2S, the same shape as the request; its first byte is
            // position data and only rarely lands on a roulette id.
            yield return Message(MessageDirection.Outbound, Movement, Bytes(24, (0, 200)), t, connection);
            yield return Message(MessageDirection.Inbound, 0xD002, new byte[40], t + 100, connection);
        }
    }

    internal static IEnumerable<DecodedMessage> QueueAndPop(long requestAt, byte roulette, long popAt,
        string connection = "zone", byte replyState = 5, byte popState = 3)
    {
        yield return Message(MessageDirection.Outbound, Request, Bytes(24, (0, roulette)), requestAt, connection);
        yield return Message(MessageDirection.Inbound, Reply, Bytes(40, (9, replyState), (16, roulette)), requestAt + 120, connection);
        yield return Message(MessageDirection.Inbound, Reply, Bytes(40, (9, popState), (16, roulette)), popAt, connection);
    }

    /// <summary>Login, queue, pop, duty entry, duty exit: the one roulette the user is asked to play.</summary>
    internal static IEnumerable<DecodedMessage> Session1(bool popInsideEcho = false, byte exitJob = 21,
        byte popState = 3, int zoneInitLength = 456)
    {
        foreach (var m in Cluster(5_000, TownTerritory, zoneInitLength: zoneInitLength)) yield return m;
        foreach (var m in Noise(10_000, 60_000)) yield return m;
        var popAt = popInsideEcho ? 60_500L : 120_000L;
        foreach (var m in QueueAndPop(60_000, 1, popAt, popState: popState)) yield return m;
        foreach (var m in Noise(61_000, 124_000)) yield return m;
        foreach (var m in Cluster(125_000, DutyTerritory, zoneInitLength: zoneInitLength)) yield return m;
        foreach (var m in Noise(130_000, 210_000)) yield return m;
        foreach (var m in Cluster(215_000, TownTerritory, exitJob, zoneInitLength: zoneInitLength)) yield return m;
        foreach (var m in Noise(220_000, 240_000)) yield return m;
    }

    private static (CalibrationObserver Observer, CalibrationDraft Draft) Run(IEnumerable<DecodedMessage> traffic,
        CalibrationRejections? rejections = null)
    {
        var template = Template();
        var observer = new CalibrationObserver(template, Region.Cn, Session);
        foreach (var message in traffic)
        {
            observer.Accept(message);
        }

        observer.Flush();
        return (observer, CalibrationDraft.Derive(observer.Snapshot(), template, rejections));
    }

    [Fact]
    public void OneRouletteFromLoginToExitLearnsEveryOpcode()
    {
        var (observer, draft) = Run(Session1());
        var snapshot = observer.Snapshot();

        Assert.Equal(3, snapshot.Clusters.Count);
        Assert.Single(snapshot.Pairs);
        Assert.Equal(Request, snapshot.Pairs[0].RequestOpcode);
        Assert.Equal(Reply, snapshot.Pairs[0].ReplyOpcode);
        Assert.Contains(snapshot.Pops, pop => pop.Opcode == Reply && !pop.WithinEcho);
        Assert.Equal(0, snapshot.OverflowCount);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Blockers);
        Assert.Equal(Request, draft.FinderRequestOpcode);
        var byName = draft.Messages.ToDictionary(message => message.Name);
        Assert.Equal(Reply, byName["CONTENT_FINDER_POP"].Opcode);
        Assert.Equal(ZoneInit, byName["ZONE_INITIALIZATION"].Opcode);
        Assert.Equal(Territory, byName["ZONE_TERRITORY"].Opcode);
        Assert.Equal(JobOpcode, byName["PLAYER_JOB"].Opcode);
        Assert.Equal(40, byName["CONTENT_FINDER_POP"].ExpectedLength);
        Assert.Equal("finder_state", byName["CONTENT_FINDER_POP"].Fields[0].Name);
        Assert.Equal(new long[] { 1 }, draft.ConfirmedRouletteIds);
        Assert.Equal("cn.template", draft.TemplateProfileId);
        Assert.True(draft.SampleCounts["messages.CONTENT_FINDER_POP.opcode"] >= 2);
        Assert.Equal(3, draft.SampleCounts["messages.ZONE_INITIALIZATION.opcode"]);
    }

    [Fact]
    public void TimelineNamesTheRouletteAndTheDutyWithoutAnyOpcode()
    {
        var (_, draft) = Run(Session1());

        var kinds = draft.Events.Select(item => item.Kind).ToArray();
        Assert.Equal(new[] { "login", "finder_request", "pop", "duty_enter", "duty_exit" }, kinds);
        Assert.Equal("排本：练级迷宫", draft.Events[1].Label);
        Assert.Equal("匹配弹窗：练级迷宫", draft.Events[2].Label);
        Assert.StartsWith("进入副本：", draft.Events[3].Label, StringComparison.Ordinal);
        Assert.Equal(DutyTerritory, draft.Events[3].TerritoryId);
        Assert.NotNull(draft.Events[3].DutyName);
        // The exit says which entry it closes: without that it reads as the end of whatever
        // line sits above it, which may be a queue made well inside the duty.
        Assert.StartsWith("离开副本（结束的是 ", draft.Events[4].Label, StringComparison.Ordinal);
        Assert.All(draft.Events, item => Assert.DoesNotContain("0x", item.Label, StringComparison.Ordinal));
        Assert.Equal(4, draft.Events.Count(item => item.RequiresConfirmation));
        Assert.True(draft.Progress.FinderRequestSeen && draft.Progress.PopSeen && draft.Progress.DutyEntrySeen &&
            draft.Progress.DutyExitSeen);
    }

    [Fact]
    public void TheDutyBurstCountsEvenWhenItArrivesOnAnotherConnection()
    {
        // The client holds several connections open at once and this project has no evidence
        // for which one carries the pop. Requiring the burst to share the pop's connection
        // would strand a player who really did enter the duty (review finding H-1).
        var traffic = Cluster(5_000, TownTerritory)
            .Concat(QueueAndPop(60_000, 1, 120_000))
            .Concat(Cluster(125_000, DutyTerritory, connection: "zone-b"))
            .Concat(Cluster(215_000, TownTerritory, connection: "zone-b"));
        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Events, item => item.Kind == "duty_enter");
        Assert.Contains(draft.Events, item => item.Kind == "duty_exit");
    }

    [Fact]
    public void ACorroboratedPopSurvivesAChattyOpcode()
    {
        // A duty finder status opcode repeats while a long queue ticks over. The frequency
        // ceiling must not throw away the one candidate a genuine pop already vouches for
        // (review finding M-4).
        var chatter = Enumerable.Range(0, CalibrationDraft.MaxCandidateOccurrences + 50)
            .Select(i => Message(MessageDirection.Inbound, Reply, Bytes(40, (9, 5), (16, 1)), 130_000 + i));
        var (_, draft) = Run(Session1().Concat(chatter));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Messages, message => message.Name == "CONTENT_FINDER_POP" && message.Opcode == Reply);
    }

    [Fact]
    public void AMatchInsideTheReplyWindowIsNotAMatchYet()
    {
        // This is not a changed structure: the template's state value means "matched" on the
        // build the template came from, and a build that answers the request with that same
        // value has simply not sent a match yet.
        var (_, draft) = Run(Session1(popInsideEcho: true));

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("匹配成功", StringComparison.Ordinal));
        Assert.DoesNotContain(draft.Blockers, blocker => blocker.Contains("分不出来", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void AStateThatAlsoArrivesAsAReplyCannotMeanMatchedAndBlocks()
    {
        // The server sends the same state as a reply to the request and later on its own.
        // Nothing in the traffic separates "queued" from "matched", so there is no honest
        // constant to write and more play will not produce one.
        var (_, draft) = Run(Session1(popState: 5));

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("分不出来", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void AMatchWhoseStateIsNotTheTemplatesIsStillLearned()
    {
        // A patch that renumbers the finder states must not end calibration. The state is read
        // off the message that arrived on the server's own initiative, and the user confirms
        // the moment it happened.
        var (_, draft) = Run(Session1(popState: 9));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        var pop = draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal(Reply, pop.Opcode);
        Assert.Equal(new long[] { 9 }, pop.Field("finder_state")!.Constraints.In);
        Assert.Equal(1, pop.Field("roulette_id")!.Constraints.Min);
        Assert.Equal(255, pop.Field("roulette_id")!.Constraints.Max);
    }

    [Fact]
    public void AStateUpdateThatArrivesDuringAZoneLoadIsNotAMatch()
    {
        // The shipped CN profile records this counter-example from 7.55: after leaving a duty
        // the server sends the pop's opcode carrying the roulette that was queued for. It rides
        // in with the zone load, and taking it for a match would date the match to the end of
        // the run and put a second candidate state on the table.
        var traffic = Cluster(5_000, TownTerritory)
            .Concat(Noise(10_000, 60_000))
            .Concat(QueueAndPop(60_000, 1, 120_000))
            .Concat(Noise(61_000, 124_000))
            .Concat(Cluster(125_000, DutyTerritory))
            .Concat(Noise(130_000, 210_000))
            .Concat(Cluster(215_000, TownTerritory))
            .Concat(new[] { Message(MessageDirection.Inbound, Reply, Bytes(40, (9, 1), (16, 1)), 215_900) })
            .Concat(Noise(220_000, 240_000));
        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        var pop = draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal(new long[] { 3 }, pop.Field("finder_state")!.Constraints.In);
        Assert.Single(draft.Events, item => item.Kind == "pop");
    }

    [Fact]
    public void TwoStatesArrivingUnpromptedAreAmbiguousRatherThanRanked()
    {
        // Ranking one of them into place would be a guess, and a wrong guess here writes a
        // profile that opens a run every time the queue ticks over.
        var traffic = Cluster(5_000, TownTerritory)
            .Concat(Noise(10_000, 60_000))
            .Concat(QueueAndPop(60_000, 1, 120_000))
            .Concat(Noise(61_000, 100_000))
            .Concat(new[] { Message(MessageDirection.Inbound, Reply, Bytes(40, (9, 7), (16, 1)), 100_000) })
            .Concat(Noise(100_000, 124_000))
            .Concat(Cluster(125_000, DutyTerritory))
            .Concat(Noise(130_000, 210_000))
            .Concat(Cluster(215_000, TownTerritory))
            .Concat(Noise(220_000, 240_000));
        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("好几种报文", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void ALobbyBurstThatCannotCarryTheMarkerNoLongerHidesIt()
    {
        // Logging in opens a burst on the lobby connection before the game server sends its
        // own, and the zone marker is never inside it. Requiring the marker in every burst -
        // which is what nine tenths means for any count up to nine - rules out the true marker.
        var traffic = Cluster(1_000, TownTerritory, connection: "lobby", includeZoneInit: false)
            .Concat(Session1());
        var (observer, draft) = Run(traffic);

        Assert.Equal(4, observer.Snapshot().Clusters.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Messages, message => message.Name == "ZONE_INITIALIZATION");
    }

    [Fact]
    public void AZoneMarkerThatChangedLengthSaysSoInsteadOfAskingForMoreZoning()
    {
        // Every burst carries a marker of a different size. No amount of play fixes that, and
        // the report has to name the size that is actually there.
        var (observer, draft) = Run(Session1(zoneInitLength: 464));
        var evidence = CalibrationEvidenceSummary.From(observer.Snapshot(), Template());

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("长得不一样", StringComparison.Ordinal));
        Assert.Equal(0, evidence.ZoneCandidates);
        Assert.Contains(evidence.ZoneOnceOnly, text => text.Contains(":464@3", StringComparison.Ordinal));
    }

    [Fact]
    public void AMessageThatOnlyFailsTheStateIsCountedRatherThanForgotten()
    {
        // "The popup was on the screen and the software says it never saw one" reads exactly
        // like "nothing arrived" unless the near misses are counted.
        var (observer, _) = Run(Session1(popState: 9));
        var snapshot = observer.Snapshot();
        var evidence = CalibrationEvidenceSummary.From(snapshot, Template());

        Assert.True(snapshot.PopShapeSeen > 0);
        Assert.Contains(snapshot.PopRefusals, entry => entry.Key.Field == "finder_state");
        Assert.Contains(evidence.FinderStates, text => text.Contains("finder_state=9", StringComparison.Ordinal));
        Assert.Contains(evidence.FinderLengths, text => text.Contains(":40=", StringComparison.Ordinal));
        Assert.Equal(0, snapshot.DiagnosticsOverflow);
        Assert.Equal(0, snapshot.OverflowCount);
    }

    [Fact]
    public void AChattyFinderOpcodeFillsNoTableAndBlocksNothing()
    {
        // A queue that ticks over for twenty minutes must not be able to push the real match
        // out of the sample table, and a full report-only table must never void a claim.
        var chatter = Enumerable.Range(0, 2_000)
            .Select(i => Message(MessageDirection.Inbound, Reply, Bytes(40, (9, 5), (16, 1)), 130_000 + (i * 10)));
        var (observer, draft) = Run(Session1().Concat(chatter));
        var snapshot = observer.Snapshot();

        Assert.True(snapshot.Pops.Count <= CalibrationObserver.MaxPops);
        Assert.Equal(0, snapshot.OverflowCount);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
    }

    [Fact]
    public void ObservingUntilTheDutyIsLeft()
    {
        var traffic = Cluster(5_000, TownTerritory)
            .Concat(QueueAndPop(60_000, 1, 120_000))
            .Concat(Cluster(125_000, DutyTerritory));
        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("打完这把副本", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
        Assert.True(draft.Progress.DutyEntrySeen);
        Assert.False(draft.Progress.DutyExitSeen);
    }

    [Fact]
    public void NothingSeenYetAsksForARoulette()
    {
        var (_, draft) = Run(Noise(0, 30_000));

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        // Blockers say what to do next; the card's four rows say what has been seen.
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("申请一次随机任务", StringComparison.Ordinal));
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("登录进入游戏", StringComparison.Ordinal));
        Assert.DoesNotContain(draft.Blockers, blocker => blocker.Contains("还没见到", StringComparison.Ordinal));
        Assert.False(draft.Progress.FinderRequestSeen);
    }

    [Fact]
    public void AZoneLoadOnABusyMapStillOpensAClusterAndKeepsItsOwnStart()
    {
        // Requiring five seconds without any large server message would let a burst open only
        // somewhere empty, and every shape from the missed loads would then count as "outside"
        // and disqualify itself as a candidate for good. A populated map must not hide a load.
        var traffic = new List<DecodedMessage>();
        for (var t = 0L; t < 20_000; t += 1000)
        {
            traffic.Add(Message(MessageDirection.Inbound, 0xE001, new byte[600], t));
        }

        traffic.AddRange(Cluster(20_500, DutyTerritory));
        var (observer, _) = Run(traffic);

        var cluster = Assert.Single(observer.Snapshot().Clusters);
        // The load starts where the load starts, not on the last thing the busy map happened to
        // send: the timeline the user confirms shows this moment.
        Assert.Equal(20_600, cluster.LoadStartTMs);
        Assert.Single(cluster.TerritoryHits);
    }

    [Fact]
    public void BusyMapTrafficOnItsOwnStillOpensNoCluster()
    {
        // The other half of the same rule: without a load's spread of shapes and its anchor,
        // ordinary large traffic is still just traffic, however much of it there is.
        var traffic = new List<DecodedMessage>();
        for (var t = 0L; t < 60_000; t += 200)
        {
            traffic.Add(Message(MessageDirection.Inbound, 0xE001, new byte[600], t));
            traffic.Add(Message(MessageDirection.Inbound, 0xE002, new byte[900], t + 50));
        }

        var (observer, _) = Run(traffic);

        Assert.Empty(observer.Snapshot().Clusters);
    }

    [Fact]
    public void AZoneMarkerSeenOutsideEveryClusterIsNotTheMarker()
    {
        var stray = Message(MessageDirection.Inbound, ZoneInit, new byte[456], 90_000);
        var (_, draft) = Run(Session1().Append(stray));

        // One stray sighting does not rule the shape out. A burst reaches back three seconds
        // from its first large message, so a load whose marker arrives earlier files its own
        // marker as an outsider. The shape still has to mark three loads exactly once each,
        // including the duty it opened and the duty it closed.
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Messages, message => message.Name == "ZONE_INITIALIZATION" && message.Opcode == ZoneInit);
    }

    [Fact]
    public void AShapeThatReallyTravelsOnItsOwnIsStillNotTheMarker()
    {
        // The forgiveness is for a boundary the software draws itself, not for behaviour. A
        // message that keeps turning up between loads is not marking them, and the ratio is
        // what says so: Session1 has three bursts, so more than one sighting outside is more
        // than one in three and the shape is out.
        var strays = Enumerable.Range(0, CalibrationDraft.MaxZoneOutside + 3)
            .Select(i => Message(MessageDirection.Inbound, ZoneInit, new byte[456], 90_000 + i * 1_000));
        var (_, draft) = Run(Session1().Concat(strays));

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "ZONE_INITIALIZATION");
    }

    [Fact]
    public void ARejectedPopOpcodeIsNotProposedAgain()
    {
        var rejections = new CalibrationRejections(new HashSet<ushort> { Reply }, new HashSet<MessageKey>());
        var (_, draft) = Run(Session1(), rejections);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "CONTENT_FINDER_POP");
    }

    [Fact]
    public void TwoEchoingOpcodesAreAmbiguousAndBlock()
    {
        var second = new[]
        {
            Message(MessageDirection.Outbound, Request, Bytes(24, (0, 2)), 70_000),
            Message(MessageDirection.Inbound, 0xC003, Bytes(40, (9, 5), (16, 2)), 70_100),
            Message(MessageDirection.Outbound, Request, Bytes(24, (0, 3)), 72_000),
            Message(MessageDirection.Inbound, 0xC003, Bytes(40, (9, 5), (16, 3)), 72_100),
        };
        var (_, draft) = Run(Session1().Concat(second));

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("分不清", StringComparison.Ordinal));
    }

    [Fact]
    public void AJobShapeWhoseValueChangesWithinAClusterIsLeftOut()
    {
        var traffic = Session1().Concat(new[]
        {
            // A second job-shaped value inside the exit cluster contradicts stability.
            Message(MessageDirection.Inbound, JobOpcode, Bytes(16, (0, 30)), 215_470),
        }).OrderBy(message => message.Mono).ToArray();
        var (_, draft) = Run(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "PLAYER_JOB");
        Assert.Contains(draft.Messages, message => message.Name == "ZONE_INITIALIZATION");
    }

    /// <summary>
    /// The lobby handshake does not announce the job on every build, and a run only ever needs
    /// the job that was current on entry and on exit. Requiring the shape in every burst records
    /// every run as 职业未知 on a build whose login burst lacks it.
    /// </summary>
    [Fact]
    public void AJobShapeMissingFromTheLoginBurstIsStillDeclared()
    {
        var traffic = Cluster(5_000, TownTerritory, includeJob: false)
            .Concat(Session1().Where(message => message.Mono >= TimeSpan.FromMilliseconds(10_000)))
            .ToArray();
        var (observer, draft) = Run(traffic);

        Assert.Equal(3, observer.Snapshot().Clusters.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Messages, message => message.Name == "PLAYER_JOB" && message.Opcode == JobOpcode);
        Assert.True(draft.Progress.JobSeen);
    }

    /// <summary>
    /// One reading outside the constraints and outside any burst must not rule the opcode out.
    /// The violation is still counted for the diagnostics report, but decides nothing on its own.
    /// </summary>
    [Fact]
    public void AJobShapeThatOnceBrokeItsConstraintOutsideABurstIsStillDeclared()
    {
        var traffic = Session1().Concat(new[]
        {
            Message(MessageDirection.Inbound, JobOpcode, Bytes(16, (0, 0)), 90_000),
        }).OrderBy(message => message.Mono).ToArray();
        var (observer, draft) = Run(traffic);

        Assert.Equal(1, observer.Snapshot().JobViolations[JobOpcode]);
        Assert.Contains(draft.Messages, message => message.Name == "PLAYER_JOB" && message.Opcode == JobOpcode);
    }

    [Fact]
    public void AJobShapeThatBreaksItsConstraintInsideTheEntryBurstIsLeftOut()
    {
        var traffic = Session1().Concat(new[]
        {
            Message(MessageDirection.Inbound, JobOpcode, Bytes(16, (0, 200)), 125_470),
        }).OrderBy(message => message.Mono).ToArray();
        var (observer, draft) = Run(traffic);

        var entry = observer.Snapshot().Clusters[1];
        Assert.Equal(1, entry.JobViolations[JobOpcode]);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "PLAYER_JOB");
        Assert.False(draft.Progress.JobSeen);
    }

    [Fact]
    public void TheLatestJobValueIsKeptForTheFirstRecord()
    {
        var (observer, _) = Run(Session1(exitJob: 30));

        Assert.Equal(30, observer.Snapshot().LatestJobValues[JobOpcode]);
    }

    [Fact]
    public void EvidenceAccumulatesAcrossARelogin()
    {
        var template = Template();
        var observer = new CalibrationObserver(template, Region.Cn, Session);
        // First login: queue once, the match never comes, the player re-logs.
        foreach (var message in Cluster(5_000, TownTerritory).Concat(QueueAndPop(60_000, 1, 300_000).Take(2)))
        {
            observer.Accept(message);
        }

        observer.Flush();
        observer.AdoptSession("second-login");
        // Second login, forty minutes later on the wall clock, session time back to zero.
        var later = TimeSpan.FromMinutes(40);
        foreach (var message in Session1())
        {
            observer.Accept(message with
            {
                CaptureSessionId = "second-login",
                ObservedAtUtc = message.ObservedAtUtc + later,
                ConnectionKey = "zone-2",
            });
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        var draft = CalibrationDraft.Derive(snapshot, template);

        Assert.Equal(2, snapshot.SessionCount);
        Assert.Equal(4, snapshot.Clusters.Count);
        Assert.Equal(2, snapshot.Pairs.Count);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(new[] { "login", "finder_request", "login", "finder_request", "pop", "duty_enter", "duty_exit" },
            draft.Events.Select(item => item.Kind).ToArray());
        Assert.Equal(draft.Events.Select(item => item.EventId).Distinct().Count(), draft.Events.Count);
    }

    [Fact]
    public void TableOverflowVoidsTheNeverOutsideClaim()
    {
        var flood = Enumerable.Range(0, CalibrationObserver.MaxOutsideKeys + 10)
            .Select(i => Message(MessageDirection.Inbound, (ushort)(i & 0xffff), new byte[(i % 7) + 1], 30_000 + i));
        var (observer, draft) = Run(Session1().Concat(flood));

        Assert.True(observer.Snapshot().OverflowCount > 0);
        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("上限", StringComparison.Ordinal));
    }

    /// <summary>
    /// A marker table that overflowed knows nothing about a position it never got a row for,
    /// and an unknown is not an agreement. That consistency check is the only thing standing
    /// between this path and a multiplexed opcode - the 1.2.1 accident, where the learned pop
    /// also fired at the retainer bell, invented a 匹配成功 and lost the real run - so once the
    /// tables have overflowed anywhere in the session, a candidate the scan never recorded
    /// cannot be locked on its roulette echoes alone (2026-09-21 full-audit finding 8).
    /// </summary>
    [Fact]
    public void AnOverflowedMarkerTableNoLongerVouchesForAnAnnouncement()
    {
        var template = Template();
        var observed = CalibrationTrafficCases.Observe(
            CalibrationTrafficCases.Traffic(CalibrationTrafficCases.Announcement));
        // No position recorded for the candidate. With the tables still inside their limits
        // that is the traffic's own silence and the announcement is locked as before, so the
        // only thing separating the two assertions below is the overflow itself.
        var unrecorded = observed with { Markers = Array.Empty<MarkerCandidate>() };

        Assert.Equal(
            CalibrationMatchSource.Announcement,
            CalibrationDraft.Derive(unrecorded, template).MatchSource);
        Assert.NotEqual(
            CalibrationMatchSource.Announcement,
            CalibrationDraft.Derive(unrecorded with { MarkerOverflow = 1 }, template).MatchSource);
    }

    [Fact]
    public void ObserverIgnoresOtherSessionsAndKeepsNoPayload()
    {
        var template = Template();
        var observer = new CalibrationObserver(template, Region.Cn, Session);
        var foreign = Message(MessageDirection.Outbound, Request, Bytes(24, (0, 1)), 1000) with { CaptureSessionId = "other" };
        observer.Accept(foreign);

        Assert.Equal(0, observer.Snapshot().MessagesSeen);
        Assert.DoesNotContain(typeof(CalibrationSnapshot).GetProperties(), property =>
            property.PropertyType == typeof(ReadOnlyMemory<byte>) || property.PropertyType == typeof(byte[]));
    }

    [Fact]
    public void TemplateSelectionPrefersTheHighestBuildAndRefusesATie()
    {
        var directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            WriteTemplate(directory, "older", "build-1");
            WriteTemplate(directory, "newer", "build-2");
            var catalog = ProfileCatalog.Load(directory);
            Assert.Equal("newer", CalibrationTemplate.Select(catalog, Region.Unknown)!.Source.ProfileId);
            Assert.Null(CalibrationTemplate.Select(catalog, Region.Cn));

            // Two files claiming build-2 are AMBIGUOUS at the catalogue level and drop out; the
            // older build is what remains.
            WriteTemplate(directory, "twin", "build-2");
            Assert.Equal("older", CalibrationTemplate.Select(ProfileCatalog.Load(directory), Region.Unknown)!.Source.ProfileId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteTemplate(string directory, string profileId, string build)
    {
        var document = CalibrationTemplateTests.VerifiedDocument();
        document["game_build"] = build;
        document["calibration"] = CalibrationTemplateTests.CalibrationSection();
        ProfileTestFiles.Write(directory, profileId, document);
    }
}
