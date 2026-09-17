using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Machina.Infrastructure;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The sanitized live-capture trace: its shape, its bounds, and what it must never contain.
///
/// A trace is written from live traffic and then handed to a human to read, so the privacy
/// rules are asserted negatively over the produced bytes -- no hexadecimal run longer than a
/// digest prefix, no address, no payload -- which is the property a reviewer of a real trace
/// can check for themselves.
///
/// Everything runs on a machine with no Npcap and no game: <see cref="FakeCaptureSource"/>
/// drives the same sink the live pipeline drives.
/// </summary>
public sealed class CaptureTraceTests
{
    private const ushort PlantedOpcode = 0x0345;
    private const ushort NoiseOpcode = 0x0111;

    [Fact]
    public async Task TraceDrainTimeoutReturnsFailureWithoutClosingAnInFlightWriterOrHashingIt()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "blocked-trace.jsonl");
        var processes = new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 1234);
        var source = new FakeCaptureSource();
        BlockingTraceWriter? writer = null;
        var run = Task.Run(() => CaptureTraceRunner.Run(new CaptureTraceOptions(path, DurationSeconds: 1, AdapterId: "adapter"),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, new FakeGameFileReader()),
                Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2"), new FakeProcessTcpTable()),
                SourceFactory = () => source,
                TraceWriterFactory = output => writer = new BlockingTraceWriter(output),
                Markers = new StringReader(string.Empty),
                Output = new StringWriter(), Status = new StringWriter(), InstallCancelHandler = false,
            }));
        Assert.True(SpinWait.SpinUntil(() => source.IsRunning, 5000));
        source.PushOpcode(1);
        Assert.True(writer!.Entered.Wait(5000));
        try
        {
            Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(6)));
            Assert.False(writer.Closed);
            Assert.False(File.Exists(path + CaptureTraceDigest.SidecarExtension));
        }
        finally { writer.Release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => writer.Closed, 5000));
    }

    private sealed class BlockingTraceWriter(string path) : TextWriter
    {
        private readonly StreamWriter _inner = new(path, append: false);
        internal readonly ManualResetEventSlim Entered = new(), Release = new();
        internal volatile bool Closed;
        public override Encoding Encoding => _inner.Encoding;
        public override void Write(string? value)
        {
            if (value?.Contains("\"seq\"", StringComparison.Ordinal) == true)
            { Entered.Set(); Release.Wait(); }
            _inner.Write(value);
        }
        public override void Write(char value) => _inner.Write(value);
        public override void Flush() => _inner.Flush();
        protected override void Dispose(bool disposing) { _inner.Dispose(); Closed = true; base.Dispose(disposing); }
    }

    [Fact]
    public void WritesHeaderMessagesAndSummary_AsOneJsonObjectPerLine()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer);

        sink.WriteHeader(Header());
        Drive(sink, (PlantedOpcode, 24), (NoiseOpcode, 8));
        sink.WriteSummary(dropped: 0);

        var lines = Lines(writer);
        Assert.Equal(4, lines.Count);

        var header = Parse(lines[0]);
        Assert.Equal(CaptureTraceSink.TraceVersion, (int)header["trace_version"]!);
        Assert.False((bool)header["synthetic"]!);
        Assert.Equal(CaptureDiagnosticsSnapshot.LiveCaptureStatus, (string)header["live_capture_status"]!);
        Assert.Equal("CN", (string)header["region"]!);
        Assert.Equal("FfxivTcp", (string)header["oodle_mode"]!);
        Assert.Equal("1.79", (string)header["npcap_version"]!);

        var first = Parse(lines[1]);
        Assert.Equal(1, (long)first["seq"]!);
        Assert.Equal("S2C", (string)first["dir"]!);
        Assert.Equal("0x0345", (string)first["op"]!);
        Assert.Equal(24, (int)first["len"]!);
        Assert.Equal(FfxivFraming.SegmentTypeIpc, (ushort)first["seg"]!);
        Assert.Equal(CaptureTraceSink.HashPrefixLength, ((string)first["h12"]!).Length);
        Assert.Matches("^[0-9a-f]+$", (string)first["h12"]!);

        var summary = Parse(lines[3]);
        Assert.True((bool)summary["summary"]!);
        Assert.Equal(2, (long)summary["messages"]!);
        Assert.Equal(0, (long)summary["dropped"]!);
        Assert.False((bool)summary["truncated"]!);
        Assert.Equal(2, summary["top_opcodes"]!.AsArray().Count);
    }

    [Fact]
    public void CountsEveryPayloadLength_ButNeverWritesAPayload()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer);
        sink.WriteHeader(Header());

        using var source = Started(sink);
        for (var i = 0; i < 3; i++)
        {
            source.PushRaw(Message(PlantedOpcode, 32, fill: 0xAB));
        }

        sink.WriteSummary(dropped: 0);
        var text = writer.ToString();

        // A length-and-digest trace must never carry the bytes it measured, in any spelling.
        Assert.DoesNotContain("abababab", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(new byte[] { 0xAB, 0xAB, 0xAB }), text, StringComparison.Ordinal);

        var top = Parse(Lines(writer)[^1])["top_opcodes"]!.AsArray()[0]!;
        Assert.Equal(3, (long)top["count"]!);
        Assert.Equal(32, (int)top["len_min"]!);
        Assert.Equal(32, (int)top["len_max"]!);
    }

    [Fact]
    public void MarkerAllowlist_RejectsAddressesHexAndArbitraryText()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer);
        sink.WriteHeader(Header());

        using (var source = Started(sink))
        {
            source.PushRaw(Message(PlantedOpcode, 64, fill: 0xDE));
        }

        // Everything a careless tester might type at the prompt.
        sink.WriteMarker("pop 192.168.1.44");
        sink.WriteMarker("deadbeefdeadbeefdeadbeef");
        sink.WriteMarker("xdeadbeefdeadbeefy");
        sink.WriteMarker("entered");
        sink.WriteSummary(dropped: 0);

        var text = writer.ToString();
        var withoutDigests = Regex.Replace(text, "\"h12\":\"[0-9a-f]{12}\"", "\"h12\":\"\"");

        Assert.DoesNotMatch("[0-9a-fA-F]{13,}", withoutDigests);
        Assert.DoesNotMatch(@"\b\d{1,3}(\.\d{1,3}){3}\b", text);
        Assert.DoesNotContain("192.168", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", text, StringComparison.OrdinalIgnoreCase);
        var markers = Lines(writer).Where(line => Kind(line) == "marker")
            .Select(line => (string)Parse(line)["marker"]!).ToList();
        Assert.Equal(new[] { "entered" }, markers);
    }

    [Fact]
    public void InterleavesMarkersWithMessages_WhenTheyAreReadFromAReader()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer);
        sink.WriteHeader(Header());

        using (var source = Started(sink))
        {
            source.PushOpcode(NoiseOpcode);
            CaptureTraceMarkerPump.PumpAll(new StringReader("pop\n\n   \nentered\n"), sink);
            source.PushOpcode(PlantedOpcode);
        }

        sink.WriteSummary(dropped: 0);

        var kinds = Lines(writer).Select(Kind).ToList();
        Assert.Equal(
            new[] { "header", "message", "marker", "marker", "message", "summary" }, kinds);
        Assert.Equal(2, sink.MarkerCount);

        // Blank and whitespace-only lines are not markers: pressing Enter by accident must
        // not put a nameless marker in the evidence.
        var markers = Lines(writer).Where(line => Kind(line) == "marker")
            .Select(line => (string)Parse(line)["marker"]!).ToList();
        Assert.Equal(new[] { "pop", "entered" }, markers);
    }

    [Fact]
    public void BackgroundMarkerPump_WritesEveryNonEmptyLineAfterStart()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer);
        sink.WriteHeader(Header());

        using (var pump = new CaptureTraceMarkerPump(
                   new StringReader("QUEUED\n\n pop \nentered\n"), sink))
        {
            pump.Start();
            Assert.True(SpinWait.SpinUntil(
                () => sink.MarkerCount == 3,
                TimeSpan.FromSeconds(2)));
        }

        sink.WriteSummary(dropped: 0);
        var markers = Lines(writer).Where(line => Kind(line) == "marker")
            .Select(line => (string)Parse(line)["marker"]!).ToList();
        Assert.Equal(new[] { "queued", "pop", "entered" }, markers);
    }

    [Fact]
    public void MarkerPump_ContainsReaderFailures_InsteadOfCrashingItsThread()
    {
        Exception? reported = null;
        var escaped = Record.Exception(() => CaptureTraceMarkerPump.PumpAll(
            new ThrowingTextReader(),
            NewSink(new StringWriter()),
            onError: error => reported = error));

        Assert.Null(escaped);
        var failure = Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal("simulated marker reader failure", failure.Message);
    }

    [Fact]
    public void MarkerPump_ReportsSinkFailuresSeparately_AndStopsReading()
    {
        Exception? inputFailure = null;
        Exception? sinkFailure = null;
        var escaped = Record.Exception(() => CaptureTraceMarkerPump.PumpAll(
            new StringReader("pop\nentered\n"),
            NewSink(new ThrowingTextWriter()),
            onError: error => inputFailure = error,
            onSinkError: error => sinkFailure = error));

        Assert.Null(escaped);
        Assert.Null(inputFailure);
        var failure = Assert.IsType<IOException>(sinkFailure);
        Assert.Equal("simulated marker sink failure", failure.Message);
    }

    [Fact]
    public void TraceRun_StopsAndKeepsAValidSummary_WhenMarkerWritingFails()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "marker-write-failure.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            "2026.08.05.0000.0000");
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var source = new RecordingTraceSource(faultOnStart: false);
        var output = new StringWriter();

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, AdapterId: "adapter"),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                Markers = new StringReader("pop\nentered\n"),
                Output = output,
                Status = new StringWriter(),
                TraceWriterFactory = path => new MarkerFailingFileWriter(path),
                InstallCancelHandler = false,
            });

        Assert.Equal(1, result);
        Assert.True(source.WasStopped);
        Assert.True(source.Disposed);
        Assert.True(File.Exists(tracePath + CaptureTraceDigest.SidecarExtension));
        Assert.Contains("抓包中断", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("写入取证文件失败", output.ToString(), StringComparison.Ordinal);

        var analysis = CaptureTraceAnalysis.Load(tracePath);
        Assert.NotNull(analysis.Summary);
        Assert.Empty(analysis.Markers);
        Assert.Equal(0, (long)analysis.Summary!["markers"]!);
    }

    [Fact]
    public void UsesMessageObservationTime_WhenQueueDeliveryIsDelayed()
    {
        var writer = new StringWriter();
        var writeClock = new TestClock(
            new DateTimeOffset(2026, 1, 1, 0, 0, 30, TimeSpan.Zero));
        var sink = new CaptureTraceSink(
            writer, writeClock, () => TimeSpan.FromSeconds(30));
        sink.WriteHeader(Header());

        var observed = new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero);
        sink.Accept(Captured(PlantedOpcode, 16, 0x2A, TimeSpan.FromSeconds(2), observed));
        sink.WriteSummary(dropped: 0);

        var message = Parse(Lines(writer)[1]);
        Assert.Equal(2000, (long)message["t_ms"]!);
        Assert.Equal("2026-01-01T00:00:02.000Z", (string)message["at_utc"]!);
    }

    [Fact]
    public void TraceRun_OptsIntoCandidateSignature_ForTheDetectedRegionAndBuild()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "candidate-opt-in.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        const string gameBuild = "2026.08.05.0000.0000";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            gameBuild);
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var source = new RecordingTraceSource();

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                Markers = new StringReader(string.Empty),
                Output = new StringWriter(),
                Status = new StringWriter(),
                InstallCancelHandler = false,
            });

        // The source deliberately reports a fault to end the otherwise unbounded trace.
        Assert.Equal(1, result);
        var options = Assert.IsType<CaptureStartOptions>(source.LastOptions);
        Assert.Equal(Region.Cn, options.Region);
        Assert.Equal(gameBuild, options.GameBuild);
        Assert.True(options.AllowCandidateOodleSignature);
        Assert.Equal(OodleMode.FfxivTcp, options.Oodle);
    }

    [Theory]
    [InlineData(null, "2026.08.05.0000.0000", false)]
    [InlineData(@"D:\sdo\game\ffxiv_dx11.exe", null, false)]
    [InlineData(@"D:\unidentified\game\ffxiv_dx11.exe", "2026.08.05.0000.0000", false)]
    [InlineData(@"D:\sdo\game\ffxiv_dx11.exe", "2026.08.05.0000.0000", true)]
    public void TraceRun_RequiresCompleteGameIdentity_BeforeOpeningLiveEvidence(
        string? executablePath, string? gameBuild, bool identityReady)
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "identity-gate.jsonl");
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader();
        if (executablePath is not null && gameBuild is not null)
        {
            files.With(
                Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
                gameBuild);
        }

        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var source = new RecordingTraceSource(CaptureTraceRunner.LiveSourceKind);
        var output = new StringWriter();

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, AdapterId: "adapter"),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, new FakeProcessTcpTable()),
                SourceFactory = () => source,
                TcpConnectionCounter = (_, _) => 0,
                Markers = new StringReader(string.Empty),
                Output = output,
                Status = new StringWriter(),
                InstallCancelHandler = false,
            });

        Assert.Equal(1, result); // The ready source reports a fault to end the test run.
        Assert.True(source.Disposed);
        Assert.Equal(identityReady, File.Exists(tracePath));
        if (identityReady)
        {
            var options = Assert.IsType<CaptureStartOptions>(source.LastOptions);
            Assert.Equal(Region.Cn, options.Region);
            Assert.Equal(gameBuild, options.GameBuild);
            Assert.Equal(executablePath, options.GameExecutablePath);
            Assert.True(options.AllowCandidateOodleSignature);
        }
        else
        {
            Assert.Null(source.LastOptions);
            Assert.False(File.Exists(tracePath + CaptureTraceDigest.SidecarExtension));
            Assert.DoesNotContain(CaptureTraceRunner.MarkerPrompt, output.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TraceRun_RefusesMidstreamLiveCapture_BeforeStartingTheSource()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "midstream-refused.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        const string gameBuild = "2026.08.05.0000.0000";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            gameBuild);
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var output = new StringWriter();
        var source = new RecordingTraceSource(CaptureTraceRunner.LiveSourceKind);

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, AdapterId: "adapter"),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                TcpConnectionCounter = (_, _) => 2,
                Markers = new StringReader(string.Empty),
                Output = output,
                Status = new StringWriter(),
                InstallCancelHandler = false,
            });

        Assert.Equal(1, result);
        Assert.Null(source.LastOptions);
        Assert.False(File.Exists(tracePath));
        Assert.Contains(CaptureTraceRunner.MidstreamRefusalPrefix, output.ToString(), StringComparison.Ordinal);
        Assert.Contains("当前连接数: 2", output.ToString(), StringComparison.Ordinal);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void TcpConnectionProbe_CountsOnlyTheSelectedAdaptersConnections()
    {
        var selected = IPAddress.Parse("10.0.0.2");
        var unrelated = IPAddress.Parse("127.0.0.1");
        var connections = new[]
        {
            new TCPConnection { LocalIP = ToMachinaAddress(selected), LocalPort = 50000 },
            new TCPConnection { LocalIP = ToMachinaAddress(unrelated), LocalPort = 50001 },
        };

        var probe = typeof(CaptureTraceRunner).Assembly.GetType(
            "MentorRecorder.Collector.Capture.GameTcpConnectionProbe");
        var count = probe?.GetMethod(
            "CountOnLocalAddress", BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(count);
        Assert.Equal(1, Assert.IsType<int>(count!.Invoke(null, new object[] { connections, selected })));
    }

    [Fact]
    public void TraceRun_RefusesALiveAdapterWithoutAnIpv4BindAddress()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "missing-bind-address.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            "2026.08.05.0000.0000");
        var adapters = new FakeAdapterProvider().Add("adapter", "No IPv4 adapter");
        var source = new RecordingTraceSource(CaptureTraceRunner.LiveSourceKind);
        var output = new StringWriter();
        var counterCalled = false;

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, AdapterId: "adapter"),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, new FakeProcessTcpTable()),
                SourceFactory = () => source,
                TcpConnectionCounter = (_, _) =>
                {
                    counterCalled = true;
                    return 0;
                },
                Markers = new StringReader(string.Empty),
                Output = output,
                Status = new StringWriter(),
                InstallCancelHandler = false,
            });

        Assert.Equal(1, result);
        Assert.False(counterCalled);
        Assert.Null(source.LastOptions);
        Assert.False(File.Exists(tracePath));
        Assert.Contains(CaptureTraceRunner.AdapterRefusal, output.ToString(), StringComparison.Ordinal);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void TraceRun_DisposesSource_WhenOutputPathCannotBeCreated()
    {
        using var directory = new TempDirectory();
        var blocker = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(blocker, "blocks Directory.CreateDirectory", new UTF8Encoding(false));
        var tracePath = Path.Combine(blocker, "trace.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            "2026.08.05.0000.0000");
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var source = new RecordingTraceSource();

        Assert.ThrowsAny<IOException>(() => CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                Output = new StringWriter(),
                Status = new StringWriter(),
                InstallCancelHandler = false,
            }));

        Assert.True(source.Disposed);
    }

    [Fact]
    public void TraceRun_DoesNotOverwriteExistingEvidence()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "existing.jsonl");
        const string original = "existing evidence\n";
        File.WriteAllText(tracePath, original, new UTF8Encoding(false));
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            "2026.08.05.0000.0000");
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var source = new RecordingTraceSource();

        Assert.Throws<IOException>(() => CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                Output = new StringWriter(),
                Status = new StringWriter(),
                InstallCancelHandler = false,
            }));

        Assert.Equal(original, File.ReadAllText(tracePath));
        Assert.Null(source.LastOptions);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void StopsWritingAndReportsTruncation_WhenTheLineCapIsReached()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer, maxLines: 3);
        sink.WriteHeader(Header());

        using (var source = Started(sink))
        {
            for (var i = 0; i < 10; i++)
            {
                source.PushOpcode(NoiseOpcode);
            }
        }

        sink.WriteSummary(dropped: 2);

        Assert.True(sink.Truncated);
        Assert.Equal(3, sink.WrittenCount);
        Assert.Equal(10, sink.MessageCount);

        var summary = Parse(Lines(writer)[^1]);
        Assert.True((bool)summary["truncated"]!);
        Assert.Equal(10, (long)summary["messages"]!);
        Assert.Equal(3, (long)summary["written"]!);
        Assert.Equal(2, (long)summary["dropped"]!);
        Assert.Equal(5, Lines(writer).Count);
    }

    [Fact]
    public void WritesASha256Sidecar_ThatMatchesTheTraceFile()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");

        using (var file = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            var sink = NewSink(file);
            sink.WriteHeader(Header());
            sink.WriteSummary(dropped: 0);
        }

        var digest = CaptureTraceDigest.WriteSidecar(path);
        var sidecar = File.ReadAllText(path + CaptureTraceDigest.SidecarExtension);

        Assert.Equal(64, digest.Length);
        Assert.Equal(CaptureTraceDigest.Compute(path), digest);
        Assert.StartsWith(digest, sidecar, StringComparison.Ordinal);
        Assert.Contains("trace.jsonl", sidecar, StringComparison.Ordinal);
    }

    [Fact]
    public void RanksThePlantedOpcodeFirst_WhenItAppearsAtEveryPop()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "sample.jsonl");
        File.WriteAllText(path, Sample(), new UTF8Encoding(false));

        var analysis = CaptureTraceAnalysis.Load(path);
        Assert.Equal(2, analysis.Markers.Count);
        Assert.Equal(0, analysis.UnreadableLines);

        var candidates = analysis.Candidates(CaptureTraceReport.DefaultWindowMs, "pop");
        var top = candidates[0];

        Assert.Equal("pop", top.Marker);
        Assert.Equal(CaptureTraceSink.FormatOpcode(PlantedOpcode), top.Opcode);
        Assert.Equal(2, top.MarkerHits);
        Assert.Equal(2, top.MarkerCount);
        Assert.Equal(1.0, top.Ratio, 3);

        // The background opcode falls inside both windows, so it is a candidate; it is also
        // everywhere else, which is what the ratio must demote.
        var noise = candidates.Single(row => row.Opcode == CaptureTraceSink.FormatOpcode(NoiseOpcode));
        Assert.True(noise.Ratio < top.Ratio);
    }

    [Fact]
    public void PrintsTheCandidateAsACandidate_NeverAsVerified()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "sample.jsonl");
        File.WriteAllText(path, Sample(), new UTF8Encoding(false));

        var writer = new StringWriter();
        Assert.Equal(0, CaptureTraceReport.Run(path, markerName: null, windowMs: 5000, output: writer));

        var text = writer.ToString();
        Assert.Contains(CaptureTraceSink.FormatOpcode(PlantedOpcode), text, StringComparison.Ordinal);
        Assert.Contains("候选", text, StringComparison.Ordinal);
        Assert.Contains(CaptureTraceReport.CandidateWarning, text, StringComparison.Ordinal);

        // A report never promotes anything: the file it came from says UNVERIFIED and the
        // table says 候选, and neither word may be quietly upgraded on the way out.
        Assert.Contains(CaptureDiagnosticsSnapshot.LiveCaptureStatus, text, StringComparison.Ordinal);
        Assert.DoesNotContain("已验证的 opcode", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsATruncatedTrace_WithoutLosingTheLinesThatSurvived()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "cut.jsonl");
        var lines = Sample().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        File.WriteAllText(
            path,
            string.Join('\n', lines.Take(lines.Length - 1)) + "\n{\"seq\":9,\"t_",
            new UTF8Encoding(false));

        var analysis = CaptureTraceAnalysis.Load(path);

        Assert.Null(analysis.Summary);
        Assert.Equal(1, analysis.UnreadableLines);
        Assert.NotEmpty(analysis.Messages);
    }

    [Fact]
    public void ReportRefusesAFileAboveItsInMemorySizeLimit()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "oversized.jsonl");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.SetLength(CaptureTraceAnalysis.MaxInputBytes + 1);
        }

        var failure = Assert.Throws<InvalidDataException>(() => CaptureTraceAnalysis.Load(path));

        Assert.Contains(
            CaptureTraceAnalysis.MaxInputBytes.ToString(),
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportRefusesMoreThanItsMessageRowLimit()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "too-many-rows.jsonl");
        using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            const string row = "{\"op\":\"0x0001\",\"dir\":\"S2C\",\"len\":0}";
            for (var index = 0; index <= CaptureTraceAnalysis.MaxMessageRows; index++)
            {
                writer.WriteLine(row);
            }
        }

        var failure = Assert.Throws<InvalidDataException>(() => CaptureTraceAnalysis.Load(path));

        Assert.Contains(
            CaptureTraceAnalysis.MaxMessageRows.ToString(),
            failure.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a small trace by hand: a steady background opcode, one planted opcode that only
    /// ever appears just before a <c>pop</c>, and two <c>pop</c> markers.
    /// </summary>
    private static string Sample()
    {
        var writer = new StringWriter();
        var now = TimeSpan.Zero;
        var sink = new CaptureTraceSink(
            writer, new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), () => now);
        sink.WriteHeader(Header());

        for (var tick = 0; tick <= 60; tick++)
        {
            now = TimeSpan.FromSeconds(tick);
            sink.Accept(Captured(NoiseOpcode, 16, 0x01, now));

            if (tick is 10 or 40)
            {
                now = TimeSpan.FromMilliseconds((tick * 1000) + 100);
                sink.Accept(Captured(PlantedOpcode, 48, 0x02, now));
                now = TimeSpan.FromMilliseconds((tick * 1000) + 300);
                sink.WriteMarker("pop");
            }
        }

        now = TimeSpan.FromSeconds(61);
        sink.WriteSummary(dropped: 0);
        return writer.ToString();
    }

    private static CaptureTraceSink NewSink(TextWriter writer, int maxLines = 1000)
    {
        var elapsed = TimeSpan.Zero;
        return new CaptureTraceSink(
            writer,
            new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            () => elapsed += TimeSpan.FromMilliseconds(10),
            maxLines);
    }

    private static CaptureTraceHeader Header() => new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "1.79",
        "2026.01.01.0000.0000",
        Region.Cn,
        "0123456789ab",
        "0.1.0",
        OodleMode.FfxivTcp,
        Synthetic: false);

    private static CaptureStartOptions StartOptions() =>
        new("trace-test", 1234, null, "adapter", OodleMode.FfxivTcp, null, null);

    [Fact]
    public void TraceRun_AllowsMidstreamLiveCapture_OnlyWhenAskedAndFlagsIt()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "midstream-allowed.jsonl");
        const string executablePath = @"D:\sdo\game\ffxiv_dx11.exe";
        const string gameBuild = "2026.08.05.0000.0000";
        var processes = new FakeGameProcessProvider()
            .Add(GameProcessLocator.Dx11ProcessName, 1234, path: executablePath);
        var files = new FakeGameFileReader().With(
            Path.Combine(Path.GetDirectoryName(executablePath)!, GameProcessLocator.VersionFileName),
            gameBuild);
        var adapters = new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2");
        var tcpTable = new FakeProcessTcpTable().With(1234, "10.0.0.2");
        var output = new StringWriter();
        var source = new RecordingTraceSource(CaptureTraceRunner.LiveSourceKind);

        var result = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, AdapterId: "adapter", AllowMidstream: true),
            new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(processes, files),
                Adapters = new AdapterEnumerator(adapters, tcpTable),
                SourceFactory = () => source,
                TcpConnectionCounter = (_, _) => 2,
                Markers = new StringReader(string.Empty),
                Output = output,
                Status = new StringWriter(),
                InstallCancelHandler = false,
            });

        // The recording source faults on start to end the run; what matters is that the source
        // was started at all and that the evidence file records the midstream join.
        Assert.Equal(1, result);
        Assert.NotNull(source.LastOptions);
        Assert.True(File.Exists(tracePath));
        Assert.Contains(CaptureTraceRunner.MidstreamWarningPrefix, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(CaptureTraceRunner.MidstreamRefusalPrefix, output.ToString(), StringComparison.Ordinal);

        var header = JsonNode.Parse(File.ReadLines(tracePath).First())!.AsObject();
        Assert.True(header["midstream"]!.GetValue<bool>());
        Assert.Equal(2, header["preexisting_connections"]!.GetValue<int>());
        Assert.Equal(CaptureDiagnosticsSnapshot.LiveCaptureStatus, header["live_capture_status"]!.GetValue<string>());
    }

    [Fact]
    public void Sink_TagsEveryLineWithItsConnection_AndSummarisesPerConnection()
    {
        var writer = new StringWriter();
        var now = TimeSpan.Zero;
        var sink = new CaptureTraceSink(
            writer, new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), () => now);
        sink.WriteHeader(Header());

        sink.Accept(Captured(0x0101, 8, 0x11, TimeSpan.FromMilliseconds(10)) with { ConnectionKey = "aaaaaaaaaaaaaaaa" });
        sink.Accept(Captured(0x0102, 8, 0x22, TimeSpan.FromMilliseconds(20)) with { ConnectionKey = "aaaaaaaaaaaaaaaa" });
        sink.Accept(Captured(0x0101, 8, 0x33, TimeSpan.FromMilliseconds(30)) with { ConnectionKey = "bbbbbbbbbbbbbbbb" });
        sink.WriteSummary(dropped: 0);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject())
            .ToList();
        var messageLines = lines.Where(line => line.ContainsKey("op")).ToList();
        Assert.Equal(3, messageLines.Count);
        Assert.All(messageLines, line => Assert.Equal(CaptureTraceSink.ConnectionTagLength, line["conn"]!.GetValue<string>().Length));
        Assert.Equal("aaaaaaaa", messageLines[0]["conn"]!.GetValue<string>());
        Assert.Equal("bbbbbbbb", messageLines[2]["conn"]!.GetValue<string>());

        var summary = lines.Single(line => line.ContainsKey("summary"));
        var connections = summary["connections"]!.AsArray();
        Assert.Equal(2, connections.Count);
        Assert.Equal("aaaaaaaa", connections[0]!["conn"]!.GetValue<string>());
        Assert.Equal(2, connections[0]!["messages"]!.GetValue<long>());
        Assert.Equal(2, connections[0]!["distinct_opcodes"]!.GetValue<int>());
        Assert.Equal(10, connections[0]!["first_t_ms"]!.GetValue<long>());
        Assert.Equal(20, connections[0]!["last_t_ms"]!.GetValue<long>());
        Assert.Equal(1, connections[1]!["messages"]!.GetValue<long>());
        Assert.Equal(0, summary["untracked_connections"]!.GetValue<long>());
        // Never the full key, never an address.
        Assert.DoesNotContain("aaaaaaaaaaaaaaaa", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Starts a fake source whose messages land in the sink through the real observer path.</summary>
    private static FakeCaptureSource Started(CaptureTraceSink sink)
    {
        var source = new FakeCaptureSource();
        source.Start(StartOptions(), new SinkObserver(sink));
        return source;
    }

    private static void Drive(CaptureTraceSink sink, params (ushort Opcode, int Length)[] messages)
    {
        using var source = Started(sink);
        foreach (var (opcode, length) in messages)
        {
            source.PushRaw(Message(opcode, length, fill: 0x7F));
        }
    }

    /// <summary>A well-formed IPC message whose payload is filled with a recognisable byte.</summary>
    private static byte[] Message(ushort opcode, int payloadLength, byte fill)
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode, payloadLength);
        message.AsSpan(FfxivFraming.HeaderBytes).Fill(fill);
        return message;
    }

    private static DecodedMessage Captured(
        ushort opcode,
        int payloadLength,
        byte fill,
        TimeSpan mono,
        DateTimeOffset? observedAt = null)
    {
        var payload = new byte[payloadLength];
        payload.AsSpan().Fill(fill);
        return new DecodedMessage(
            "trace-test",
            MessageDirection.Inbound,
            observedAt ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).Add(mono),
            mono,
            0,
            FfxivFraming.SegmentTypeIpc,
            opcode,
            payload,
            "connection-not-written");
    }

    private static uint ToMachinaAddress(IPAddress address) =>
        BitConverter.ToUInt32(address.GetAddressBytes());

    private static List<string> Lines(StringWriter writer) =>
        writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static JsonObject Parse(string line) => JsonNode.Parse(line)!.AsObject();

    private static string Kind(string line)
    {
        var node = Parse(line);
        if (node.ContainsKey("trace_version"))
        {
            return "header";
        }

        if (node.ContainsKey("summary"))
        {
            return "summary";
        }

        return node.ContainsKey("marker") ? "marker" : "message";
    }

    /// <summary>Forwards a fake source's callbacks into the trace sink, as the runner does.</summary>
    private sealed class SinkObserver(CaptureTraceSink sink) : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) => sink.Accept(message);

        public void OnDecodeError() => sink.CountDecodeError();

        public void OnFault(string reason, Exception? error)
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MentorRecorderTrace", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private sealed class ThrowingTextReader : TextReader
    {
        public override string? ReadLine() =>
            throw new InvalidOperationException("simulated marker reader failure");
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("simulated marker sink failure");
    }

    /// <summary>A trace file writer that throws on the first marker line.</summary>
    private sealed class MarkerFailingFileWriter : TextWriter
    {
        private readonly StreamWriter _inner;
        private bool _failed;

        public MarkerFailingFileWriter(string path) =>
            _inner = new StreamWriter(
                new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(string? value)
        {
            if (!_failed && value?.Contains("\"marker\":", StringComparison.Ordinal) == true)
            {
                _failed = true;
                throw new IOException("simulated marker sink failure");
            }

            _inner.Write(value);
        }

        public override void Write(char value) => _inner.Write(value);

        public override void Flush() => _inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Captures start options and optionally ends a trace without waiting on real time.</summary>
    private sealed class RecordingTraceSource(
        string? kind = null,
        bool faultOnStart = true) : ICaptureSource
    {
        public string Kind { get; } = kind ?? "recording-trace";

        public bool IsRunning { get; private set; }

        public bool ReadsGameExecutable => false;

        public CaptureStartOptions? LastOptions { get; private set; }

        public bool Disposed { get; private set; }

        public bool WasStopped { get; private set; }

        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer)
        {
            LastOptions = options;
            IsRunning = true;
            if (faultOnStart)
            {
                observer.OnFault("test trace complete", null);
            }
        }

        public void Stop()
        {
            WasStopped = true;
            IsRunning = false;
        }

        public void Dispose()
        {
            Disposed = true;
            Stop();
        }
    }
}
