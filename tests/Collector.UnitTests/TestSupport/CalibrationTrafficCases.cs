using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// One synthetic evening per way a draft can name the match, plus the variants in which the
/// optional territory and job messages cannot be declared. Shared by the golden regression of
/// <see cref="LocalProfileWriter"/>, the share-code round trip and the shared-candidate
/// verifier, so all three are pinned to the same traffic.
/// </summary>
internal static class CalibrationTrafficCases
{
    internal const string Build = "2026.09.01.0000.0000";
    internal const ushort Request = 0xC001;
    internal const ushort Reply = 0xC002;
    internal const ushort ZoneInit = 0xA107;
    internal const ushort Territory = 0xA108;
    internal const ushort Job = 0xA109;
    internal const ushort Announce = 0xF00D;
    internal const ushort DecoyTerritory = 0xA20A;

    internal const string ReplyState = "reply-state";
    internal const string ReplyStateMinimal = "reply-state-minimal";
    internal const string Announcement = "announcement";
    internal const string MarkerOffset = "marker-offset";
    internal const string QueueRequest = "queue-request";
    internal const string QueueRequestNoJob = "queue-request-no-job";
    internal const string QueueRequestAnnounced = "queue-request-announced";

    internal static readonly string[] All =
    {
        ReplyState, ReplyStateMinimal, Announcement, MarkerOffset, QueueRequest, QueueRequestNoJob,
        QueueRequestAnnounced,
    };

    private const int DutyTerritory = 1039;
    private const int TownTerritory = 5000;

    /// <summary>Payload length of the timed announcement; it carries nothing readable at all.</summary>
    internal const int AnnouncedLength = 12;

