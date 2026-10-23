using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The two claims shared calibration holds while work runs off the pipeline's gate - "a download is running" and
/// "a candidate is being written" - are given back however that work ends. A throw nobody foresaw used to leave
/// either set for the rest of the arm: every later 立即检查 answered "already fetching", or the candidate that passed
/// never bound again (audit 2026-10-03, unclosed suspicions under OE).
/// </summary>
public sealed class SharedCalibrationRobustnessTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public async Task ADownloadThatThrowsBeforeItIsClaimedDoesNotBlockTheNextCheck()
    {
        var services = _bed.Services();
        var real = services.FetchSharedCalibration;
        var calls = 0;
        services = services with
        {
            // The first answer carries a code with no payload, which nothing downstream expects.
            FetchSharedCalibration = (region, build, token) => Interlocked.Increment(ref calls) == 1
                ? Task.FromResult(new SharedCalibrationFetchResult(
                    SharedFetchStatus.Ok,
                    new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Ok, 200) },
                    new[]
                    {
                        new SharedCalibrationCandidate(
                            new string('a', 64), "MRC1.unexpected", null!, 1, Bed.Confirmed, SharedCalibrationIndexTests.Commit()),
                    },
                    Array.Empty<SharedCodeDiscard>(),
                    Array.Empty<SharedIndexSkip>(),
                    Array.Empty<string>()))
                : real(region, build, token),
        };
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(1, calls);
        Assert.Empty(pipeline.CalibrationStatus().Shared.Candidates);

        _bed.Db.Clock.Elapsed += SharedCalibrationSession.ManualCheckInterval;
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(2, calls);
        Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
    }

    [Fact]
    public async Task ABindThatThrowsBeforeItIsCommittedLetsTheCandidateBindInTheNextSession()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var thrown = 0;
        _bed.BeforeReload = () =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new KeyNotFoundException("the catalogue reload failed in a way nothing foresaw");
            }
        };
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var login = Bed.Before(Bed.Evening(), 30_000);
        _bed.Play(pipeline, login, hour: 0);
        await Bed.Idle(pipeline);
        Assert.Equal(1, thrown);
        Assert.NotEqual(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, login, hour: 24);
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
    }
}
