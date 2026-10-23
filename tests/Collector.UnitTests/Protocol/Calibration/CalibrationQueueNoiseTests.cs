using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-03, OCal-1. The CN client's movement packet is a 24-byte client message, the
/// queue request's exact shape, sent about fifty times a second, and its first byte - where the
/// request carries the roulette - is the low byte of a rotation, so roughly one in twenty-five of
/// them starts with a roulette id. Every one of those used to stand as the player's queue: the
/// marker scan compared each announcement against a random roulette, the timing table counted the
/// whole town as "seen while queueing", and a real match that came within a second of one was
/// filed as the server's reply to it.
///
/// The synthetic traffic's movement packets start with 200, which is no roulette at all, so every
/// evening here is played twice: as it is, and with every movement packet starting with a roulette
/// id. Nothing the queue decides may come out differently.
/// </summary>
public sealed class CalibrationQueueNoiseTests
{
    private const ushort Movement = 0xD001;

    /// <summary>The same traffic with every movement packet starting with <paramref name="roulette"/>.</summary>
    private static IEnumerable<DecodedMessage> MovingWith(IEnumerable<DecodedMessage> traffic, byte roulette) =>
        traffic.Select(message =>
        {
            if (message.Opcode != Movement || message.Direction != MessageDirection.Outbound)
            {
                return message;
            }

            var payload = message.Payload.ToArray();
            payload[0] = roulette;
            return message with { Payload = payload };
        });

    private static string Markers(CalibrationSnapshot snapshot) => string.Join(" ", snapshot.Markers
        .OrderBy(marker => marker.Opcode).ThenBy(marker => marker.Length).ThenBy(marker => marker.Offset)
        .Select(marker => $"{marker.Opcode:x4}:{marker.Length}@{marker.Offset}={marker.Hits}" +
                          $"[{string.Join(",", marker.RouletteIds)}]" +
                          $"({string.Join(",", marker.Sightings.Select(sighting => sighting.TMs + "/" + sighting.RouletteId))})")
        .Concat(snapshot.MarkerShapeTotals.OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key.Opcode:x4}:{entry.Key.Length}#{entry.Value}"))
        .Append("overflow=" + snapshot.MarkerOverflow));

    private static string Timing(CalibrationSnapshot snapshot) => string.Join(" ", snapshot.TimedShapes
        .Select(shape => $"{shape.Opcode:x4}:{shape.Length}={shape.Total}/{shape.InQueue}+{shape.PreDuty}!{shape.Stray}")
        .Concat(snapshot.TimedDead.OrderBy(dead => dead).Select(dead => $"dead {dead.Opcode:x4}:{dead.Length}"))
        .Append("overflow=" + snapshot.TimingOverflow));

    private static string Echoes(CalibrationSnapshot snapshot) => string.Join(" ", snapshot.Pops
        .Select(pop => $"{pop.Opcode:x4}@{pop.TMs}:{pop.RouletteId}{(pop.WithinEcho ? "echo" : "")}")
        .Concat(snapshot.Pairs.Select(pair => $"pair {pair.RequestOpcode:x4}->{pair.ReplyOpcode:x4}@{pair.ReplyTMs}"))
        .Concat(snapshot.RouletteEchoHits.Select(hit => $"hit {hit.Opcode:x4}:{hit.Length}@{hit.TMs}")));

    private static string Draft(CalibrationDraft draft) =>
        $"{draft.Status} {draft.MatchSource} " +
        string.Join(",", draft.Messages.Select(message => $"{message.Name}={message.Opcode:x4}")) +
        (draft.TimedAnnouncement is { } timed ? $" timed={timed.Shape.Opcode:x4}" : string.Empty);

    /// <summary>
    /// Every way a draft can name the match, each played with movement that starts with the id of a
    /// roulette the evening queues (1), one it queues later (2), and one it never queues (5).
    /// </summary>
    [Theory]
    [InlineData(CalibrationTrafficCases.ReplyState, 1)]
    [InlineData(CalibrationTrafficCases.ReplyState, 5)]
    [InlineData(CalibrationTrafficCases.MarkerOffset, 2)]
    [InlineData(CalibrationTrafficCases.MarkerOffset, 5)]
    [InlineData(CalibrationTrafficCases.QueueRequestAnnounced, 1)]
    [InlineData(CalibrationTrafficCases.QueueRequestAnnounced, 5)]
    public void MovementStartingWithARouletteIdChangesNothingTheQueueDecides(string name, byte roulette)
    {
        var quiet = CalibrationTrafficCases.Observe(CalibrationTrafficCases.Traffic(name));
        var noisy = CalibrationTrafficCases.Observe(MovingWith(CalibrationTrafficCases.Traffic(name), roulette));

        Assert.Equal(Markers(quiet), Markers(noisy));
        Assert.Equal(Timing(quiet), Timing(noisy));
        Assert.Equal(Echoes(quiet), Echoes(noisy));
        Assert.Equal(quiet.OverflowCount, noisy.OverflowCount);

        var template = CalibrationObserverTests.Template();
        Assert.Equal(Draft(CalibrationDraft.Derive(quiet, template)), Draft(CalibrationDraft.Derive(noisy, template)));
    }

