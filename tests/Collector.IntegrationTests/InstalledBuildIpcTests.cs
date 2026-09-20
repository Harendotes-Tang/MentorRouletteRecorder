using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The wire when the client version is known but the game is not running.
///
/// This is a state the contract already allows - <c>game_build</c> has never depended on
/// <c>ffxiv_running</c> - and the two must be reported independently: a client that infers "no
/// version yet" from "not running" would be wrong, and one that infers "the game is up" from a
/// version would start a capture that cannot work.
/// </summary>
public sealed class InstalledBuildIpcTests
{
    private const string Build = "2026.09.18.0000.0000";

    [Fact]
    public async Task GetCaptureStatus_ReportsTheInstalledBuildWithTheGameClosed()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(Build));

        var payload = (await fixture.CallAsync("GetCaptureStatus")).Require();

        Assert.False(payload["ffxiv_running"]!.GetValue<bool>());
        Assert.Null(payload["ffxiv_process_id"]);
        Assert.Equal(Build, payload["game_build"]!.GetValue<string>());
        Assert.Equal("CN", payload["region"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetStatus_NeverRendersTheRememberedInstallPath()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(Build));

        var payload = (await fixture.CallAsync("GetStatus")).Require();
        var game = payload["game"]!;

        Assert.False(game["running"]!.GetValue<bool>());
        Assert.Equal(Build, game["game_build"]!.GetValue<string>());
        Assert.Equal(0, game["instance_count"]!.GetValue<int>());

        // The path itself stays inside the service: install_path_readable speaks for the
        // running client's path and there is no running client (docs/privacy-boundary.md §5).
        Assert.False(game["install_path_readable"]!.GetValue<bool>());
        Assert.DoesNotContain("SdoA", payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheShippingHostWritesTheInstallNoteBesideItsDatabase()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source, Build));

        // One ordinary status poll is enough: the locator sees the client and remembers where
        // it found it, so the next launch knows the version before the game is started.
        _ = (await fixture.CallAsync("GetCaptureStatus")).Require();

        var note = Path.Combine(fixture.DataDirectory, DatabasePaths.GameInstallFileName);
        Assert.True(File.Exists(note), note + " was not written");
        var written = File.ReadAllText(note);
        Assert.Contains(FileGameInstallMemory.PathField, written, StringComparison.Ordinal);
        Assert.Contains("ffxiv_dx11.exe", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCapture_IsStillRefused_WhileTheGameIsNotRunning()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(Build));

        var response = await fixture.CallAsync("StartCapture");

        // Knowing the version says nothing about whether there is a client to observe.
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.FfxivNotRunning, response.ErrorCode);
    }
}
