using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Recognising the message that announces a match by <em>when</em> it arrives, on a build where
/// no search by value can find it.
///
/// On the CN 2026.09.15 client the announcement carries the roulette id at no offset at all, so
/// the three value-based paths come back empty and the profile stands the player's own queue
/// request in for the announcement. That profile records the run correctly and says nothing when
/// the popup appears, which is what the speech was for. This last path leaves the roulette to the
/// queue request, exactly as before, and takes only the moment from the server: a message that
/// arrives before every duty the player queued into, inside the match window, never inside a zone
/// load, and never when nothing was queued.
/// </summary>
public sealed class TimedAnnouncementDraftTests
{
    private const ushort Announce = CalibrationTrafficCases.Announce;
    private const ushort SecondAnnounce = 0xF00E;
    private const int DutyTerritory = 1039;

    private static DecodedMessage Sighting(long t, ushort opcode = Announce, int length = 12) =>
        CalibrationObserverTests.Message(MessageDirection.Inbound, opcode, new byte[length], t);

    private static CalibrationDraft Derive(
        IEnumerable<DecodedMessage> traffic, CalibrationRejections? rejections = null) =>
        CalibrationDraft.Derive(
            CalibrationTrafficCases.Observe(traffic.OrderBy(message => message.Mono)),
            CalibrationObserverTests.Template(),
            rejections);

    /// <summary>The evening that names the announcement, with its sightings left out.</summary>
    private static IEnumerable<DecodedMessage> WithoutSightings() =>
        CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Where(message => message.Opcode != Announce);

