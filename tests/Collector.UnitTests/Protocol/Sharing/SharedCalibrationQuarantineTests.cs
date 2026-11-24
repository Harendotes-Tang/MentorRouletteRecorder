using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 可逆隔离与永久撤回分开；旧索引兼容，隔离和恢复经过磁盘重启仍保留 epoch。流水线读到隔离时卸下只经登录验证的共享绑定，
/// 但不拒绝该码；被隔离的码在重启后也要等本机完整验证。
/// </summary>
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

    [Fact]
    public async Task QuarantinedCodeWaitsPastLoginAndRestartForFullLocalVerification()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        var pipeline = await bed.ReadyPipelineAsync();
        var first = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, first, Bed.Before(Bed.Evening(), 30_000));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        var candidate = Assert.Single(pipeline.CalibrationStatus().Shared.Candidates);
        Assert.Equal(code.Sha, candidate.CodeSha256);
        Assert.False(candidate.AuditPending);
        pipeline.OnCaptureStopped(first, CaptureEndReason.UserStop);
        pipeline.StopSharedCalibration();
        await Bed.Idle(pipeline);

        var restarted = await bed.ReadyPipelineAsync();
        var second = bed.Start(restarted);
        await Bed.FeedAndSettleAsync(restarted, second, Bed.Before(Bed.Evening(), 30_000), hour: 1);
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        await Bed.FeedAndSettleAsync(restarted, second, Bed.From(Bed.Evening(), 30_000), hour: 1);
        Assert.Equal(ProfileStatus.Verified, restarted.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        restarted.OnCaptureStopped(second, CaptureEndReason.UserStop);
    }

    [Fact]
    public async Task NewQuarantineTemporarilyUnloadsLoginOnlyProfileWithoutRejectingCode()
    {
        using var bed = new Bed();
        var (code, pipeline, session) = await BoundAtLoginAsync(bed);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);

        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        AssertNotAccused(bed, code);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.From(Bed.Evening(), 30_000));
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        // This capture straddled the epoch update and cannot become a recovery session.
        Assert.False(pipeline.GetCalibrationEvidence(code.Sha).Available);
    }

    [Fact]
    public async Task RestartDoesNotAdoptAnUnprovenProfileWhoseStoredCodeIsNowQuarantined()
    {
        using var bed = new Bed();
        var (code, pipeline, session) = await BoundAtLoginAsync(bed);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        pipeline.StopSharedCalibration();
        await Bed.Idle(pipeline);
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        var fetched = await bed.Services().FetchSharedCalibration(Region.Cn, Bed.Build, CancellationToken.None);
        Assert.True(bed.Store.RecordFetch(Region.Cn, Bed.Build, Bed.TemplateSha, fetched, bed.Db.Clock.UtcNow).Recorded);

        var restarted = await bed.ReadyPipelineAsync();
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        var second = bed.Start(restarted);
        await Bed.FeedAndSettleAsync(restarted, second, Bed.Before(Bed.Evening(), 30_000), hour: 1);
        Assert.Equal(ProfileStatus.UnsupportedBuild, restarted.Current.Status);
        await Bed.FeedAndSettleAsync(restarted, second, Bed.From(Bed.Evening(), 30_000), hour: 1);
        Assert.Equal(ProfileStatus.Verified, restarted.Current.Status);
        restarted.OnCaptureStopped(second, CaptureEndReason.UserStop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadFailureCannotKeepLoginOnlyBindingAfterAReadQuarantine(bool blockStateWrite)
    {
        using var bed = new Bed();
        var (code, pipeline, session) = await BoundAtLoginAsync(bed);
        using var stateLock = blockStateWrite ? bed.HoldStateFile(FileShare.Read) : null;
        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        FailCodeDownloads(bed);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        AssertNotAccused(bed, code);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    [Fact]
    public async Task ReadQuarantineUnloadsLoginOnlyBindingOutsideTheEightDownloadedAndCachedCandidates()
    {
        using var bed = new Bed();
        var (code, pipeline, session) = await BoundAtLoginAsync(bed);

        PublishBehindEightCachedCandidates(bed, code);
        Assert.DoesNotContain(bed.Store.LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha), item => item.CodeSha256 == code.Sha);
        FailCodeDownloads(bed);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        AssertNotAccused(bed, code);
        Assert.True(pipeline.CalibrationStatus().Shared.Candidates.Count <= SharedCalibrationIndex.MaxCandidates);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Audit 2026-11-03, E-1: a quarantine read mid-duty waits for the run to end, as a revocation does, instead of
    /// closing the duty the player is completing as interrupted. A quarantine asks only for full verification on this
    /// machine, and a duty recorded in full with every audit passed is exactly that, so the profile stays afterwards.
    /// </summary>
    [Fact]
    public async Task AQuarantineReadDuringADutyWaitsForTheRunAndADutyRecordedInFullKeepsTheProfile()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = await bed.ReadyPipelineAsync();
        var evening = Bed.Evening().ToArray();
        var session = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Before(evening, 200_000));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.True(File.Exists(bed.SharedProfilePath));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);

        await Bed.FeedAndSettleAsync(pipeline, session, Bed.From(evening, 200_000));

        var recorded = Assert.Single(bed.AllRuns());
        Assert.NotNull(recorded.EndedAtUtc);
        Assert.NotEqual(RunResult.Interrupted, recorded.Result);
        Assert.True(File.Exists(bed.SharedProfilePath));
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Audit 2026-11-03, E-1: held while the player is matched, the quarantine still takes the unproven profile out of use
    /// once the run is over - here when the capture stops before the duty is entered - without accusing the code.
    /// </summary>
    [Fact]
    public async Task AQuarantineReadWhileMatchedUnloadsTheUnprovenProfileOnceTheRunHasEnded()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = await bed.ReadyPipelineAsync();
        var session = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Before(Bed.Evening(), 123_000));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.MentorMatched, pipeline.RunState);

        bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(RunState.MentorMatched, pipeline.RunState);
        Assert.True(File.Exists(bed.SharedProfilePath));

        await Bed.StopAndSettleAsync(pipeline, session);

        Assert.False(File.Exists(bed.SharedProfilePath));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.Equal("QUARANTINED", pipeline.CalibrationStatus().Shared.LastRefusal);
        AssertNotAccused(bed, code);
    }

    [Fact]
    public async Task RefreshedOriginalCodeEpochCannotLabelAnIdentityRecoveredThroughAnotherTemplate()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var built = Bed.BuildSharedProfile(code.Payload);
        Assert.Equal(SharedProfileBuildStatus.Built, built.Status);
        SharedProfileFiles.Write(built, bed.SharedRoot);
        Assert.True(bed.Store.RecordBound(Region.Cn, Bed.Build, built.ProfileSha256!, code.Sha, bed.Db.Clock.UtcNow));
        var changed = CalibrationTemplate.From(Bed.Template.Source with { ProfileSha256 = new string('1', 64) })!;
        var recovered = SharedProfileBuilder.RecoverPayload(ProfileLoader.Load(bed.SharedProfilePath), changed)!;
        var alias = ShareCode.Sha256(recovered);
        Assert.NotEqual(code.Sha, alias);
        bed.PublishListed(new Bed.Listing(code, QuarantineEpoch: 7));
        var pipeline = await bed.ReadyPipelineAsync(bed.Services() with { SelectTemplate = _ => changed });

        var session = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Evening());
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        var evidence = pipeline.GetCalibrationEvidence(alias);
        Assert.False(evidence.Available);
        Assert.Equal("NO_CURRENT_CANDIDATE", evidence.Reason);
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
        var pipeline = await bed.ReadyPipelineAsync();
        bed.HoldReload(writing, release);
        var session = bed.Start(pipeline);
        try
        {
            Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 30_000));
            Assert.True(writing.Wait(TimeSpan.FromSeconds(30)));
            Assert.True(File.Exists(bed.SharedProfilePath));
            bed.PublishListed(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1));
            bed.CheckNowAndAwaitClaim(pipeline, "fresh quarantine fetch was not claimed");
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
            AssertNotAccused(bed, code);
            if (replaceWrittenDocument)
                Assert.Equal(replacementSha, ProfileLoader.Load(bed.SharedProfilePath).ProfileSha256);
            else
            {
                Assert.False(File.Exists(bed.SharedProfilePath));
                if (!stopBeforeCommit)
                {
                    await Bed.FeedAndSettleAsync(pipeline, session, Bed.From(Bed.Evening(), 30_000));
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
        var pipeline = await bed.ReadyPipelineAsync();
        bed.HoldReload(writing, release);
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
            bed.CheckNowAndAwaitClaim(pipeline, "fresh quarantine fetch was not claimed");
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

    [Fact]
    public async Task ReadQuarantineWithFailedDownloadsDoesNotUnloadAFullyVerifiedLocalBindingAfterRestart()
    {
        using var bed = new Bed();
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var services = bed.Services();
        var pipeline = await bed.ReadyPipelineAsync(services);
        var session = bed.Start(pipeline);
        var traffic = Bed.Evening().ToArray();
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Before(traffic, 200_000));
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.From(traffic, 200_000));
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
        var restarted = await bed.ReadyPipelineAsync(services);
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
        // A store folder of its own that must come away whole: a state file left open fails the test.
        using var root = TempDirectory.Reserve(strict: true);
        var payload = ShareCodeTests.Payload(CalibrationMatchSource.Announcement,
            new ShareCodePop(512, Length: 64)) with { GameBuild = Bed.Build };
        var candidate = new SharedCalibrationCandidate(ShareCode.Sha256(payload), ShareCode.Encode(payload), payload,
            3, DateTimeOffset.UtcNow, SharedCalibrationIndexTests.Commit(), Quarantined: true, QuarantineEpoch: 2);
        var store = new SharedCalibrationStore(root.Path);
        Assert.True(store.RecordFetch(Region.Cn, payload.GameBuild, payload.TemplateSha256,
            Bed.Fetched(SharedFetchStatus.Ok, candidate), DateTimeOffset.UtcNow).Recorded);
        var quarantined = Assert.Single(new SharedCalibrationStore(root.Path)
            .LoadCandidates(Region.Cn, payload.GameBuild, payload.TemplateSha256));
        Assert.True(quarantined.Quarantined, File.ReadAllText(Path.Combine(
            SharedCalibrationStore.DirectoryFor(root.Path, Region.Cn, payload.GameBuild), SharedCalibrationStore.StateFileName)));
        Assert.Equal(2, quarantined.QuarantineEpoch);
        Assert.Equal(SharedPublication.Published, store.Publication(Region.Cn, payload.GameBuild, candidate.CodeSha256));

        Assert.True(store.RecordFetch(Region.Cn, payload.GameBuild, payload.TemplateSha256,
            Bed.Fetched(SharedFetchStatus.Ok, candidate with { Quarantined = false }), DateTimeOffset.UtcNow).Recorded);
        var recovered = Assert.Single(new SharedCalibrationStore(root.Path)
            .LoadCandidates(Region.Cn, payload.GameBuild, payload.TemplateSha256));
        Assert.False(recovered.Quarantined);
        Assert.Equal(2, recovered.QuarantineEpoch);
        Assert.Equal(candidate.Code, recovered.Code);
        Assert.False(store.IsRejected(Region.Cn, payload.GameBuild, payload.TemplateSha256, candidate.CodeSha256));
    }

    /// <summary>A published code whose shared profile the login burst bound, in a capture that is still running.</summary>
    private static async Task<(SharedCode Code, LiveProtocolPipeline Pipeline, string Session)> BoundAtLoginAsync(Bed bed)
    {
        var code = bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        bed.Publish(code);
        var pipeline = await bed.ReadyPipelineAsync();
        var session = bed.Start(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Before(Bed.Evening(), 30_000));
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        return (code, pipeline, session);
    }

    /// <summary>A quarantine never accuses the code: it is not rejected and its text stays in the store.</summary>
    private static void AssertNotAccused(Bed bed, SharedCode code)
    {
        Assert.False(bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));
        Assert.NotNull(bed.Store.CodeText(Region.Cn, Bed.Build, code.Sha));
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
        Assert.True(bed.Store.RecordFetch(
            Region.Cn, Bed.Build, Bed.TemplateSha, Bed.Fetched(SharedFetchStatus.Ok, candidates), bed.Db.Clock.UtcNow).Recorded);
        bed.PublishListed(higher.Select(item => new Bed.Listing(item, Submitters: 2))
            .Append(new Bed.Listing(code, Quarantined: true, QuarantineEpoch: 1)).ToArray());
    }
}
