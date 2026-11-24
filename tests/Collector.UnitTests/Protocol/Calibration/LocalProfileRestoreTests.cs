using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 恢复上一份本机校准: the way back when 重新校准 turned out to be a mistake.
///
/// The real machine this is for: a player whose local profile read the server's own match
/// message - the popup spoke, everything worked - retired it to try a friend's code. The client
/// downloaded the published queue-inferred one first, she consented, and from then on she
/// recorded with the weaker shared profile and had no way back. Her own calibration was still
/// sitting on disk under another name. Retiring is one click, so undoing it has to be one too.
///
/// What the rollback is not: it is not a withdrawal of the profile it replaces. A shared profile
/// in use is let go the way losing a selection lets it go - its code is not marked contradicted,
/// the player's refusal is not recorded, and nothing it recorded is flagged.
/// </summary>
public sealed class LocalProfileRestoreTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    // ------------------------------------------------------------------ helpers

    private string RetiredPath => _bed.LocalProfilePath + LocalProfileFiles.RetiredByRequestSuffix;

    private CalibrationServices Services(bool fetch = false) =>
        _bed.Services(fetch).WithLocalProfilesIn(_bed.LocalRoot);

    // ------------------------------------------------------------------ the round trip

    [Fact]
    public void RetiringAndRestoringBringsBackTheSameFileAndRecordsWithItAgain()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var before = File.ReadAllBytes(_bed.LocalProfilePath);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        pipeline.DiscardCalibration(retireLocalProfile: true);
        Assert.False(File.Exists(_bed.LocalProfilePath));
        Assert.True(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        pipeline.DiscardCalibration(restoreLocalProfile: true);

        // Byte for byte: the rollback is a rename, never a rewrite. A profile whose hash changed
        // would be refused by the loader, and a rewritten one is no longer the file the player
        // confirmed.
        Assert.Equal(before, File.ReadAllBytes(_bed.LocalProfilePath));
        Assert.False(File.Exists(RetiredPath));
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        // Bound inside the session that is already running, so the next match is recorded.
        Bed.Feed(pipeline, session, new[] { Bed.Announced(9, 300_000) });
        Assert.Equal(9, Assert.Single(_bed.RunsOf(session)).MentorRouletteId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// A restored profile that never had the job takes up the silent "completing" role again,
    /// rather than coming back as a finished calibration nothing looks at.
    /// </summary>
    [Fact]
    public void ARestoredProfileWithoutTheJobGoesBackToLookingForIt()
    {
        _bed.WriteLocalProfileLackingTheJob(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        pipeline.DiscardCalibration(retireLocalProfile: true);

        pipeline.DiscardCalibration(restoreLocalProfile: true);

        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.True(pipeline.CalibrationArmed);
        // Armed, observing underneath, and saying nothing: that is the completing role.
        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    // ------------------------------------------------------------------ letting a shared profile go

    /// <summary>
    /// The case this exists for. The shared profile is let go, not withdrawn: its code keeps its
    /// standing, the player's refusal is not recorded, and what it recorded is left alone.
    /// </summary>
    [Fact]
    public async Task RestoringTakesOverFromASharedProfileInUseWithoutRejectingItsCode()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(Services(fetch: true));
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        // She retires her own calibration to try the friend's code.
        pipeline.DiscardCalibration(retireLocalProfile: true);
        await Bed.Idle(pipeline);
        await Bed.FeedAndSettleAsync(pipeline, session, Bed.Before(Bed.Evening(pop: false), 200_000));
        Assert.Equal(SharedCalibrationPhase.AwaitingConsent, pipeline.CalibrationStatus().Shared.Phase);
        Assert.Equal(SharedConsentOutcome.Accepted, pipeline.AcceptSharedQueueInference());
        await Bed.Idle(pipeline);
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);

        pipeline.DiscardCalibration(restoreLocalProfile: true);
        await Bed.Idle(pipeline);

        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);

        var shared = pipeline.CalibrationStatus().Shared;
        Assert.DoesNotContain(shared.Candidates, candidate => candidate.Status == SharedCandidateStatus.InUse);
        Assert.DoesNotContain(shared.Candidates, candidate => candidate.Status == SharedCandidateStatus.Rejected);
        Assert.False(shared.UserRejected);
        // Nothing the shared profile recorded is suspect: nothing contradicted it.
        Assert.DoesNotContain(_bed.RunsOf(session), run => run.PendingReview);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// Audit 2026-10-03, CS-8. A queue-inferred profile that comes back beside a shared one reading
    /// the server's match is outranked (protocol-profile-format §11.2), not unusable. It stays back
    /// under its own name, the shared profile goes on recording without its run being cut, and the
    /// player is told exactly that - not that their calibration no longer works.
    /// </summary>
    [Fact]
    public void ARestoredProfileTheCatalogueRanksBelowTheOneInUseStaysRestoredAndIsAnsweredTruthfully()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        // Retired through 重新校准 on an earlier evening.
        File.Move(_bed.LocalProfilePath, RetiredPath);
        var retired = File.ReadAllBytes(RetiredPath);
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.WriteSharedProfile(code.Payload);
        var pipeline = _bed.Pipeline(Services());
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        var session = _bed.Start(pipeline);
        var shared = pipeline.Current.ProfileId;
        Assert.True(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        // A mentor roulette is matched and in flight when the player asks.
        Bed.Feed(pipeline, session, CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(100_000, 9, 110_000)));
        var inFlight = pipeline.RunState;
        Assert.NotEqual(RunState.Idle, inFlight);

        var answer = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Equal(ErrorCodes.CalibrationNotReady, answer.Code);
        Assert.Contains("已恢复并留作备用", answer.Message, StringComparison.Ordinal);
        Assert.Contains("匹配成功的时间", answer.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("无法使用", answer.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("重新校准", answer.Message, StringComparison.Ordinal);

        // Back under its own name, byte for byte, and not offered again: nothing is left to restore.
        Assert.Equal(retired, File.ReadAllBytes(_bed.LocalProfilePath));
        Assert.False(File.Exists(RetiredPath));
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        // What was recording records on, with the run it had in flight.
        Assert.Equal(ProfileOrigin.Shared, pipeline.Current.Origin);
        Assert.Equal(shared, pipeline.Current.ProfileId);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(inFlight, pipeline.RunState);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    // ------------------------------------------------------------------ what it refuses

    [Fact]
    public void RestoreIsRefusedWhenALocalProfileIsAlreadyOnDisk()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);

        // A fresh calibration produced a profile for the same build in the meantime.
        _bed.WriteLocalProfile(CalibrationTrafficCases.ReplyState);
        var current = File.ReadAllBytes(_bed.LocalProfilePath);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Equal(ErrorCodes.CalibrationNotReady, refused.Code);
        Assert.Matches("[\\u4e00-\\u9fff]", refused.Message);
        Assert.Equal(current, File.ReadAllBytes(_bed.LocalProfilePath));
        Assert.True(File.Exists(RetiredPath));
    }

    /// <summary>
    /// A profile the machine's own traffic disproved is never offered back. The player did not
    /// put it away; the traffic did, and it would be just as wrong tomorrow.
    /// </summary>
    [Fact]
    public void AProfileTheTrafficDisprovedIsNeverOfferedBack()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        File.Move(_bed.LocalProfilePath, _bed.LocalProfilePath + LocalProfileFiles.RetiredSuffix);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());

        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);
        Assert.Throws<CollectorException>(() => pipeline.DiscardCalibration(restoreLocalProfile: true));
        Assert.True(File.Exists(_bed.LocalProfilePath + LocalProfileFiles.RetiredSuffix));
        Assert.False(File.Exists(_bed.LocalProfilePath));
    }

    [Fact]
    public void RestoreIsRefusedWhenThereIsNothingToRestore()
    {
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());

        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);
        Assert.Throws<CollectorException>(() => pipeline.DiscardCalibration(restoreLocalProfile: true));
    }

    /// <summary>A disk that refuses the rename is a refusal the player can read, never a crash.</summary>
    [Fact]
    public void ARenameThatThrowsIsAnswerdAsARefusal()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var services = Services();
        var pipeline = _bed.Pipeline(services with
        {
            RestoreLocalProfile = (_, _) => throw new IOException("the retired file is held open"),
        });
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Equal(ErrorCodes.CalibrationNotReady, refused.Code);
        Assert.True(File.Exists(RetiredPath));
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);
    }

    /// <summary>
    /// The file came back but the loader refuses it - it was written by a version whose output
    /// this one no longer accepts. It goes back into retirement rather than sitting in the
    /// directory being refused on every selection, and the player is told to calibrate again.
    /// </summary>
    [Fact]
    public void AFileTheLoaderRefusesGoesBackIntoRetirement()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);
        File.WriteAllText(RetiredPath, "{\"schema_version\": 1}");

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Contains("重新校准", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_bed.LocalProfilePath));
        Assert.True(File.Exists(RetiredPath));
    }

    /// <summary>
    /// Audit 2026-10-03, CS-8. A file the loader refuses changes nothing the catalogue selects, so
    /// what is recording is not stopped for it: the run in flight goes on under the same profile.
    /// </summary>
    [Fact]
    public void AFileTheLoaderRefusesDoesNotInterruptTheProfileInUse()
    {
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        _bed.WriteSharedProfile(code.Payload);
        Directory.CreateDirectory(Path.GetDirectoryName(RetiredPath)!);
        File.WriteAllText(RetiredPath, "{\"schema_version\": 1}");
        var pipeline = _bed.Pipeline(Services());
        Assert.Equal(ProfileOrigin.Shared, pipeline.Refresh(Bed.Game()).Origin);
        var session = _bed.Start(pipeline);
        var shared = pipeline.Current.ProfileId;
        Bed.Feed(pipeline, session, CalibrationObserverTests.Cluster(5_000, 5000)
            .Concat(CalibrationObserverTests.QueueAndPop(100_000, 9, 110_000)));
        var inFlight = pipeline.RunState;
        Assert.NotEqual(RunState.Idle, inFlight);

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Contains("重新校准", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_bed.LocalProfilePath));
        Assert.True(File.Exists(RetiredPath));
        Assert.Equal(shared, pipeline.Current.ProfileId);
        Assert.Equal(inFlight, pipeline.RunState);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// A catalogue that cannot be read for a moment says nothing about the file that came back.
    /// The restore is undone rather than the profile condemned: whatever was recording keeps
    /// recording, the retired file is the same bytes, and asking again works.
    /// </summary>
    [Fact]
    public void ACatalogueThatCannotBeReadJustNowUndoesTheRestoreInsteadOfCondemningTheProfile()
    {
        _bed.WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var before = File.ReadAllBytes(_bed.LocalProfilePath);
        var services = Services();
        var unreadable = false;
        var pipeline = _bed.Pipeline(services with
        {
            ReloadSelect = () => unreadable
                ? throw new IOException("the profile directory is being scanned")
                : services.ReloadSelect(),
        });
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);

        unreadable = true;
        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Equal(ErrorCodes.CalibrationNotReady, refused.Code);
        Assert.DoesNotContain("重新校准", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_bed.LocalProfilePath));
        Assert.Equal(before, File.ReadAllBytes(RetiredPath));
        Assert.True(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        unreadable = false;
        pipeline.DiscardCalibration(restoreLocalProfile: true);

        Assert.Equal(before, File.ReadAllBytes(_bed.LocalProfilePath));
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
    }
}
