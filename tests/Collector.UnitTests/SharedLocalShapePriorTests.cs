using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The shape prior this machine computes itself (plan §20.5, security review 2026-10-15): read from the profiles it
/// verified for the latest strictly earlier build of the same region and template line, naming the match the same way.
/// The same rule as the publishing tool's <c>_shape_priors</c>, but over evidence nobody else can plant.
/// </summary>
public sealed class SharedLocalShapePriorTests : IDisposable
{
    private const string Earlier = "2026.08.20.0000.0000";
    private const string Older = "2026.08.05.0000.0000";

    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static ShareCodePayload WithSelectors(ShareCodePayload payload, params long[] values) =>
        payload with { Pop = payload.Pop with { SelectorValues = values } };

    private static ShareCodePayload At(ShareCodePayload payload, string build) => payload with { GameBuild = build };

    [Theory]
    [InlineData("2026.09.01.0000.0000", 2026, 9, 1)]
    [InlineData("2024.02.29.0001.0002", 2024, 2, 29)]
    public void ABuildCarriesItsCalendarDate(string build, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), SharedLocalShapePrior.BuildDate(build));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026.09.01")]
    [InlineData("2026.13.01.0000.0000")]
    [InlineData("2025.02.29.0000.0000")]
    [InlineData("0000.01.01.0000.0000")]
    [InlineData("2026.09.01.0000.0000 ")]
    [InlineData("v2026.09.01.0000.0000")]
    public void ABuildWithoutARealDateHasNone(string? build) => Assert.Null(SharedLocalShapePrior.BuildDate(build));

    [Fact]
    public void TheShapeIsWhatAPatchLeavesAloneAndAQueueRequestHasNone()
    {
        var marker = ShareCodeTests.Payload(CalibrationMatchSource.MarkerOffset, new ShareCodePop(599, Length: 24, RouletteOffset: 2));
        Assert.Equal("MARKER_OFFSET:24,2", SharedLocalShapePrior.Shape(marker));
        Assert.Equal(SharedLocalShapePrior.Shape(marker), SharedLocalShapePrior.Shape(marker with { Pop = marker.Pop with { Opcode = 650 } }));
        Assert.NotEqual(SharedLocalShapePrior.Shape(marker), SharedLocalShapePrior.Shape(marker with { Pop = marker.Pop with { Length = 40 } }));
        Assert.Equal("ANNOUNCEMENT:12",
            SharedLocalShapePrior.Shape(ShareCodeTests.Payload(CalibrationMatchSource.Announcement, new ShareCodePop(1, Length: 12))));
        Assert.Equal("REPLY_STATE:3,7",
            SharedLocalShapePrior.Shape(ShareCodeTests.Payload(CalibrationMatchSource.ReplyState, new ShareCodePop(1, SelectorValues: new long[] { 3, 7 }))));
        Assert.Null(SharedLocalShapePrior.Shape(ShareCodeTests.Payload(CalibrationMatchSource.QueueRequest, new ShareCodePop(1))));
    }

    [Fact]
    public void TheLatestEarlierBuildDecidesTrueFalseOrNothing()
    {
        var candidate = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState).Payload;
        var same = At(candidate, Earlier);
        var other = WithSelectors(At(candidate, Earlier), 4);

        Assert.True(SharedLocalShapePrior.For(candidate, new[] { same }));
        Assert.False(SharedLocalShapePrior.For(candidate, new[] { other }));
        Assert.True(SharedLocalShapePrior.For(candidate, new[] { other, same }));
        Assert.Null(SharedLocalShapePrior.For(candidate, Array.Empty<ShareCodePayload>()));

        // Only the latest earlier build counts: an older one that agrees does not outweigh a newer one that does not.
        Assert.False(SharedLocalShapePrior.For(candidate, new[] { At(candidate, Older), other }));
        Assert.True(SharedLocalShapePrior.For(candidate, new[] { WithSelectors(At(candidate, Older), 4), same }));
    }

    [Fact]
    public void OnlyAStrictlyEarlierDatedBuildOfTheSameRegionTemplateAndKindIsCompared()
    {
        var candidate = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState).Payload;
        var other = WithSelectors(candidate, 4);

        // The same day, a later day, the same build: not comparable.
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { At(other, "2026.09.01.0001.0000") }));
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { At(other, "2026.09.02.0000.0000") }));
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { other }));
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { At(other, "not-a-dated-build") }));

        // Another region, template or kind of match.
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { At(other, Earlier) with { Region = Region.Global } }));
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { At(other, Earlier) with { TemplateSha256 = new string('1', 64) } }));
        var marker = At(_bed.CodeFromEveningA(CalibrationTrafficCases.MarkerOffset).Payload, Earlier);
        Assert.Null(SharedLocalShapePrior.For(candidate, new[] { marker }));

        // A candidate with no dated build, or no shape, has no prior.
        Assert.Null(SharedLocalShapePrior.For(At(candidate, "custom-build"), new[] { At(candidate, Earlier) }));
        var queue = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest).Payload;
        Assert.Null(SharedLocalShapePrior.For(queue, new[] { At(queue, Earlier) }));
    }

    [Fact]
    public void AVerifiedProfileIsReadThroughTheTemplateInForceAndAnythingElseIsLeftOut()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var shared = SharedProfileBuilder.Build(At(code.Payload, Earlier), Bed.Template, Bed.Confirmed, new Dictionary<string, int>()).Profile!;
        var local = ProfileLoader.Load(LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.MarkerOffset), Bed.Template, Older, Bed.Confirmed, _bed.LocalRoot).Path);
        var foreign = shared with { MentorRouletteId = shared.MentorRouletteId + 1 };
        var otherRegion = shared with { Region = Region.Global };

        var verified = SharedLocalShapePrior.Verified(new[] { shared, local, foreign, otherRegion }, Bed.Template);

        Assert.Equal(2, verified.Count);
        Assert.Equal(At(code.Payload, Earlier) with { Pop = verified[0].Pop }, verified[0]);
        Assert.Equal(SharedLocalShapePrior.Shape(code.Payload), SharedLocalShapePrior.Shape(verified[0]));
        Assert.Equal(CalibrationMatchSource.MarkerOffset, verified[1].MatchSource);
        Assert.Equal(Older, verified[1].GameBuild);
        Assert.True(SharedLocalShapePrior.For(code.Payload, verified));
    }
}
