using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage.Repositories;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A local profile this machine calibrated for itself is never looked at again once it binds -
/// a shared one is watched and can be withdrawn, a local one had nothing. The real machine's
/// 1.2.0 bug is what that costs: a VERIFIED local profile whose CONTENT_FINDER_POP was really
/// the retainer bell's list, so opening the bell announced a match and the next opening cancelled
/// it, and the evening's real matches were never recorded at all. These tests pin the way out:
/// live traffic that disproves a learned announcement withdraws it, marks what it recorded for
/// review, and puts calibration back to work.
/// </summary>
public sealed class LocalProfileWithdrawalTests : IDisposable
{
    private readonly Bed _bed = new();
    private readonly List<(Region Region, string GameBuild)> _retired = new();

    public void Dispose() => _bed.Dispose();

    // ------------------------------------------------------------------ helpers

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
    /// <param name="select">Selector the reload hands back; the bed's own by default.</param>
    private CalibrationServices Services(Func<GameProcessDetection, ProfileSelection>? select = null)
    {
        var services = _bed.Services(fetch: false).WithLocalProfilesIn(_bed.LocalRoot);
        var retire = services.RetireLocalProfile;
        return services with
        {
            ReloadSelect = select is null ? services.ReloadSelect : () => select,
            RetireLocalProfile = (region, build, suffix) =>
            {
                _retired.Add((region, build));
                retire(region, build, suffix);
            },
        };
    }

    /// <summary>One message of the shape the learned announcement profile declares as the match.</summary>
    /// <param name="rouletteId">Value at the declared roulette offset.</param>
    /// <param name="at">Session time in milliseconds.</param>
    private static DecodedMessage Announced(byte rouletteId, long at) => CalibrationObserverTests.Message(
        MessageDirection.Inbound, CalibrationTrafficCases.Announce,
        CalibrationObserverTests.Bytes(64, (16, rouletteId)), at);

    /// <summary>
    /// What the player really did: opened the retainer bell. Ten rows of the declared shape inside
    /// one second, each carrying its own slot number where the profile reads the roulette id.
    /// </summary>
    /// <param name="at">Session time of the first row.</param>
    private static IEnumerable<DecodedMessage> BellRows(long at) =>
        Enumerable.Range(0, 10).Select(slot => Announced((byte)slot, at + slot));

    /// <summary>The same list against a profile that reads the match off the queue reply's state.</summary>
    /// <param name="at">Session time of the first row.</param>
    private static IEnumerable<DecodedMessage> ReplyRows(long at) =>
        Enumerable.Range(0, 10).Select(slot => CalibrationObserverTests.Message(
            MessageDirection.Inbound, CalibrationTrafficCases.Reply,
            CalibrationObserverTests.Bytes(40, (9, 3), (16, (byte)slot)), at + slot));

    /// <summary>The same list against a profile that stands the player's own queue request in for a match.</summary>
    /// <param name="at">Session time of the first row.</param>
    private static IEnumerable<DecodedMessage> RequestRows(long at) =>
        Enumerable.Range(0, 10).Select(slot => CalibrationObserverTests.Message(
            MessageDirection.Outbound, CalibrationTrafficCases.Request,
            CalibrationObserverTests.Bytes(24, (0, (byte)slot)), at + slot));

    /// <summary>Login, a mentor match, the duty, and another roulette two minutes later.</summary>
    private static DecodedMessage[] OrdinaryEvening() => CalibrationObserverTests.Cluster(5_000, 5000)
        .Append(Announced(9, 60_000))
        .Concat(CalibrationObserverTests.Cluster(65_000, 1039))
        .Concat(CalibrationObserverTests.Cluster(125_000, 5000))
        .Append(Announced(2, 190_000))
        .OrderBy(message => message.Mono)
        .ToArray();

    // ------------------------------------------------------------------ the withdrawal

