using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>可逆隔离与永久撤回分开；旧索引兼容，隔离和恢复经过磁盘重启仍保留 epoch。</summary>
public sealed class SharedCalibrationQuarantineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadIndexUpdatesCachedRestrictionEvenWhenEveryCodeDownloadFails(bool quarantined)
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.PublishListed(new Bed.Listing(code, Quarantined: !quarantined, QuarantineEpoch: 1));
        var services = bed.Services();
        var initial = await services.FetchSharedCalibration(Region.Cn, Bed.Build, CancellationToken.None);
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, initial, bed.Db.Clock.UtcNow).Recorded);

        bed.PublishListed(new Bed.Listing(code, Quarantined: quarantined, QuarantineEpoch: 2));
        FailCodeDownloads(bed);
        var refreshed = await services.FetchSharedCalibration(Region.Cn, Bed.Build, CancellationToken.None);
        Assert.Equal(SharedFetchStatus.CodesUnavailable, refreshed.Status);
        Assert.True(refreshed.IndexWasRead);
        Assert.Empty(refreshed.Candidates);
        Assert.Single(refreshed.Discards);
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, refreshed, bed.Db.Clock.UtcNow).Recorded);

        var cached = Assert.Single(new SharedCalibrationStore(bed.StoreRoot)
            .LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha));
        Assert.Equal(quarantined, cached.Quarantined);
        Assert.Equal(2, cached.QuarantineEpoch);
        Assert.Equal(code.Code, cached.Code);
        Assert.Equal(SharedPublication.Published, bed.Store.Publication(Region.Cn, Bed.Build, code.Sha));
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadFailureCannotKeepLoginOnlyBindingAfterAReadQuarantine(bool blockStateWrite)
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
        using var stateLock = blockStateWrite ? new FileStream(Path.Combine(
            SharedCalibrationStore.DirectoryFor(bed.StoreRoot, Region.Cn, Bed.Build), SharedCalibrationStore.StateFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        FailCodeDownloads(bed);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        Assert.NotNull(bed.Store.CodeText(Region.Cn, Bed.Build, code.Sha));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    [Fact]
    public async Task ReadQuarantineUnloadsLoginOnlyBindingOutsideTheEightDownloadedAndCachedCandidates()
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

        PublishBehindEightCachedCandidates(bed, code);
        Assert.DoesNotContain(bed.Store.LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha), item => item.CodeSha256 == code.Sha);
        FailCodeDownloads(bed);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        Assert.NotNull(bed.Store.CodeText(Region.Cn, Bed.Build, code.Sha));
        Assert.True(pipeline.CalibrationStatus().Shared.Candidates.Count <= SharedCalibrationIndex.MaxCandidates);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    [Fact]
    public async Task RefreshedOriginalCodeEpochCannotLabelAnIdentityRecoveredThroughAnotherTemplate()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var built = SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>());
        Assert.Equal(SharedProfileBuildStatus.Built, built.Status);
        SharedProfileFiles.Write(built, bed.SharedRoot);
        Assert.True(bed.Store.RecordBound(Region.Cn, Bed.Build, built.ProfileSha256!, code.Sha, bed.Db.Clock.UtcNow));
        var changed = CalibrationTemplate.From(Bed.Template.Source with { ProfileSha256 = new string('1', 64) })!;
        var recovered = SharedProfileBuilder.RecoverPayload(ProfileLoader.Load(bed.SharedProfilePath), changed)!;
        var alias = ShareCode.Sha256(recovered);
        Assert.NotEqual(code.Sha, alias);
        bed.PublishListed(new Bed.Listing(code, QuarantineEpoch: 7));
        var pipeline = bed.Pipeline(bed.Services() with { SelectTemplate = _ => changed });
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Evening());
        await Bed.Idle(pipeline);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        var evidence = pipeline.GetCalibrationEvidence(alias);
        Assert.False(evidence.Available);
        Assert.Equal("NO_CURRENT_CANDIDATE", evidence.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadFailureCannotCommitAnOldLoginGateWhileProfileWriteIsBlocked(bool outsideCandidateLimit)
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
            if (outsideCandidateLimit)
                PublishBehindEightCachedCandidates(bed, code);
            else
                bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
            FailCodeDownloads(bed);
            bed.Db.Clock.UtcNow += TimeSpan.FromSeconds(6);
            var claimedAt = bed.Db.Clock.UtcNow;
            Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
            Assert.True(SpinWait.SpinUntil(() => pipeline.CalibrationStatus().Shared.LastSentAtUtc == claimedAt,
                TimeSpan.FromSeconds(30)), "fresh quarantine fetch was not claimed");
            release.Set();
            await Bed.Idle(pipeline);
            Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
            Assert.False(File.Exists(bed.SharedProfilePath));
        }
        finally
        {
            release.Set();
            await Bed.Idle(pipeline);
            bed.BeforeReload = null;
            pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        }
    }

    private static void FailCodeDownloads(Bed bed) => bed.Transport.BeforeAnswer = (uri, _) =>
        uri == SharedCalibrationClient.IndexUri(SharedCalibrationSource.GithubRaw)
            ? Task.CompletedTask
            : Task.FromException(new IOException("code file unavailable"));

    private static void PublishBehindEightCachedCandidates(Bed bed, SharedCode code)
    {
        var higher = Enumerable.Range(0, SharedCalibrationIndex.MaxCandidates)
            .Select(index => Bed.Encode(code.Payload with { ZoneOpcode = (ushort)(60000 + index) }))
            .ToArray();
        var candidates = higher.Select(item => new SharedCalibrationCandidate(item.Sha, item.Code, item.Payload,
            2, bed.Db.Clock.UtcNow, SharedCalibrationIndexTests.Commit())).ToArray();
        var fetched = new SharedCalibrationFetchResult(SharedFetchStatus.Ok,
            new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Ok, 200) },
            candidates, Array.Empty<SharedCodeDiscard>(), Array.Empty<SharedIndexSkip>(), Array.Empty<string>());
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, fetched, bed.Db.Clock.UtcNow).Recorded);
        bed.PublishListed(higher.Select(item => new Bed.Listing(item, Submitters: 2))
            .Append(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1)).ToArray());
    }

    [Fact]
    public async Task ReadQuarantineWithFailedDownloadsDoesNotUnloadAFullyVerifiedLocalBindingAfterRestart()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var services = bed.Services();
        var pipeline = bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = bed.Start(pipeline);
        var traffic = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(traffic, 200_000));
        await Bed.Idle(pipeline);
        Bed.Feed(pipeline, session, Bed.From(traffic, 200_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.CalibrationArmed);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        pipeline.StopSharedCalibration();
        await Bed.Idle(pipeline);

        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        FailCodeDownloads(bed);
        var refreshed = await services.FetchSharedCalibration(Region.Cn, Bed.Build, CancellationToken.None);
        Assert.Equal(SharedFetchStatus.CodesUnavailable, refreshed.Status);
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, refreshed, bed.Db.Clock.UtcNow).Recorded);
        var restarted = bed.Pipeline(services);
        restarted.Refresh(Bed.Game());
        await Bed.Idle(restarted);
        Assert.True(Assert.Single(bed.Store.LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha)).Quarantined);
        Assert.Equal(ProfileStatus.Verified, restarted.Current.Status);
        Assert.False(restarted.CalibrationArmed);
    }

    [Fact]
    public void OldIndexDefaultsToUnrestrictedWithoutLosingCandidate()
    {
        var read = SharedCalibrationIndex.Read(SharedCalibrationIndexTests.Index(
            SharedCalibrationIndexTests.Entry(SharedCalibrationIndexTests.Sha('a'))));
        var entry = Assert.Single(read.Entries);
        Assert.False(entry.Quarantined);
        Assert.Equal(0, entry.QuarantineEpoch);
    }

    [Fact]
    public void QuarantineRemainsDownloadableAndIsNotRevocation()
    {
        var item = SharedCalibrationIndexTests.Entry(SharedCalibrationIndexTests.Sha('a'));
        item["quarantined"] = true;
        item["quarantine_epoch"] = 7;
        var read = SharedCalibrationIndex.Read(SharedCalibrationIndexTests.Index(item));
        var entry = Assert.Single(read.Entries);
        Assert.True(entry.Quarantined);
        Assert.Equal(7, entry.QuarantineEpoch);
        Assert.False(entry.Revoked);
        Assert.Single(SharedCalibrationIndex.Select(read.Entries, Region.Cn, SharedCalibrationIndexTests.Build));
    }

    [Theory]
    [InlineData("quarantined", "null")]
    [InlineData("quarantined", "1")]
    [InlineData("quarantine_epoch", "true")]
    [InlineData("quarantine_epoch", "-1")]
    [InlineData("quarantine_epoch", "2147483648")]
    [InlineData("quarantine_epoch", "1.5")]
    public void MalformedRestrictionCannotSilentlyBecomeUnrestricted(string field, string json)
    {
        var item = SharedCalibrationIndexTests.Entry(SharedCalibrationIndexTests.Sha('a'));
        item[field] = JsonNode.Parse(json);
        var read = SharedCalibrationIndex.Read(SharedCalibrationIndexTests.Index(item));
        Assert.Empty(read.Entries);
        Assert.Equal("INVALID:" + field, Assert.Single(read.Skipped).Reason);
    }

    [Fact]
    public void DiskRoundTripPreservesQuarantineThenRecoveryEpochAndCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var payload = ShareCodeTests.Payload(CalibrationMatchSource.Announcement,
                new ShareCodePop(512, Length: 64)) with { GameBuild = SharedCalibrationIndexTests.Build };
            var candidate = new SharedCalibrationCandidate(ShareCode.Sha256(payload), ShareCode.Encode(payload), payload,
                3, DateTimeOffset.UtcNow, SharedCalibrationIndexTests.Commit(), Quarantined: true, QuarantineEpoch: 2);
            var store = new SharedCalibrationStore(root);
            SharedCalibrationFetchResult Fetched(SharedCalibrationCandidate code) => new(SharedFetchStatus.Ok,
                new[] { new SharedSourceAttempt(SharedCalibrationSource.GithubRaw, SharedFetchOutcome.Ok, 200) },
                new[] { code }, Array.Empty<SharedCodeDiscard>(),
                Array.Empty<SharedIndexSkip>(), Array.Empty<string>());
            Assert.True(store.RecordFetch(Region.Cn, payload.GameBuild, payload.TemplateSha256,
                Fetched(candidate), DateTimeOffset.UtcNow).Recorded);
            var quarantined = Assert.Single(new SharedCalibrationStore(root)
                .LoadCandidates(Region.Cn, payload.GameBuild, payload.TemplateSha256));
            Assert.True(quarantined.Quarantined, File.ReadAllText(Path.Combine(
                SharedCalibrationStore.DirectoryFor(root, Region.Cn, payload.GameBuild), SharedCalibrationStore.StateFileName)));
            Assert.Equal(2, quarantined.QuarantineEpoch);
            Assert.Equal(SharedPublication.Published, store.Publication(Region.Cn, payload.GameBuild, candidate.CodeSha256));

            Assert.True(store.RecordFetch(Region.Cn, payload.GameBuild, payload.TemplateSha256,
                Fetched(candidate with { Quarantined = false }), DateTimeOffset.UtcNow).Recorded);
            var recovered = Assert.Single(new SharedCalibrationStore(root)
                .LoadCandidates(Region.Cn, payload.GameBuild, payload.TemplateSha256));
            Assert.False(recovered.Quarantined);
            Assert.Equal(2, recovered.QuarantineEpoch);
            Assert.Equal(candidate.Code, recovered.Code);
            Assert.False(store.IsRejected(Region.Cn, payload.GameBuild, payload.TemplateSha256, candidate.CodeSha256));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
