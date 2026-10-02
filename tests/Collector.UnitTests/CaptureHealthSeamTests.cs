using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The capture-health seam: the controller hands each session's silent reason, the connections it
/// attached to midway and the adapter's drops to its health listener when the session starts, on
/// every poll and one last time before the session ends, so the pipeline can tell an absence that
/// means something from one the capture could not have seen.
/// </summary>
public sealed class CaptureHealthSeamTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;
    private readonly List<FakeCaptureSource> _sources = new();
    private readonly FakeGameProcessProvider _processes = new();
    private readonly FakeProcessTcpTable _tcp = new();
    private readonly FakeAdapterProvider _adapters = new();
    private readonly RecordingHealthListener _health = new();
    private int? _preexisting = 0;
    private TimeSpan _uptime = TimeSpan.Zero;

    public CaptureHealthSeamTests()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        _settings.EnsureDefaults();
        _settings.SetSetting(CaptureController.FollowGameSetting, "false");
        _adapters.Add("wifi", "Wi-Fi", addresses: "192.168.31.77");
        _processes.Add(GameProcessLocator.Dx11ProcessName, 4321, DateTimeOffset.UnixEpoch, @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
        _tcp.With(4321, "192.168.31.77");
    }

    public void Dispose() => _database.Dispose();

    private FakeCaptureSource Source => _sources[^1];

    [Fact]
    public void TheStartEveryPollAndTheStopEachHandTheSessionsHealthToTheListener()
    {
        using var controller = Build();
        controller.Start();
        var started = Assert.Single(_health.Readings);
        Assert.True(started.IsHealthy);

        Source.IngressCounters = new CaptureIngressCounters(RawPackets: 10, AdapterDropped: 4);
        controller.Poll();
        controller.Stop();

        var readings = _health.Readings;
        Assert.Equal(3, readings.Count);
        Assert.Single(readings.Select(reading => reading.CaptureSessionId).Distinct());
        Assert.All(readings, reading => Assert.Equal(0, reading.PreexistingConnections));
        Assert.Equal(new long[] { 0, 4, 4 }, readings.Select(reading => reading.AdapterDropped));
        Assert.False(readings[^1].IsHealthy);
    }

    [Fact]
    public void AMidstreamVerdictReachesTheListenerWithTheConnectionsItAttachedTo()
    {
        _preexisting = 3;
        using var controller = Build();
        controller.Start();

        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        var reading = _health.Readings[^1];
        Assert.Equal(CaptureSilentReason.Midstream, reading.SilentReason);
        Assert.Equal(3, reading.PreexistingConnections);
        Assert.False(reading.IsHealthy);
    }

    [Fact]
    public void AListenerThatThrowsNeverFailsCapture()
    {
        using var controller = Build(new ThrowingHealthListener());

        controller.Start();
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.Snapshot().State);
        controller.Stop();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void StreamDamageReachesTheListenerBeforeTheNextMessageIsParsed(long damaged, bool healthy)
    {
        using var delivered = new ManualResetEventSlim();
        CaptureSessionHealth? atDelivery = null;
        using var controller = Build(sink: new CallbackSink(_ =>
        {
            atDelivery = _health.Readings[^1];
            delivered.Set();
        }));
        controller.Start();
        // A new SYN can increment StreamResets without losing any owned observations.
        Source.IngressCounters = new CaptureIngressCounters(StreamResets: 1, DamagedGameDirections: damaged);
        Source.PushOpcode(1);
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(healthy, atDelivery!.IsHealthy);
        Assert.Equal(damaged, atDelivery.DamagedGameDirections);
        controller.Stop();
        controller.Start();
        Assert.True(_health.Readings[^1].IsHealthy);
    }

    [Fact]
    public void QueueOverflowIsUnhealthyBeforeTheNextSurvivingMessageIsParsed()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var resumed = new ManualResetEventSlim();
        CaptureSessionHealth? atDelivery = null;
        var calls = 0;
        _settings.SetSetting(CaptureController.QueueCapacitySetting, "512");
        using var controller = Build(sink: new CallbackSink(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            else
            {
                atDelivery = _health.Readings[^1];
                resumed.Set();
            }
        }));
        controller.Start();
        try
        {
            Source.PushOpcode(1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 600; i++) Source.PushOpcode(2);
            release.Set();
            Assert.True(resumed.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(atDelivery!.IsHealthy);
            Assert.True(atDelivery.DecodedMessagesDropped > 0);
            Assert.Equal(0, atDelivery.AdapterDropped);
        }
        finally { release.Set(); }
        controller.Stop();
    }

    private CaptureController Build(ICaptureHealthListener? health = null, IDecodedMessageSink? sink = null) => new(new CaptureServices
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(
            _processes,
            new FakeGameFileReader().With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000")),
        Adapters = new AdapterEnumerator(_adapters, _tcp),
        SourceFactory = () =>
        {
            var source = new FakeCaptureSource { PreexistingTcpConnections = _preexisting };
            _sources.Add(source);
            return source;
        },
        Profile = new FakeProfileStatusProvider(ProfileStatus.Verified),
        Health = health ?? _health,
        Sink = sink,
        Clock = _database.Clock,
        Uptime = () => _uptime,
        Settings = _settings,
        Sessions = new CaptureSessionRepository(_database.Database),
        Database = _database.Database,
        CollectorVersion = "0.1.0",
        DetectionTtl = TimeSpan.Zero,
        EnableFollowTimer = false,
    });

    private sealed class RecordingHealthListener : ICaptureHealthListener
    {
        private readonly List<CaptureSessionHealth> _readings = new();

        public IReadOnlyList<CaptureSessionHealth> Readings
        {
            get
            {
                lock (_readings)
                {
                    return _readings.ToArray();
                }
            }
        }

        public void OnCaptureHealth(CaptureSessionHealth health)
        {
            lock (_readings)
            {
                _readings.Add(health);
            }
        }
    }

    private sealed class ThrowingHealthListener : ICaptureHealthListener
    {
        public void OnCaptureHealth(CaptureSessionHealth health) => throw new InvalidOperationException("listener failed");
    }

    private sealed class CallbackSink(Action<DecodedMessage> accept) : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message) => accept(message);
    }
}
