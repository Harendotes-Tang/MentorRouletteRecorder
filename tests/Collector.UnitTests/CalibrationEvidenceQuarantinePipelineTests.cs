using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CalibrationEvidenceQuarantinePipelineTests
{
    [Fact]
    public async Task QuarantinedCodeWaitsPastLoginAndRestartForFullLocalVerification()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var first = bed.Start(pipeline);
        Bed.Feed(pipeline, first, Bed.Before(Bed.Evening(), 30_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        var candidate = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(code.Sha, candidate.CodeSha256);
        Assert.False(candidate.AuditPending);
        pipeline.OnCaptureStopped(first, CaptureEndReason.UserStop);
        pipeline.StopSharedCalibration();
        await Bed.Idle(pipeline);

        var restarted = bed.Pipeline(bed.Services());
        restarted.Refresh(Bed.Game());
        await Bed.Idle(restarted);
        var second = bed.Start(restarted);
        Bed.Feed(restarted, second, Bed.Before(Bed.Evening(), 30_000), hour: 1);
        await Bed.Idle(restarted);
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        Bed.Feed(restarted, second, Bed.From(Bed.Evening(), 30_000), hour: 1);
        await Bed.Idle(restarted);
        Assert.Equal(ProfileStatus.Verified, restarted.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        restarted.OnCaptureStopped(second, CaptureEndReason.UserStop);
    }

    [Fact]
    public async Task NewQuarantineTemporarilyUnloadsLoginOnlyProfileWithoutRejectingCode()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 30_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);

        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        Assert.NotNull(bed.Store.CodeText(Region.Cn, Bed.Build, code.Sha));
        Bed.Feed(pipeline, session, Bed.From(Bed.Evening(), 30_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        // This capture straddled the epoch update and cannot become a recovery session.
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);
    }

    [Fact]
    public async Task RestartDoesNotAdoptAnUnprovenProfileWhoseStoredCodeIsNowQuarantined()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 30_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        pipeline.StopSharedCalibration();
        await Bed.Idle(pipeline);
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        var fetched = await bed.Services().FetchSharedCalibration(Region.Cn, Bed.Build, CancellationToken.None);
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, fetched, bed.Db.Clock.UtcNow).Recorded);

        var restarted = bed.Pipeline(bed.Services());
        restarted.Refresh(Bed.Game());
        await Bed.Idle(restarted);
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        var second = bed.Start(restarted);
        Bed.Feed(restarted, second, Bed.Before(Bed.Evening(), 30_000), hour: 1);
        await Bed.Idle(restarted);
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        Bed.Feed(restarted, second, Bed.From(Bed.Evening(), 30_000), hour: 1);
        await Bed.Idle(restarted);
        Assert.Equal(ProfileStatus.Verified, restarted.Current.Status);
        restarted.OnCaptureStopped(second, CaptureEndReason.UserStop);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task QuarantineArrivingDuringProfileWriteCannotCommitTheOldLoginGate(bool replaceWrittenDocument, bool stopBeforeCommit)
    {
        using var bed = new Bed();
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = bed.Pipeline(bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        bed.BeforeReload = () =>
        {
            writing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
        };
        var session = bed.Start(pipeline);
        try
        {
            Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 30_000));
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(File.Exists(bed.SharedProfilePath));
            bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
            bed.Db.Clock.UtcNow += TimeSpan.FromSeconds(6);
            var claimedAt = bed.Db.Clock.UtcNow;
            Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
            Assert.True(SpinWait.SpinUntil(() => pipeline.CalibrationStatus().Shared.LastSentAtUtc == claimedAt,
                TimeSpan.FromSeconds(30)), "fresh quarantine fetch was not claimed");
            if (stopBeforeCommit) pipeline.StopSharedCalibration();
            string? replacementSha = null;
            if (replaceWrittenDocument)
            {
                var replacement = SharedProfileBuilder.Build(code.Payload with { ZoneOpcode = 65000 }, Bed.Template,
                    bed.Db.Clock.UtcNow, new Dictionary<string, int>());
                replacementSha = replacement.ProfileSha256;
                SharedProfileFiles.Write(replacement, bed.SharedRoot);
            }
            release.Set();
            await Bed.Idle(pipeline);
            bed.BeforeReload = null;
            Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
            Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
            Assert.NotNull(bed.Store.CodeText(Region.Cn, Bed.Build, code.Sha));
            if (replaceWrittenDocument)
                Assert.Equal(replacementSha, ProfileLoader.Load(bed.SharedProfilePath).ProfileSha256);
            else
            {
                Assert.False(File.Exists(bed.SharedProfilePath));
                if (!stopBeforeCommit)
                {
                    Bed.Feed(pipeline, session, Bed.From(Bed.Evening(), 30_000));
                    await Bed.Idle(pipeline);
                    Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
                }
            }
        }
        finally
        {
            release.Set();
            await Bed.Idle(pipeline);
            bed.BeforeReload = null;
            pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        }
    }
}
