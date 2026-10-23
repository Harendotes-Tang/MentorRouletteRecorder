using System.Collections.Concurrent;
using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Deterministic host-fact fixtures; these tests do not query Npcap, a game or the system TCP tables.</summary>
public sealed class NetworkPathControllerTests
{
    [Fact]
    public async Task ClientSwitchAfterGameDetectionCannotAssignOldPidFactsToTheNewSource()
    {
        using var fixture = new Fixture();
        fixture.Controller.Start("wifi");
        var firstSource = Assert.Single(fixture.Sources);
        fixture.Profile.PauseNextRead();
        var pending = Task.Run(fixture.Controller.Snapshot);
        Task? switched = null;
        try
        {
            Assert.True(fixture.Profile.Entered.Wait(TimeSpan.FromSeconds(5)),
                "the snapshot did not reach the profile boundary after detecting the first client");
            fixture.Processes.Add(GameProcessLocator.Dx11ProcessName, Fixture.SecondPid,
                DateTimeOffset.UnixEpoch.AddMinutes(1), @"D:\SdoA\Other\game\ffxiv_dx11.exe");
            var choice = Assert.Single(fixture.Controller.RescanGame().Processes,
                process => process.ProcessId == Fixture.SecondPid);
            switched = Task.Run(() =>
            {
                fixture.Controller.SelectGameProcess(choice.ProcessId, choice.Token);
                fixture.Controller.Start("wifi");
            });
            await switched.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, fixture.Sources.Count);
            Assert.Equal(1, firstSource.StopCount);
            Assert.Equal(Fixture.SecondPid, fixture.Sources[^1].LastOptions!.ProcessId);
        }
        finally
        {
            fixture.Profile.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (switched is not null) await switched.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var stale = await pending;
        Assert.Equal(Fixture.FirstPid, stale.Game.ProcessId);
        Assert.Equal(fixture.Sources[^1].LastOptions!.CaptureSessionId, stale.CaptureSessionId);
        Assert.Equal(NetworkPathDiagnosticsSnapshot.Unmeasured, stale.NetworkPath);
        // This sample deliberately used the old PID after capturing the new source/generation.
        // A generation-only final check would publish it as the second client's path.
        Assert.Equal(new[] { Fixture.FirstPid }, fixture.Connections.RequestedPids);

        var current = fixture.Controller.Snapshot();
        Assert.Equal(Fixture.SecondPid, current.Game.ProcessId);
        Assert.Equal(NetworkQueryStatus.Success, current.NetworkPath.QueryIPv4);
        Assert.Equal(2, current.NetworkPath.IPv4Connections);
        Assert.Equal(new[] { Fixture.FirstPid, Fixture.SecondPid }, fixture.Connections.RequestedPids);
    }

