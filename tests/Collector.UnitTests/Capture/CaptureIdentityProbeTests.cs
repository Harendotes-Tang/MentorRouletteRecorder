using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The opt-in identity probe of a live trace: which keyed digests it writes, how it bounds them,
/// and that no identifier it measured can be read back from the file.
/// </summary>
public sealed class CaptureIdentityProbeTests
{
    private const uint Self = 0x1034_5678;
    private const uint Other = 0x1098_7654;
    private const ushort LoginOpcode = 0x0222;

    private static readonly byte[] KeyA = Enumerable.Range(1, CaptureIdentityProbe.KeyBytes).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(101, CaptureIdentityProbe.KeyBytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void FakeSource_CarriesTheActorsOntoTheDecodedMessage()
    {
        var seen = new List<DecodedMessage>();
        using var source = new FakeCaptureSource();
        source.Start(StartOptions(), new Collecting(seen));

        source.PushRaw(Message(LoginOpcode, new byte[4], source: Other, target: Self));

        var decoded = Assert.Single(seen);
        Assert.Equal(Other, decoded.SourceActor);
        Assert.Equal(Self, decoded.TargetActor);
    }

    [Fact]
    public void ADecodedMessageAndItsFrame_NeverPrintTheirActors()
    {
        var message = Message(LoginOpcode, new byte[4], source: Other, target: Self);
        Assert.True(FfxivFraming.TryRead(message, out var frame));
        var texts = frame + " " + Decoded(MessageDirection.Inbound, LoginOpcode, Other, Self, new byte[4]);

        foreach (var forbidden in new[] { Self.ToString(), Other.ToString(), Self.ToString("x8"), Other.ToString("x8") })
        {
            Assert.DoesNotContain(forbidden, texts, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("0x0222", texts, StringComparison.Ordinal);
    }

    [Fact]
    public void SameActorAndKey_GiveTheSameId_AndAnotherKeyGivesAnother()
    {
        var first = new CaptureIdentityProbe(KeyA);
        var second = new CaptureIdentityProbe(KeyA);
        var foreign = new CaptureIdentityProbe(KeyB);

        Assert.Equal(first.ActorId(Self), second.ActorId(Self));
        Assert.NotEqual(first.ActorId(Self), first.ActorId(Other));
        Assert.NotEqual(first.ActorId(Self), foreign.ActorId(Self));
        Assert.Equal(CaptureIdentityProbe.IdLength, first.ActorId(Self).Length);
        Assert.NotEqual(first.KeyId, foreign.KeyId);
    }

    [Fact]
    public void InboundTarget_IsWrittenOncePerConnection_AndAgainWhenItChanges()
    {
        var probe = new CaptureIdentityProbe(KeyA);

        var lines = new List<JsonObject>();
        lines.AddRange(probe.Observe(Inbound(0x0100, Self, new byte[0]), "conn-a", 10, 1));
        lines.AddRange(probe.Observe(Inbound(0x0101, Self, new byte[0]), "conn-a", 20, 2));
        lines.AddRange(probe.Observe(Inbound(0x0102, Other, new byte[0]), "conn-a", 30, 3));
        lines.AddRange(probe.Observe(Inbound(0x0103, Self, new byte[0]), "conn-b", 40, 4));

        var targets = lines.Where(line => (string)line["identity"]! == CaptureIdentityProbe.HeaderTargetKind).ToList();
        Assert.Equal(3, targets.Count);
        Assert.Equal(probe.ActorId(Self), (string)targets[0]["id"]!);
        Assert.Equal(probe.ActorId(Other), (string)targets[1]["id"]!);
        Assert.Equal("conn-b", (string)targets[2]["conn"]!);
        Assert.Equal(probe.ActorId(Self), (string)targets[2]["id"]!);
    }

    [Fact]
    public void OutboundSource_SharesTheIdOfTheSameActorSeenInbound()
    {
        var probe = new CaptureIdentityProbe(KeyA);

        var inbound = probe.Observe(Inbound(0x0100, Self, new byte[0]), "conn-a", 10, 1);
        var outbound = probe.Observe(
            Decoded(MessageDirection.Outbound, 0x0200, source: Self, target: 0, new byte[0]), "conn-a", 20, 2);

        var sent = Assert.Single(outbound);
        Assert.Equal(CaptureIdentityProbe.HeaderSourceKind, (string)sent["identity"]!);
        Assert.Equal((string)inbound[0]["id"]!, (string)sent["id"]!);
    }

    [Fact]
    public void ZeroActors_AreNeverWritten()
    {
        var probe = new CaptureIdentityProbe(KeyA);

        Assert.Empty(probe.Observe(Inbound(0x0100, 0, new byte[0]), "conn-a", 10, 1));
        Assert.Empty(probe.Summary()["actors"]!.AsArray());
    }

    [Fact]
    public void PayloadPrefix_IsWrittenForTheFirstOccurrencesOfAnOpcodeOnly()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var payload = Payload(0x1122_3344_5566_7788, 32);

        var written = 0;
        for (var i = 0; i < CaptureIdentityProbe.OccurrencesPerOpcode + 3; i++)
        {
            written += probe.Observe(Inbound(LoginOpcode, Self, payload), "conn-a", i, i + 1)
                .Count(line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);
        }

        Assert.Equal(CaptureIdentityProbe.OccurrencesPerOpcode, written);
    }

    [Fact]
    public void PayloadPrefix_IsStableForTheSameBytes_AndDiffersForOthers()
    {
        var probe = new CaptureIdentityProbe(KeyA);

        var first = Prefix(probe.Observe(Inbound(LoginOpcode, Self, Payload(0x1122_3344_5566_7788, 32)), "conn-a", 1, 1));
        var again = Prefix(probe.Observe(Inbound(LoginOpcode, Self, Payload(0x1122_3344_5566_7788, 64)), "conn-b", 2, 2));
        var other = Prefix(probe.Observe(Inbound(0x0333, Self, Payload(0x1122_3344_5566_7789, 32)), "conn-a", 3, 3));

        Assert.Equal((string)first["p8"]!, (string)again["p8"]!);
        Assert.NotEqual((string)first["p8"]!, (string)other["p8"]!);
        Assert.Equal("0x0222", (string)first["op"]!);
        Assert.Equal(32, (int)first["len"]!);
    }

    [Fact]
    public void PayloadPrefix_SaysWhereTheHeaderTargetAppearsInThePayload()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var payload = Payload(0x1122_3344_5566_7788, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), Self);

        var found = Prefix(probe.Observe(Inbound(LoginOpcode, Self, payload), "conn-a", 1, 1));
        var absent = Prefix(probe.Observe(Inbound(0x0333, Self, Payload(0x1122_3344_5566_7788, 32)), "conn-a", 2, 2));

        Assert.Equal(12, (int)found["self_at"]!);
        Assert.Null(absent["self_at"]);
    }

