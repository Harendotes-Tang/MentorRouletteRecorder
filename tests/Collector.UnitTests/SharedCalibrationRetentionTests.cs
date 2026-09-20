using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The life of a shared calibration after it arrives: contradictions count only from healthy sessions and
/// reject only from the second one, 重新观察 forgets them, a profile in use stays watched and is withdrawn -
/// with calibration re-armed - once it stops explaining the traffic or is revoked, and a restart keeps
/// looking for the real announcement beside a queue-inferred profile.
/// </summary>
public sealed class SharedCalibrationRetentionTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static IEnumerable<DecodedMessage> TrueEvening => CalibrationTrafficCases.Traffic(CalibrationTrafficCases.ReplyState);

    /// <summary>The same evening on a client whose zone-change marker is not the declared opcode.</summary>
    private static IEnumerable<DecodedMessage> EveningWithoutTheDeclaredMarker => TrueEvening.Select(message =>
        message.Opcode == CalibrationTrafficCases.ZoneInit ? message with { Opcode = 0xA1F7 } : message);

    private bool Rejected(SharedCode code) => _bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha);

    [Fact]
    public async Task OneHealthyContradictingSessionRejectsNothingTwoDoAndRediscoveringForgetsIt()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var wrong = Bed.Encode(truth.Payload with { ZoneOpcode = 0x7778 });
        _bed.Publish(wrong);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        _bed.Play(pipeline, TrueEvening, hour: 24);
        await Bed.Idle(pipeline);

        Assert.False(Rejected(wrong));
        var afterOne = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCandidateStatus.Verifying, Assert.Single(afterOne.Candidates).Status);

        _bed.Play(pipeline, TrueEvening, hour: 48);
        await Bed.Idle(pipeline);

        Assert.True(Rejected(wrong));
        var afterTwo = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Rejected, afterTwo.Phase);
        Assert.Equal(SharedCandidateStatus.Rejected, Assert.Single(afterTwo.Candidates).Status);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.True(pipeline.CalibrationArmed);

        var discarded = pipeline.DiscardCalibration();
        await Bed.Idle(pipeline);

        Assert.False(Rejected(wrong));
        Assert.Equal(SharedCalibrationPhase.Verifying, pipeline.CalibrationStatus().Shared.Phase);
        Assert.NotEqual(SharedCalibrationPhase.Rejected, discarded.Shared.Phase);
    }

    public static TheoryData<string, CaptureSilentReason, int?, long, bool> Unhealthy() => new()
    {
        { "midstream", CaptureSilentReason.Midstream, 0, 0L, true },
        { "attached to open connections", CaptureSilentReason.None, 2, 0L, true },
        { "connections unknown", CaptureSilentReason.None, null, 0L, true },
        { "adapter dropped packets", CaptureSilentReason.None, 0, 12L, true },
        { "no reading at all", CaptureSilentReason.None, 0, 0L, false },
    };

    [Theory]
    [MemberData(nameof(Unhealthy))]
    public async Task SessionsThatCouldNotHaveSeenTheMessageNeverContradict(
        string why, CaptureSilentReason silent, int? preexisting, long dropped, bool report)
    {
        var wrong = Bed.Encode(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState).Payload with { ZoneOpcode = 0x7778 });
        _bed.Publish(wrong);
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        foreach (var hour in new[] { 24, 48, 72 })
        {
            _bed.Play(pipeline, TrueEvening, hour, new CaptureSessionHealth("placeholder", silent, preexisting, dropped), report);
        }

        await Bed.Idle(pipeline);

        Assert.False(Rejected(wrong), why);
        Assert.Equal(SharedCandidateStatus.Verifying, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
    }

    [Fact]
    public async Task ARefusedLocalProfileNeitherDisarmsCalibrationNorStopsASharedOneFromBinding()
    {
        var refused = _bed.WriteRefusedLocalProfile();
        Assert.False(ProfileLoader.Load(refused).ToBinding().IsUsable);
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.True(pipeline.CalibrationArmed);
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCalibrationPhase.Verifying, pipeline.CalibrationStatus().Shared.Phase);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 200_000));
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.True(pipeline.CalibrationArmed);
        Assert.True(File.Exists(refused));
    }

    /// <summary>Binds the true code on an evening that records no mentor duty, so the watch continues afterwards.</summary>
    private async Task<Protocol.Pipeline.LiveProtocolPipeline> BoundAndStillWatched(SharedCode code)
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
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(SharedCandidateStatus.InUse, Assert.Single(pipeline.CalibrationStatus().Shared.Candidates).Status);
        return pipeline;
    }

    [Fact]
    public async Task ASharedProfileContradictedAfterBindingIsWithdrawnAndCalibrationReArms()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = await BoundAndStillWatched(code);

        _bed.Play(pipeline, EveningWithoutTheDeclaredMarker, hour: 48);
        await Bed.Idle(pipeline);
        Assert.True(File.Exists(_bed.SharedProfilePath));
        Assert.False(Rejected(code));

        var third = _bed.Start(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Bed.Feed(pipeline, third, EveningWithoutTheDeclaredMarker, hour: 72);
        pipeline.OnCaptureStopped(third, CaptureEndReason.UserStop);
        await Bed.Idle(pipeline);

        Assert.True(Rejected(code));
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Rejected, shared.Phase);
        Assert.Null(shared.ProfileId);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Refresh(Bed.Game()).Status);
        Assert.True(pipeline.CalibrationArmed);
    }

    /// <summary>
    /// docs/privacy-boundary.md §8.2 as it stands since 1.3.2: a shared profile in force is one of the two
    /// cases in which the index is still read, and a revocation of its code then takes it out of use at once.
    /// </summary>
    [Fact]
    public async Task WhileASharedProfileRecordsItsCodeIsStillCheckedForRevocation()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var pipeline = await BoundAndStillWatched(code);
        _bed.PublishRevoked(code);
        var sent = _bed.Transport.Requests.Count;

        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.True(_bed.Transport.Requests.Count > sent);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Refresh(Bed.Game()).Status);
        Assert.True(pipeline.CalibrationArmed);
    }

    [Fact]
    public async Task ACandidateTheIndexRevokesIsDroppedOnTheNextPermittedCheck()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish();
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);

        _bed.PublishRevoked(code);
        Assert.Equal(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Equal(SharedCalibrationPhase.Rejected, shared.Phase);
        Assert.Equal(SharedCandidateStatus.Rejected, Assert.Single(shared.Candidates).Status);
        Assert.True(pipeline.CalibrationArmed);
    }

    /// <summary>
    /// B2a review finding 1: the revocation branch is reachable. An automatic download is still pending
    /// when the player imports a code that binds, and the index that download then reads revokes that
    /// very code. The download went out before any profile was usable; nothing further is sent afterwards.
    /// </summary>
    [Fact]
    public async Task ACodeImportedAndBoundWhileADownloadWaitsIsWithdrawnWhenThatDownloadRevokesIt()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        using var asked = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bed.Transport.BeforeAnswer = async (_, _) =>
        {
            asked.Release();
            await release.Task.ConfigureAwait(false);
        };
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        Assert.True(await asked.WaitAsync(TimeSpan.FromSeconds(30)), "the automatic download never started");

        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 200_000));
        Assert.True(
            SpinWait.SpinUntil(() => pipeline.CalibrationStatus().Shared.ProfileId is not null, TimeSpan.FromSeconds(30)),
            "the imported code never bound");
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.True(File.Exists(_bed.SharedProfilePath));

        // The download that was already out has not answered yet, so 立即检查 finds it and starts nothing new.
        Assert.Equal(SharedCheckOutcome.AlreadyFetching, pipeline.CheckSharedCalibrationNow());
        Assert.Single(_bed.Transport.Requests);

        _bed.PublishRevoked(code);
        release.SetResult();
        await Bed.Idle(pipeline);

        // The duty is still under way, so the withdrawal waits rather than cutting the run short.
        Assert.Equal(RunState.EnteredDuty, pipeline.RunState);
        Assert.NotNull(pipeline.CalibrationStatus().Shared.ProfileId);

        Bed.Feed(pipeline, session, Bed.From(Bed.Evening(), 200_000));
        await Bed.Idle(pipeline);

        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Null(shared.ProfileId);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        // Only the index request of the download that was already out; the revoked code is never fetched.
        Assert.Single(_bed.Transport.Requests);
    }

    [Fact]
    public async Task AfterARestartASharedProfileWhoseCodeWasRejectedIsWithdrawnNotTrusted()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()),
            _bed.SharedRoot);
        foreach (var session in new[] { "session-1", "session-2" })
        {
            _bed.Store.RecordContradiction(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha, session, Bed.Confirmed);
        }

        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);

        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Refresh(Bed.Game()).Status);
        Assert.Equal("REJECTED", pipeline.CalibrationStatus().Shared.LastRefusal);
    }

    [Fact]
    public void AfterARestartAQueueInferredLocalProfileStillKeepsLookingForTheAnnouncement()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.QueueRequest), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        var current = pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.Verified, current.Status);
        Assert.Equal(ProfileOrigin.Local, current.Origin);
        Assert.True(pipeline.CalibrationArmed);
        Assert.Contains(pipeline.CalibrationStatus().Blockers, text => text.Contains("已经可以正常记录导随", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterARestartAQueueInferredSharedProfileStillKeepsLookingForTheAnnouncement()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>(), Bed.Confirmed),
            _bed.SharedRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        var current = pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.Verified, current.Status);
        Assert.Equal(ProfileOrigin.Shared, current.Origin);
        Assert.True(pipeline.CalibrationArmed);
        Assert.Contains(pipeline.CalibrationStatus().Blockers, text => text.Contains("已经可以正常记录导随", StringComparison.Ordinal));
    }

    [Fact]
    public void AfterARestartASharedProfileWithoutACompleteDutyIsStillWatchedUnderItsCode()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        SharedProfileFiles.Write(
            SharedProfileBuilder.Build(code.Payload, Bed.Template, Bed.Confirmed, new Dictionary<string, int>()),
            _bed.SharedRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);

        Assert.True(pipeline.CalibrationArmed);
        var status = pipeline.CalibrationStatus();
        Assert.Contains(status.Blockers, text => text.Contains("其他玩家分享的校准", StringComparison.Ordinal));
        Assert.Equal(SharedCalibrationPhase.Verified, status.Shared.Phase);
        var inUse = Assert.Single(status.Shared.Candidates);
        Assert.Equal(code.Sha[..12], inUse.Sha12);
        Assert.Equal(SharedCandidateStatus.InUse, inUse.Status);
    }
}