    [Fact]
    public async Task AQueryAlreadyInFlightCannotPublishIntoAReplacementCaptureGeneration()
    {
        using var fixture = new Fixture();
        fixture.Controller.Start("wifi");
        var firstSession = fixture.Sources[^1].LastOptions!.CaptureSessionId;
        fixture.Connections.PauseNextRead();
        var pending = Task.Run(fixture.Controller.Snapshot);
        Task? restarted = null;
        try
        {
            Assert.True(fixture.Connections.Entered.Wait(TimeSpan.FromSeconds(5)),
                "the snapshot did not enter the injected host-connection query");
            restarted = Task.Run(() =>
            {
                fixture.Controller.Stop();
                fixture.Controller.Start("wifi");
            });
            // Stop/start snapshots must skip host queries while their lifecycle gate is held;
            // otherwise this restart waits on the paused inspector's cache lock.
            await restarted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
            Assert.Equal(2, fixture.Sources.Count);
            Assert.NotEqual(firstSession, fixture.Sources[^1].LastOptions!.CaptureSessionId);
        }
        finally
        {
            fixture.Connections.Release();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (restarted is not null) await restarted.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var stale = await pending;
        Assert.Equal(Fixture.FirstPid, stale.Game.ProcessId);
        Assert.Equal(NetworkPathDiagnosticsSnapshot.Unmeasured, stale.NetworkPath);
        var current = fixture.Controller.Snapshot();
        Assert.Equal(fixture.Sources[^1].LastOptions!.CaptureSessionId, current.CaptureSessionId);
        Assert.Equal(NetworkQueryStatus.Success, current.NetworkPath.QueryIPv4);
        Assert.Equal(1, current.NetworkPath.IPv4Connections);
    }

    [Fact]
    public void StartSnapshotsLeaveHostFactsUnmeasuredUntilAnOrdinarySnapshot()
    {
        using var fixture = new Fixture();

        var starting = fixture.Controller.Start("wifi");

        Assert.Equal(CaptureControllerState.Running, starting.State);
        Assert.Equal(NetworkQueryStatus.Unmeasured, starting.NetworkPath.QueryIPv4);
        Assert.Equal(NetworkQueryStatus.Unmeasured, starting.NetworkPath.QueryIPv6);
        Assert.Empty(fixture.Connections.RequestedPids);

        var current = fixture.Controller.Snapshot();
        Assert.Equal(NetworkQueryStatus.Success, current.NetworkPath.QueryIPv4);
        Assert.Equal(NetworkPathKind.SelectedInterface, current.NetworkPath.Kind);
        Assert.Equal(1, current.NetworkPath.IPv4Connections);
        Assert.Equal(new[] { Fixture.FirstPid }, fixture.Connections.RequestedPids);
    }

    private sealed class Fixture : IDisposable
    {
        internal const int FirstPid = 4321, SecondPid = 9876;
        private readonly TestDatabase _database = new();
        internal readonly FakeGameProcessProvider Processes = new();
        internal readonly GatedProfile Profile = new();
        internal readonly GatedConnections Connections = new();
        internal readonly List<FakeCaptureSource> Sources = new();
        internal readonly CaptureController Controller;

        internal Fixture()
        {
            var settings = new SettingsRepository(_database.Database, _database.Clock);
            settings.EnsureDefaults();
            Processes.Add(GameProcessLocator.Dx11ProcessName, FirstPid, DateTimeOffset.UnixEpoch,
                @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
            var adapters = new FakeAdapterProvider().Add("wifi", "Wi-Fi", addresses: "192.0.2.10");
            var tcp = new FakeProcessTcpTable().With(FirstPid, "192.0.2.10").With(SecondPid, "192.0.2.10");
            Controller = new CaptureController(new CaptureServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(Processes, new FakeGameFileReader()
                    .With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000")
                    .With(@"D:\SdoA\Other\game\ffxivgame.ver", "2024.06.18.0000.0000")),
                Adapters = new AdapterEnumerator(adapters, tcp),
                NetworkPaths = new NetworkPathInspector(adapters, Connections),
                SourceFactory = () =>
                {
                    var source = new FakeCaptureSource { PreexistingTcpConnections = 0 };
                    Sources.Add(source);
                    return source;
                },
                Profile = Profile,
                Clock = _database.Clock,
                Uptime = () => TimeSpan.Zero,
                Settings = settings,
                Sessions = new CaptureSessionRepository(_database.Database),
                Database = _database.Database,
                CollectorVersion = "0.1.0",
                DetectionTtl = TimeSpan.Zero,
                EnableFollowTimer = false,
            });
        }

        public void Dispose()
        {
            Profile.Release();
            Connections.Release();
            Controller.Dispose();
            foreach (var source in Sources) source.Dispose();
            Profile.Dispose();
            Connections.Dispose();
            _database.Dispose();
        }
    }

    private sealed class GatedProfile : IProfileStatusProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(true);
        private int _pauseNext;
        internal readonly ManualResetEventSlim Entered = new();

        internal void PauseNextRead()
        {
            Entered.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _pauseNext, 1);
        }

        internal void Release() => _release.Set();

        public ProfileStatusSnapshot Current
        {
            get
            {
                if (Interlocked.Exchange(ref _pauseNext, 0) == 1)
                {
                    Entered.Set();
                    if (!_release.Wait(TimeSpan.FromSeconds(20)))
                        throw new TimeoutException("the synthetic profile boundary was not released");
                }
                return new ProfileStatusSnapshot(ProfileStatus.Verified, null, Region.Unknown, null, null, null, null);
            }
        }

        public void Dispose() { Entered.Dispose(); _release.Dispose(); }
    }

    private sealed class GatedConnections : IProcessNetworkConnectionProvider, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(true);
        private readonly ConcurrentQueue<int> _requested = new();
        private int _pauseNext;
        internal readonly ManualResetEventSlim Entered = new();
        internal int[] RequestedPids => _requested.ToArray();

        internal void PauseNextRead()
        {
            Entered.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _pauseNext, 1);
        }

        internal void Release() => _release.Set();

        public ProcessNetworkConnectionTables Read(int processId)
        {
            _requested.Enqueue(processId);
            if (Interlocked.Exchange(ref _pauseNext, 0) == 1)
            {
                Entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("the synthetic connection query was not released");
            }
            var count = processId == Fixture.SecondPid ? 2 : 1;
            var connections = Enumerable.Range(0, count).Select(index => new ProcessNetworkConnection(
                IPAddress.Parse("192.0.2.10"), (ushort)(41000 + index),
                IPAddress.Parse("203.0.113.20"), 55000)).ToArray();
            return new ProcessNetworkConnectionTables(new(NetworkQueryStatus.Success, connections),
                new(NetworkQueryStatus.Success, Array.Empty<ProcessNetworkConnection>()));
        }

        public void Dispose() { Entered.Dispose(); _release.Dispose(); }
    }
}