    [Fact]
    public void PayloadPrefix_IsNotWrittenForAMessageAboutSomebodyElse()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var payload = Payload(0x1122_3344_5566_7788, 32);

        var lines = probe.Observe(Decoded(MessageDirection.Inbound, LoginOpcode, source: Other, target: Self, payload), "conn-a", 1, 1)
            .Concat(probe.Observe(Decoded(MessageDirection.Inbound, 0x0333, source: 0, target: Self, payload), "conn-a", 2, 2))
            .Concat(probe.Observe(Decoded(MessageDirection.Inbound, 0x0444, source: Self, target: 0, payload), "conn-a", 3, 3));

        Assert.DoesNotContain(lines, line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);
    }

    [Fact]
    public void PayloadPrefix_IsNotWrittenOutboundOrForASegmentWithoutAnIpcHeader()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var payload = Payload(0x1122_3344_5566_7788, 32);
        var control = Decoded(MessageDirection.Inbound, 0, Self, Self, payload) with { SegmentType = 7 };

        var lines = probe.Observe(Decoded(MessageDirection.Outbound, LoginOpcode, Self, Self, payload), "conn-a", 1, 1)
            .Concat(probe.Observe(control, "conn-a", 2, 2));

        Assert.DoesNotContain(lines, line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);
    }

    [Fact]
    public void PayloadPrefix_HandlesAnEightBytePayloadAndAnUnalignedTail()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var eight = Payload(0x1122_3344_5566_7788, CaptureIdentityProbe.PrefixBytes);
        var ten = new byte[10];
        ten[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(ten.AsSpan(4), Self);

        var exact = Prefix(probe.Observe(Inbound(LoginOpcode, Self, eight), "conn-a", 1, 1));
        var tail = Prefix(probe.Observe(Inbound(0x0333, Self, ten), "conn-a", 2, 2));

        Assert.Equal(CaptureIdentityProbe.PrefixBytes, (int)exact["len"]!);
        Assert.Null(exact["self_at"]);
        Assert.Equal(4, (int)tail["self_at"]!);
    }

    [Fact]
    public void ShortOrAllZeroPayloads_GetNoPrefixLine()
    {
        var probe = new CaptureIdentityProbe(KeyA);

        var shortPayload = probe.Observe(Inbound(LoginOpcode, Self, new byte[4]), "conn-a", 1, 1);
        var zeroPayload = probe.Observe(Inbound(0x0333, Self, new byte[32]), "conn-a", 2, 2);

        Assert.DoesNotContain(shortPayload.Concat(zeroPayload),
            line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);
    }

    [Fact]
    public void LineCap_StopsWritingButKeepsCounting()
    {
        var probe = new CaptureIdentityProbe(KeyA, maxLines: 2);

        var written = 0;
        for (uint actor = 1; actor <= 5; actor++)
        {
            written += probe.Observe(Inbound(0x0100, actor, new byte[0]), "conn-" + actor, actor, actor).Count;
        }

        var summary = probe.Summary();
        Assert.Equal(2, written);
        Assert.True((bool)summary["truncated"]!);
        Assert.Equal(5, summary["actors"]!.AsArray().Count);
    }

    [Fact]
    public void ActorsBeyondThePerConnectionCap_AreCountedButNotListed()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var extra = 4;

        var written = 0;
        for (uint actor = 1; actor <= CaptureIdentityProbe.MaxActorsPerConnection + extra; actor++)
        {
            written += probe.Observe(Inbound(0x0100, actor, new byte[0]), "conn-a", actor, actor).Count;
        }

        var summary = probe.Summary();
        Assert.Equal(CaptureIdentityProbe.MaxActorsPerConnection, written);
        Assert.Equal(CaptureIdentityProbe.MaxActorsPerConnection, summary["actors"]!.AsArray().Count);
        Assert.Equal(extra, (long)summary["untracked_sightings"]!);
    }

    [Fact]
    public void ConnectionsBeyondTheCap_AreCountedButNotListed()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        var extra = 6;

        for (var index = 0; index < CaptureIdentityProbe.MaxTrackedConnections + extra; index++)
        {
            probe.Observe(Inbound(0x0100, Self, new byte[0]), "conn-" + index, index, index + 1);
        }

        var summary = probe.Summary();
        Assert.Equal(CaptureIdentityProbe.MaxTrackedConnections, summary["actors"]!.AsArray().Count);
        Assert.Equal(extra, (long)summary["untracked_sightings"]!);
    }

    [Fact]
    public void Summary_TalliesEveryActorPerConnection()
    {
        var probe = new CaptureIdentityProbe(KeyA);
        probe.Observe(Inbound(0x0100, Self, new byte[0]), "conn-a", 10, 1);
        probe.Observe(Inbound(0x0101, Self, new byte[0]), "conn-a", 25, 2);

        var summary = probe.Summary();
        var row = Assert.Single(summary["actors"]!.AsArray())!;

        Assert.Equal(probe.KeyId, (string)summary["key_id"]!);
        Assert.Equal("conn-a", (string)row["conn"]!);
        Assert.Equal(CaptureIdentityProbe.HeaderTargetKind, (string)row["kind"]!);
        Assert.Equal(2, (long)row["count"]!);
        Assert.Equal(10, (long)row["first_t_ms"]!);
        Assert.Equal(25, (long)row["last_t_ms"]!);
    }

    [Fact]
    public void Sink_WithoutAProbe_WritesNothingAboutIdentity()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer, probe: null);
        sink.WriteHeader(Header());
        Push(sink, Message(LoginOpcode, Payload(0x1122_3344_5566_7788, 32), Self, Self));
        sink.WriteSummary(dropped: 0);

        Assert.DoesNotContain("identity", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Sink_WithoutAProbe_WritesTheSameBytesWhateverTheActors()
    {
        static string Trace(uint source, uint target, int maxLines)
        {
            var writer = new StringWriter();
            var sink = NewSink(writer, probe: null, maxLines);
            sink.WriteHeader(Header());

            // Straight into the sink with fixed timestamps: a fake source would stamp its own clock.
            sink.Accept(Decoded(MessageDirection.Inbound, LoginOpcode, source, target, Payload(0x1122_3344_5566_7788, 32)));
            sink.Accept(Decoded(MessageDirection.Inbound, 0x0333, source, target, Payload(0x0102_0304_0506_0708, 16)));
            sink.WriteSummary(dropped: 0);
            return writer.ToString();
        }

        Assert.Equal(Trace(0, 0, 1000), Trace(Self, Self, 1000));
        Assert.Equal(Trace(0, 0, 1), Trace(Other, Self, 1));
    }

    [Fact]
    public void Sink_WithAProbe_WritesIdentityLinesAndSummary_ButNoIdentifier()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer, new CaptureIdentityProbe(KeyA));
        sink.WriteHeader(Header());
        Push(sink,
            Message(LoginOpcode, Payload(0x1122_3344_5566_7788, 32), Self, Self),
            Message(0x0333, Payload(0x99AA_BBCC_DDEE_FF11, 32), Other, Self));
        sink.WriteSummary(dropped: 0);

        var text = writer.ToString();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToList();

        Assert.True((bool)lines[0]["identity_probe"]!);
        Assert.Contains(lines, line => IdentityKind(line) == CaptureIdentityProbe.HeaderTargetKind);
        Assert.Single(lines, line => IdentityKind(line) == CaptureIdentityProbe.PayloadPrefixKind);
        Assert.Single(lines[^1]["identity"]!["actors"]!.AsArray());

        // The documented bound of a trace holds with the probe on: every digest is twelve
        // hexadecimal characters, and nothing longer appears anywhere.
        Assert.DoesNotMatch("[0-9a-fA-F]{13,}", text);

        // Neither actor, nor a payload prefix, nor the key may be readable, in hexadecimal or decimal.
        foreach (var forbidden in new[]
                 {
                     Self.ToString("x8"), Other.ToString("x8"), Self.ToString(), Other.ToString(),
                     "1122334455667788", "8877665544332211", "78563410", "54769810",
                     "99aabbccddeeff11", "11ffeeddccbbaa99",
                     Convert.ToHexString(KeyA)[..12], Convert.ToBase64String(KeyA)[..12],
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Sink_KeepsWritingIdentityLinesAfterTheMessageCap()
    {
        var writer = new StringWriter();
        var sink = NewSink(writer, new CaptureIdentityProbe(KeyA), maxLines: 1);
        sink.WriteHeader(Header());
        Push(sink,
            Message(0x0100, new byte[0], Other, Self),
            Message(0x0101, new byte[0], Other, Other));
        sink.WriteSummary(dropped: 0);

        var targets = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(CaptureIdentityProbe.HeaderTargetKind, StringComparison.Ordinal) &&
                           line.Contains("\"seq\"", StringComparison.Ordinal));
        Assert.Equal(2, targets);
        Assert.True(sink.Truncated);
    }

    [Fact]
    public void Report_DoesNotCountIdentityLinesAsUnreadable_AndPrintsTheTally()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "identity.jsonl");
        var probe = new CaptureIdentityProbe(KeyA);
        using (var file = new StreamWriter(path))
        {
            var sink = NewSink(file, probe);
            sink.WriteHeader(Header());
            Push(sink, Message(LoginOpcode, Payload(0x1122_3344_5566_7788, 32), Other, Self));
            sink.WriteSummary(dropped: 0);
        }

        var analysis = CaptureTraceAnalysis.Load(path);
        var output = new StringWriter();
        CaptureTraceReport.Run(path, null, CaptureTraceReport.DefaultWindowMs, output);

        Assert.Equal(0, analysis.UnreadableLines);
        Assert.Single(analysis.Messages);
        Assert.Contains(probe.ActorId(Self), output.ToString(), StringComparison.Ordinal);
        Assert.Contains(probe.KeyId, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Key_IsCreatedOnce_AndReadBackUnchanged()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "nested", DatabasePathsKeyName);

        var created = IdentityProbeKey.LoadOrCreate(path);
        var loaded = IdentityProbeKey.LoadOrCreate(path);

        Assert.Equal(CaptureIdentityProbe.KeyBytes, created.Length);
        Assert.Equal(created, loaded);
        Assert.Contains(created, value => value != 0);
        Assert.Equal(new[] { DatabasePathsKeyName }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(file => Path.GetFileName(file)!));
    }

    [Fact]
    public void Key_IsEncryptedAtRest_SoACopiedFileDoesNotCarryIt()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, DatabasePathsKeyName);

        var key = IdentityProbeKey.LoadOrCreate(path);
        var stored = File.ReadAllBytes(path);

        Assert.True(stored.Length > CaptureIdentityProbe.KeyBytes);
        Assert.Equal(-1, stored.AsSpan().IndexOf(key.AsSpan(0, 8)));

        // Written for another purpose, the same bytes are not a key here.
        File.WriteAllBytes(path, Speech.DpapiProtector.Instance.Protect(key));
        Assert.Throws<InvalidDataException>(() => IdentityProbeKey.LoadOrCreate(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Key_ThatIsNotAKey_IsRefusedRatherThanReplaced(int length)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, DatabasePathsKeyName);
        File.WriteAllBytes(path, new byte[length]);

        Assert.Throws<InvalidDataException>(() => IdentityProbeKey.LoadOrCreate(path));
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public void Key_OfTheWrongLengthInsideAValidBlob_IsRefused()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, DatabasePathsKeyName);
        File.WriteAllBytes(path, new byte[7]);

        Assert.Throws<InvalidDataException>(() => IdentityProbeKey.LoadOrCreate(path, new PlainProtector()));
    }

    [Fact]
    public void Key_CreatedByManyAtOnce_IsTheSameForAll()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, DatabasePathsKeyName);
        var keys = new byte[16][];

        Parallel.For(0, keys.Length, index => keys[index] = IdentityProbeKey.LoadOrCreate(path));

        Assert.All(keys, key => Assert.Equal(keys[0], key));
        Assert.Equal(new[] { DatabasePathsKeyName }, Directory.GetFiles(directory.Path).Select(file => Path.GetFileName(file)!));
    }

    [Fact]
    public async Task TraceRun_WithTheFlag_CreatesTheKeyAndWritesIdentityLines()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "trace.jsonl");
        var keyPath = Path.Combine(directory.Path, DatabasePathsKeyName);
        var source = new FakeCaptureSource();
        var output = new StringWriter();

        var run = Task.Run(() => CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, DurationSeconds: 2, AdapterId: "adapter", IdentityProbe: true),
            Services(source, output) with { IdentityKeyPath = keyPath }));
        Assert.True(SpinWait.SpinUntil(() => source.IsRunning, 5000));
        source.PushRaw(Message(LoginOpcode, Payload(0x1122_3344_5566_7788, 32), Other, Self));

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(File.Exists(keyPath));
        var text = File.ReadAllText(tracePath);
        Assert.Contains("\"identity_probe\":true", text, StringComparison.Ordinal);
        Assert.Contains(CaptureIdentityProbe.HeaderTargetKind, text, StringComparison.Ordinal);
        Assert.Contains(CaptureTraceRunner.IdentityProbeNotice, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TraceRun_WithoutTheFlag_TouchesNoKey()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "trace.jsonl");
        var keyPath = Path.Combine(directory.Path, DatabasePathsKeyName);
        var source = new FakeCaptureSource();

        var exit = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, DurationSeconds: 1, AdapterId: "adapter"),
            Services(source, new StringWriter()) with { IdentityKeyPath = keyPath });

        Assert.Equal(0, exit);
        Assert.False(File.Exists(keyPath));
        Assert.DoesNotContain("identity", File.ReadAllText(tracePath), StringComparison.Ordinal);
    }

    [Fact]
    public void TraceRun_WithAnUnusableKey_RefusesBeforeOpeningTheTrace()
    {
        using var directory = new TempDirectory();
        var tracePath = Path.Combine(directory.Path, "trace.jsonl");
        var keyPath = Path.Combine(directory.Path, DatabasePathsKeyName);
        File.WriteAllBytes(keyPath, new byte[3]);
        var output = new StringWriter();

        var exit = CaptureTraceRunner.Run(
            new CaptureTraceOptions(tracePath, DurationSeconds: 1, AdapterId: "adapter", IdentityProbe: true),
            Services(new FakeCaptureSource(), output) with { IdentityKeyPath = keyPath });

        Assert.Equal(1, exit);
        Assert.False(File.Exists(tracePath));
        Assert.Contains(CaptureTraceRunner.IdentityKeyInvalidRefusal, output.ToString(), StringComparison.Ordinal);
    }

    private const string DatabasePathsKeyName = Storage.DatabasePaths.IdentityProbeKeyFileName;

    private static CaptureTraceServices Services(FakeCaptureSource source, TextWriter output) => new()
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(
            new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 1234), new FakeGameFileReader()),
        Adapters = new AdapterEnumerator(
            new FakeAdapterProvider().Add("adapter", "Ethernet", addresses: "10.0.0.2"), new FakeProcessTcpTable()),
        SourceFactory = () => source,
        Markers = new StringReader(string.Empty),
        Output = output,
        Status = new StringWriter(),
        InstallCancelHandler = false,
    };

    /// <summary>Kind of an identity line; null for any other line, the summary included.</summary>
    private static string? IdentityKind(JsonObject line) =>
        line["identity"] is JsonValue kind && kind.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Prefix(IReadOnlyList<JsonObject> lines) =>
        lines.Single(line => (string)line["identity"]! == CaptureIdentityProbe.PayloadPrefixKind);

    private static byte[] Payload(ulong prefix, int length)
    {
        var payload = new byte[length];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, prefix);
        return payload;
    }

    /// <summary>A server-to-client message about this client: source and target are the same actor.</summary>
    private static DecodedMessage Inbound(ushort opcode, uint target, byte[] payload) =>
        Decoded(MessageDirection.Inbound, opcode, source: target, target, payload);

    private static DecodedMessage Decoded(
        MessageDirection direction, ushort opcode, uint source, uint target, byte[] payload) =>
        new(
            "identity-test",
            direction,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.Zero,
            0,
            FfxivFraming.SegmentTypeIpc,
            opcode,
            payload,
            "connection-not-written")
        {
            SourceActor = source,
            TargetActor = target,
        };

    /// <summary>A well-formed IPC message with both header actors and the given payload.</summary>
    private static byte[] Message(ushort opcode, byte[] payload, uint source, uint target)
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(4), source);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(8), target);
        payload.CopyTo(message.AsSpan(FfxivFraming.HeaderBytes));
        return message;
    }

    private static void Push(CaptureTraceSink sink, params byte[][] messages)
    {
        using var source = new FakeCaptureSource();
        source.Start(StartOptions(), new SinkObserver(sink));
        foreach (var message in messages)
        {
            source.PushRaw(message);
        }
    }

    private static CaptureStartOptions StartOptions() =>
        new("identity-test", 1234, null, "adapter", OodleMode.FfxivTcp, null, null);

    private static CaptureTraceSink NewSink(TextWriter writer, CaptureIdentityProbe? probe, int maxLines = 1000)
    {
        var elapsed = TimeSpan.Zero;
        return new CaptureTraceSink(
            writer,
            new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            () => elapsed += TimeSpan.FromMilliseconds(10),
            maxLines,
            probe);
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

    private sealed class SinkObserver(CaptureTraceSink sink) : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) => sink.Accept(message);

        public void OnDecodeError() => sink.CountDecodeError();

        public void OnFault(string reason, Exception? error)
        {
        }
    }

    /// <summary>Stores what it is given, so a test can plant a well-formed blob of the wrong content.</summary>
    private sealed class PlainProtector : Speech.ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext.ToArray();

        public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
    }

    private sealed class Collecting(List<DecodedMessage> seen) : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) => seen.Add(message);

        public void OnDecodeError()
        {
        }

        public void OnFault(string reason, Exception? error)
        {
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MentorRecorderIdentity", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
