using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CaptureValidationTests
{
    private static GameProcessDetection Game(int pid = 42) => new(true, pid, "ffxiv_dx11", DateTimeOffset.UnixEpoch,
        Region.Cn, "2026.01", 1, @"D:\SdoA\game\ffxiv_dx11.exe", Array.Empty<string>());

    private static CaptureValidationServices Services(Func<GameProcessDetection> game, ICaptureSource? source = null,
        Func<int, System.Net.IPAddress?, int?>? tcp = null, FakeAdapterProvider? adapters = null,
        Func<string, TextWriter>? writer = null) => new()
    {
        LocateGame = game,
        PollInterval = TimeSpan.FromMilliseconds(10),
        Trace = new CaptureTraceServices
        {
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Adapters = new AdapterEnumerator(adapters ?? new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"), new FakeProcessTcpTable()),
            SourceFactory = () => source ?? new FakeCaptureSource(),
            TcpConnectionCounter = tcp ?? ((_, _) => 0),
            TraceWriterFactory = writer,
        },
    };

    private static void Until(CaptureValidationController controller, string field, string value) =>
        Assert.True(SpinWait.SpinUntil(() => controller.Snapshot()[field]!.GetValue<string>() == value, 5000), controller.Snapshot().ToJsonString());

    [Theory]
    [InlineData("WAITING_GAME")]
    [InlineData("WAITING_IDENTITY")]
    [InlineData("WAITING_ADAPTER")]
    [InlineData("WAITING_CONNECTION_CHECK")]
    public void NotReadyRemainsWaitingWithNoFile(string reason)
    {
        using var db = new TestDatabase();
        var game = reason == "WAITING_GAME" ? GameProcessDetection.NotRunning :
            reason == "WAITING_IDENTITY" ? Game() with { GameBuild = null } : Game();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => game, tcp: (_, _) => null));
        controller.Start(reason == "WAITING_ADAPTER" ? null : "wifi");
        Until(controller, "reason", reason);
        Assert.Null(controller.Snapshot()["trace_path"]);
    }

    [Fact]
    public void DownAdapterIsNeverUsedEvenWhenExplicitlySelected()
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(),
            adapters: new FakeAdapterProvider().Add("wifi", "WiFi", isUp: false, addresses: "192.168.1.2")));
        controller.Start("wifi");
        Until(controller, "reason", "WAITING_ADAPTER");
    }

    [Fact]
    public void MultipleTrafficAdaptersAreNotGuessed()
    {
        using var db = new TestDatabase();
        var services = Services(() => Game());
        services = services with { Trace = services.Trace with
        {
            Adapters = new AdapterEnumerator(new FakeAdapterProvider()
                .Add("a", "A", addresses: "192.168.1.2").Add("b", "B", addresses: "192.168.2.2"),
                new FakeProcessTcpTable().With(42, "192.168.1.2", "192.168.2.2")),
        } };
        using var controller = new CaptureValidationController(db.Path, new(), services);
        controller.Start();
        Until(controller, "reason", "WAITING_ADAPTER");
    }

    [Fact]
    public void CandidateIsRecheckedBeforeSourceStart()
    {
        using var db = new TestDatabase();
        var reads = 0;
        var source = new FakeCaptureSource();
        using var controller = new CaptureValidationController(db.Path, new(), Services(
            () => Interlocked.Increment(ref reads) == 1 ? Game() : GameProcessDetection.NotRunning, source));
        controller.Start("wifi");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref reads) >= 2, 5000));
        Until(controller, "reason", "WAITING_GAME");
        controller.Stop();
        Until(controller, "state", "COMPLETED");
        Assert.Equal(0, source.StartCount);
        Assert.Null(controller.Snapshot()["trace_path"]);
    }

    [Fact]
    public void SamePidCannotBecomeEligibleAfterConnectionsDropButNewPidCan()
    {
        using var db = new TestDatabase();
        var game = Game();
        var connections = 1;
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Volatile.Read(ref game), tcp: (_, _) => Volatile.Read(ref connections)));
        controller.Start("wifi");
        Until(controller, "reason", "WAITING_RESTART");
        Volatile.Write(ref connections, 0);
        Assert.False(SpinWait.SpinUntil(() => controller.Snapshot()["state"]!.GetValue<string>() == "RECORDING", 100));
        Volatile.Write(ref game, Game(43));
        Until(controller, "state", "RECORDING");
        controller.Stop();
        Until(controller, "state", "COMPLETED");
        var snapshot = controller.Snapshot();
        var path = snapshot["trace_path"]!.GetValue<string>();
        Assert.Equal(CaptureTraceDigest.Compute(path), snapshot["sha256"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(File.ReadLines(path).First())!["synthetic"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("pop")]
    [InlineData("entered")]
    [InlineData("victory")]
    [InlineData("left")]
    public void AllowedMarkerIsSavedExactlyOnce(string marker)
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game()));
        Assert.Throws<CollectorException>(() => controller.AddMarker(marker));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        controller.AddMarker(marker);
        controller.Stop();
        Until(controller, "state", "COMPLETED");
        var lines = File.ReadLines(controller.Snapshot()["trace_path"]!.GetValue<string>()).Select(l => JsonNode.Parse(l)!).ToArray();
        Assert.Single(lines, l => l["marker"]?.GetValue<string>() == marker);
        Assert.Throws<CollectorException>(() => controller.AddMarker(marker));
    }

    [Theory]
    [InlineData("QUEUED")]
    [InlineData(" queued ")]
    [InlineData("name")]
    [InlineData("")]
    public void MarkerWhitelistIsStrict(string marker)
    {
        using var db = new TestDatabase();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game()));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        Assert.Throws<CollectorException>(() => controller.AddMarker(marker));
        Assert.Equal(0, controller.Snapshot()["marker_count"]!.GetValue<long>());
    }

    [Fact]
    public void WriterFailureIsFailedWithoutHashAndReleasesOwnership()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => Game(), writer: _ => throw new IOException("sensitive detail")));
        controller.Start("wifi");
        Until(controller, "state", "FAILED");
        Assert.Null(controller.Snapshot()["sha256"]);
        Assert.DoesNotContain("sensitive detail", controller.Snapshot().ToJsonString());
        using var lease = ownership.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceFaultPreservesHashOfClosedPartialEvidence(bool duringStart)
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource { FaultDuringStart = duringStart ? "private detail" : null };
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(), source));
        controller.Start("wifi");
        if (!duringStart) { Until(controller, "state", "RECORDING"); source.Fault("private detail"); }
        Until(controller, "state", "FAILED");
        Assert.NotNull(controller.Snapshot()["sha256"]);
        Assert.DoesNotContain("private detail", controller.Snapshot().ToJsonString());
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void WaitingOwnsFormalAndFollowCaptureAndDuplicateStartIsRejected()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => GameProcessDetection.NotRunning));
        using var formal = new CaptureController(new CaptureServices { Ownership = ownership, EnableFollowTimer = false });
        controller.Start();
        Assert.Throws<CollectorException>(() => controller.Start());
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() => formal.Start()).Code);
        controller.Stop();
        Until(controller, "state", "COMPLETED");
        using var lease = ownership.Acquire();
        Assert.Throws<CollectorException>(() => controller.Start());
    }

    [Fact]
    public async Task StopIsAsynchronousAndLeaseRemainsOwnedUntilSourceIsReleased()
    {
        using var db = new TestDatabase();
        using var source = new BlockingSource { BlockStop = true };
        var ownership = new CaptureOwnership();
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => Game(), source));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        try
        {
            Assert.Equal("STOPPING", controller.Stop()["state"]!.GetValue<string>());
            Assert.True(source.Stopping.Wait(5000));
            Assert.Throws<CollectorException>(() => ownership.Acquire());
            Assert.Null(controller.Snapshot()["sha256"]);
            var disposing = Task.Run(controller.Dispose);
            Assert.False(disposing.IsCompleted);
            source.ReleaseStop.Set();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("COMPLETED", controller.Snapshot()["state"]!.GetValue<string>());
            Assert.True(source.Disposed);
            using var lease = ownership.Acquire();
        }
        finally { source.ReleaseStop.Set(); }
    }

    [Fact]
    public void CancellingDuringSourceStartupStillCreatesNoSavedTrace()
    {
        using var db = new TestDatabase();
        using var source = new BlockingSource { BlockStart = true };
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(), source));
        controller.Start("wifi");
        Assert.True(source.Starting.Wait(5000));
        try
        {
            Assert.Equal("WAITING", controller.Snapshot()["state"]!.GetValue<string>());
            Assert.Null(controller.Snapshot()["message_count"]);
            controller.Stop();
        }
        finally { source.ReleaseStart.Set(); }
        Until(controller, "state", "COMPLETED");
        Assert.Equal("CANCELLED", controller.Snapshot()["reason"]!.GetValue<string>());
        Assert.Null(controller.Snapshot()["trace_path"]);
        Assert.Empty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(db.Path)!, "traces"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FormalShutdownDuringStartMustNotReleaseLeaseOrLeakTheSource()
    {
        using var db = new TestDatabase();
        using var source = new BlockingSource { BlockStart = true };
        var ownership = new CaptureOwnership();
        var processes = new FakeGameProcessProvider().Add("ffxiv_dx11", 42, path: @"D:\SdoA\game\ffxiv_dx11.exe");
        using var formal = new CaptureController(new CaptureServices
        {
            Ownership = ownership, EnableFollowTimer = false,
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Game = new GameProcessLocator(processes, new FakeGameFileReader()),
            Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"), new FakeProcessTcpTable()),
            Profile = new FakeProfileStatusProvider(ProfileStatus.Verified), SourceFactory = () => source,
        });
        var starting = Task.Run(() => formal.Start("wifi"));
        Assert.True(source.Starting.Wait(5000));
        try
        {
            var disposing = Task.Run(formal.Dispose);
            await Task.Delay(50);
            Assert.Throws<CollectorException>(() => ownership.Acquire());
            source.ReleaseStart.Set();
            await Task.WhenAll(starting, disposing).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(source.Disposed);
            Assert.False(source.IsRunning);
            using var lease = ownership.Acquire();
        }
        finally { source.ReleaseStart.Set(); }
    }

    [Fact]
    public async Task QueueMustFullyDrainBeforeWriterCloseOrOwnershipRelease()
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        var ownership = new CaptureOwnership();
        ControlledWriter? writer = null;
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => Game(), source,
            writer: path => writer = new ControlledWriter(path) { BlockMessage = true }));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        source.PushOpcode(0x1234);
        Assert.True(writer!.MessageEntered.Wait(5000));
        try
        {
            controller.Stop();
            await Task.Delay(2200); // Beyond the legacy queue's two-second abandonment timeout.
            Assert.Equal("STOPPING", controller.Snapshot()["state"]!.GetValue<string>());
            Assert.False(writer.Closed);
            Assert.Null(controller.Snapshot()["sha256"]);
            Assert.Throws<CollectorException>(() => ownership.Acquire());
        }
        finally { writer.ReleaseMessage.Set(); }
        Until(controller, "state", "COMPLETED");
        Assert.True(writer.Closed);
        Assert.Equal(1, controller.Snapshot()["message_count"]!.GetValue<long>());
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("message")]
    [InlineData("close")]
    public void EveryWriterFailureIsFailedAndNeverPublishesHash(string fault)
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(), source,
            writer: path => new ControlledWriter(path) { Failure = fault }));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        if (fault == "marker") controller.AddMarker("queued");
        else if (fault == "message") source.PushOpcode(0x1234);
        else controller.Stop();
        Until(controller, "state", "FAILED");
        Assert.Null(controller.Snapshot()["sha256"]);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public void LateCallbacksCannotChangeFinalizedCountersOrNewSession()
    {
        using var db = new TestDatabase();
        var source = new LateSource();
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(), source));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        var old = source.Observer!;
        controller.Stop();
        Until(controller, "state", "COMPLETED");
        old.OnDecodeError();
        old.OnFault("late private fault", null);
        Assert.Equal("COMPLETED", controller.Snapshot()["state"]!.GetValue<string>());
        Assert.Equal(0, controller.Snapshot()["decode_error_count"]!.GetValue<long>());
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        old.OnFault("obsolete", null);
        Assert.Equal("RECORDING", controller.Snapshot()["state"]!.GetValue<string>());
    }

    [Fact]
    public void FailedSourceReleaseRetainsOwnershipUntilShutdownRetriesSuccessfully()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        var source = new LateSource { FailCleanup = true };
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => Game(), source));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        controller.Stop();
        Until(controller, "state", "FAILED");
        Assert.Throws<CollectorException>(() => ownership.Acquire());
        Assert.Null(controller.Snapshot()["sha256"]);
        source.FailCleanup = false;
        controller.Dispose();
        using var lease = ownership.Acquire();
    }

    private static CaptureServices FormalServices(ICaptureSource source, CaptureOwnership ownership) => new()
    {
        Ownership = ownership, EnableFollowTimer = false,
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(new FakeGameProcessProvider().Add("ffxiv_dx11", 42, path: @"D:\SdoA\game\ffxiv_dx11.exe"), new FakeGameFileReader()),
        Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"), new FakeProcessTcpTable()),
        Profile = new FakeProfileStatusProvider(ProfileStatus.Verified), SourceFactory = () => source,
    };

    [Fact]
    public void FormalCleanupFailureAlsoRetainsTheSharedLease()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        var source = new LateSource { FailCleanup = true };
        using var formal = new CaptureController(FormalServices(source, ownership));
        formal.Start("wifi");
        formal.Stop();
        Assert.Throws<CollectorException>(() => ownership.Acquire());
        Assert.Equal(CaptureControllerState.Faulted, formal.State);
        source.FailCleanup = false;
        formal.Dispose();
        Assert.False(source.IsRunning);
        using var lease = ownership.Acquire();
    }

    [Fact]
    public void AutomaticFollowCannotStartDuringValidationWaiting()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        var source = new FakeCaptureSource();
        var settings = new MentorRecorder.Collector.Storage.Repositories.SettingsRepository(db.Database, db.Clock);
        settings.EnsureDefaults();
        settings.SetSetting(CaptureController.FollowGameSetting, "true");
        settings.SetSetting(CaptureController.AdapterSetting, "\"wifi\"");
        using var formal = new CaptureController(FormalServices(source, ownership) with { Settings = settings });
        using var validation = new CaptureValidationController(db.Path, ownership, Services(() => GameProcessDetection.NotRunning));
        validation.Start();
        formal.Poll();
        Assert.Equal(0, source.StartCount);
        validation.Stop();
        Until(validation, "state", "COMPLETED");
        formal.Poll();
        Assert.Equal(1, source.StartCount);
        Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<CollectorException>(() => validation.Start()).Code);
    }

    [Fact]
    public void LateFormalFaultCannotStopValidationOrANewerFormalSession()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        var source = new LateSource();
        using var formal = new CaptureController(FormalServices(source, ownership));
        formal.Start("wifi");
        var old = source.Observer!;
        formal.Stop();
        formal.Start("wifi");
        old.OnFault("obsolete session", null);
        Assert.Equal(CaptureControllerState.Running, formal.State);
    }

    private sealed class LateSource : ICaptureSource
    {
        public bool FailCleanup;
        public ICaptureSourceObserver? Observer;
        public string Kind => "synthetic-late";
        public bool IsRunning { get; private set; }
        public bool ReadsGameExecutable => false;
        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer) { Observer = observer; IsRunning = true; }
        public void Stop() { if (FailCleanup) throw new IOException(); IsRunning = false; }
        public void Dispose() => Stop();
    }

    private sealed class ControlledWriter(string path) : TextWriter
    {
        private readonly StreamWriter _inner = new(path, append: false);
        public readonly ManualResetEventSlim MessageEntered = new(), ReleaseMessage = new();
        public readonly ManualResetEventSlim MarkerEntered = new(), ReleaseMarker = new();
        public bool BlockMessage, BlockMarker, Closed;
        public string? Failure;
        public override System.Text.Encoding Encoding => _inner.Encoding;
        public override void Write(string? value)
        {
            if (value?.Contains("\"seq\"", StringComparison.Ordinal) == true)
            {
                MessageEntered.Set();
                if (BlockMessage && !ReleaseMessage.Wait(10000)) throw new TimeoutException();
                if (Failure == "message") throw new IOException("message failed");
            }
            if (value?.Contains("\"marker\"", StringComparison.Ordinal) == true)
            {
                MarkerEntered.Set();
                if (BlockMarker && !ReleaseMarker.Wait(10000)) throw new TimeoutException();
                if (Failure == "marker") throw new IOException("marker failed");
            }
            _inner.Write(value);
        }
        public override void Write(char value) => _inner.Write(value);
        public override void Flush() => _inner.Flush();
        protected override void Dispose(bool disposing)
        {
            _inner.Dispose(); Closed = true;
            if (Failure == "close") throw new IOException("close failed");
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task TimedOutTraceDrainRetainsItsWriterAndOwnershipUntilCallbacksReallyStop()
    {
        using var db = new TestDatabase();
        var ownership = new CaptureOwnership();
        var source = new FakeCaptureSource();
        ControlledWriter? writer = null;
        var services = Services(() => Game(), source,
            writer: path => writer = new ControlledWriter(path) { BlockMessage = true })
            with { ShutdownTimeout = TimeSpan.FromMilliseconds(40) };
        using var controller = new CaptureValidationController(db.Path, ownership, services);
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        source.PushOpcode(1);
        Assert.True(writer!.MessageEntered.Wait(5000));
        source.PushOpcode(2);
        try
        {
            controller.Stop();
            await Task.Delay(150);
            Assert.Equal("STOPPING", controller.Snapshot()["state"]!.GetValue<string>());
            Assert.False(writer.Closed);
            Assert.Null(controller.Snapshot()["sha256"]);
            Assert.Throws<CollectorException>(() => ownership.Acquire());
        }
        finally { writer.ReleaseMessage.Set(); }
        Until(controller, "state", "FAILED");
        Assert.True(writer.Closed);
        Assert.Equal(1, controller.Snapshot()["queue_dropped"]!.GetValue<long>());
        Assert.Null(controller.Snapshot()["sha256"]);
        using var lease = ownership.Acquire();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerReceiptNeverWaitsForDiskAndAcceptedMarkerDrainsAfterStop(bool blockedMarker)
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        ControlledWriter? writer = null;
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-05T01:02:03Z"));
        var services = Services(() => Game(), source, writer: path => writer = new ControlledWriter(path)
        { BlockMessage = !blockedMarker, BlockMarker = blockedMarker });
        long elapsedMs = 123;
        services = services with { Trace = services.Trace with { Clock = clock }, Elapsed = () => TimeSpan.FromMilliseconds(Interlocked.Read(ref elapsedMs)) };
        using var controller = new CaptureValidationController(db.Path, new(), services);
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        if (!blockedMarker)
        {
            source.PushOpcode(0x1234);
            Assert.True(writer!.MessageEntered.Wait(5000));
        }
        var receipt = Task.Run(() => controller.AddMarker("queued"));
        try
        {
            Assert.Same(receipt, await Task.WhenAny(receipt, Task.Delay(500)));
            Assert.Equal(0, (await receipt)["marker_count"]!.GetValue<long>());
            if (blockedMarker) Assert.True(writer!.MarkerEntered.Wait(5000));
            var snapshot = Task.Run(controller.Snapshot);
            var stop = Task.Run(controller.Stop);
            await Task.WhenAll(snapshot, stop).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("STOPPING", (await stop)["state"]!.GetValue<string>());
            clock.UtcNow = clock.UtcNow.AddHours(1);
            Interlocked.Exchange(ref elapsedMs, 60000);
            await Task.Delay(80);
        }
        finally { writer!.ReleaseMessage.Set(); writer.ReleaseMarker.Set(); }
        await receipt;
        Until(controller, "state", "COMPLETED");
        var path = controller.Snapshot()["trace_path"]!.GetValue<string>();
        var marker = Assert.Single(File.ReadLines(path).Select(line => JsonNode.Parse(line)!), row => row["marker"] is not null);
        Assert.Equal("2026-09-05T01:02:03.000Z", marker["at_utc"]!.GetValue<string>());
        Assert.Equal(123, marker["t_ms"]!.GetValue<long>());
        Assert.Equal(1, controller.Snapshot()["marker_count"]!.GetValue<long>());
    }

    [Fact]
    public void FullMarkerQueueRefusesNewWorkWithoutDroppingAcceptedMarkers()
    {
        using var db = new TestDatabase();
        ControlledWriter? writer = null;
        using var controller = new CaptureValidationController(db.Path, new(), Services(() => Game(),
            writer: path => writer = new ControlledWriter(path) { BlockMarker = true }));
        controller.Start("wifi");
        Until(controller, "state", "RECORDING");
        controller.AddMarker("queued");
        Assert.True(writer!.MarkerEntered.Wait(5000));
        try
        {
            for (var i = 0; i < CaptureValidationController.MarkerQueueCapacity; i++) controller.AddMarker("pop");
            Assert.Throws<CollectorException>(() => controller.AddMarker("left"));
            Assert.Equal(0, controller.Snapshot()["marker_count"]!.GetValue<long>());
            controller.Stop();
        }
        finally { writer.ReleaseMarker.Set(); }
        Until(controller, "state", "COMPLETED");
        var rows = File.ReadLines(controller.Snapshot()["trace_path"]!.GetValue<string>()).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(CaptureValidationController.MarkerQueueCapacity + 1, rows.Count(row => row["marker"] is not null));
        Assert.DoesNotContain(rows, row => row["marker"]?.GetValue<string>() == "left");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealMachinaSourceAdapterFailureCannotFinalizeOrReleaseValidationOwnership(bool failStartup)
    {
        using var db = new TestDatabase();
        var native = new MachinaCleanupTests.NativeMonitor { FailStart = failStartup, FailStop = true, FailDispose = true };
        var source = new MachinaCaptureSource(_ => native);
        var ownership = new CaptureOwnership();
        using var controller = new CaptureValidationController(db.Path, ownership, Services(() => Game(), source));
        controller.Start("wifi");
        if (!failStartup) { Until(controller, "state", "RECORDING"); controller.Stop(); }
        Until(controller, "state", "FAILED");
        Assert.Null(controller.Snapshot()["sha256"]);
        Assert.True(source.IsRunning);
        Assert.Throws<CollectorException>(() => ownership.Acquire());
        native.FailStop = native.FailDispose = false;
        controller.Dispose();
        Assert.True(native.Released);
        Assert.False(source.IsRunning);
        using var lease = ownership.Acquire();
    }

    [Fact]
    public void RealMachinaSourceAdapterFailureKeepsFormalLeaseUntilConfirmedRetry()
    {
        using var db = new TestDatabase();
        var native = new MachinaCleanupTests.NativeMonitor { FailStop = true, FailDispose = true };
        var source = new MachinaCaptureSource(_ => native);
        var ownership = new CaptureOwnership();
        using var formal = new CaptureController(FormalServices(source, ownership));
        formal.Start("wifi");
        formal.Stop();
        Assert.Equal(CaptureControllerState.Faulted, formal.State);
        Assert.Throws<CollectorException>(() => ownership.Acquire());
        native.FailStop = native.FailDispose = false;
        formal.Stop();
        Assert.True(native.Released);
        using var lease = ownership.Acquire();
    }

    private sealed class BlockingSource : ICaptureSource
    {
        public readonly ManualResetEventSlim Starting = new(), ReleaseStart = new(), Stopping = new(), ReleaseStop = new();
        public bool BlockStart, BlockStop, Disposed;
        public string Kind => "synthetic-blocking";
        public bool IsRunning { get; private set; }
        public bool ReadsGameExecutable => false;
        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer)
        {
            Starting.Set();
            if (BlockStart && !ReleaseStart.Wait(5000)) throw new TimeoutException();
            IsRunning = true;
        }
        public void Stop()
        {
            Stopping.Set();
            if (BlockStop && !ReleaseStop.Wait(5000)) throw new TimeoutException();
            IsRunning = false;
        }
        public void Dispose() { Stop(); Disposed = true; }
    }

    [Fact]
    public void WaitingCanBeCancelledWithoutInventingMeasurementsOrFiles()
    {
        using var database = new TestDatabase();
        using var host = CollectorHost.Open(database.Database.Path + ".validation.db", capture: new CaptureServices
        {
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Game = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader()),
            EnableFollowTimer = false,
        });
        var dispatcher = new MessageDispatcher(host);
        JsonObject Call(string name) => dispatcher.Dispatch(new IpcRequest(Guid.NewGuid().ToString("D"), name, new JsonObject()));
        var started = Call("StartCaptureValidation");
        Assert.Equal("WAITING", started["state"]!.GetValue<string>());
        Assert.Null(started["message_count"]);
        Assert.Null(started["trace_path"]);
        Call("StopCaptureValidation");
        Assert.True(SpinWait.SpinUntil(() => !Call("GetCaptureValidationStatus")["active"]!.GetValue<bool>(), 5000));
        var stopped = Call("GetCaptureValidationStatus");
        Assert.Equal("CANCELLED", stopped["reason"]!.GetValue<string>());
        Assert.Null(stopped["trace_path"]);
    }
}
