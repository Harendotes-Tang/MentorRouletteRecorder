using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Reading the index again although a profile already records (plans/shared-calibration-rollback.md §3):
/// which profiles allow it, what a revocation of the code in use does, and how a code that outranks the
/// one in force takes over without accusing it.
/// </summary>
public sealed class SharedCalibrationRecheckTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static IEnumerable<DecodedMessage> TrueEvening => CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState);

    /// <summary>Every combination of what may be in force, and whether the index may be read beside it.</summary>
    public static TheoryData<string, bool, ProfileOrigin, bool, SharedRecheckReason?> InForce() => new()
    {
        { "nothing usable", false, ProfileOrigin.Shipped, false, null },
        { "shipped", true, ProfileOrigin.Shipped, false, null },
        { "local, reads the match", true, ProfileOrigin.Local, false, null },
        { "shared, reads the match", true, ProfileOrigin.Shared, false, SharedRecheckReason.SharedInUse },
        { "shared, infers the match", true, ProfileOrigin.Shared, true, SharedRecheckReason.SharedInUse },
        { "local, infers the match", true, ProfileOrigin.Local, true, SharedRecheckReason.QueueInferredInUse },
        { "shipped, infers the match", true, ProfileOrigin.Shipped, true, SharedRecheckReason.QueueInferredInUse },
    };

    [Theory]
    [MemberData(nameof(InForce))]
    public void TheIndexIsReadBesideASharedOrQueueInferredProfileAndNoOther(
        string what, bool usable, ProfileOrigin origin, bool matchFromQueue, SharedRecheckReason? expected) =>
        Assert.True(expected == SharedRecheck.ReasonFor(usable, origin, matchFromQueue), what);

    [Fact]
    public async Task NothingIsSentWhileALocalProfileThatReadsTheMatchRecords()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCheckOutcome.NotNeeded, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Empty(_bed.Transport.Requests);
        Assert.Null(pipeline.CalibrationStatus().Shared.Recheck);
    }

    [Fact]
    public async Task TheIndexIsReadBesideTheMachinesOwnQueueInferredProfile()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        Assert.NotEmpty(_bed.Transport.Requests);
        var recheck = pipeline.CalibrationStatus().Shared.Recheck;
        Assert.NotNull(recheck);
        Assert.Equal(SharedRecheckReason.QueueInferredInUse, recheck!.Reason);
        Assert.Equal(SharedFetchStatus.Ok, recheck.Status);
    }

    [Fact]
    public async Task TheIndexIsReadBesideASharedProfileStillBeingWatched()
    {
        var pipeline = await BoundAndStillWatched(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var sent = _bed.Transport.Requests.Count;

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.True(_bed.Transport.Requests.Count > sent);
        Assert.Equal(SharedRecheckReason.SharedInUse, pipeline.CalibrationStatus().Shared.Recheck?.Reason);
    }

    /// <summary>
    /// When a withdrawal may leave the file alone (2026-09-21 full-audit finding 12). One file serves a region
    /// and build, so a withdrawal that waited behind a bind would otherwise delete the profile that bind had just
    /// verified and adopted, with nothing in memory the wiser: the next catalogue scan, a restart included, would
    /// find the build uncalibrated again. The file lock orders the two; this is the question the withdrawal then
    /// asks. Only a hash known on both sides and different proves another document stands on the path - an
    /// unknown must never pass for "replaced", or a contradicted profile would go on recording.
    /// </summary>
    [Theory]
    [InlineData("another profile was bound meanwhile", "b-sha", "a-sha", true)]
    [InlineData("the withdrawn profile is still the one bound", "a-sha", "a-sha", false)]
    [InlineData("nothing is bound any more", null, "a-sha", false)]
    [InlineData("the withdrawn profile was bound before its hash was known", "b-sha", null, false)]
    [InlineData("neither hash is known", null, null, false)]
    public void AWithdrawalLeavesTheFileOnlyWhenAnotherKnownProfileNowStandsThere(
        string what, string? current, string? withdrawn, bool replaced) =>
        Assert.True(replaced == SharedCalibrationSession.IsReplacedBy(current, withdrawn), what);

    /// <summary>
    /// The race itself (2026-09-21 full-audit finding 12), driven through the product rather than the decision
    /// alone. A queue-inferred shared code A is in force and has recorded an evening; a published code B that
    /// reads the match outranks it, is verified on that evening's evidence and is being bound - its document is
    /// already written to the one path the region and build share, and the catalogue reload behind it is held
    /// open. Right then 立即检查 reads an index that revokes A, and A's withdrawal is scheduled. Before the fix
    /// that withdrawal deleted the path at once and took B's freshly written file with it; B was verified and
    /// then lost, and the next catalogue scan, a restart included, found the build uncalibrated. Now the
    /// withdrawal waits behind the bind, sees B standing on the path and leaves it. Only B's reload is held: the
    /// hook pauses the first reload that finds a document other than A's on disk, never the withdrawal's own.
    /// </summary>
    [Fact]
    public async Task AWithdrawalScheduledWhileABetterCodeIsBeingWrittenLeavesThatCodesFileInPlace()
    {
        var queue = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(queue.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>(), Bed.Confirmed),
            _bed.SharedRoot);
        var queueDocument = File.ReadAllBytes(_bed.SharedProfilePath);
        var announced = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish();
        var pipeline = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        // A records the evening. With the capture session over nothing stages, so B binds on the evidence alone.
        _bed.Play(pipeline, TrueEvening, hour: 24);
        await Bed.Idle(pipeline);
        Assert.NotNull(pipeline.CalibrationStatus().Shared.ProfileId);

        using var written = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        var held = 0;
        _bed.BeforeReload = () =>
        {
            // Runs inside the session's file lock: it reads the path and waits, and takes no lock of its own.
            if (File.Exists(_bed.SharedProfilePath) &&
                !File.ReadAllBytes(_bed.SharedProfilePath).AsSpan().SequenceEqual(queueDocument) &&
                Interlocked.Exchange(ref held, 1) == 0)
            {
                written.Release();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        try
        {
            _bed.Publish(announced);
            Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
            Assert.True(await written.WaitAsync(TimeSpan.FromSeconds(30)), "the better code was never written");

            // B's document is on disk and its bind has not committed. Now the index revokes A.
            _bed.Db.Clock.Elapsed += SharedCalibrationSession.ManualCheckInterval;
            _bed.PublishListed(new Bed.Listing(queue, Revoked: true), new Bed.Listing(announced));
            Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
            Assert.True(
                SpinWait.SpinUntil(() => pipeline.CalibrationStatus().Shared.LastRefusal == "REVOKED", TimeSpan.FromSeconds(30)),
                "the revocation of the code in use was never claimed");
        }
        finally
        {
            // Released whatever happened above, so a failure can never leave the bind parked. The withdrawal is
            // not awaited while B is held: it waits on the lock B holds.
            release.Set();
        }

        await Bed.Idle(pipeline);
        _bed.BeforeReload = null;

        Assert.True(File.Exists(_bed.SharedProfilePath), "the withdrawal of A deleted the file B had just written");
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.NotNull(shared.ProfileId);
        Assert.False(File.ReadAllBytes(_bed.SharedProfilePath).AsSpan().SequenceEqual(queueDocument));
        Assert.Equal(announced.Sha[..12], shared.Candidates[0].Sha12);
        Assert.Equal(SharedCandidateStatus.InUse, shared.Candidates[0].Status);
        Assert.Contains(shared.Candidates, item => item.Sha12 == queue.Sha[..12] && item.Status == SharedCandidateStatus.Rejected);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);

        // What a restart would read: B, the code that reads the match, from disk.
        var restarted = _bed.DiskSelect()(Bed.Game());
        Assert.Equal(ProfileOrigin.Shared, restarted.Origin);
        Assert.False(restarted.Profile?.MatchFromQueue ?? true);
    }

    /// <summary>
    /// 「立即检查」之间有一个最短间隔（2026-09-21 full-audit finding 13）。Before it, the only
    /// throttle was "a download is already running", so the instant one ended - however quickly
    /// it had failed - the next call could start another whole round: the index from up to three
    /// sources, then each candidate code from up to three sources. Anything able to open the pipe
    /// could keep the repository and its mirrors busy simply by asking in a loop.
    ///
    /// The refusal has its own outcome rather than borrowing ALREADY_FETCHING: nothing is running,
    /// so the desktop's line for that one would send the player to watch the card for a result
    /// that already arrived.
    /// </summary>
    [Fact]
    public async Task ASecondManualCheckWithinTheIntervalDoesNotReachTheNetworkAgain()
    {
        var pipeline = await BoundAndStillWatched(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        var sent = _bed.Transport.Requests.Count;

        Assert.Equal(SharedCheckOutcome.RecentlyChecked, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.Equal(sent, _bed.Transport.Requests.Count);

        // Past the interval the player who really does want another look gets one. The interval is
        // read off the monotonic clock, so moving the wall clock cannot stretch or shorten it.
        _bed.Db.Clock.Elapsed += SharedCalibrationSession.ManualCheckInterval;
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.True(_bed.Transport.Requests.Count > sent);
    }

    // ------------------------------------------------------------------ the code in use was revoked

    [Fact]
    public async Task ARevokedCodeInUseIsWithdrawnAndTheNextBestCodeOfTheSameIndexTakesOver()
    {
        var wrong = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var better = Bed.Encode(wrong.Payload with { JobOpcode = null });
        var pipeline = await BoundAndStillWatched(wrong);
        _bed.PublishListed(new Bed.Listing(wrong, Revoked: true), new Bed.Listing(better));

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        // Withdrawn - the code is refused from now on - and the rest of that same index carries on exactly
        // as a fresh download would, so the next best code is verified and takes over.
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedPublication.Revoked, _bed.Store.Publication(Region.Cn, Bed.Build, wrong.Sha));
        Assert.DoesNotContain(
            _bed.Store.LoadCandidates(Region.Cn, Bed.Build, Bed.TemplateSha), item => item.CodeSha256 == wrong.Sha);
        Assert.Contains(shared.Candidates, item => item.Sha12 == wrong.Sha[..12] && item.Status == SharedCandidateStatus.Rejected);
        Assert.Equal(better.Sha[..12], shared.Candidates[0].Sha12);
        Assert.Equal(SharedCandidateStatus.InUse, shared.Candidates[0].Status);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        Assert.True(pipeline.CalibrationArmed);
    }

    [Fact]
    public async Task ARevokedCodeInUseWithNothingToReplaceItLeavesLocalCalibrationRunning()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = await BoundAndStillWatched(code);
        _bed.PublishRevoked(code);

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Null(pipeline.CalibrationStatus().Shared.ProfileId);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Refresh(Bed.Game()).Status);
        Assert.True(pipeline.CalibrationArmed);
    }

    /// <summary>
    /// A withdrawal mid-duty would close the run the way a stopped capture closes it. The revocation is
    /// therefore held until the machine is back between runs, and the run is recorded and flagged instead.
    /// </summary>
    [Fact]
    public async Task ARevocationThatArrivesDuringARunWaitsUntilTheRunEnds()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var evening = Bed.Evening().ToArray();
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);

        _bed.PublishRevoked(code);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.True(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);

        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));
        await Bed.Idle(pipeline);

        // The duty was recorded in full and only then did the profile go, with its records flagged.
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Null(pipeline.CalibrationStatus().Shared.ProfileId);
        Assert.True(pipeline.CalibrationArmed);
        var recorded = Assert.Single(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items);
        Assert.NotNull(recorded.EndedAtUtc);
        Assert.True(recorded.PendingReview);
    }

    // ------------------------------------------------------------------ a code that outranks what is in force

    /// <summary>
    /// Plan §3: the old code was weak, not wrong, so it keeps its records and is never refused; and a code
    /// that reads the server's own match never asks the player to accept queue inference.
    /// </summary>
    [Fact]
    public async Task ACodeThatReadsTheMatchReplacesABoundQueueInferredSharedProfileWithoutAccusingIt()
    {
        var queue = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(queue.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>(), Bed.Confirmed),
            _bed.SharedRoot);
        var announced = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(announced);
        var pipeline = _bed.Pipeline(_bed.Services());

        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, TrueEvening);
        await Bed.Idle(pipeline);

        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Verified, shared.Phase);
        Assert.Equal(announced.Sha[..12], shared.Candidates[0].Sha12);
        Assert.Equal(CalibrationMatchSource.ReplyState, shared.Candidates[0].MatchSource);
        // Weak, not wrong: no accusation anywhere.
        Assert.False(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, queue.Sha));
        Assert.Null(shared.LastRefusal);
        Assert.DoesNotContain(shared.Candidates, item => item.Status == SharedCandidateStatus.Rejected);
        Assert.DoesNotContain(new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items, run => run.PendingReview);
    }

    [Fact]
    public async Task ACodeThatReadsTheMatchOutranksTheMachinesOwnQueueInferredProfileAndLeavesItsFileAlone()
    {
        var local = LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, TrueEvening);
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(
            CalibrationMatchSource.ReplyState, pipeline.CalibrationStatus().Shared.Candidates[0].MatchSource);
        Assert.True(File.Exists(local.Path));
        Assert.Equal(SharedCalibrationPhase.Verified, pipeline.CalibrationStatus().Shared.Phase);
    }

    [Fact]
    public async Task ACodeThatFailsVerificationLeavesTheProfileInForceRecording()
    {
        var local = LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(Bed.Encode(truth.Payload with { ZoneOpcode = 0x7778 }));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        _bed.Play(pipeline, TrueEvening, hour: 24);
        _bed.Play(pipeline, TrueEvening, hour: 48);
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.True(File.Exists(local.Path));
        Assert.False(File.Exists(_bed.SharedProfilePath));
    }

    [Fact]
    public async Task ACodeTheRepositoryMarkedConflictingIsTriedLast()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var second = Bed.Encode(truth.Payload with { JobOpcode = null });
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.PublishListed(new Bed.Listing(truth, Conflicting: true), new Bed.Listing(second));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, TrueEvening);
        await Bed.Idle(pipeline);

        Assert.Equal(second.Sha[..12], pipeline.CalibrationStatus().Shared.Candidates[0].Sha12);
    }

    // ------------------------------------------------------------------ the recheck is still suppressible

    public static TheoryData<string> Suppressed() => new() { "kill switch", "setting off" };

    [Theory]
    [MemberData(nameof(Suppressed))]
    public async Task TheKillSwitchAndTheSettingStillStopTheRecheck(string how)
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        if (how == "kill switch")
        {
            _bed.Environment = name => name == SharedCalibrationClient.DisableVariable ? "1" : null;
        }

        var pipeline = _bed.Pipeline(_bed.Services());
        if (how == "setting off")
        {
            pipeline.ApplySharedCalibrationSetting(false);
        }

        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        await Bed.Idle(pipeline);

        Assert.Empty(_bed.Transport.Requests);
        Assert.False(File.Exists(_bed.SharedProfilePath));
    }

    /// <summary>The six-hour throttle counts a recheck like any other download; only 立即检查 goes past it.</summary>
    [Fact]
    public async Task TheSixHourThrottleCountsTheRecheckToo()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        _bed.Publish(Bed.Encode(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState).Payload with { ZoneOpcode = 0x7778 }));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var sent = _bed.Transport.Requests.Count;
        Assert.NotEqual(0, sent);

        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(sent, _bed.Transport.Requests.Count);

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);
        Assert.True(_bed.Transport.Requests.Count > sent);
    }

    /// <summary>Binds the code on an evening with no mentor duty, so the profile is still watched afterwards.</summary>
    private async Task<LiveProtocolPipeline> BoundAndStillWatched(SharedCode code)
    {
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, TrueEvening, hour: 24);
        await Bed.Idle(pipeline);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        Assert.True(File.Exists(_bed.SharedProfilePath));
        return pipeline;
    }
}
