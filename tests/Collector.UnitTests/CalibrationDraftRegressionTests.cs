using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationDraftRegressionTests
{
    private const ushort Decoy = 0xB0FF;
    private const ushort LobbyAnchor = 0xE100;

    /// <summary>A lobby handshake burst that carries exactly one 456-byte shape.</summary>
    private static IEnumerable<DecodedMessage> LobbyBurst(long t)
    {
        yield return CalibrationObserverTests.Message(MessageDirection.Inbound, LobbyAnchor, new byte[3000], t + 100, "lobby");
        for (var i = 1; i <= 5; i++)
        {
            yield return CalibrationObserverTests.Message(
                MessageDirection.Inbound, (ushort)(LobbyAnchor + i), new byte[300 + i * 100], t + 100 + i * 50, "lobby");
        }

        yield return CalibrationObserverTests.Message(MessageDirection.Inbound, Decoy, new byte[456], t + 420, "lobby");
    }

    [Fact]
    public void ALobbyOnlyShapeCannotBecomeTheZoneMarker()
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        var traffic = CalibrationObserverTests.Session1(zoneInitLength: 464)
            .Concat(LobbyBurst(300_000))
            .Concat(LobbyBurst(320_000))
            .Concat(LobbyBurst(340_000));
        foreach (var message in traffic)
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        var draft = CalibrationDraft.Derive(snapshot, template);
        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "ZONE_INITIALIZATION" && message.Opcode == Decoy);
    }

    [Fact]
    public void QuickEntryDoesNotSwallowThePopIntoTheLoad()
    {
        var traffic = CalibrationObserverTests.Session1().Select(message =>
            message.Opcode == 0xC002 && message.Mono.TotalMilliseconds == 120_000
                ? message with
                {
                    Mono = TimeSpan.FromMilliseconds(123_500),
                    ObservedAtUtc = message.ObservedAtUtc.AddMilliseconds(3_500),
                }
                : message);
        var draft = Derive(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.PopSeen);
        var pop = Assert.Single(draft.Events, item => item.Kind == "pop");
        var entry = Assert.Single(draft.Events, item => item.Kind == "duty_enter");
        Assert.True(entry.AtUtc > pop.AtUtc);
        Assert.Equal(125_100, entry.TMs);
    }

    [Fact]
    public void ADelayedReplyAndOrdinaryTravelDoNotProveAMatch()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 61_500, popState: 7))
            .Concat(CalibrationObserverTests.Cluster(125_000, 5000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void ARequestCannotRetroactivelyConfirmAnEarlierPop()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 50_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.False(draft.Progress.PopSeen);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    public void ALateReplyWithoutDutyEntryContinuesObserving(byte state)
    {
        var draft = Derive(CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 61_500, popState: state)));

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.False(draft.Progress.PopSeen);
        Assert.True(draft.Progress.PopShapeSeen);
        Assert.Empty(draft.Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APostExitStatusWithoutANewRequestDoesNotCompete(bool laterDuty)
    {
        var traffic = CalibrationObserverTests.Session1().Append(
            CalibrationObserverTests.Message(MessageDirection.Inbound, 0xC002,
                CalibrationObserverTests.Bytes(40, (9, 7), (16, 1)), 245_000));
        if (laterDuty)
        {
            traffic = traffic.Concat(CalibrationObserverTests.Cluster(300_000, 1039));
        }

        var draft = Derive(traffic);

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(new long[] { 3 }, draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP")
            .Field("finder_state")!.Constraints.In);
        Assert.Single(draft.Events, item => item.Kind == "pop");
    }

    [Fact]
    public void AnUnrelatedOpcodePairCannotSupplyTheRouletteRequest()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000).Take(2))
            .Concat(CalibrationObserverTests.QueueAndPop(70_000, 2, 120_000).Take(2)
                .Select(message => message with { Opcode = (ushort)(message.Opcode + 10) }))
            .Append(CalibrationObserverTests.Message(MessageDirection.Inbound, 0xC002,
                CalibrationObserverTests.Bytes(40, (9, 3), (16, 2)), 120_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.False(draft.Progress.PopSeen);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void AGenuineRapidMatchNeedsNoInventedMinimumQueueDuration()
    {
        var draft = Derive(CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 61_500))
            .Concat(CalibrationObserverTests.Cluster(63_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(153_000, 5000)));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.True(draft.Progress.PopSeen);
    }

    [Fact]
    public void ADutyEntryOutsideTheTemplateWindowDoesNotConfirmThePop()
    {
        var draft = Derive(CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000))
            .Concat(CalibrationObserverTests.Cluster(245_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(335_000, 5000)));

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.False(draft.Progress.PopSeen);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void MultiplePlausibleDutyEntriesAreNotRankedByConnectionOrTime()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039, connection: "other-zone"))
            .Concat(CalibrationObserverTests.Cluster(155_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(245_000, 5000));
        var draft = Derive(traffic);

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.False(draft.Progress.PopSeen);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void TwoMarkerObservationsAreNotCompletedByALobbyWithoutTheMarker()
    {
        var traffic = CalibrationObserverTests.Cluster(5_000, 5000, includeZoneInit: false)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 1039))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.True(draft.Progress.DutyEntrySeen && draft.Progress.DutyExitSeen);
        Assert.Empty(draft.Messages);
    }

    [Fact]
    public void ALobbyDecoyDoesNotCompeteWithTheMarkerOnTheDutyChain()
    {
        var draft = Derive(CalibrationObserverTests.Session1()
            .Concat(LobbyBurst(300_000)).Concat(LobbyBurst(320_000)).Concat(LobbyBurst(340_000)));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal((ushort)0xA107, draft.Messages.Single(message => message.Name == "ZONE_INITIALIZATION").Opcode);
        Assert.Equal(3, draft.SampleCounts["messages.ZONE_INITIALIZATION.opcode"]);
    }

    [Fact]
    public void TwoMarkersPresentOnTheSameDutyChainRemainAmbiguous()
    {
        var decoys = new long[] { 5_420, 125_420, 215_420 }.Select(t =>
            CalibrationObserverTests.Message(MessageDirection.Inbound, Decoy, new byte[456], t));
        var draft = Derive(CalibrationObserverTests.Session1().Concat(decoys));

        Assert.Equal(CalibrationDraftStatus.Blocked, draft.Status);
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("多条报文都像换区标记", StringComparison.Ordinal));
        Assert.Empty(draft.Messages);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5_000)]
    public void LoadStartIsRecordedFromTheFirstLargeMessage(long start)
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in CalibrationObserverTests.Cluster(start, 5000))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var cluster = Assert.Single(observer.Snapshot().Clusters);
        Assert.Equal(start + 100, cluster.LoadStartTMs);
        Assert.Equal(TimeSpan.FromMilliseconds(100), cluster.LoadStartedAtUtc - cluster.StartedAtUtc);
        Assert.True(cluster.StartTMs <= cluster.LoadStartTMs);
    }

    /// <summary>
    /// A request echoed, a duty entered and left, and no further message on the echoed opcode.
    /// Entry and exit must count as observations in their own right rather than only as
    /// corroboration of the match, or the card tells a player who has just finished a roulette
    /// that it has seen neither.
    /// </summary>
    [Fact]
    public void ADutyPlayedWithNoMatchMessageIsStillReportedAsSeen()
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in WithoutThePop())
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        var draft = CalibrationDraft.Derive(snapshot, template);
        var evidence = CalibrationEvidenceSummary.From(snapshot, template);

        Assert.True(draft.Progress.FinderRequestSeen);
        Assert.False(draft.Progress.PopSeen);
        Assert.False(draft.Progress.DutyEntrySeen);
        Assert.False(draft.Progress.DutyExitSeen);
        Assert.True(draft.Progress.DutyZoneSeen);
        Assert.Equal(1, evidence.DutyZones);
        Assert.NotEqual(0, evidence.TerritoryCandidates);
        // Telling a player who has just finished a duty to wait for the match is the one
        // instruction that cannot be carried out.
        Assert.DoesNotContain(draft.Blockers, blocker => blocker.Contains("等这次匹配成功"));
        Assert.Contains(draft.Blockers, blocker => blocker.Contains("已经看到你进过副本"));
    }

    /// <summary>
    /// The same traffic on a build that also moved the territory message. Nothing names a duty,
    /// so the card must keep saying it has not seen one and the report must say the shape found
    /// no candidate either - that is what separates "keep playing" from "this build needs a new
    /// version of the software".
    /// </summary>
    [Fact]
    public void ATerritoryShapeThatMovedLeavesTheDutyUnseen()
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in WithoutThePop())
        {
            observer.Accept(message.Payload.Length == 136 ? message with { Payload = new byte[144] } : message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        var draft = CalibrationDraft.Derive(snapshot, template);
        var evidence = CalibrationEvidenceSummary.From(snapshot, template);

        Assert.False(draft.Progress.DutyZoneSeen);
        Assert.Equal(0, evidence.DutyZones);
        Assert.Equal(0, evidence.TerritoryCandidates);
    }

    /// <summary>Login, request, echo, duty entry and exit - everything but the match itself.</summary>
    private static IEnumerable<DecodedMessage> WithoutThePop() =>
        CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == 0xC002 && message.Mono == TimeSpan.FromMilliseconds(120_000)));

    /// <summary>
    /// A populated map sends large server messages continuously, so a burst that must wait for
    /// five seconds of silence before it can open never opens: the loads are missed, and every
    /// shape inside them then counts as "outside" and disqualifies itself for good. The map's
    /// population must not change the outcome.
    /// </summary>
    [Fact]
    public void APopulatedMapDoesNotHideTheZoneLoads()
    {
        var quiet = Observe(CalibrationObserverTests.Session1());
        var busy = Observe(CalibrationObserverTests.Session1().Concat(PopulatedMap(0, 240_000)));

        Assert.Equal(CalibrationDraftStatus.Ready, quiet.Draft.Status);
        Assert.Equal(quiet.Draft.Status, busy.Draft.Status);
        Assert.Equal(quiet.Snapshot.Clusters.Count, busy.Snapshot.Clusters.Count);
        Assert.True(busy.Draft.Progress.DutyZoneSeen);
        Assert.True(busy.Draft.Progress.DutyEntrySeen);
        Assert.True(busy.Draft.Progress.DutyExitSeen);
        var evidence = CalibrationEvidenceSummary.From(busy.Snapshot, CalibrationObserverTests.Template());
        Assert.NotEqual(0, evidence.DutyZones);
        Assert.NotEqual(0, evidence.ZoneCandidates);
        Assert.NotEqual(0, evidence.TerritoryCandidates);
    }

    /// <summary>
    /// On some builds the message that says "matched" is not the template's length: the echoed
    /// opcode sends exactly the replies its requests earn and nothing more, and no server message
    /// of the pop's length passes the pop's shape. The scan therefore looks at every length and
    /// names the shape that carries a requested roulette id once the echo window has closed.
    /// </summary>
    [Fact]
    public void TheScanNamesAMatchMessageThatIsNotThePopsLength()
    {
        var traffic = new List<DecodedMessage>
        {
            // The request and the two echoes that pair with it.
            CalibrationObserverTests.Message(
                MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 1)), 60_000),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 0), (16, 1)), 60_120),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 7), (16, 1)), 60_180),
            // The popup: a different opcode, a different length, the same roulette id.
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(64, (16, 1)), 200_000),
            // A shape that only ever carries the id inside the echo window is the echo, not a match.
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00E, CalibrationObserverTests.Bytes(64, (16, 1)), 60_300),
            // A roulette the player never asked for cannot be an answer to a request they never made.
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00F, CalibrationObserverTests.Bytes(64, (16, 12)), 200_500),
        };

        var evidence = CalibrationEvidenceSummary.From(
            Observe(traffic).Snapshot, CalibrationObserverTests.Template());

        Assert.Contains(evidence.RouletteEchoes!, text => text.StartsWith("0xf00d:64=1/1~", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence.RouletteEchoes!, text => text.StartsWith("0xf00e", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence.RouletteEchoes!, text => text.StartsWith("0xf00f", StringComparison.Ordinal));
    }

    /// <summary>
    /// A roulette id is a number between 1 and 17, so a shape carrying one at the right offset is
    /// nearly no evidence: about one server message in a hundred does it by accident. What only
    /// the real announcement does is arrive shortly before the player enters a duty, every time
    /// they enter one. Two duties are enough to separate the two.
    /// </summary>
    [Fact]
    public void TheScanScoresAMatchMessageAgainstEveryDutyEntry()
    {
        var traffic = new List<DecodedMessage>();
        foreach (var (requestAt, popAt, entryAt, exitAt) in new[]
                 { (60_000L, 100_000L, 130_000L, 400_000L), (500_000L, 540_000L, 570_000L, 900_000L) })
        {
            traffic.Add(CalibrationObserverTests.Message(
                MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 1)), requestAt));
            traffic.Add(CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 0), (16, 1)), requestAt + 120));
            // The announcement: another opcode, another length, the id, minutes after the echo.
            traffic.Add(CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(64, (16, 1)), popAt));
            traffic.AddRange(CalibrationObserverTests.Cluster(entryAt, 1039));
            traffic.AddRange(CalibrationObserverTests.Cluster(exitAt, 5000));
        }

        // A shape that carries the id once, far from any duty entry: the accident this separates out.
        traffic.Add(CalibrationObserverTests.Message(
            MessageDirection.Inbound, 0xBEEF, CalibrationObserverTests.Bytes(64, (16, 1)), 950_000));

        var evidence = CalibrationEvidenceSummary.From(
            Observe(traffic).Snapshot, CalibrationObserverTests.Template());

        Assert.StartsWith("0xf00d:64=2/2", evidence.MatchEchoes!.First(), StringComparison.Ordinal);
        Assert.DoesNotContain(evidence.MatchEchoes!, text => text.StartsWith("0xbeef", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two duties can leave two shapes both reading 2/2, because plenty of traffic accompanies a
    /// duty load and any of it may carry a small byte at the right offset. Two things separate
    /// the announcement from the escort: it waits out the player's decision and the loading
    /// screen, so it sits tens of seconds before the load while the escort sits on top of it;
    /// and it is sent whether the player accepts or refuses, so it can appear with no duty
    /// behind it at all, which the escort never does.
    /// </summary>
    [Fact]
    public void TheScanSeparatesTheAnnouncementFromWhatEscortsALoad()
    {
        var traffic = new List<DecodedMessage>
        {
            CalibrationObserverTests.Message(
                MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 1)), 60_000),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 0), (16, 1)), 60_120),
            // The announcement, well clear of the echo window and 68 s before the load starts.
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(64, (16, 1)), 62_000),
            // The escort, right as the load begins.
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xF00E, CalibrationObserverTests.Bytes(64, (16, 1)), 130_050),
        };
        traffic.AddRange(CalibrationObserverTests.Cluster(130_000, 1039));
        // A second match the player refused: the announcement fires, nothing follows it.
        traffic.Add(CalibrationObserverTests.Message(
            MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 1)), 500_000));
        traffic.Add(CalibrationObserverTests.Message(
            MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 0), (16, 1)), 500_120));
        traffic.Add(CalibrationObserverTests.Message(
            MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(64, (16, 1)), 540_000));

        var echoes = CalibrationEvidenceSummary.From(
            Observe(traffic).Snapshot, CalibrationObserverTests.Template()).MatchEchoes!;

        // The announcement: one entry preceded, one loose hit left over from the refusal, and a
        // distance from the load that the escort cannot have.
        Assert.Contains("0xf00d:64=1/1+1@68s", echoes);
        // The escort: same entry, no life of its own, no distance.
        Assert.Contains("0xf00e:64=1/1+0@0s", echoes);
    }

    /// <summary>
    /// Some builds send the queue reply and the match announcement as two different messages:
    /// the reply opcode emits exactly the echoes each request earns and never speaks again, and
    /// no message of the template's length passes the pop's shape. The announcement must
    /// therefore be learnable as a message of its own, and told apart from the traffic that
    /// merely escorts a duty load, which also precedes an entry.
    /// </summary>
    [Fact]
    public void AnAnnouncementOnAMessageOfItsOwnIsLearnedAndBeatsTheLoadsEscort()
    {
        var traffic = CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == 0xC002 && message.Mono == TimeSpan.FromMilliseconds(120_000)))
            .Concat(new[]
            {
                // The announcement, on an opcode and a length the template does not name.
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(24, (16, 1)), 120_000),
                // Traffic escorting the load. It precedes the entry too, and it is not the pop.
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xF00E, CalibrationObserverTests.Bytes(24, (16, 1)), 124_900),
                // A second roulette, matched and refused: the announcement fires, no duty follows.
                CalibrationObserverTests.Message(
                    MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 2)), 300_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 5), (16, 2)), 300_120),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(24, (16, 2)), 340_000),
            });

        var draft = Observe(traffic).Draft;
        var pop = draft.Messages.FirstOrDefault(message => message.Name == "CONTENT_FINDER_POP");

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.NotNull(pop);
        Assert.Equal(0xF00D, pop!.Opcode);
        Assert.Equal(24, pop.ExpectedLength);
        // The template's selector describes a state of the queue reply. A dedicated announcement
        // has no state to carry: the message arriving is the state.
        Assert.DoesNotContain(pop.Fields, field => field.Role == ProfileFieldRole.Selector);
        Assert.Contains(pop.Fields, field => field.Name == "roulette_id" && field.Offset == 16);
        Assert.True(draft.Progress.PopSeen);
        Assert.True(draft.Progress.DutyEntrySeen);
        Assert.True(draft.Progress.DutyExitSeen);
    }

    /// <summary>
    /// Refusing a match costs the player a duty-finder penalty and the game only allows so many,
    /// so calibration must not need one. Where a shape lives settles it instead: traffic that
    /// belongs to a zone load travels inside zone loads, and the announcement of a match never
    /// does. Same traffic as the test above with the refusal taken out.
    /// </summary>
    [Fact]
    public void TheAnnouncementIsLearnedWithoutAnyRefusedMatch()
    {
        var traffic = CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == 0xC002 && message.Mono == TimeSpan.FromMilliseconds(120_000)))
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xF00D, CalibrationObserverTests.Bytes(24, (16, 1)), 120_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xF00E, CalibrationObserverTests.Bytes(24, (16, 1)), 124_900),
                // A second roulette, queued and echoed, so the reply opcode can be locked at all.
                CalibrationObserverTests.Message(
                    MessageDirection.Outbound, 0xC001, CalibrationObserverTests.Bytes(24, (0, 2)), 300_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, 0xC002, CalibrationObserverTests.Bytes(40, (9, 5), (16, 2)), 300_120),
            });

        var draft = Observe(traffic).Draft;
        var pop = draft.Messages.FirstOrDefault(message => message.Name == "CONTENT_FINDER_POP");

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.NotNull(pop);
        Assert.Equal(0xF00D, pop!.Opcode);
        Assert.Equal(24, pop.ExpectedLength);
    }

    /// <summary>One large server message every 500 ms, the way a map full of other players looks.</summary>
    private static IEnumerable<DecodedMessage> PopulatedMap(long from, long to)
    {
        for (var t = from; t < to; t += 500)
        {
            yield return CalibrationObserverTests.Message(
                MessageDirection.Inbound, 0xE001, new byte[400], t, "zone");
        }
    }

    private static (CalibrationSnapshot Snapshot, CalibrationDraft Draft) Observe(IEnumerable<DecodedMessage> traffic)
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(message => message.Mono))
        {
            observer.Accept(message);
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        return (snapshot, CalibrationDraft.Derive(snapshot, template));
    }

    private static CalibrationDraft Derive(IEnumerable<DecodedMessage> traffic)
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        foreach (var message in traffic.OrderBy(message => message.ObservedAtUtc))
        {
            observer.Accept(message);
        }

        observer.Flush();
        return CalibrationDraft.Derive(observer.Snapshot(), template);
    }
}
