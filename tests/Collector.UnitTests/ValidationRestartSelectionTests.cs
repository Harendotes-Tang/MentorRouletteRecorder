using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Exercises the actual sticky selector together with validation's exclusive lease.</summary>
public sealed class ValidationRestartSelectionTests
{
    private const string GamePath = @"D:\SdoA\game\ffxiv_dx11.exe";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestedRestartRebindsNewIncarnationBeforeStartingTheSource(bool reusedPid)
    {
        using var fixture = new Fixture();
        fixture.StartWaitingForRestart();
        fixture.Processes.Set();
        Assert.Equal("EXITED", fixture.Capture.RescanGame().SelectionReason);
        // With the game closed there is nothing to choose, and validation keeps saying what
        // it waits for: the restart it asked for.
        Assert.False(fixture.Capture.RescanGame().SelectionRequired);
        fixture.WaitForAnotherPoll();
        Assert.Equal("WAITING_RESTART", fixture.Validation.Snapshot()["reason"]!.GetValue<string>());
        fixture.Connections = 0;
        var nextPid = reusedPid ? 42 : 43;
        fixture.Processes.Set(Client(nextPid, 1));
        fixture.Until("state", "RECORDING");
        Assert.Equal(nextPid, fixture.Source.LastOptions!.ProcessId);
        Assert.Equal(nextPid, fixture.Capture.RescanGame().ProcessId);
        Assert.Equal(1, fixture.Source.StartCount);
        Assert.Throws<CollectorException>(() => fixture.Ownership.Acquire());
        fixture.Validation.Stop();
        fixture.Until("state", "COMPLETED");
        Assert.False(fixture.Ownership.IsHeld);
    }

    [Fact]
    public void OtherExistingClientIsNotTakenOverWhileWaitingForTheReplacement()
    {
        using var fixture = new Fixture();
        // Capture has already pinned 42 before the second client appears.
        fixture.Processes.Set(Client(42, 0), Client(99, 1));
        fixture.StartWaitingForRestart();
        fixture.Processes.Set(Client(99, 1));
        Assert.Equal("EXITED", fixture.Capture.RescanGame().SelectionReason);
        fixture.Connections = 0;
        fixture.WaitForAnotherPoll();
        Assert.Equal("WAITING_RESTART", fixture.Validation.Snapshot()["reason"]!.GetValue<string>());
        Assert.Equal(0, fixture.Source.StartCount);
        var other = Assert.Single(fixture.Capture.RescanGame().Processes);
        Assert.Equal(ErrorCodes.CaptureAlreadyRunning, Assert.Throws<CollectorException>(() =>
            fixture.Capture.SelectGameProcess(other.ProcessId, other.Token)).Code);
        fixture.Processes.Set(Client(99, 1), Client(43, 2));
        fixture.Until("state", "RECORDING");
        Assert.Equal(43, fixture.Source.LastOptions!.ProcessId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AmbiguousReplacementRequiresExplicitSelectionEvenWhenOneLaterExits(bool unreadableStart, bool unreadablePath)
    {
        using var fixture = new Fixture();
        fixture.StartWaitingForRestart();
        fixture.Connections = 0;
        fixture.Processes.Set(Client(43, 1), Client(44, 2) with
        {
            StartedAtUtc = unreadableStart ? null : DateTimeOffset.UnixEpoch.AddMinutes(2),
            ExecutablePath = unreadablePath ? null : GamePath,
        });
        fixture.Until("reason", "WAITING_GAME");
        Assert.Equal("MULTIPLE", fixture.Capture.RescanGame().SelectionReason);
        fixture.Processes.Set(Client(43, 1));
        fixture.WaitForAnotherPoll();
        Assert.Equal(0, fixture.Source.StartCount);
        Assert.True(fixture.Capture.RescanGame().SelectionRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentInstallationOrUnreadableIdentityCannotSatisfyRestart(bool unreadableStart)
    {
        using var fixture = new Fixture();
        fixture.StartWaitingForRestart();
        fixture.Connections = 0;
        fixture.Processes.Set(Client(43, 1) with
        {
            ExecutablePath = unreadableStart ? GamePath : @"D:\Other\game\ffxiv_dx11.exe",
            StartedAtUtc = unreadableStart ? null : DateTimeOffset.UnixEpoch.AddMinutes(1),
        });
        Assert.Equal("EXITED", fixture.Capture.RescanGame().SelectionReason);
        fixture.WaitForAnotherPoll();
        Assert.Equal(0, fixture.Source.StartCount);
        Assert.Null(fixture.Capture.RescanGame().ProcessId);
    }

    private static GameProcessCandidate Client(int pid, int minute) => new(
        pid, GameProcessLocator.Dx11ProcessName, DateTimeOffset.UnixEpoch.AddMinutes(minute), GamePath, false);

    private sealed class Processes : IGameProcessProvider
    {
        private GameProcessCandidate[] _clients = [Client(42, 0)];
        public void Set(params GameProcessCandidate[] clients) => Volatile.Write(ref _clients, clients);
        public IReadOnlyList<GameProcessCandidate> ByName(string name) =>
            name == GameProcessLocator.Dx11ProcessName ? Volatile.Read(ref _clients) : [];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDatabase _db = new();
        private int _connections = 1, _polls;
        public int Connections { set => Volatile.Write(ref _connections, value); }
        public readonly Processes Processes = new();
        public readonly CaptureOwnership Ownership = new();
        public readonly FakeCaptureSource Source = new();
        public readonly CaptureController Capture;
        public readonly CaptureValidationController Validation;

        public Fixture()
        {
            var npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy());
            Capture = new CaptureController(new CaptureServices
            {
                Game = new GameProcessLocator(Processes, new FakeGameFileReader()
                    .With(@"D:\SdoA\game\ffxivgame.ver", "2026.01")),
                Ownership = Ownership, Npcap = npcap, EnableFollowTimer = false,
            });
            Assert.Equal(42, Capture.RescanGame().ProcessId);
            Validation = new CaptureValidationController(_db.Path, Ownership, new CaptureValidationServices
            {
                LocateGame = Capture.RescanGame,
                LocateRestartedGame = previous =>
                {
                    var game = Capture.RescanGameAfterValidationRestart(previous);
                    Interlocked.Increment(ref _polls);
                    return game;
                },
                PollInterval = TimeSpan.FromMilliseconds(10),
                Trace = new CaptureTraceServices
                {
                    Npcap = npcap,
                    Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"), new FakeProcessTcpTable()),
                    TcpConnectionCounter = (_, _) => Volatile.Read(ref _connections),
                    SourceFactory = () => Source,
                },
            });
        }

        public void StartWaitingForRestart()
        {
            Validation.Start("wifi");
            Until("reason", "WAITING_RESTART");
        }

        public void Until(string field, string value) => Assert.True(SpinWait.SpinUntil(
            () => Validation.Snapshot()[field]!.GetValue<string>() == value, 5000), Validation.Snapshot().ToJsonString());

        public void WaitForAnotherPoll()
        {
            var target = Volatile.Read(ref _polls) + 2;
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref _polls) >= target, 5000));
        }

        public void Dispose()
        {
            Validation.Dispose();
            Capture.Dispose();
            _db.Dispose();
        }
    }
}
