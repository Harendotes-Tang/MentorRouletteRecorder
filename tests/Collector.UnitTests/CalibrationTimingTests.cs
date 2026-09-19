using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What the observer learns about a server shape from <em>when</em> it arrives rather than from
/// what it carries.
///
/// On the CN 2026.09.15 client the message announcing a match carries no roulette id anywhere in
/// its payload, so every search by value comes back empty and the player gets no spoken
/// "匹配成功" when the popup appears. The one thing that message still does is arrive at a
/// particular moment: only while the player has a queue standing, before every duty they enter,
/// and never inside a zone load. These counts are what lets the draft say so.
/// </summary>
public sealed class CalibrationTimingTests
{
    private const ushort Announce = 0xF00D;
    private const ushort Request = 0xC001;
    private const ushort Reply = 0xC002;
    private const int DutyTerritory = 1039;
    private const int TownTerritory = 5000;

    /// <summary>A server message of the announcement's shape: twelve bytes that say nothing.</summary>
    /// <param name="t">Session time in milliseconds.</param>
    /// <param name="opcode">Opcode to send it on.</param>
    private static DecodedMessage Announcement(long t, ushort opcode = Announce) =>
        CalibrationObserverTests.Message(MessageDirection.Inbound, opcode, new byte[12], t);

    private static CalibrationSnapshot Observe(params IEnumerable<DecodedMessage>[] parts) =>
        CalibrationTrafficCases.Observe(parts.SelectMany(part => part).OrderBy(message => message.Mono));

    private static TimedShape? Shape(CalibrationSnapshot snapshot, ushort opcode) =>
        snapshot.TimedShapes.FirstOrDefault(shape => shape.Opcode == opcode);

    /// <summary>Login, one queue, one duty entry and its exit; the announcement is added per test.</summary>
    private static IEnumerable<DecodedMessage> Evening() =>
        CalibrationObserverTests.Cluster(5_000, TownTerritory)
            .Concat(CalibrationObserverTests.QueueAndPop(60_000, 1, 120_000))
            .Concat(CalibrationObserverTests.Cluster(125_000, DutyTerritory))
            .Concat(CalibrationObserverTests.Cluster(215_000, TownTerritory));

    /// <summary>
    /// The whole point of the table: a shape that only ever travels while the player is waiting
    /// in a queue. Nothing about its payload is kept, only that it happened and when.
    /// </summary>
    [Fact]
    public void AShapeSeenOnlyWhileAQueueStandsIsCountedAsInQueue()
    {
        var snapshot = Observe(Evening(), new[] { Announcement(118_000) });

        var shape = Shape(snapshot, Announce);
        Assert.NotNull(shape);
        Assert.Equal(1, shape!.Total);
        Assert.Equal(1, shape.InQueue);
        Assert.Equal(0, shape.PreDuty);
        Assert.Equal(0, shape.Stray);
        Assert.Equal(0, snapshot.TimingOverflow);
    }

    /// <summary>
    /// An instant match pops less than a second after the request, inside the window the server
    /// answers the request in. The marker scan skips that window on purpose - what arrives there
    /// is the echo the pairing already knows - but timing must not, or every instant pop would be
    /// filed as a sighting the announcement failed to produce.
    /// </summary>
    [Fact]
    public void ASightingInsideTheReplyWindowStillCountsAsInQueue()
    {
        var snapshot = Observe(Evening(), new[] { Announcement(60_500) });

        Assert.Equal(1, Shape(snapshot, Announce)!.InQueue);
    }

    /// <summary>
    /// The player queued for nothing that evening but was brought into a duty by a party member:
    /// the client sends no request of its own, and the announcement arrives anyway. Counting that
    /// as a stray sighting would kill the true announcement on any player who runs with friends.
    /// </summary>
    [Fact]
    public void ASightingWithNoRequestThatADutyFollowsCountsAsPreDuty()
    {
        var snapshot = Observe(
            Evening(),
            new[] { Announcement(118_000), Announcement(300_000) },
            CalibrationObserverTests.Cluster(305_000, DutyTerritory));

        var shape = Shape(snapshot, Announce)!;
        Assert.Equal(2, shape.Total);
        Assert.Equal(1, shape.InQueue);
        Assert.Equal(1, shape.PreDuty);
        Assert.Equal(0, shape.Stray);
    }