    /// <summary>The traffic of one case, in arrival order.</summary>
    /// <param name="name">Case name.</param>
    internal static IEnumerable<DecodedMessage> Traffic(string name) => (name switch
    {
        ReplyState => CalibrationObserverTests.Session1(),
        ReplyStateMinimal => WithoutExitJob(CalibrationObserverTests.Session1()).Concat(TerritoryDecoys()),
        Announcement => WithoutTheMatch().Concat(SecondQueue()).Append(
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(64, (16, 1)), 120_000)),
        MarkerOffset => WithoutTheMatch().Concat(SecondQueue()).Concat(new[]
        {
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 1)), 120_000),
            CalibrationObserverTests.Message(
                MessageDirection.Inbound, Announce, CalibrationObserverTests.Bytes(24, (8, 2)), 340_000),
        }),
        QueueRequest => WithoutTheMatch().Concat(SecondQueue()),
        QueueRequestNoJob => WithoutExitJob(WithoutTheMatch()).Concat(SecondQueue()),
        QueueRequestAnnounced => WithoutTheMatch()
            .Concat(SecondQueue())
            .Concat(CalibrationObserverTests.Cluster(365_000, DutyTerritory))
            .Concat(CalibrationObserverTests.Cluster(455_000, TownTerritory))
            .Concat(TimedAnnouncements()),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown traffic case"),
    }).OrderBy(message => message.Mono).ToArray();

    /// <summary>Which source the draft must name for a case.</summary>
    /// <param name="name">Case name.</param>
    internal static CalibrationMatchSource Source(string name) => name switch
    {
        ReplyState or ReplyStateMinimal => CalibrationMatchSource.ReplyState,
        Announcement => CalibrationMatchSource.Announcement,
        MarkerOffset => CalibrationMatchSource.MarkerOffset,
        _ => CalibrationMatchSource.QueueRequest,
    };

    /// <summary>The messages a ready draft declares for a case, in declaration order.</summary>
    /// <param name="name">Case name.</param>
    internal static string[] ExpectedMessages(string name) => name switch
    {
        ReplyStateMinimal => new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION" },
        QueueRequestNoJob => new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY" },
        QueueRequestAnnounced => new[]
        {
            "CONTENT_FINDER_POP", "MATCH_ANNOUNCED", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB",
        },
        _ => new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "PLAYER_JOB" },
    };

    /// <summary>Feeds traffic to a fresh observer and freezes it.</summary>
    /// <param name="traffic">Messages stamped with the shared helper session.</param>
    internal static CalibrationSnapshot Observe(IEnumerable<DecodedMessage> traffic)
    {
        var observer = new CalibrationObserver(CalibrationObserverTests.Template(), Region.Cn, "calibration-session");
        foreach (var message in traffic)
        {
            observer.Accept(message);
        }

        observer.Flush();
        return observer.Snapshot();
    }

    /// <summary>The draft one case derives.</summary>
    /// <param name="name">Case name.</param>
    internal static CalibrationDraft Derive(string name) =>
        CalibrationDraft.Derive(Observe(Traffic(name)), CalibrationObserverTests.Template());

    /// <summary>Session1 without its match message: a build whose announcement nobody has found.</summary>
    internal static IEnumerable<DecodedMessage> WithoutTheMatch() =>
        CalibrationObserverTests.Session1()
            .Where(message => !(message.Opcode == Reply && message.Mono == TimeSpan.FromMilliseconds(120_000)));

    /// <summary>A second roulette queued and echoed, so the reply opcode can be locked at all.</summary>
    /// <param name="roulette">Roulette id of the second queue.</param>
    /// <param name="at">Session time of the request.</param>
    internal static IEnumerable<DecodedMessage> SecondQueue(byte roulette = 2, long at = 300_000)
    {
        yield return CalibrationObserverTests.Message(
            MessageDirection.Outbound, Request, CalibrationObserverTests.Bytes(24, (0, roulette)), at);
        yield return CalibrationObserverTests.Message(
            MessageDirection.Inbound, Reply, CalibrationObserverTests.Bytes(40, (9, 5), (16, roulette)), at + 120);
    }

    /// <summary>
    /// The duty-exit burst without its job messages. The entry and exit bursts must both carry the
    /// job for PLAYER_JOB to be declared, so dropping the exit job is the minimal way to withhold
    /// it.
    /// </summary>
    private static IEnumerable<DecodedMessage> WithoutExitJob(IEnumerable<DecodedMessage> traffic) =>
        traffic.Where(message => !(message.Opcode == Job &&
            message.Mono >= TimeSpan.FromMilliseconds(215_000) && message.Mono < TimeSpan.FromMilliseconds(216_000)));

    /// <summary>
    /// The server announcing each of the two matches, three times over as the CN 2026.09.15
    /// client does. The payload says nothing: twelve zero bytes, no roulette id anywhere in it,
    /// which is exactly why no search by value can find this message.
    /// </summary>
    private static IEnumerable<DecodedMessage> TimedAnnouncements()
    {
        foreach (var at in new long[] { 118_000, 118_100, 118_200, 358_000, 358_100, 358_200 })
        {
            yield return CalibrationObserverTests.Message(
                MessageDirection.Inbound, Announce, new byte[AnnouncedLength], at);
        }
    }

    /// <summary>
    /// A second territory-shaped message in every burst, carrying the same territory as the real
    /// one (town, duty, town). The duty is still recognised exactly once, but no single opcode
    /// can be written as THE territory message.
    /// </summary>
    private static IEnumerable<DecodedMessage> TerritoryDecoys() => new[]
    {
        CalibrationObserverTests.Message(
            MessageDirection.Inbound, DecoyTerritory, CalibrationObserverTests.Bytes(136, (2, 0x88), (3, 0x13)), 5_010),
        CalibrationObserverTests.Message(
            MessageDirection.Inbound, DecoyTerritory, CalibrationObserverTests.Bytes(136, (2, 15), (3, 4)), 125_010),
        CalibrationObserverTests.Message(
            MessageDirection.Inbound, DecoyTerritory, CalibrationObserverTests.Bytes(136, (2, 0x88), (3, 0x13)), 215_010),
    };
}
