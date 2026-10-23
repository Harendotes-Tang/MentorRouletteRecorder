using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Plan §20.5: the codes being verified for one build vote on each field they declare. The zone marker, the
/// territory and the job are compared across match sources; the match itself only among codes that name it the
/// same way. A field a code does not declare neither votes nor disputes. A code is preferred when it carries the
/// value with strictly the most submitters on every field in dispute, or - on the match only - when the top is
/// tied and it alone among the tied values has the shape prior this machine computed from its own verified profiles
/// (never the index's). Pure: every case is a list in, a verdict out.
/// </summary>
public sealed class SharedCandidateConsensusTests
{
    private const ushort Zone = 0xA107;
    private const ushort OtherZone = 0xA1F7;

    private static readonly ShareCodePop MarkerPop = new(599, Length: 24, RouletteOffset: 2);
    private static readonly ShareCodePop OtherMarkerPop = new(650, Length: 40, RouletteOffset: 2);
    private static readonly ShareCodePop ReplyPop = new(0xC002, SelectorValues: new long[] { 7 });

    private static ShareCodePayload Payload(
        CalibrationMatchSource source, ShareCodePop pop, ushort zone = Zone, ushort? territory = 0xA108, ushort? job = 0xA109) =>
        ShareCodeTests.Payload(source, pop, territory, job) with { ZoneOpcode = zone };

    private static SharedConsensusCandidate Code(
        string sha, ShareCodePayload payload, int submitters = 1, bool? localPrior = null) =>
        new(sha, payload, submitters, localPrior);

    private static IReadOnlyDictionary<string, SharedConsensus> Judge(params SharedConsensusCandidate[] codes) =>
        SharedCandidateConsensus.Judge(codes);

