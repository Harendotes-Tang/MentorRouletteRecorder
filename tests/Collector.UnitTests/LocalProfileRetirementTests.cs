using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 重新校准: the way out for a player whose own machine calibrated the wrong thing.
///
/// Once a local profile is in force the calibration card disappears, and with it the only two
/// entry points the software had - 清空进度并重新观察 and 导入校准码, which the Collector refuses
/// unless calibration is running. A player who suspects their profile is wrong was left renaming
/// a file in Explorer. These tests pin the request that retires it: the file is put away rather
/// than deleted, calibration re-arms and starts observing inside the running session, and a
/// friend's share code is accepted again.
///
/// What it deliberately does not do is what the contradiction withdrawal does: nothing accuses
/// this profile, so its records are not marked for review and its opcodes are not refused. The
/// player asked; the traffic said nothing.
/// </summary>
public sealed class LocalProfileRetirementTests : IDisposable
{
    private readonly Bed _bed = new();
    private readonly List<(Region Region, string GameBuild, string Suffix)> _retired = new();

    public void Dispose() => _bed.Dispose();

    // ------------------------------------------------------------------ helpers

    private string ProfilePath => LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build);

    private IReadOnlyList<MentorRun> RunsOf(string session) =>
        new RunRepository(_bed.Db.Database).Query(null, null, 1, 50).Items
            .Where(run => string.Equals(run.CaptureSessionId, session, StringComparison.Ordinal))
            .ToArray();

    /// <summary>Writes the local profile one synthetic evening would have produced.</summary>
    /// <param name="trafficCase">Case from <see cref="CalibrationTrafficCases"/>.</param>
    /// <param name="root">Directory to write into; the bed's local root by default.</param>
    private string WriteLocalProfile(string trafficCase, string? root = null) =>
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(trafficCase), Bed.Template, Bed.Build, Bed.Confirmed,
            root ?? _bed.LocalRoot).Path;

    /// <summary>The bed's services with the local-profile seams wired and the retirement watched.</summary>
    private CalibrationServices Services()
    {
        var services = _bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot);
        var retire = services.RetireLocalProfile;
        return services with
        {
            RetireLocalProfile = (region, build, suffix) =>
            {
                _retired.Add((region, build, suffix));
                retire(region, build, suffix);
            },
        };
    }

    /// <summary>One match of the shape a learned announcement profile declares.</summary>
    /// <param name="rouletteId">Value at the declared roulette offset.</param>
    /// <param name="at">Session time in milliseconds.</param>
    private static DecodedMessage Announced(byte rouletteId, long at) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, CalibrationTrafficCases.Announce,
        CalibrationObserverTests.Bytes(64, (16, rouletteId)), at);

    // ------------------------------------------------------------------ the retirement

    [Fact]
    public void TheProfileThePlayerRetiredIsPutAwayAndCalibrationObservesAgain()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var session = _bed.Start(pipeline);

        // Before: the card is gone and a friend's code has nowhere to go.
        var code = _bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState);
        Assert.Equal(SharedImportOutcome.NotApplicable, pipeline.ImportCalibrationCode(code.Code).Outcome);

        pipeline.DiscardCalibration(retireLocalProfile: true);

        Assert.Equal((Region.Cn, Bed.Build, LocalProfileFiles.RetiredByRequestSuffix), Assert.Single(_retired));
        Assert.False(File.Exists(ProfilePath));
        Assert.True(File.Exists(ProfilePath + LocalProfileFiles.RetiredByRequestSuffix));
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);

        // Observing inside the session that is already running, so the player need not restart.
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        // And the other way out is open again.
        Assert.Equal(SharedImportOutcome.Applied, pipeline.ImportCalibrationCode(code.Code).Outcome);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The difference from the contradiction withdrawal, and the reason this is a separate path:
    /// the player asked for a fresh start, the traffic did not accuse the profile of anything.
    /// Marking an evening of correct records for review would be the software inventing a fault.
    /// </summary>
    [Fact]
    public void RetiringAProfileMarksNoneOfItsRecordsForReview()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, new[] { Announced(9, 50_000) });
        var recorded = Assert.Single(RunsOf(session));
        Assert.False(recorded.PendingReview);

        pipeline.DiscardCalibration(retireLocalProfile: true);

        var afterwards = new RunRepository(_bed.Db.Database).Get(recorded.RunId)!;
        Assert.False(afterwards.PendingReview);
        // The run in flight is closed the way a stopped capture closes it, not left open.
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.NotNull(afterwards.EndedAtUtc);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The opcode is not refused either. A player who retires a working profile by mistake, or
    /// whose replacement search finds the very same message again, must be able to get it back.
    /// </summary>
    [Fact]
    public void TheRetiredProfilesMatchMessageMayBeProposedAgain()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        pipeline.DiscardCalibration(retireLocalProfile: true);
        Bed.Feed(pipeline, session, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.Announcement));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        var status = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, status.State);
        Assert.Contains(status.Events, item => string.Equals(item.Kind, "pop", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what it leaves alone

    [Fact]
    public void APlainDiscardLeavesTheProfileInForce()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        pipeline.DiscardCalibration();

        Assert.Empty(_retired);
        Assert.True(File.Exists(ProfilePath));
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// 重新校准 is only ever offered beside a profile this machine wrote, but the request can
    /// arrive against any selection. A shipped profile is not this machine's guess to retract and
    /// a shared one has its own withdrawal, so the flag degrades into the plain discard.
    /// </summary>
    [Fact]
    public void AProfileThisMachineDidNotWriteIsNotRetired()
    {
        var shipped = Path.Combine(_bed.Root, "shipped");
        WriteLocalProfile(CalibrationTrafficCases.Announcement, shipped);
        var selector = new ProfileSelector(ProfileCatalog.LoadMerged(shipped, _bed.LocalRoot, _bed.SharedRoot));
        ProfileSelection Select(GameProcessDetection game) => selector.Select(game.Region, game.GameBuild);
        var pipeline = new LiveProtocolPipeline(
            _bed.Db.Database, _bed.Db.Clock, new LiveEventBus(_bed.Db.Clock), Select, null,
            Services() with { ReloadSelect = () => Select });
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Shipped, pipeline.Current.Origin);
        var session = _bed.Start(pipeline);

        pipeline.DiscardCalibration(retireLocalProfile: true);

        Assert.Empty(_retired);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.True(File.Exists(LocalProfileFiles.PathFor(shipped, Region.Cn, Bed.Build)));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    // ------------------------------------------------------------------ failure paths

    /// <summary>
    /// The likelier disk failure, mirrored from the contradiction withdrawal: the file is held
    /// open so it cannot be renamed, but the directory still lists it. Binding it again would
    /// hand the player back the very profile they asked the software to stop using.
    /// </summary>
    [Fact]
    public void AProfileThatCannotBePutAwayIsStillNotBoundAgain()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var services = _bed.Services(fetch: false) with
        {
            RetireLocalProfile = (_, _, _) => throw new IOException("the profile file is held open"),
        };
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        pipeline.DiscardCalibration(retireLocalProfile: true);

        Assert.True(File.Exists(ProfilePath));
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }
}
