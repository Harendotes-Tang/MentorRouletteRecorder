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
