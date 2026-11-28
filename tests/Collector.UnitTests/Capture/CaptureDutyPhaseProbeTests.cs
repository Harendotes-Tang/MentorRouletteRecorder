using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CaptureDutyPhaseProbeTests
{
    private const uint Npc = 0x40a1_b2c3;
    private const uint OtherNpc = 0x40a1_b2c4;
    private const uint Player = 0x10aa_5555;
    private const int EffectSequence = 54321;
    private const string Build = "2026.11.03.0000.0000";

    [Fact]
    public void ATraceLinksSpawnCastActionAndDefeatWithOnePrivateAlias()
    {
        using var directory = new TempDirectory("DutyPhaseTrace");
        var path = Path.Combine(directory.Path, "sample.jsonl");
        using (var writer = File.CreateText(path))
        {
            var sink = new CaptureTraceSink(writer, dutyPhaseProbe: true);
            sink.WriteHeader(new CaptureTraceHeader(DateTimeOffset.UnixEpoch, null, Build, Region.Cn,
                null, "test", OodleMode.FfxivTcp, Synthetic: true));
            sink.Accept(Packet(0x0176, new byte[656], Npc, Player));
            var cast = new byte[32];
            BinaryPrimitives.WriteUInt32LittleEndian(cast.AsSpan(12), Npc);
            sink.Accept(Packet(0x0160, cast, Player, Player));
            sink.Accept(Packet(0x0190, Action(), Player, Player));
            sink.Accept(Packet(0x02df, ActorControl(6), Npc, Player));
            sink.WriteSummary(0);
        }

        var text = File.ReadAllText(path);
        foreach (var actor in new[] { Npc, Player })
        {
            Assert.DoesNotContain(actor.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(actor.ToString("x8"), text, StringComparison.OrdinalIgnoreCase);
        }

        var rows = File.ReadLines(path).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(1, rows[0]["duty_phase_probe"]!.GetValue<int>());
        var events = rows.Skip(1).Take(4).Select(row => row["duty_phase"]!).ToArray();
        Assert.Equal(new[] { "spawn", "cast", "action", "defeat" },
            events.Select(row => row["kind"]!.GetValue<string>()));
        var alias = events[0]["source_npc"]!.GetValue<int>();
        Assert.Null(events[0]["target_npc"]);
        Assert.Equal(alias, events[1]["target_npc"]!.GetValue<int>());
        Assert.Equal(alias, events[2]["targets"]![0]!["npc"]!.GetValue<int>());
        Assert.True(events[2]["targets"]![0]!["damage_candidate"]!.GetValue<bool>());
        Assert.Equal(EffectSequence, (int)events[2]["effect_sequence"]!.GetValue<uint>());
        Assert.Equal(alias, events[3]["source_npc"]!.GetValue<int>());
        Assert.Equal(4, rows[^1]["duty_phase_probe"]!["rows"]!.GetValue<int>());

        using var report = new StringWriter();
        Assert.Equal(0, CaptureTraceReport.Run(path, markerName: null, windowMs: 5000, output: report));
        Assert.Contains("actors=1", report.ToString(), StringComparison.Ordinal);
        Assert.Contains("defeat=1", report.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, CaptureTraceAnalysis.Load(path).UnreadableLines);
    }

    [Fact]
    public void AnAreaActionKeepsTargetIndicesAndNeverWritesPlayerIds()
    {
        var payload = new byte[636];
        payload[33] = 3;
        const int targets = 560;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(targets), Npc);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(targets + 8), Player);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(targets + 16), OtherNpc);
        payload[42 + 128 + 8] = 5; // The second effect of target index 2.
        var probe = new CaptureDutyPhaseProbe();

        var action = probe.Observe(Packet(0x0233, payload, Player, Player))!;
        var measured = action["targets"]!.AsArray();
        Assert.Null(action["source_npc"]);
        Assert.Equal(new[] { 0, 2 }, measured.Select(row => row!["index"]!.GetValue<int>()));
        Assert.False(measured[0]!["damage_candidate"]!.GetValue<bool>());
        Assert.True(measured[1]!["damage_candidate"]!.GetValue<bool>());
        Assert.NotEqual(measured[0]!["npc"]!.GetValue<int>(), measured[1]!["npc"]!.GetValue<int>());

        var bossAction = probe.Observe(Packet(0x0233, payload, Npc, Player))!;
        Assert.Equal(3, bossAction["targets"]!.AsArray().Count);
        Assert.Null(bossAction["targets"]![1]!["npc"]);
        Assert.Equal(measured[0]!["npc"]!.GetValue<int>(), bossAction["source_npc"]!.GetValue<int>());
    }

    [Fact]
    public void ProbeRejectsNearMissesAndScopesAliasesToTheConnection()
    {
        var probe = new CaptureDutyPhaseProbe();
        var packet = Packet(0x0176, new byte[656], Npc, Player);
        Assert.Null(probe.Observe(packet with { Direction = MessageDirection.Outbound }));
        Assert.Null(probe.Observe(packet with { SegmentType = 2 }));
        Assert.Null(probe.Observe(packet with { Payload = new byte[655] }));
        Assert.Null(probe.Observe(Packet(0x02df, ActorControl(0x14), Npc, Player)));
        Assert.Null(probe.Observe(Packet(0x02df, ActorControl(6), Player, Player)));
        var invalidCount = Action();
        invalidCount[33] = 2;
        Assert.Null(probe.Observe(Packet(0x0190, invalidCount, Player, Player)));
        Assert.Null(probe.Observe(Packet(0x0190, Action(Player), Player, Player)));

        var first = probe.Observe(packet)!["source_npc"]!.GetValue<int>();
        var second = probe.Observe(packet with { ConnectionKey = "c2" })!["source_npc"]!.GetValue<int>();
        Assert.NotEqual(first, second);
        Assert.Equal(first, probe.Observe(packet)!["source_npc"]!.GetValue<int>());
    }

    [Fact]
    public void AProbeIsOptInAndItsRowsFollowTheMessageCap()
    {
        var message = Packet(0x0176, new byte[656], Npc, Player);
        using var normal = new StringWriter();
        var ordinary = new CaptureTraceSink(normal);
        ordinary.Accept(message);
        ordinary.WriteSummary(0);
        Assert.DoesNotContain("duty_phase", normal.ToString(), StringComparison.Ordinal);

        using var limited = new StringWriter();
        var phase = new CaptureTraceSink(limited, maxLines: 1, dutyPhaseProbe: true);
        phase.Accept(message);
        phase.Accept(message with { SourceActor = OtherNpc });
        phase.WriteSummary(0);
        Assert.True(phase.Truncated);
        var summary = JsonNode.Parse(limited.ToString().Trim().Split('\n')[^1])!;
        Assert.Equal(1, summary["duty_phase_probe"]!["rows"]!.GetValue<int>());
        Assert.Equal(1, summary["duty_phase_probe"]!["actors"]!.GetValue<int>());
    }

    [Fact]
    public void AProbeReusesDirectorSignalsForAnyDutyNumber()
    {
        var probe = new CaptureDutyPhaseProbe();
        var payload = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, DutyClearSignal.Category);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 66);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), DutyClearSignal.DirectorType);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), DutyStartSignal.CommencedCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 5400);
        var start = probe.Observe(Packet(0x00a9, payload, Player, Player))!;
        Assert.Equal("duty_start", start["kind"]!.GetValue<string>());
        Assert.Equal(66, start["director"]!.GetValue<ushort>());
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), DutyClearSignal.Command);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), 0);
        var clear = probe.Observe(Packet(0x00a9, payload, Player, Player))!;
        Assert.Equal("duty_clear", clear["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(Build, true)]
    [InlineData("2026.08.05.0000.0000", false)]
    public void LiveTraceCliSkipsMarkerInputAndRefusesAnUnknownCombatLayout(string build, bool supported)
    {
        using var directory = new TempDirectory("DutyPhaseTrace");
        var path = Path.Combine(directory.Path, "test.jsonl");
        const int pid = 42;
        const string executable = @"D:\SdoA\game\ffxiv_dx11.exe";
        var game = new GameProcessLocator(
            new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, pid, path: executable),
            new FakeGameFileReader().With(
                Path.Combine(Path.GetDirectoryName(executable)!, GameProcessLocator.VersionFileName), build));
        using var output = new StringWriter();
        using var status = new StringWriter();
        using var input = new MarkerInput();
        var services = new CaptureTraceServices
        {
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Game = game,
            Adapters = new AdapterEnumerator(
                new FakeAdapterProvider().Add("wifi", "Wi-Fi", addresses: "192.168.1.2"),
                new FakeProcessTcpTable().With(pid, "192.168.1.2")),
            SourceFactory = () => new ProbeSource(),
            Output = output,
            Status = status,
            Markers = input,
            InstallCancelHandler = false,
        };
        var args = CommandLineOptions.Parse(new[] { "--capture-trace", path, "--duty-phase-probe",
            "--duration-seconds", "1" });

        Assert.Equal(supported ? 0 : 1, CaptureTraceCli.RunCapture(args, services));
        Assert.Equal(0, input.Reads);
        Assert.Equal(supported, File.Exists(path));
        if (supported)
        {
            Assert.Contains(CaptureTraceRunner.DutyPhaseProbeNotice, output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"spawn\"", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Empty(CaptureTraceAnalysis.Load(path).Markers);
        }
        else
        {
            Assert.Contains(CaptureTraceRunner.DutyPhaseProbeRefusal, output.ToString(), StringComparison.Ordinal);
        }
    }

    private static byte[] Action(uint target = Npc)
    {
        var payload = new byte[124];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), EffectSequence);
        payload[33] = 1;
        payload[42] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(112), target);
        return payload;
    }

    private static byte[] ActorControl(ushort category)
    {
        var payload = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, category);
        return payload;
    }

    private static DecodedMessage Packet(ushort opcode, byte[] payload, uint source, uint target) =>
        new("session", MessageDirection.Inbound, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            1000, 3, opcode, payload, "connection")
        {
            SourceActor = source,
            TargetActor = target,
        };

    private sealed class ProbeSource : ICaptureSource
    {
        public string Kind => "synthetic";
        public bool IsRunning => true;
        public bool ReadsGameExecutable => false;
        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer) =>
            observer.OnMessage(Packet(0x0176, new byte[656], Npc, Player) with
            {
                CaptureSessionId = options.CaptureSessionId,
            });
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class MarkerInput : TextReader
    {
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public override string? ReadLine() =>
            Interlocked.Increment(ref _reads) == 1 ? "boss1" : null;
    }
}
