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
