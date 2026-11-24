using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
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
    private const ushort Request = CalibrationTrafficCases.Request;
    private const ushort Reply = CalibrationTrafficCases.Reply;
    private const ushort Announce = CalibrationTrafficCases.Announce;

    private static CalibrationDraft Derive(IEnumerable<DecodedMessage> traffic) =>
        CalibrationTrafficCases.ObserveAndDerive(traffic).Draft;

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
        var draft = CalibrationTrafficCases.Derive(CalibrationTrafficCases.MarkerOffset);
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
    /// Audit 2026-10-03, OCal-7. An announcement that carries the queued roulette twice - at byte 8
    /// and again at byte 12 - qualifies at both positions, and two candidates used to read as an
    /// ambiguity, so the build fell back to inferring the match from the queue. Two positions of
    /// one shape that agree on every sighting are one answer; the lower offset is written, so every
    /// machine on the build writes the same profile and share code.
    /// </summary>
    [Fact]
    public void AnAnnouncementCarryingTheIdTwiceIsStillFoundAtItsFirstOffset()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1), (12, 1)), 120_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2), (12, 2)), 340_000),
            }));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
        var pop = draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal(Announce, pop.Opcode);
        Assert.Equal(8, pop.Field("roulette_id")!.Offset);
    }

    /// <summary>
    /// Two positions of one shape that part company even once are not twins, and the software does
    /// not know which of them is the roulette. Live traffic cannot produce them - both carried the
    /// queued roulette on every message the scan looked at - so the pair is built by hand, as carried
    /// evidence written by two different versions could leave it.
    /// </summary>
    [Fact]
    public void TwoPositionsThatDisagreeOnASightingAreNotCollapsed()
    {
        var evening = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest);
        var snapshot = CalibrationTrafficCases.Observe(evening
            .Concat(new[]
            {
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1), (12, 1)), 120_000),
                CalibrationObserverTests.Message(
                    MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2), (12, 2)), 340_000),
            }));
        var markers = snapshot.Markers.Select(marker => marker.Opcode == Announce && marker.Offset == 12
            ? marker with { Sightings = marker.Sightings.Select(sighting => sighting with { TMs = sighting.TMs + 1 }).ToArray() }
            : marker).ToArray();

        var draft = CalibrationDraft.Derive(snapshot with { Markers = markers }, CalibrationObserverTests.Template());

        Assert.NotEqual(CalibrationMatchSource.MarkerOffset, draft.MatchSource);
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
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
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
    /// The CN 2026.09.15 client, real machine: the player opened the retainer bell while queued,
    /// and the 80-byte retainer-list rows carry their slot number - 0 to 9 - at the template's
    /// byte 16. A list that counts through the small numbers contains every roulette id the
    /// player can queue, so one of its rows "announced" the match before every duty entry and
    /// the opcode was written into a VERIFIED profile. The rows beside it said otherwise: an
    /// announcement carries the queued roulette every time it is sent while the queue stands.
    /// </summary>
    [Fact]
    public void AListThatCountsThroughTheSmallNumbersIsNotTheAnnouncement()
    {
        var rows = Enumerable.Range(0, 10).Select(slot => CalibrationObserverTests.Message(
            MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(80, (16, (byte)slot)), 110_000 + slot));
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest).Concat(rows));

        Assert.NotEqual(CalibrationMatchSource.Announcement, draft.MatchSource);
        Assert.DoesNotContain(draft.Messages,
            message => message.Name == "CONTENT_FINDER_POP" && message.Opcode == Announce);
    }

    /// <summary>
    /// The evidence is kept on disk, so one odd message of the announcement's shape must not bar
    /// the true announcement for good. A list disagrees on every row but one; this does not.
    /// </summary>
    [Fact]
    public void OneStraySightingDoesNotDisqualifyTheAnnouncement()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.Announcement).Append(
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(64, (16, 200)), 100_000)));

        Assert.Equal(CalibrationMatchSource.Announcement, draft.MatchSource);
        Assert.Contains(draft.Messages,
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
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
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
    /// Audit 2026-10-03, OCal-4. The pairs persist, so the first one of a build is there for good,
    /// and it can be a movement packet that carried the queued id between the request and its echo.
    /// The frequency ceiling is about the request opcode the pairs agree on, not whichever paired
    /// first: asking it of the chatty stray disqualified the real reply opcode for the life of the
    /// build, however many real requests paired after it.
    /// </summary>
    [Fact]
    public void AChattyStrayThatPairedFirstDoesNotDisqualifyTheReply()
    {
        var snapshot = CalibrationTrafficCases.Observe(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));
        Assert.Equal(2, snapshot.Pairs.Count);
        var stray = snapshot.Pairs[0] with { RequestOpcode = 0xD001, RequestTMs = snapshot.Pairs[0].RequestTMs + 50 };
        var counts = new Dictionary<(PacketDirection Direction, ushort Opcode), int>(snapshot.OpcodeCounts)
        {
            [(PacketDirection.ClientToServer, 0xD001)] = CalibrationDraft.MaxCandidateOccurrences + 1,
        };

        var draft = CalibrationDraft.Derive(
            snapshot with { Pairs = snapshot.Pairs.Prepend(stray).ToArray(), OpcodeCounts = counts },
            CalibrationObserverTests.Template());

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.Equal(Request, draft.FinderRequestOpcode);
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
        var (snapshot, draft) = CalibrationTrafficCases.ObserveAndDerive(
            CalibrationObserverTests.Session1().Concat(strays));

        // Not BLOCKED: blocking says "wait for a new version of the software", and the shape is
        // sitting right there inside the bursts.
        Assert.Equal(CalibrationDraftStatus.Observing, draft.Status);
        Assert.Contains(draft.Blockers, text => text.Chinese.Contains("进出副本的时间还在检查中", StringComparison.Ordinal));
        Assert.DoesNotContain(draft.Blockers, text => text.Chinese.Contains("需要新版本的软件", StringComparison.Ordinal));

        var evidence = CalibrationEvidenceSummary.From(snapshot, CalibrationObserverTests.Template());
        Assert.Equal(1, evidence.ZoneOutside);
        Assert.Contains(evidence.ZoneShapes!, text => text.StartsWith("0xa107:456=", StringComparison.Ordinal));
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
        var snapshot = CalibrationTrafficCases.Observe(CalibrationObserverTests.Session1(), template);
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
    /// Audit 2026-11-20, Cb-2. Evidence is carried for the life of a build, and the report kept the sixteen
    /// OLDEST loads and pairs: on evidence from a few evenings, tonight's duties were missing from it, which
    /// reads as "never seen". The report keeps the newest sixteen, still newest last.
    /// </summary>
    [Fact]
    public void TheEvidenceKeepsTheNewestLoadsAndPairsWhenThereAreMany()
    {
        var template = CalibrationObserverTests.Template();
        var tonight = CalibrationTrafficCases.Observe(CalibrationObserverTests.Session1());
        var cluster = tonight.Clusters[0];
        var pair = tonight.Pairs[0];
        // Twenty loads and queues from earlier evenings, carried in front of tonight's.
        var earlier = Enumerable.Range(1, 20).Select(day => TimeSpan.FromDays(day)).Reverse().ToArray();
        var snapshot = tonight with
        {
            Clusters = earlier.Select(age => cluster with { LoadStartedAtUtc = cluster.LoadStartedAtUtc - age })
                .Concat(tonight.Clusters).ToArray(),
            Pairs = earlier.Select(age => pair with { ReplyAtUtc = pair.ReplyAtUtc - age })
                .Concat(tonight.Pairs).ToArray(),
        };
        var asOf = tonight.LastMessageAtUtc!.Value.AddMinutes(1);

        var evidence = CalibrationEvidenceSummary.From(snapshot, template, asOf);

        var clustersAt = evidence.ClustersAt!;
        var pairsAt = evidence.PairsAt!;
        Assert.Equal(16, clustersAt.Count);
        Assert.Equal(16, pairsAt.Count);
        var newestLoad = tonight.Clusters.Max(item => item.LoadStartedAtUtc);
        var newestPair = tonight.Pairs.Max(item => item.ReplyAtUtc);
        Assert.Equal((long)(asOf - newestLoad).TotalSeconds, clustersAt[clustersAt.Count - 1]);
        Assert.Equal((long)(asOf - newestPair).TotalSeconds, pairsAt[pairsAt.Count - 1]);
        Assert.All(clustersAt, seconds => Assert.True(seconds < TimeSpan.FromDays(20).TotalSeconds));
        Assert.Equal(clustersAt.OrderByDescending(seconds => seconds), clustersAt);
    }

    /// <summary>
    /// Nothing announces the match anywhere in the traffic. Rather than record nothing at all,
    /// the player's own request stands in for it, and the profile says so by carrying a
    /// CONTENT_FINDER_POP that travels from the client to the server.
    /// </summary>
    [Fact]
    public void WithNoAnnouncementAnywhereTheQueueRequestStandsIn()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));
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
        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest).Concat(decoys);
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Contains(draft.Blockers, text => text.Chinese.Contains("还没识别到你进入的副本", StringComparison.Ordinal));
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
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest));

        Assert.DoesNotContain(draft.Events, item => item.Kind == "pop");
        var entered = Assert.Single(draft.Events, item => item.Kind == "duty_enter");
        Assert.Contains("排队", entered.Label.Chinese, StringComparison.Ordinal);
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
            .Concat(CalibrationTrafficCases.SecondQueue(1, 60_000))
            .Concat(CalibrationTrafficCases.SecondQueue(2, 90_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, 5000))
            .Concat(CalibrationObserverTests.Cluster(215_000, 5000));
        var draft = Derive(traffic);

        Assert.NotEqual(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.ReplyState, draft.MatchSource);
        Assert.Empty(draft.Messages);
    }
}
