using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
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

    /// <summary>
    /// Audit 2026-10-20, E-2: a state file another process holds open is no answer. While it cannot be read nothing shared
    /// is fetched - the refusal it may hold is unknown - and that unknown is not remembered: once the file opens again the
    /// player's 不用共享的 stands exactly as before.
    /// </summary>
    [Fact]
    public async Task ARefusalTheStateFileCannotShowForAMomentStillStandsOnceItCan()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Assert.True(_bed.Store.RecordUserRejection(Region.Cn, Bed.Build, Bed.Confirmed));
        _bed.Publish(code);
        var state = Path.Combine(
            SharedCalibrationStore.DirectoryFor(_bed.StoreRoot, Region.Cn, Bed.Build), SharedCalibrationStore.StateFileName);
        var pipeline = _bed.Pipeline(_bed.Services());
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            pipeline.Refresh(Bed.Game());
            await Bed.Idle(pipeline);
            Assert.Empty(_bed.Transport.Requests);
            Assert.Empty(pipeline.CalibrationStatus().Shared.Candidates);
        }

        Assert.NotEqual(SharedCheckOutcome.Started, pipeline.CheckSharedCalibrationNow());
        await Bed.Idle(pipeline);

        Assert.Empty(_bed.Transport.Requests);
        Assert.True(pipeline.CalibrationStatus().Shared.UserRejected);
        Assert.Equal(SharedCalibrationSession.UserRejectedReason, pipeline.ImportCalibrationCode(code.Code).Reason);
    }

    /// <summary>
    /// Re-review 2026-10-20, RV2-2: a code pasted while the state file is held open, before this process has read the
    /// player's refusal, is refused as unknown with a sentence that says so - not with one promising a check that, while
    /// the answer is unknown, never runs.
    /// </summary>
    [Fact]
    public async Task ACodePastedBeforeTheRefusalIsKnownSaysTheRecordsCannotBeRead()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Assert.True(_bed.Store.RecordUserRejection(Region.Cn, Bed.Build, Bed.Confirmed));
        var state = Path.Combine(
            SharedCalibrationStore.DirectoryFor(_bed.StoreRoot, Region.Cn, Bed.Build), SharedCalibrationStore.StateFileName);
        var pipeline = _bed.Pipeline(_bed.Services());

        SharedImportResult pasted;
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            pipeline.Refresh(Bed.Game());
            await Bed.Idle(pipeline);
            pasted = pipeline.ImportCalibrationCode(code.Code);
        }

        Assert.Equal(SharedImportOutcome.NotApplicable, pasted.Outcome);
        Assert.Equal(SharedCalibrationSession.StateUnreadableReason, pasted.Reason);
        Assert.Equal(CollectorTexts.SharingImportStateUnreadable(), pasted.Text);
    }

    /// <summary>
    /// Re-review 2026-10-21, RV5-5: 立即检查 while the state file is held open, before this process knows whether the
    /// player refused shared calibration for the build. Nothing is fetched - an unknown counts as a refusal (E-2) - but the
    /// answer carries a sentence saying the records cannot be read, so the Desktop does not say a calibration is in use or
    /// that the player chose local calibration. Once the file opens and the refusal is known, the answer is the plain
    /// NOT_NEEDED it always was.
    /// </summary>
    [Fact]
    public async Task ACheckBeforeTheRefusalIsKnownSaysTheRecordsCannotBeRead()
    {
        Assert.True(_bed.Store.RecordUserRejection(Region.Cn, Bed.Build, Bed.Confirmed));
        var state = Path.Combine(
            SharedCalibrationStore.DirectoryFor(_bed.StoreRoot, Region.Cn, Bed.Build), SharedCalibrationStore.StateFileName);
        var pipeline = _bed.Pipeline(_bed.Services());

        SharedCheckAnswer unknown;
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            pipeline.Refresh(Bed.Game());
            await Bed.Idle(pipeline);
            unknown = pipeline.CheckSharedCalibration();
        }

        await Bed.Idle(pipeline);
        Assert.Equal(SharedCheckOutcome.NotNeeded, unknown.Outcome);
        Assert.Equal(CollectorTexts.SharingCheckStateUnreadable(), unknown.Text);
        Assert.Empty(_bed.Transport.Requests);

        var known = pipeline.CheckSharedCalibration();
        await Bed.Idle(pipeline);
        Assert.Equal(SharedCheckOutcome.NotNeeded, known.Outcome);
        Assert.Null(known.Text);
        Assert.Empty(_bed.Transport.Requests);
        Assert.True(pipeline.CalibrationStatus().Shared.UserRejected);
    }

    /// <summary>
    /// Re-review 2026-10-20, RV2-1 (the import side): once the player's refusal is known not to stand, a code pasted while
    /// the state file is held open cannot be checked against its own record either. It is refused as unknown
    /// (STATE_UNREADABLE), not as a code this build's traffic rejected, and can be pasted again once the file opens.
    /// </summary>
    [Fact]
    public async Task ACodePastedWhileTheStateFileIsHeldOpenIsRefusedAsUnknownNotAsRejected()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.Publish(Bed.Encode(code.Payload with { ZoneOpcode = 0x7778 }));
        var pipeline = _bed.Pipeline(_bed.Services());
        pipeline.Refresh(Bed.Game());
        await Bed.Idle(pipeline);
        Assert.False(pipeline.CalibrationStatus().Shared.UserRejected);
        var state = Path.Combine(
            SharedCalibrationStore.DirectoryFor(_bed.StoreRoot, Region.Cn, Bed.Build), SharedCalibrationStore.StateFileName);
        Assert.True(File.Exists(state));

        SharedImportResult pasted;
        using (new FileStream(state, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            pasted = pipeline.ImportCalibrationCode(code.Code);
        }

        Assert.Equal(SharedImportOutcome.NotApplicable, pasted.Outcome);
        Assert.Equal(SharedCalibrationSession.StateUnreadableReason, pasted.Reason);
        // Says what is wrong - the bookkeeping cannot be read - not that a check is under way (re-review RV2-2).
        Assert.Equal(CollectorTexts.SharingImportStateUnreadable(), pasted.Text);
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);
    }
}
