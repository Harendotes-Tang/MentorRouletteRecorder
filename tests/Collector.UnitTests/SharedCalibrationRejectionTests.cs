using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 不用共享的，我自己校准 (<c>RejectSharedCalibration</c>): the player's own refusal withdraws a shared profile in
/// force and re-arms local calibration, drops the candidates being verified, and is remembered apart from the
/// contradiction records, so nothing shared is fetched, pasted or bound for the build again until 重新观察.
/// </summary>
public sealed class SharedCalibrationRejectionTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    private static Dictionary<string, CalibrationVerdict> AllCorrect(CalibrationStatusSnapshot status) =>
        status.Events.Where(item => item.RequiresConfirmation)
            .ToDictionary(item => item.EventId, _ => CalibrationVerdict.Correct, StringComparer.Ordinal);

    [Fact]
    public void TheStoreKeepsTheUserRejectionApartFromContradictionsAndRediscoveringClearsBoth()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Assert.False(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));

        Assert.True(_bed.Store.RecordUserRejection(Region.Cn, Bed.Build, Bed.Confirmed));
        _bed.Store.RecordContradiction(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha, "session-1", Bed.Confirmed);

        var reread = new SharedCalibrationStore(_bed.StoreRoot);
        Assert.True(reread.IsUserRejected(Region.Cn, Bed.Build));
        Assert.False(reread.IsUserRejected(Region.Cn, Bed.OtherBuild));
        // One contradicting session is a record, not a rejection; the player's refusal is not a contradiction either.
        Assert.False(reread.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, code.Sha));

        Assert.True(reread.ClearRejections(Region.Cn, Bed.Build));
        Assert.False(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
    }

    [Fact]
    public async Task RejectingAProvenSharedProfileWithdrawsItReArmsCalibrationAndLocalCalibrationCarriesOn()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        var session = _bed.Start(pipeline);
        var evening = Bed.Evening().ToArray();
        Bed.Feed(pipeline, session, Bed.Before(evening, 200_000));
        await Bed.Idle(pipeline);
        Bed.Feed(pipeline, session, Bed.From(evening, 200_000));
        Assert.Equal(CalibrationState.Done, pipeline.CalibrationStatus().State);
        Assert.False(pipeline.CalibrationArmed);
        var inForce = pipeline.Current.ProfileId;

        var result = pipeline.RejectSharedCalibration();
        await Bed.Idle(pipeline);

        Assert.Equal(inForce, result.WithdrawnProfileId);
        Assert.Equal(0, result.DroppedCandidates);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        // Re-armed from DONE, not merely left armed: it observes the running session again.
        Assert.True(pipeline.CalibrationArmed);
        var status = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Observing, status.State);
        Assert.True(status.Shared.UserRejected);
        Assert.Equal(SharedCalibrationPhase.Rejected, status.Shared.Phase);
        Assert.Null(status.Shared.ProfileId);
        Assert.True(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));

        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
        var next = _bed.Start(pipeline);
        Bed.Feed(pipeline, next, CalibrationObserverTests.Session1(), hour: 24);
        var ready = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, ready.State);
        Assert.True(pipeline.ConfirmCalibration(AllCorrect(ready)).BoundInSession);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
    }

    [Fact]
    public async Task RejectingDropsTheCandidatesAndNothingSharedIsFetchedPastedOrBoundUntilRediscover()
    {
        var truth = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        var other = Bed.Encode(truth.Payload with { ZoneOpcode = 0x7778 });
        _bed.Publish(truth, other);
        var pipeline = _bed.Pipeline(_bed.Services());
        var announced = 0;
        pipeline.CalibrationChanged += _ => Interlocked.Increment(ref announced);
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.Equal(2, pipeline.CalibrationStatus().Shared.Candidates.Count);
        var before = announced;

        var result = pipeline.RejectSharedCalibration();

        Assert.Null(result.WithdrawnProfileId);
        Assert.Equal(2, result.DroppedCandidates);
        Assert.True(announced > before, "calibration_changed must announce the refusal");
        var shared = pipeline.CalibrationStatus().Shared;
        Assert.Empty(shared.Candidates);
        Assert.True(shared.UserRejected);
        Assert.Equal(SharedCalibrationPhase.Rejected, shared.Phase);
        Assert.True(pipeline.CalibrationArmed);
        Assert.True(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
        Assert.False(_bed.Store.IsRejected(Region.Cn, Bed.Build, Bed.TemplateSha, truth.Sha));

        var sent = _bed.Transport.Requests.Count;
        Assert.Equal(SharedCheckOutcome.NotNeeded, pipeline.CheckSharedCalibrationNow());
        var pasted = pipeline.ImportCalibrationCode(truth.Code);
        Assert.Equal(SharedImportOutcome.NotApplicable, pasted.Outcome);
        Assert.Equal("USER_REJECTED", pasted.Reason);
        Assert.Matches("[\\u4e00-\\u9fff]", pasted.Message);
        Assert.DoesNotContain("0x", pasted.Message, StringComparison.OrdinalIgnoreCase);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(), 200_000));
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileStatus.UnsupportedBuild, pipeline.Current.Status);
        Assert.False(File.Exists(_bed.SharedProfilePath));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        // A restart remembers the choice: not even 立即检查 sends anything.
        var restarted = _bed.Pipeline(_bed.Services());
        restarted.Refresh(Bed.Game());
        await Bed.Idle(restarted);
        Assert.Equal(SharedCheckOutcome.NotNeeded, restarted.CheckSharedCalibrationNow());
        await Bed.Idle(restarted);
        Assert.True(restarted.CalibrationStatus().Shared.UserRejected);
        Assert.Equal(sent, _bed.Transport.Requests.Count);

        restarted.DiscardCalibration();
        await Bed.Idle(restarted);

        Assert.False(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
        Assert.False(restarted.CalibrationStatus().Shared.UserRejected);
        Assert.Equal(SharedImportOutcome.Applied, restarted.ImportCalibrationCode(truth.Code).Outcome);
    }

    [Fact]
    public void RejectingBeforeAnyBuildIsKnownChangesAndRecordsNothing()
    {
        var pipeline = _bed.Pipeline(_bed.Services());

        var result = pipeline.RejectSharedCalibration();

        Assert.Null(result.WithdrawnProfileId);
        Assert.Equal(0, result.DroppedCandidates);
        Assert.False(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
        Assert.False(pipeline.CalibrationStatus().Shared.UserRejected);
    }

    [Fact]
    public void WhileALocalProfileRecordsTheRefusalIsStillShownAndRediscoveringStillClearsIt()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services());
        Assert.Equal(ProfileOrigin.Local, pipeline.Refresh(Bed.Game()).Origin);
        Assert.False(pipeline.CalibrationArmed);

        var result = pipeline.RejectSharedCalibration();

        Assert.Null(result.WithdrawnProfileId);
        Assert.True(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
        Assert.True(pipeline.CalibrationStatus().Shared.UserRejected);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);

        pipeline.DiscardCalibration();

        Assert.False(_bed.Store.IsUserRejected(Region.Cn, Bed.Build));
        Assert.False(pipeline.CalibrationStatus().Shared.UserRejected);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
    }
}