    [Fact]
    public void ALocalAnnouncementTheTrafficDisprovesWithdrawsItself()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);

        // One match recorded under the profile before the traffic catches it out.
        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, new[] { Announced(9, 50_000) });
        var recorded = Assert.Single(RunsOf(session));
        Assert.False(recorded.PendingReview);

        Bed.Feed(pipeline, session, BellRows(100_000));

        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal((Region.Cn, Bed.Build), Assert.Single(_retired));
        Assert.False(File.Exists(LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build)));
        Assert.True(File.Exists(
            LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build) + LocalProfileFiles.RetiredSuffix));

        // Calibration is observing again, not idle and not done, so this build can be learned afresh.
        Assert.True(pipeline.CalibrationArmed);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);

        // The run it left in flight is closed, and everything it recorded is marked for review.
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Null(pipeline.GetCurrentRun().Run);
        var flagged = new RunRepository(_bed.Db.Database).Get(recorded.RunId)!;
        Assert.True(flagged.PendingReview);
        Assert.NotNull(flagged.EndedAtUtc);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);

        // And the opcode is refused for the rest of this process: a whole evening that would have
        // named it again infers the match from the queue instead.
        _bed.Play(pipeline, CalibrationTrafficCases.Traffic(CalibrationTrafficCases.Announcement), hour: 1);
        var status = pipeline.CalibrationStatus();
        Assert.Equal(CalibrationState.Ready, status.State);
        Assert.DoesNotContain(status.Events, item => string.Equals(item.Kind, "pop", StringComparison.Ordinal));
    }

    [Fact]
    public void ARejectedPopOpcodeIsNeverProposedAgainInThisProcess()
    {
        var coordinator = new CalibrationCoordinator();
        coordinator.Arm(Bed.Template, Region.Cn, Bed.Build);
        coordinator.Begin("calibration-session");
        coordinator.RejectPopOpcode(CalibrationTrafficCases.Announce);
        foreach (var message in CalibrationTrafficCases.Traffic(CalibrationTrafficCases.Announcement))
        {
            coordinator.Accept(message);
        }

        coordinator.Stop();

        var draft = coordinator.CurrentDraft()!;
        Assert.NotEqual(CalibrationMatchSource.Announcement, draft.MatchSource);
        Assert.DoesNotContain(draft.Messages, message => message.Opcode == CalibrationTrafficCases.Announce);
    }

    // ------------------------------------------------------------------ what the watch leaves alone

    [Fact]
    public void TheSameBurstAgainstAShippedProfileChangesNothing()
    {
        var shipped = Path.Combine(_bed.Root, "shipped");
        WriteLocalProfile(CalibrationTrafficCases.Announcement, shipped);
        var selector = new ProfileSelector(ProfileCatalog.LoadMerged(shipped, _bed.LocalRoot, _bed.SharedRoot));
        ProfileSelection Select(GameProcessDetection game) => selector.Select(game.Region, game.GameBuild);
        var pipeline = new LiveProtocolPipeline(
            _bed.Db.Database, _bed.Db.Clock, new LiveEventBus(_bed.Db.Clock), Select, null, Services(Select));
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Shipped, pipeline.Current.Origin);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, BellRows(100_000));

        Assert.Empty(_retired);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
    }

    [Fact]
    public void TheSameBurstAgainstALocalProfileWithASelectorChangesNothing()
    {
        WriteLocalProfile(CalibrationTrafficCases.ReplyState);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, ReplyRows(100_000));

        Assert.Empty(_retired);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
    }

    [Fact]
    public void TheSameBurstAgainstAQueueInferredLocalProfileChangesNothing()
    {
        WriteLocalProfile(CalibrationTrafficCases.QueueRequest);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());
        Assert.Equal(ProfileOrigin.Local, pipeline.Current.Origin);
        Assert.True(pipeline.Current.ProfileId is not null);

        var session = _bed.Start(pipeline);
        Bed.Feed(pipeline, session, RequestRows(100_000));

        Assert.Empty(_retired);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
    }

    [Fact]
    public void OrdinaryPlayAgainstALocalAnnouncementProfileWithdrawsNothing()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var pipeline = _bed.Pipeline(Services());
        pipeline.Refresh(Bed.Game());

        var session = _bed.Play(pipeline, OrdinaryEvening(), hour: 0);

        Assert.Empty(_retired);
        Assert.Equal(ProfileStatus.Verified, pipeline.Current.Status);
        var run = Assert.Single(RunsOf(session));
        Assert.Equal(9, run.MentorRouletteId);
        Assert.False(run.PendingReview);
    }

    // ------------------------------------------------------------------ failure paths

    [Fact]
    public void TheWithdrawalSurvivesARetirementAndAReloadThatThrow()
    {
        WriteLocalProfile(CalibrationTrafficCases.Announcement);
        var services = _bed.Services(fetch: false) with
        {
            RetireLocalProfile = (_, _, _) => throw new IOException("the profile file is held open"),
            ReloadSelect = () => throw new InvalidOperationException("the profile directory cannot be read"),
        };
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        var session = _bed.Start(pipeline);

        Bed.Feed(pipeline, session, BellRows(100_000));

        // The profile stops recording whatever the disk answers; only putting it away is lost.
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.True(File.Exists(LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build)));
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The likelier disk failure: the file is held open, so it cannot be renamed, but the directory
    /// still lists it. The reloaded catalogue then offers the very profile the traffic has just
    /// disproved, and binding it again would replay the whole fault at the next bell opening.
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

        Bed.Feed(pipeline, session, BellRows(100_000));

        Assert.True(File.Exists(LocalProfileFiles.PathFor(_bed.LocalRoot, Region.Cn, Bed.Build)));
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Current.Status);
        Assert.Equal(CalibrationState.Observing, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }
}
