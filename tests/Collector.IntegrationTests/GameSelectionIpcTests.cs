using System.Net;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class GameSelectionIpcTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASingleSelectedClientAutomaticallyContinuesAfterRestart(bool reusedPid)
    {
        var processes = new RestartingClient();
        var network = new RestartNetwork(processes);
        var sources = new List<FakeCaptureSource>();
        var services = CaptureFakes.Ready(new FakeCaptureSource()) with
        {
            Game = new GameProcessLocator(processes),
            Adapters = new AdapterEnumerator(network, network),
            SourceFactory = () => { var source = new FakeCaptureSource(); sources.Add(source); return source; },
        };
        await using var fixture = CaptureServerFixture.Start(services);
        await using var client = await fixture.ConnectAsync();
        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        var choice = Assert.Single(status["game_processes"]!.AsArray())!;
        var selection = new JsonObject
        {
            ["process_id"] = choice["process_id"]!.DeepClone(),
            ["selection_token"] = choice["selection_token"]!.DeepClone(),
        };
        (await client.SendAsync("SelectGameProcess", selection)).Require();
        fixture.Host.Capture.Poll();
        var oldSession = Assert.Single(sources).LastOptions!.CaptureSessionId;
        processes.Client = null;
        fixture.Host.Capture.Poll();
        status = (await client.SendAsync("GetCaptureStatus")).Require();
        Assert.Equal("EXITED", status["game_selection_reason"]!.GetValue<string>());
        Assert.Equal(1, sources[0].StopCount);

        var nextPid = reusedPid ? 4321 : 4322;
        processes.Client = new(nextPid, GameProcessLocator.Dx11ProcessName,
            DateTimeOffset.UnixEpoch.AddMinutes(1), CaptureFakes.RememberedExecutable, false);
        fixture.Host.Capture.Poll();
        status = (await client.SendAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/CaptureStatus", status, "restarted game");
        Assert.False(status["game_selection_required"]!.GetValue<bool>());
        Assert.Equal(nextPid, status["ffxiv_process_id"]!.GetValue<int>());
        Assert.Equal(2, sources.Count);
        Assert.Equal(nextPid, sources[1].LastOptions!.ProcessId);
        Assert.NotEqual(oldSession, sources[1].LastOptions!.CaptureSessionId);
        Assert.Equal(ErrorCodes.FfxivNotRunning,
            (await client.SendAsync("SelectGameProcess", selection.DeepClone().AsObject())).ErrorCode);
    }

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

    [Fact]
    public async Task WithTheOnlyClientClosedNothingIsOfferedForChoiceAndStartingIsRetryable()
    {
        var processes = new RestartingClient();
        var services = CaptureFakes.Ready(new FakeCaptureSource()) with { Game = new GameProcessLocator(processes) };
        await using var fixture = CaptureServerFixture.Start(services);
        await using var client = await fixture.ConnectAsync();
        fixture.Host.Capture.Poll();
        processes.Client = null;
        fixture.Host.Capture.Poll();

        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/CaptureStatus", status, "closed game");
        Assert.Equal("EXITED", status["game_selection_reason"]!.GetValue<string>());
        Assert.False(status["game_selection_required"]!.GetValue<bool>());
        Assert.Empty(status["game_processes"]!.AsArray());

        var refused = await client.SendAsync("StartCapture", new JsonObject());
        Assert.Equal(ErrorCodes.FfxivNotRunning, refused.ErrorCode);
        Assert.True(refused.Payload["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ASelectionTokenIsAcceptedInEitherLetterCase()
    {
        var processes = new Clients();
        var services = CaptureFakes.Ready(new FakeCaptureSource()) with { Game = new GameProcessLocator(processes) };
        await using var fixture = CaptureServerFixture.Start(services);
        await using var client = await fixture.ConnectAsync();
        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        var choice = status["game_processes"]!.AsArray().Single(p => p!["process_id"]!.GetValue<int>() == 9876)!;

        var selected = (await client.SendAsync("SelectGameProcess", new JsonObject
        {
            ["process_id"] = 9876,
            ["selection_token"] = choice["selection_token"]!.GetValue<string>().ToUpperInvariant(),
        })).Require();

        Assert.Equal(9876, selected["ffxiv_process_id"]!.GetValue<int>());
    }

    [Fact]
    public async Task AMissingProcessIdNamesItsFieldLikeEveryOtherMissingField()
    {
        var services = CaptureFakes.Ready(new FakeCaptureSource()) with { Game = new GameProcessLocator(new Clients()) };
        await using var fixture = CaptureServerFixture.Start(services);
        await using var client = await fixture.ConnectAsync();

        var refused = await client.SendAsync("SelectGameProcess", new JsonObject
        {
            ["selection_token"] = Guid.NewGuid().ToString("D"),
        });

        Assert.Equal(ErrorCodes.BadRequest, refused.ErrorCode);
        Assert.Equal("payload.process_id", refused.Payload["field"]!.GetValue<string>());
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

    private sealed class RestartingClient : IGameProcessProvider
    {
        public GameProcessCandidate? Client { get; set; } = new(
            4321, GameProcessLocator.Dx11ProcessName, DateTimeOffset.UnixEpoch, CaptureFakes.RememberedExecutable, false);
        public IReadOnlyList<GameProcessCandidate> ByName(string name) =>
            name == GameProcessLocator.Dx11ProcessName && Client is { } client ? [client] : [];
    }

    private sealed class RestartNetwork(RestartingClient processes) : IAdapterProvider, IProcessTcpTable
    {
        private static readonly IPAddress Address = IPAddress.Parse("192.168.31.77");
        public IReadOnlyList<AdapterInfo> List() =>
            [new("restart-adapter", "Test", "Test", true, false, [Address])];
        public IReadOnlyList<IPAddress> LocalAddresses(int processId) =>
            processes.Client?.ProcessId == processId ? [Address] : [];
    }
}