    /// <summary>
    /// The same sighting with no duty behind it is a stray, and a shape whose strays outweigh a
    /// fifth of its sightings is out for good: the announcement of a match is not sent to a player
    /// who is neither queueing nor about to load into anything.
    /// </summary>
    [Fact]
    public void AStraySightingRetiresAShapeAndItStaysRetired()
    {
        var snapshot = Observe(
            Evening(),
            new[] { Announcement(118_000), Announcement(300_000) },
            // Far enough past the stray for the wait to have run out, and then one more sighting
            // that must not bring the shape back.
            CalibrationObserverTests.Noise(500_000, 520_000),
            new[] { Announcement(600_000) });

        Assert.Null(Shape(snapshot, Announce));
        Assert.Contains((Announce, 12), snapshot.TimedDead);
    }

    /// <summary>
    /// Everything a zone load brings with it arrives inside the load. Counting those would put
    /// every message of the loading screen in the table and let one of them claim to have
    /// announced the match that preceded it.
    /// </summary>
    [Fact]
    public void ASightingInsideAZoneLoadIsNotCounted()
    {
        var snapshot = Observe(
            Evening(),
            new[] { Announcement(118_000), Announcement(125_300) });

        Assert.Equal(1, Shape(snapshot, Announce)!.Total);
    }

    /// <summary>
    /// The server's answer to the queue request travels on the opcode the request/echo pairing
    /// already named, and it arrives while the queue stands every single time. It is the one
    /// shape guaranteed to look like the announcement, and it is not one.
    /// </summary>
    [Fact]
    public void TheQueueReplyOpcodeIsNeverATimedShape()
    {
        var snapshot = Observe(Evening(), new[] { Announcement(118_000) });

        Assert.DoesNotContain(snapshot.TimedShapes, shape => shape.Opcode == Reply);
        Assert.DoesNotContain(snapshot.TimedShapes, shape => shape.Opcode == Request);
    }

    /// <summary>
    /// A shape first met outside every queue is ordinary traffic, and a client sends hundreds of
    /// kinds of it. They are not admitted at all, which is what keeps the table small enough to
    /// be bounded without overflowing on a busy evening in a capital city.
    /// </summary>
    [Fact]
    public void AShapeFirstSeenOutsideEveryQueueIsNeverTracked()
    {
        var snapshot = Observe(
            Evening(),
            new[] { Announcement(30_000), Announcement(118_000) });

        var shape = Shape(snapshot, Announce);
        Assert.NotNull(shape);
        Assert.Equal(1, shape!.Total);
    }

    /// <summary>
    /// A full table is reported rather than quietly treated as a smaller sample: the draft's claim
    /// is "this shape appeared before every entry", and a shape the table could not take cannot
    /// support or refute it.
    /// </summary>
    [Fact]
    public void AFullTableIsReportedAsAnOverflow()
    {
        var flood = Enumerable.Range(0, CalibrationObserver.MaxTimingShapes + 40)
            .Select(index => Announcement(70_000 + index, (ushort)(0xE000 + index)));
        var snapshot = Observe(Evening(), flood);

        Assert.True(snapshot.TimedShapes.Count <= CalibrationObserver.MaxTimingShapes);
        Assert.True(snapshot.TimingOverflow > 0);
    }

    /// <summary>
    /// A stuck calibration has to be readable by a maintainer holding nothing but the report.
    /// One row per surviving shape says how it behaved, how many of the evening's duties it came
    /// before, and by how little - which is what separates the popup from whatever the client
    /// sends as the loading screen starts.
    /// </summary>
    [Fact]
    public void TheReportNamesEveryTimedCandidateAndItsLead()
    {
        var snapshot = CalibrationTrafficCases.Observe(
            CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced));
        var evidence = CalibrationEvidenceSummary.From(snapshot, CalibrationObserverTests.Template());

        Assert.Equal(0, evidence.TimingOverflow);
        var row = Assert.Single(evidence.TimedCandidates!, text =>
            text.StartsWith("0xf00d:12=", StringComparison.Ordinal));
        Assert.Contains("=6+0/6!0", row, StringComparison.Ordinal);
        Assert.Contains("e2/2", row, StringComparison.Ordinal);
        Assert.Contains("lead7s", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// A match is announced three or four times in a row on this client. That is one popup, and
    /// the shape must survive it: every repeat is in-queue, none of them is a stray.
    /// </summary>
    [Fact]
    public void RepeatedAnnouncementsOfOneMatchAreAllInQueue()
    {
        var snapshot = Observe(
            Evening(),
            new[] { Announcement(118_000), Announcement(118_100), Announcement(118_200) });

        var shape = Shape(snapshot, Announce)!;
        Assert.Equal(3, shape.Total);
        Assert.Equal(3, shape.InQueue);
        Assert.Equal(3, shape.Sightings.Count);
        Assert.True(shape.SightingsComplete);
    }
}
