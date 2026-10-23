using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class NetworkPathFlowTests
{
    private static ProcessNetworkConnection Connection(string local, ushort localPort = 41000,
        string remote = "203.0.113.20", ushort remotePort = 55000) =>
        new(IPAddress.Parse(local), localPort, IPAddress.Parse(remote), remotePort);

    private static ProcessNetworkConnectionQuery Complete(params ProcessNetworkConnection[] connections) =>
        new(NetworkQueryStatus.Success, connections);

    private static CapturePathPlan Plan(params CaptureInterfaceBinding[] bindings) => new(bindings, true, false);

    private static CaptureInterfaceBinding Binding(string id, params string[] addresses) =>
        new(id, addresses.Select(IPAddress.Parse).ToArray(), CapturePathPlan.IsCanonicalLoopback(id));

    [Fact]
    public void PlannedConnectionCountDeduplicatesFullTuplesAcrossSharedAddressesAndIncludesLoopbackRange()
    {
        var tuple = Connection("192.0.2.10");
        var provider = new ConnectionProvider(new(Complete(tuple, tuple,
            tuple with { RemotePort = 55001 }, Connection("10.8.0.2"),
            Connection("127.0.0.8", remote: "127.0.0.1"), Connection("198.51.100.3")), Complete()));
        var plan = Plan(Binding("physical", "192.0.2.10"), Binding("virtual", "10.8.0.2", "192.0.2.10"),
            Binding(CapturePathPlan.LoopbackDeviceId));

        Assert.Equal(4, GameTcpConnectionProbe.TryCountPaths(42, plan, provider));
        Assert.Equal(1, provider.Reads);
        Assert.Equal(42, provider.RequestedProcessId);
    }

    [Theory]
    [InlineData(NetworkQueryStatus.Failed, NetworkQueryStatus.Success)]
    [InlineData(NetworkQueryStatus.Truncated, NetworkQueryStatus.Success)]
    [InlineData(NetworkQueryStatus.Success, NetworkQueryStatus.Failed)]
    [InlineData(NetworkQueryStatus.Success, NetworkQueryStatus.Unmeasured)]
    public void IncompleteFamilyNeverClaimsZeroExistingConnections(NetworkQueryStatus ipv4, NetworkQueryStatus ipv6)
    {
        var provider = new ConnectionProvider(new(new(ipv4, Array.Empty<ProcessNetworkConnection>()),
            new(ipv6, Array.Empty<ProcessNetworkConnection>())));
        Assert.Null(GameTcpConnectionProbe.TryCountPaths(42, Plan(Binding("physical", "192.0.2.10")), provider));
    }

    [Fact]
    public void Ipv6OnlyIsUnknownForTheIpv4ColdStartGuard()
    {
        var provider = new ConnectionProvider(new(Complete(), Complete(Connection("::1", remote: "::1"))));
        Assert.Null(GameTcpConnectionProbe.TryCountPaths(42, Plan(Binding("physical", "192.0.2.10")), provider));
    }

    [Fact]
    public void CompleteEmptyQueriesAreZeroAndAProviderFailureRemainsUnknown()
    {
        var plan = Plan(Binding("physical", "192.0.2.10"));
        var provider = new ConnectionProvider(new(Complete(), Complete()));
        Assert.Equal(0, GameTcpConnectionProbe.TryCountPaths(42, plan, provider));
        provider.Fail = true;
        Assert.Null(GameTcpConnectionProbe.TryCountPaths(42, plan, provider));
        Assert.Null(GameTcpConnectionProbe.TryCountPaths(0, plan, provider));
        Assert.Equal(2, provider.Reads);
    }

    [Fact]
    public void ManualPlanDoesNotCountConnectionsOnUnselectedInterfaces()
    {
        var provider = new ConnectionProvider(new(Complete(Connection("192.0.2.10"), Connection("10.8.0.2")), Complete()));
        var manual = new CapturePathPlan(new[] { Binding("physical", "192.0.2.10") }, false, false);
        Assert.Equal(1, GameTcpConnectionProbe.TryCountPaths(42, manual, provider));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraceReceivesTheSameBoundedAutomaticOrManualPlanAsItsColdStartQuery(bool manual)
    {
        using var db = new TestDatabase();
        var source = new TraceSource();
        var queries = new List<CapturePathPlan>();
        const string executable = @"D:\SdoA\game\ffxiv_dx11.exe";
        var result = CaptureTraceRunner.Run(new CaptureTraceOptions(
            Path.Combine(Path.GetDirectoryName(db.Path)!, "network-path-trace.jsonl"), AdapterId: manual ? "physical" : null),
            TraceServices(source) with
            {
                Game = new GameProcessLocator(new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 42,
                    path: executable), new FakeGameFileReader().With(@"D:\SdoA\game\" + GameProcessLocator.VersionFileName, "2026.01")),
                TcpConnectionCounter = (_, _) => throw new InvalidOperationException("legacy must not override explicit path provider"),
                TcpPathConnectionCounter = (_, plan) => { queries.Add(plan); return 0; },
            });

        Assert.Equal(1, result); // The source ends this synthetic trace with a fault.
        var queried = Assert.Single(queries);
        Assert.Same(queried, source.LastOptions!.PathPlan);
        Assert.Equal(!manual, queried.Automatic);
        Assert.Equal(manual ? 1 : 3, queried.Bindings.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationKeepsInitialAutomaticScopeAcrossItsSecondIdentityCheck(bool manual)
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        var queried = new List<CapturePathPlan>();
        using var controller = new CaptureValidationController(db.Path, new(), ValidationServices(source) with
        {
            Trace = TraceServices(source) with
            {
                TcpPathConnectionCounter = (_, plan) => { lock (queried) queried.Add(plan); return 0; },
            },
        });
        controller.Start(manual ? "physical" : null);
        Until(controller, "state", "RECORDING");
        var actual = source.LastOptions!.PathPlan!;
        Assert.Equal(!manual, actual.Automatic);
        Assert.Equal(manual ? 1 : 3, actual.Bindings.Count);
        lock (queried)
        {
            Assert.Equal(2, queried.Count);
            Assert.All(queried, plan => Assert.Equal(!manual, plan.Automatic));
            Assert.Same(queried[1], actual);
        }
        controller.Stop();
        Until(controller, "state", "COMPLETED");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RememberedAdapterIsAPreferenceAndAutomaticColdCheckIncludesVirtualPaths(bool virtualConnected)
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        var provider = new ConnectionProvider(new(
            virtualConnected ? Complete(Connection("10.8.0.2")) : Complete(), Complete()));
        var queried = new List<CapturePathPlan>();
        using var controller = new CaptureValidationController(db.Path, new(), ValidationServices(source) with
        {
            RememberedAdapterId = "physical",
            Trace = TraceServices(source) with
            {
                TcpPathConnectionCounter = (pid, plan) =>
                {
                    lock (queried) queried.Add(plan);
                    return GameTcpConnectionProbe.TryCountPaths(pid, plan, provider);
                },
            },
        });
        controller.Start();
        Until(controller, virtualConnected ? "reason" : "state", virtualConnected ? "WAITING_RESTART" : "RECORDING");
        lock (queried)
        {
            Assert.NotEmpty(queried);
            Assert.All(queried, plan =>
            {
                Assert.True(plan.Automatic);
                Assert.Equal(new[] { "physical", "virtual", CapturePathPlan.LoopbackDeviceId }.Order(StringComparer.Ordinal),
                    plan.Bindings.Select(binding => binding.AdapterId).Order(StringComparer.Ordinal));
            });
        }
        Assert.Equal(virtualConnected ? 0 : 1, source.StartCount);
        if (virtualConnected)
            Assert.Null(controller.Snapshot()["trace_path"]);
        else
            Assert.True(source.LastOptions!.PathPlan!.Automatic);
        controller.Stop();
        Until(controller, "state", "COMPLETED");
    }

    [Fact]
    public void ValidationAcceptsCanonicalLoopbackWithoutAnEnumeratedAddress()
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        using var controller = new CaptureValidationController(db.Path, new(), ValidationServices(source));
        controller.Start(CapturePathPlan.LoopbackDeviceId);
        Until(controller, "state", "RECORDING");
        var binding = Assert.Single(source.LastOptions!.PathPlan!.Bindings);
        Assert.True(binding.IsLoopback);
        Assert.Contains(IPAddress.Loopback, binding.LocalAddresses);
        controller.Stop();
        Until(controller, "state", "COMPLETED");
    }

    [Fact]
    public void ValidationWaitsForUnknownPathQueryAndDoesNotCreateTraceOrSource()
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        var count = -1;
        using var controller = new CaptureValidationController(db.Path, new(), ValidationServices(source) with
        {
            Trace = TraceServices(source) with { TcpPathConnectionCounter = (_, _) => Volatile.Read(ref count) < 0 ? null : 0 },
        });
        controller.Start();
        Until(controller, "reason", "WAITING_CONNECTION_CHECK");
        Assert.Null(controller.Snapshot()["trace_path"]);
        Assert.Equal(0, source.StartCount);
        Volatile.Write(ref count, 0);
        Until(controller, "state", "RECORDING");
        controller.Stop();
        Until(controller, "state", "COMPLETED");
    }

    [Fact]
    public void SourceQueriesEveryPlannedPathAfterPrepareAndForwardsNativeCoverage()
    {
        var monitor = new Monitor();
        var plan = Plan(Binding("physical", "192.0.2.10"), Binding("virtual", "10.8.0.2"));
        var calls = 0;
        using var source = new MachinaCaptureSource(_ => monitor, pathConnectionCounter: (pid, actual) =>
        {
            Assert.True(monitor.Prepared);
            Assert.Equal(42, pid);
            Assert.Same(plan, actual);
            calls++;
            return 3;
        });
        source.Start(Options(plan), new Observer());
        Assert.Equal(1, calls);
        Assert.Equal(3, source.PreexistingTcpConnections);
        Assert.Equal(monitor.PathStatus, source.PathStatus);
        source.Stop();
        Assert.Equal(CapturePathStatus.Unmeasured, source.PathStatus);
    }

    [Fact]
    public void SourceRetainsAnInjectedLegacyCounterEvenWhenPlanIsPresent()
    {
        var monitor = new Monitor();
        using var source = new MachinaCaptureSource(_ => monitor, (_, address) =>
        {
            Assert.True(monitor.Prepared);
            Assert.Equal(IPAddress.Parse("192.0.2.10"), address);
            return 2;
        }, (_, _) => throw new InvalidOperationException("legacy injection is authoritative"));
        source.Start(Options(Plan(Binding("physical", "192.0.2.10"))), new Observer());
        Assert.Equal(2, source.PreexistingTcpConnections);
    }

    private static CaptureStartOptions Options(CapturePathPlan plan) => new("flow", 42, IPAddress.Parse("192.0.2.10"),
        "physical", OodleMode.LibraryTcp, null, null) { PathPlan = plan };

    private static CaptureTraceServices TraceServices(ICaptureSource source) => new()
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Adapters = new AdapterEnumerator(new FakeAdapterProvider()
            .Add("physical", "Ethernet", hasDefaultRoute: true, addresses: "192.0.2.10")
            .Add("virtual", "Virtual", addresses: "10.8.0.2")
            .Add(CapturePathPlan.LoopbackDeviceId, "Loopback", isLoopback: true), new FakeProcessTcpTable()),
        SourceFactory = () => source,
        TcpConnectionCounter = (_, _) => 0,
        Markers = new StringReader(string.Empty), Output = new StringWriter(), Status = new StringWriter(),
        InstallCancelHandler = false,
    };

    private static CaptureValidationServices ValidationServices(ICaptureSource source) => new()
    {
        Trace = TraceServices(source), PollInterval = TimeSpan.FromMilliseconds(10),
        LocateGame = () => new(true, 42, "ffxiv_dx11", DateTimeOffset.UnixEpoch, Region.Cn, "2026.01", 1,
            @"D:\SdoA\game\ffxiv_dx11.exe", Array.Empty<UserText>()),
    };

    private static void Until(CaptureValidationController controller, string field, string value) =>
        Assert.True(SpinWait.SpinUntil(() => controller.Snapshot()[field]!.GetValue<string>() == value, 5000),
            controller.Snapshot().ToJsonString());

    private sealed class ConnectionProvider(ProcessNetworkConnectionTables tables) : IProcessNetworkConnectionProvider
    {
        public int Reads, RequestedProcessId;
        public bool Fail;
        public ProcessNetworkConnectionTables Read(int processId)
        {
            Reads++;
            RequestedProcessId = processId;
            return Fail ? throw new InvalidOperationException("synthetic query failure") : tables;
        }
    }

    private sealed class TraceSource : ICaptureSource
    {
        public string Kind => CaptureTraceRunner.LiveSourceKind;
        public bool IsRunning { get; private set; }
        public bool ReadsGameExecutable => false;
        public CaptureStartOptions? LastOptions;
        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer)
        {
            LastOptions = options;
            IsRunning = true;
            observer.OnFault("synthetic trace completed", null);
        }
        public void Stop() => IsRunning = false;
        public void Dispose() => Stop();
    }

    private sealed class Monitor : IMachinaMonitor
    {
        public bool Prepared;
        public CapturePathStatus PathStatus { get; } = new(2, 2, 0, false, false);
        public void Prepare() => Prepared = true;
        public void Start() { }
        public void Stop() { }
        public void DetachCallbacks() { }
        public void Dispose() { }
    }

    private sealed class Observer : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) { }
        public void OnDecodeError() { }
        public void OnFault(string reason, Exception? error) { }
    }
}