    [Fact]
    public void ALoneCodeIsUndisputedAndPreferred()
    {
        var verdict = Assert.Single(Judge(Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop))));

        Assert.Equal("a", verdict.Key);
        Assert.Empty(verdict.Value.Disputed);
        Assert.True(verdict.Value.Preferred);
        Assert.Empty(SharedCandidateConsensus.Judge(Array.Empty<SharedConsensusCandidate>()));
        Assert.Empty(SharedConsensus.Undisputed.Disputed);
        Assert.True(SharedConsensus.Undisputed.Preferred);
    }

    [Fact]
    public void TheFieldsAreNamedAsTheMessagesTheyDeclareInAFixedOrder()
    {
        Assert.Equal(
            new[] { CalibratedShape.ZoneName, CalibratedShape.TerritoryName, CalibratedShape.JobName, CalibratedShape.PopName },
            SharedCandidateConsensus.Fields);

        var verdicts = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop, zone: OtherZone, territory: 0xB108, job: 0xB109)));

        Assert.Equal(SharedCandidateConsensus.Fields, verdicts["a"].Disputed);
        Assert.Equal(SharedCandidateConsensus.Fields, verdicts["b"].Disputed);
    }

    [Fact]
    public void ANullFieldNeitherVotesNorDisputes()
    {
        var verdicts = Judge(
            Code("bare", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, territory: null, job: null)),
            Code("one", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, job: 0xA109)),
            Code("two", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, job: 0xB109), submitters: 2));

        // The code without a job is not in the job dispute, and does not side with either job.
        Assert.Empty(verdicts["bare"].Disputed);
        Assert.True(verdicts["bare"].Preferred);
        Assert.Equal(new[] { CalibratedShape.JobName }, verdicts["one"].Disputed);
        Assert.False(verdicts["one"].Preferred);
        Assert.Equal(new[] { CalibratedShape.JobName }, verdicts["two"].Disputed);
        Assert.True(verdicts["two"].Preferred);

        // Against a code that declares nothing there, a declared value has no rival.
        var lone = Judge(
            Code("bare", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, job: null)),
            Code("job", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, job: 0xB109)));
        Assert.All(lone.Values, verdict => Assert.Empty(verdict.Disputed));
        Assert.All(lone.Values, verdict => Assert.True(verdict.Preferred));
    }

    [Fact]
    public void TheZoneMarkerIsComparedAcrossMatchSources()
    {
        var verdicts = Judge(
            Code("reply", Payload(CalibrationMatchSource.ReplyState, ReplyPop), submitters: 2),
            Code("marker", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, zone: OtherZone)));

        Assert.Equal(new[] { CalibratedShape.ZoneName }, verdicts["reply"].Disputed);
        Assert.Equal(new[] { CalibratedShape.ZoneName }, verdicts["marker"].Disputed);
        Assert.True(verdicts["reply"].Preferred);
        Assert.False(verdicts["marker"].Preferred);
    }

    [Fact]
    public void TheMatchIsOnlyComparedAmongCodesThatNameItTheSameWay()
    {
        var verdicts = Judge(
            Code("first", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
            Code("second", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop)),
            Code("reply", Payload(CalibrationMatchSource.ReplyState, ReplyPop)),
            Code("queue", Payload(CalibrationMatchSource.QueueRequest, new ShareCodePop(0xC001))));

        Assert.Equal(new[] { CalibratedShape.PopName }, verdicts["first"].Disputed);
        Assert.Equal(new[] { CalibratedShape.PopName }, verdicts["second"].Disputed);
        Assert.Empty(verdicts["reply"].Disputed);
        Assert.Empty(verdicts["queue"].Disputed);
        Assert.True(verdicts["reply"].Preferred);
        Assert.True(verdicts["queue"].Preferred);
    }

    [Fact]
    public void EveryPartOfThePopTakesPartAndSelectorValuesAreComparedByValue()
    {
        var sameSelectors = Judge(
            Code("a", Payload(CalibrationMatchSource.ReplyState, new ShareCodePop(0xC002, SelectorValues: new long[] { 7, 3 }))),
            Code("b", Payload(CalibrationMatchSource.ReplyState, new ShareCodePop(0xC002, SelectorValues: new List<long> { 7, 3 }), job: null)));
        Assert.All(sameSelectors.Values, verdict => Assert.Empty(verdict.Disputed));

        foreach (var other in new[]
                 {
                     new ShareCodePop(0xC002, SelectorValues: new long[] { 3, 7 }),
                     new ShareCodePop(0xC002, SelectorValues: new long[] { 7 }),
                     new ShareCodePop(0xC003, SelectorValues: new long[] { 7, 3 }),
                 })
        {
            var verdicts = Judge(
                Code("a", Payload(CalibrationMatchSource.ReplyState, new ShareCodePop(0xC002, SelectorValues: new long[] { 7, 3 }))),
                Code("b", Payload(CalibrationMatchSource.ReplyState, other)));
            Assert.Equal(new[] { CalibratedShape.PopName }, verdicts["a"].Disputed);
        }

        foreach (var other in new[] { MarkerPop with { Length = 25 }, MarkerPop with { RouletteOffset = 3 }, MarkerPop with { Opcode = 600 } })
        {
            var verdicts = Judge(
                Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
                Code("b", Payload(CalibrationMatchSource.MarkerOffset, other)));
            Assert.Equal(new[] { CalibratedShape.PopName }, verdicts["b"].Disputed);
        }
    }

    [Fact]
    public void AValueIsVotedForByTheSubmittersOfEveryCodeCarryingIt()
    {
        // Two codes agree on the pop and disagree on the zone; a third has the other pop and the first zone.
        var verdicts = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, zone: OtherZone)),
            Code("c", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop)));

        Assert.Equal(new[] { CalibratedShape.ZoneName, CalibratedShape.PopName }, verdicts["a"].Disputed);
        Assert.True(verdicts["a"].Preferred);
        Assert.Equal(new[] { CalibratedShape.ZoneName, CalibratedShape.PopName }, verdicts["b"].Disputed);
        Assert.False(verdicts["b"].Preferred);
        Assert.Equal(new[] { CalibratedShape.ZoneName, CalibratedShape.PopName }, verdicts["c"].Disputed);
        Assert.False(verdicts["c"].Preferred);
    }

    [Fact]
    public void MoreSubmittersOnOneCodeOutvoteSeveralCodesWithFewer()
    {
        var verdicts = Judge(
            Code("many", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), submitters: 3),
            Code("few-1", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop)),
            Code("few-2", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop, job: null)));

        Assert.True(verdicts["many"].Preferred);
        Assert.False(verdicts["few-1"].Preferred);
        Assert.False(verdicts["few-2"].Preferred);
    }

    [Fact]
    public void ACodeNoIndexListedLosesEveryDisputeToAPublishedOne()
    {
        var verdicts = Judge(
            Code("pasted", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), submitters: 0),
            Code("published", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)));

        Assert.False(verdicts["pasted"].Preferred);
        Assert.True(verdicts["published"].Preferred);

        // Two codes nobody submitted are tied, and a tie is not a preference; a negative count is no count at all.
        var unknown = Judge(
            Code("x", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), submitters: 0),
            Code("y", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), submitters: -3));
        Assert.All(unknown.Values, verdict => Assert.False(verdict.Preferred));
    }

    [Fact]
    public void ATiedMatchIsDecidedByTheLocalPriorWhenOnlyOneSideHasIt()
    {
        var verdicts = Judge(
            Code("prior", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), localPrior: true),
            Code("other", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), localPrior: false));

        Assert.True(verdicts["prior"].Preferred);
        Assert.False(verdicts["other"].Preferred);
        Assert.Equal(new[] { CalibratedShape.PopName }, verdicts["prior"].Disputed);
    }

    public static TheoryData<bool?, bool?> UndecidedPriors() => new()
    {
        { null, null },
        { false, null },
        { false, false },
        { true, true },
    };

    [Theory]
    [MemberData(nameof(UndecidedPriors))]
    public void ATiedMatchWithoutALonePriorPrefersNeither(bool? first, bool? second)
    {
        var verdicts = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), localPrior: first),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), localPrior: second));

        Assert.All(verdicts.Values, verdict => Assert.False(verdict.Preferred));
        Assert.All(verdicts.Values, verdict => Assert.Equal(new[] { CalibratedShape.PopName }, verdict.Disputed));
    }

    [Fact]
    public void TheLocalPriorNeitherOutweighsVotesNorDecidesATieOnAnyOtherField()
    {
        var outvoted = Judge(
            Code("prior", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), localPrior: true),
            Code("voted", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), submitters: 2));
        Assert.False(outvoted["prior"].Preferred);
        Assert.True(outvoted["voted"].Preferred);

        // The prior speaks about the match's shape only: a tied zone stays undecided.
        var zone = Judge(
            Code("prior", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), localPrior: true),
            Code("other", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, zone: OtherZone)));
        Assert.Equal(new[] { CalibratedShape.ZoneName }, zone["prior"].Disputed);
        Assert.False(zone["prior"].Preferred);
        Assert.False(zone["other"].Preferred);
    }

    [Fact]
    public void AThreeWayTieIsDecidedOnlyWhenNoOtherTiedValueHasThePrior()
    {
        var decided = Judge(
            Code("prior", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), localPrior: true),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop)),
            Code("c", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop with { Opcode = 700 })));
        Assert.True(decided["prior"].Preferred);
        Assert.False(decided["b"].Preferred);
        Assert.False(decided["c"].Preferred);

        // A value below the top cannot be rescued by its prior, and does not spoil the top's own win.
        var below = Judge(
            Code("top", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), submitters: 2),
            Code("low", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), localPrior: true));
        Assert.True(below["top"].Preferred);
        Assert.False(below["low"].Preferred);
    }

    /// <summary>
    /// Submitters come from the network: two entries at the largest count the index format allows must add up without
    /// overflowing, or judging the consensus would throw on the thread that feeds the packets.
    /// </summary>
    [Fact]
    public void VotesFromTheLargestSubmitterCountsAddUpWithoutOverflow()
    {
        var verdicts = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), submitters: int.MaxValue),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop), submitters: int.MaxValue),
            Code("c", Payload(CalibrationMatchSource.ReplyState, ReplyPop, zone: OtherZone), submitters: int.MaxValue));

        // The first two share the zone: twice the largest count against once.
        Assert.Equal(new[] { CalibratedShape.ZoneName, CalibratedShape.PopName }, verdicts["a"].Disputed);
        Assert.False(verdicts["a"].Preferred);
        Assert.False(verdicts["b"].Preferred);
        Assert.Equal(new[] { CalibratedShape.ZoneName }, verdicts["c"].Disputed);
        Assert.False(verdicts["c"].Preferred);

        var zoneOnly = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop), submitters: int.MaxValue),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop, job: null), submitters: int.MaxValue),
            Code("c", Payload(CalibrationMatchSource.ReplyState, ReplyPop, zone: OtherZone), submitters: int.MaxValue));
        Assert.True(zoneOnly["a"].Preferred);
        Assert.True(zoneOnly["b"].Preferred);
        Assert.False(zoneOnly["c"].Preferred);
    }

    [Fact]
    public void ACodeListedTwiceVotesOnce()
    {
        var verdicts = Judge(
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
            Code("a", Payload(CalibrationMatchSource.MarkerOffset, MarkerPop)),
            Code("b", Payload(CalibrationMatchSource.MarkerOffset, OtherMarkerPop)));

        Assert.Equal(2, verdicts.Count);
        Assert.False(verdicts["a"].Preferred);
        Assert.False(verdicts["b"].Preferred);
    }
}
