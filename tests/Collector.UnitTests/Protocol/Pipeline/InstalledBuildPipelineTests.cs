using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using Bed = MentorRecorder.Collector.UnitTests.SharedCalibrationTestBed;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What the protocol layer does with a client version read from the install directory while the
/// game is closed: it selects, arms and fetches exactly as it does for a running client, because
/// none of it ever looked at whether the game was up.
///
/// The status poll calls <c>Refresh</c> about once a second, so the one place this could go
/// wrong is the six-hour download throttle; it is asserted here over repeated refreshes.
/// </summary>
public sealed class InstalledBuildPipelineTests : IDisposable
{
    private readonly Bed _bed = new();

    public void Dispose() => _bed.Dispose();

    [Fact]
    public void ASelectionIsMadeForABuildReadWhileTheGameIsClosed()
    {
        LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.ReplyState),
            Bed.Template, Bed.Build, Bed.Confirmed, _bed.LocalRoot);
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        // Exactly the detection the locator returns from the remembered install: not running,
        // no process, but a region and a build.
        var current = pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.Verified, current.Status);
        Assert.Equal(ProfileOrigin.Local, current.Origin);
        Assert.Equal(Bed.Build, current.GameBuild);
        Assert.False(pipeline.CalibrationArmed);

        // Patch day: the launcher updates the client, and the build the running client reports
        // reselects exactly as a build change does today.
        Assert.NotEqual(ProfileStatus.Verified, pipeline.Refresh(Bed.Game(Bed.OtherBuild)).Status);
    }

    [Fact]
    public void AnUnknownBuildArmsCalibrationWithNoCaptureSession()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));

        var current = pipeline.Refresh(Bed.Game());

        Assert.Equal(ProfileStatus.UnsupportedBuild, current.Status);
        Assert.True(pipeline.CalibrationArmed);
        var status = pipeline.CalibrationStatus();
        // WAITING is precisely "armed, but nothing is observing yet": no session has started,
        // so there is no observer, no progress and no timeline.
        Assert.Equal(CalibrationState.Waiting, status.State);
        Assert.Equal(Bed.Build, status.GameBuild);
        Assert.NotNull(status.TemplateProfileId);
        Assert.Empty(status.Events);
    }

    /// <summary>
    /// A client that is running but whose version file cannot be read: another region's client, as
    /// often as not. Audit 2026-11-03, C-1: the arming made for the client before it stayed in force,
    /// so that client's traffic fed - and was saved into - the first client's evidence. The arming is
    /// kept only while no client runs at all.
    /// </summary>
    [Fact]
    public void ARunningClientWhoseBuildCannotBeReadDisarmsCalibration()
    {
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false));
        pipeline.Refresh(Bed.Game());
        Assert.True(pipeline.CalibrationArmed);

        // The game closed and nothing is remembered: the template is kept for the client's return.
        Assert.True(pipeline.Refresh(GameProcessDetection.NotRunning).CalibrationActive);
        Assert.True(pipeline.CalibrationArmed);

        var current = pipeline.Refresh(RunningClient(Region.Tc, build: null));

        Assert.False(current.CalibrationActive);
        Assert.False(pipeline.CalibrationArmed);
        var session = _bed.Start(pipeline);
        Assert.Equal(CalibrationState.Idle, pipeline.CalibrationStatus().State);
        pipeline.OnCaptureStopped(session, CaptureEndReason.UserStop);
    }

    /// <summary>
    /// The evidence belongs to the region calibration was armed for, not to whatever the locator says
    /// now: 清空校准进度 with the game closed removed an "unknown region" file and left the armed
    /// region's evidence to come back at the next launch (audit 2026-11-03, C-1).
    /// </summary>
    [Fact]
    public void ObservingAgainWithTheGameClosedForgetsTheArmedRegionsEvidence()
    {
        var deleted = new List<(Region Region, string Build)>();
        var services = _bed.Services(fetch: false) with
        {
            DeleteEvidence = (region, build) => deleted.Add((region, build)),
        };
        var pipeline = _bed.Pipeline(services);
        pipeline.Refresh(Bed.Game());
        pipeline.Refresh(GameProcessDetection.NotRunning);
        Assert.True(pipeline.CalibrationArmed);

        pipeline.DiscardCalibration();

        Assert.Equal(new[] { (Region.Cn, Bed.Build) }, deleted);
    }

    /// <summary>
    /// Audit 2026-11-03, D2-1. The capture card's explanation of why nothing records was the profile
    /// directory's English reason inside a Chinese frame, in every interface language, and on the main
    /// path of every traditional-Chinese and global client. Each reason is now a catalogue sentence of
    /// its own, with nothing untranslated inside it.
    /// </summary>
    [Theory]
    [InlineData(Region.Tc, "2026.10.01.0000.0000", false, "profile.unavailable.region_unsupported")]
    [InlineData(Region.Global, "2026.10.01.0000.0000", false, "profile.unavailable.region_unsupported")]
    [InlineData(Region.Cn, "2099.01.01.0000.0000", false, "profile.unavailable.no_match")]
    [InlineData(Region.Cn, null, false, "profile.unavailable.build_unknown")]
    [InlineData(Region.Unknown, "2026.08.05.0000.0000", false, "profile.unavailable.region_unknown")]
    [InlineData(Region.Cn, "2099.01.01.0000.0000", true, "profile.unavailable.failed")]
    public void WhyNothingRecordsIsACatalogueSentence(Region region, string? build, bool selectorFails, string key)
    {
        var shipped = new ProfileSelector(ProfileCatalog.LoadDefault());
        var pipeline = new Protocol.Pipeline.LiveProtocolPipeline(
            _bed.Db.Database, _bed.Db.Clock, new Ipc.LiveEventBus(_bed.Db.Clock),
            game => selectorFails
                ? throw new IOException("C:\\Users\\someone\\AppData\\Local\\MentorRecorder is locked")
                : shipped.Select(game.Region, game.GameBuild),
            null, _bed.Services(fetch: false));

        var message = pipeline.Refresh(RunningClient(region, build)).Message;

        Assert.NotNull(message);
        Assert.Equal(key, message!.Key);
        Assert.Empty(message.Args);
        Assert.DoesNotMatch("[A-Za-z]", message.Chinese);
    }

    /// <summary>
    /// Re-review 2026-11-03, RV6-1, through the request the desktop sends. With the game closed and calibration armed from
    /// the remembered install, 清空校准进度 deletes the evidence file; the pop opcode the traffic disproved is written
    /// back, alone, so the next start still refuses it. Nothing else of the discarded evidence comes back.
    /// </summary>
    [Fact]
    public void ObservingAgainWithTheGameClosedKeepsTheDisprovedOpcodesOnDisk()
    {
        var evidence = Path.Combine(_bed.Root, "evidence");
        var observed = CalibrationTrafficCases.Observe(CalibrationTrafficCases.Traffic(CalibrationTrafficCases.MarkerOffset));
        var disproved = new HashSet<ushort> { CalibrationTrafficCases.Announce };
        Assert.True(CalibrationEvidenceStore.Save(evidence, Region.Cn, Bed.Build, Bed.TemplateSha, observed with
        {
            Rejections = CalibrationRejections.None with { PopOpcodes = disproved, DisprovedPopOpcodes = disproved },
        }));
        var pipeline = _bed.Pipeline(_bed.Services(fetch: false).WithEvidenceIn(evidence));
        pipeline.Refresh(Bed.Game());
        Assert.True(pipeline.CalibrationArmed);

        pipeline.DiscardCalibration();

        var kept = CalibrationEvidenceStore.Load(evidence, Region.Cn, Bed.Build, Bed.TemplateSha);
        Assert.NotNull(kept);
        Assert.Equal(0, kept!.MessagesSeen);
        Assert.Empty(kept.Pairs);
        Assert.Equal(disproved, kept.Rejections.DisprovedPopOpcodes);
        Assert.Equal(disproved, kept.Rejections.PopOpcodes);
    }

    private static GameProcessDetection RunningClient(Region region, string? build) =>
        GameProcessDetection.NotRunning with
        {
            Running = true,
            ProcessId = 4242,
            ProcessName = "ffxiv_dx11",
            StartedAtUtc = Bed.Confirmed,
            Region = region,
            GameBuild = build,
            InstanceCount = 1,
        };

    [Fact]
    public async Task OneDownloadIsSentHoweverOftenTheStatusIsPolled()
    {
        _bed.Publish(_bed.CodeFromEveningA(CalibrationTrafficCases.ReplyState));
        var pipeline = _bed.Pipeline(_bed.Services());

        for (var poll = 0; poll < 20; poll++)
        {
            pipeline.Refresh(Bed.Game());
            await Bed.Idle(pipeline);
        }

        var index = SharedCalibrationClient.IndexUri(SharedCalibrationSource.GithubRaw).AbsoluteUri;
        Assert.Equal(1, _bed.Transport.Requests.Count(uri =>
            string.Equals(uri.AbsoluteUri, index, StringComparison.Ordinal)));
        Assert.Equal(SharedCalibrationPhase.Verifying, pipeline.CalibrationStatus().Shared.Phase);
    }
}
