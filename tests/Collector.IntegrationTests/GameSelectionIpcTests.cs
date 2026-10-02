using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class GameSelectionIpcTests
{
    [Fact]
    public async Task SelectionAndExitAreConsistentAcrossThePipe()
    {
        var processes = new Clients();
        var sources = new List<FakeCaptureSource>();
        var services = CaptureFakes.Ready(new FakeCaptureSource()) with
        {
            Game = new GameProcessLocator(processes),
            SourceFactory = () => { var source = new FakeCaptureSource(); sources.Add(source); return source; },
        };
        await using var fixture = CaptureServerFixture.Start(services);
        await using var client = await fixture.ConnectAsync();
        fixture.Host.Capture.Poll();
        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        Assert.True(status["game_selection_required"]!.GetValue<bool>());
        Assert.Empty(sources);
        var choice = status["game_processes"]!.AsArray().First()!;
        var selection = new JsonObject
        {
            ["process_id"] = choice["process_id"]!.DeepClone(),
            ["selection_token"] = choice["selection_token"]!.DeepClone(),
        };
        var selected = (await client.SendAsync("SelectGameProcess", selection)).Require();
        ContractSchema.Validate("$defs/CaptureStatus", selected, "selected game");
        Assert.Equal(4321, selected["ffxiv_process_id"]!.GetValue<int>());
        fixture.Host.Capture.Poll();
        Assert.Equal(4321, Assert.Single(sources).LastOptions!.ProcessId);

        processes.FirstRunning = false;
        fixture.Host.Capture.Poll();
        status = (await client.SendAsync("GetCaptureStatus")).Require();
        Assert.Equal("EXITED", status["game_selection_reason"]!.GetValue<string>());
        Assert.Null(status["ffxiv_process_id"]);
        Assert.Equal(1, sources[0].StopCount);
        Assert.Equal(ErrorCodes.FfxivNotRunning,
            (await client.SendAsync("SelectGameProcess", selection.DeepClone().AsObject())).ErrorCode);
        Assert.Single(sources);
    }

    private sealed class Clients : IGameProcessProvider
    {
        public bool FirstRunning { get; set; } = true;
        public IReadOnlyList<GameProcessCandidate> ByName(string name)
        {
            if (name != GameProcessLocator.Dx11ProcessName) return Array.Empty<GameProcessCandidate>();
            var rows = new List<GameProcessCandidate>
            {
                new(9876, name, DateTimeOffset.UnixEpoch.AddMinutes(1), CaptureFakes.RememberedExecutable, false),
            };
            if (FirstRunning) rows.Add(new(4321, name, DateTimeOffset.UnixEpoch, CaptureFakes.RememberedExecutable, false));
            return rows;
        }
    }
}
