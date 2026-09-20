using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
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

    private string ProfilePath => LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build);

    private string RetiredPath => ProfilePath + LocalProfileFiles.RetiredByRequestSuffix;

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    private CalibrationServices Services(bool fetch = false) =>
        _bed.Services(fetch).WithLocalProfilesIn(_bed.LocalRoot);

    /// <summary>Writes the local profile one synthetic evening would have produced.</summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    private string WriteLocalProfile(string trafficCase) =>
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(trafficCase), Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot).Path;

    /// <summary>The same profile with its job message withheld, as a machine wrote it before the job rule worked.</summary>
    private string WriteLocalProfileLackingTheJob(string trafficCase)
    {
        var draft = CalibrationTrafficCases.Derive(trafficCase);
        var withheld = draft with
        {
            Messages = draft.Messages.Where(message => message.Name != "PLAYER_JOB").ToArray(),
            SampleCounts = draft.SampleCounts
                .Where(sample => !sample.Key.Contains("PLAYER_JOB", StringComparison.Ordinal))
                .ToDictionary(sample => sample.Key, sample => sample.Value, StringComparer.Ordinal),
        };
        return LocalProfileWriter.Write(withheld, Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot).Path;
    }

    /// <summary>One match of the shape a learned announcement profile declares.</summary>
    /// <param name="rouletteId">Value at the declared roulette offset.</param>
    /// <param name="at">Session time in milliseconds.</param>
    private static DecodedMessage Announced(byte rouletteId, long at) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, CalibrationTrafficCases.Announce,
        CalibrationObserverTests.Bytes(64, (16, rouletteId)), at);

    // ------------------------------------------------------------------ the round trip

    [Fact]
    public void RetiringAndRestoringBringsBackTheSameFileAndRecordsWithItAgain()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var before = File.ReadAllBytes(ProfilePath);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        pipeline.DiscardCalibration(retireLocalProfile: true);
        Assert.False(File.Exists(ProfilePath));
        Assert.True(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        pipeline.DiscardCalibration(restoreLocalProfile: true);

        // Byte for byte: the rollback is a rename, never a rewrite. A profile whose hash changed
        // would be refused by the loader, and a rewritten one is no longer the file the player
        // confirmed.
        Assert.Equal(before, File.ReadAllBytes(ProfilePath));
        Assert.False(File.Exists(RetiredPath));
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        // Bound inside the session that is already running, so the next match is recorded.
        Bed.Feed(pipeline, session, new[] { Announced(9, 300_000) });
        Assert.Equal(9, Assert.Single(RunsOf(session)).MentorRouletteId);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// A restored profile that never had the job takes up the silent "completing" role again,
    /// rather than coming back as a finished calibration nothing looks at.
    /// </summary>
    [Fact]
    public void ARestoredProfileWithoutTheJobGoesBackToLookingForIt()
    {
        WriteLocalProfileLackingTheJob(CalibrationTrafficCases.Announcement);
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
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.QueueRequest);
        _bed.Publish(code);
        var pipeline = _bed.Pipeline(Services(fetch: true));
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        // She retires her own calibration to try the friend's code.
        pipeline.DiscardCalibration(retireLocalProfile: true);
        await Bed.Idle(pipeline);
        Bed.Feed(pipeline, session, Bed.Before(Bed.Evening(pop: false), 200_000));
        await Bed.Idle(pipeline);
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
        Assert.DoesNotContain(RunsOf(session), run => run.PendingReview);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    // ------------------------------------------------------------------ what it refuses

    [Fact]
    public void RestoreIsRefusedWhenALocalProfileIsAlreadyOnDisk()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);

        // A fresh calibration produced a profile for the same build in the meantime.
        WriteLocalProfile(CalibrationTrafficCases.ReplyState);
        var current = File.ReadAllBytes(ProfilePath);
        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Equal(ErrorCodes.CalibrationNotReady, refused.Code);
        Assert.Matches("[\\u4e00-\\u9fff]", refused.Message);
        Assert.Equal(current, File.ReadAllBytes(ProfilePath));
        Assert.True(File.Exists(RetiredPath));
    }

    /// <summary>
    /// A profile the machine's own traffic disproved is never offered back. The player did not
    /// put it away; the traffic did, and it would be just as wrong tomorrow.
    /// </summary>
    [Fact]
    public void AProfileTheTrafficDisprovedIsNeverOfferedBack()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        File.Move(ProfilePath, ProfilePath + LocalProfileFiles.RetiredSuffix);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());

        Assert.False(pipeline.CalibrationStatus().RetiredLocalProfileAvailable);
        Assert.Throws<CollectorException>(() => pipeline.DiscardCalibration(restoreLocalProfile: true));
        Assert.True(File.Exists(ProfilePath + LocalProfileFiles.RetiredSuffix));
        Assert.False(File.Exists(ProfilePath));
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
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
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
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        pipeline.DiscardCalibration(retireLocalProfile: true);
        File.WriteAllText(RetiredPath, "{\"schema_version\": 1}");

        var refused = Assert.Throws<CollectorException>(
            () => pipeline.DiscardCalibration(restoreLocalProfile: true));

        Assert.Contains("重新校准", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(ProfilePath));
        Assert.True(File.Exists(RetiredPath));
    }
}