    /// <summary>
    /// The second half of the defect, on its own: the match arrives half a second after a movement
    /// packet that happens to carry the queued roulette. That packet is not a request, so the match
    /// is not the server answering one, and the draft still finds it.
    /// </summary>
    [Fact]
    public void AMatchRightAfterAMovementPacketCarryingTheQueuedIdIsStillAMatch()
    {
        var snapshot = CalibrationTrafficCases.Observe(
            MovingWith(CalibrationObserverTests.Session1(), 1).OrderBy(message => message.Mono));

        var match = Assert.Single(snapshot.Pops, pop => pop.TMs == 120_000);
        Assert.False(match.WithinEcho);
        Assert.All(snapshot.Pairs, pair => Assert.Equal(CalibrationTrafficCases.Request, pair.RequestOpcode));
        var draft = CalibrationDraft.Derive(snapshot, CalibrationObserverTests.Template());
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.ReplyState, draft.MatchSource);
    }

    /// <summary>
    /// What a shared code's verifier counts: a pop that came while the player's own queue for its
    /// roulette stood, or one carrying a roulette nobody asked for. A movement packet carrying
    /// another roulette is not the queue, so the real match still counts as answering it.
    /// </summary>
    [Fact]
    public void MovementStartingWithARouletteIdChangesNothingASharedCodeIsCountedOn()
    {
        string Counts(IEnumerable<DecodedMessage> traffic)
        {
            var observer = new CalibrationObserver(
                CalibrationObserverTests.Template(), Domain.Region.Cn, "calibration-session");
            observer.RegisterCandidate(CalibrationObserverSharedStateTests.Candidate(
                "true", CalibrationObserverSharedStateTests.TrueValues));
            foreach (var message in traffic.OrderBy(message => message.Mono))
            {
                observer.Accept(message);
            }

            observer.Flush();
            return string.Join(" ", observer.Snapshot().Candidates.SelectMany(candidate => candidate.Sessions.Values)
                .Select(counts => $"{counts.PopWithRequest}/{counts.PopWithoutRequest} " +
                                  string.Join(",", counts.Sightings.Select(sighting => $"{sighting.RouletteId}{(sighting.WithRequest ? "+" : "-")}"))));
        }

        var traffic = CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState).ToArray();

        Assert.Equal(Counts(traffic), Counts(MovingWith(traffic, 5)));
    }

    /// <summary>
    /// The client's movement packet outnumbers any real queue request by thousands to one. Once an
    /// opcode has been seen more often than a request ever is, a coincidence with it can neither
    /// pair with the server's echo nor stand as the queue - even when it is the first thing that
    /// ever paired on this machine, which would otherwise lock the true request out for good.
    /// </summary>
    [Fact]
    public void AChattyClientOpcodeThatPairedFirstCannotHoldTheQueueRequestOut()
    {
        var chatter = Enumerable.Range(0, CalibrationDraft.MaxCandidateOccurrences + 1)
            .Select(index => CalibrationObserverTests.Message(
                MessageDirection.Outbound, Movement, CalibrationObserverTests.Bytes(24, (0, 200)), 70_000 + index * 20));
        var traffic = CalibrationObserverTests.Session1()
            // A movement packet carrying the queued roulette between the request and its echo.
            .Append(CalibrationObserverTests.Message(
                MessageDirection.Outbound, Movement, CalibrationObserverTests.Bytes(24, (0, 1)), 60_050))
            .Concat(chatter)
            .Concat(CalibrationTrafficCases.SecondQueue())
            .Concat(CalibrationTrafficCases.SecondQueue(roulette: 3, at: 330_000))
            .OrderBy(message => message.Mono);

        var snapshot = CalibrationTrafficCases.Observe(traffic);

        Assert.Equal(2, snapshot.Pairs.Count(pair => pair.RequestOpcode == CalibrationTrafficCases.Request));
        Assert.Equal(CalibrationTrafficCases.Request, CalibrationDraft.DominantRequest(snapshot.Pairs));
    }
}