    /// <summary>
    /// The whole feature in one case: two duties, queued for two different roulettes, each
    /// preceded by the same shape. The draft still infers the roulette from the request - nothing
    /// in the announcement says which duty it is - and gains one message that says when.
    /// </summary>
    [Fact]
    public void AShapeThatPrecedesEveryQueuedEntryIsTheAnnouncement()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced));

        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.NotNull(draft.TimedAnnouncement);
        Assert.Equal(Announce, draft.TimedAnnouncement!.Shape.Opcode);
        Assert.Equal(12, draft.TimedAnnouncement.Shape.Length);

        var announced = draft.Messages.Single(message => message.Name == "MATCH_ANNOUNCED");
        Assert.Equal(Announce, announced.Opcode);
        Assert.Equal(12, announced.ExpectedLength);
        Assert.Equal(PacketDirection.ServerToClient, announced.Direction);
        Assert.Empty(announced.Fields);

        // The pop is still the player's own request travelling the other way: the announcement
        // is an add-on, never the thing that names the roulette.
        var pop = draft.Messages.Single(message => message.Name == "CONTENT_FINDER_POP");
        Assert.Equal(PacketDirection.ClientToServer, pop.Direction);
    }

    /// <summary>
    /// The user confirms what they saw, not an opcode: one line per popup, named after the
    /// roulette the request asked for and marked as recognised by its timing.
    /// </summary>
    [Fact]
    public void EverySupportingSightingBecomesALineToConfirm()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced));
        var pops = draft.Events.Where(item => item.Kind == "pop").ToArray();

        Assert.Equal(draft.TimedAnnouncement!.Samples.Count, pops.Length);
        Assert.All(pops, item => Assert.True(item.RequiresConfirmation));
        Assert.All(pops, item => Assert.Contains("按出现时机认出", item.Label, StringComparison.Ordinal));
        Assert.All(pops, item => Assert.DoesNotContain("0x", item.Label, StringComparison.Ordinal));
        Assert.Contains(pops, item => item.Label.Contains("练级迷宫", StringComparison.Ordinal));
        Assert.Equal(pops.Length, pops.Select(item => item.EventId).Distinct().Count());
    }

    /// <summary>
    /// One duty proves nothing. A shape that happened to arrive before the only entry of the
    /// evening is every shape that was travelling at the time.
    /// </summary>
    [Fact]
    public void OneEntryIsNotEnough()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequest)
            .Concat(new[] { Sighting(118_000) }));

        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.Null(draft.TimedAnnouncement);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "MATCH_ANNOUNCED");
    }

    /// <summary>
    /// Two entries on one roulette are not two tests either: a message the client only sends
    /// while that particular roulette is queued would pass both of them and then never fire
    /// again for the rest of the build's life.
    /// </summary>
    [Fact]
    public void TwoEntriesOnOneRouletteAreNotTwoTests()
    {
        var draft = Derive(WithoutSightings()
            // The second queue, for the same roulette as the first. A third one that never
            // matched keeps the request/echo pairing itself decided, which needs two values.
            .Where(message => message.Mono < TimeSpan.FromMilliseconds(300_000) ||
                message.Mono > TimeSpan.FromMilliseconds(300_200))
            .Concat(CalibrationTrafficCases.SecondQueue(roulette: 3, at: 250_000))
            .Concat(CalibrationTrafficCases.SecondQueue(roulette: 1, at: 300_000))
            .Concat(new[] { Sighting(118_000), Sighting(358_000) }));

        Assert.Null(draft.TimedAnnouncement);
    }

    /// <summary>
    /// A match is announced three or four times in a row on this client, and that is still one
    /// popup. A shape that fires dozens of times inside one queue is a status update the finder
    /// sends while the player waits, and dating a match by it would put the match at the moment
    /// the player queued.
    /// </summary>
    [Fact]
    public void AShapeThatFiresThroughoutTheQueueIsNotAnAnnouncement()
    {
        var chatter = Enumerable.Range(0, 12).Select(index => Sighting(100_000 + (index * 1_000)))
            .Concat(Enumerable.Range(0, 12).Select(index => Sighting(340_000 + (index * 1_000))));
        var draft = Derive(WithoutSightings().Concat(chatter));

        Assert.Null(draft.TimedAnnouncement);
    }

    /// <summary>
    /// A message that belongs to a zone load travels in every load - teleports, logins, duties -
    /// and one of them always precedes a duty entry. Membership of any burst rules it out
    /// whatever a particular sighting did, which is the same rule the value-based paths use.
    /// </summary>
    [Fact]
    public void AShapeThatAlsoTravelsInsideAZoneLoadIsNotAnAnnouncement()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(new[] { Sighting(125_500), Sighting(365_500) }));

        Assert.Null(draft.TimedAnnouncement);
    }

    /// <summary>
    /// The announcement is not sent to a player who is neither queueing nor about to load into
    /// anything, so one stray sighting retires the shape - and it stays retired, because the
    /// evidence outlives the session it was collected in.
    /// </summary>
    [Fact]
    public void AShapeSeenWithNoQueueAndNoDutyBehindItIsNotAnAnnouncement()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(new[] { Sighting(200_000), Sighting(205_000) }));

        Assert.Null(draft.TimedAnnouncement);
    }

    /// <summary>
    /// Two shapes can both precede every entry - the announcement, and whatever the client sends
    /// as the loading screen begins. The popup comes first, so the one that leads by the most in
    /// its worst window wins.
    /// </summary>
    [Fact]
    public void TheShapeThatLeadsByTheMostWins()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(new[]
            {
                Sighting(121_000, SecondAnnounce, 16),
                Sighting(361_000, SecondAnnounce, 16),
            }));

        Assert.Equal(Announce, draft.TimedAnnouncement!.Shape.Opcode);
    }

    /// <summary>
    /// Tied, nothing is written. Picking one of two shapes that behave identically is a coin
    /// toss, and the cost of losing it is a run recorded from a message that means something else.
    /// </summary>
    [Fact]
    public void ATieIsNotResolvedByGuessing()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(new[]
            {
                Sighting(118_000, SecondAnnounce, 16),
                Sighting(358_000, SecondAnnounce, 16),
            }));

        Assert.Null(draft.TimedAnnouncement);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
    }

    /// <summary>
    /// A full timing table means some shape never got a row, and the claim being made is about
    /// every shape there was. Better no announcement than one chosen out of a partial table.
    /// </summary>
    [Fact]
    public void AFullTimingTableNamesNoAnnouncement()
    {
        var flood = Enumerable.Range(0, CalibrationObserver.MaxTimingShapes + 40)
            .Select(index => Sighting(70_000 + index, (ushort)(0xE000 + index)));
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(flood));

        Assert.Null(draft.TimedAnnouncement);
    }

    /// <summary>
    /// The player marked the popup line wrong: that opcode is not the announcement, and it is not
    /// offered again. The queue request underneath it was a separate line and a separate answer,
    /// so the profile they already record with is untouched.
    /// </summary>
    [Fact]
    public void ARejectedAnnouncementIsNotProposedAgainAndKeepsTheQueueRequest()
    {
        var rejections = CalibrationRejections.None with
        {
            TimedOpcodes = new HashSet<ushort> { Announce },
        };
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced), rejections);

        Assert.Null(draft.TimedAnnouncement);
        Assert.Equal(CalibrationDraftStatus.Ready, draft.Status);
        Assert.Equal(CalibrationMatchSource.QueueRequest, draft.MatchSource);
        Assert.Contains(draft.Messages, message => message.Name == "CONTENT_FINDER_POP");
    }

    /// <summary>
    /// The timing path is the last resort it was designed to be. A build whose queue reply still
    /// carries the match is read the way it always was, and nothing is added to that profile.
    /// </summary>
    [Fact]
    public void ABuildWhoseReplyCarriesTheMatchGainsNothing()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState)
            .Concat(new[] { Sighting(118_000) }));

        Assert.Equal(CalibrationMatchSource.ReplyState, draft.MatchSource);
        Assert.Null(draft.TimedAnnouncement);
        Assert.DoesNotContain(draft.Messages, message => message.Name == "MATCH_ANNOUNCED");
    }

    /// <summary>
    /// A duty the player was brought into by a party member has no request of their own behind
    /// it, so it cannot test anything: the software does not know what was queued. It must not
    /// count against the shape either, which is what the pre-duty classification is for.
    /// </summary>
    [Fact]
    public void ADutyEnteredWithoutAQueueOfOnesOwnNeitherProvesNorDisproves()
    {
        var draft = Derive(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.QueueRequestAnnounced)
            .Concat(new[] { Sighting(600_000) })
            .Concat(CalibrationObserverTests.Cluster(605_000, DutyTerritory, connection: "zone")));

        Assert.NotNull(draft.TimedAnnouncement);
        Assert.Equal(Announce, draft.TimedAnnouncement!.Shape.Opcode);
    }
}
