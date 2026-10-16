using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class TemplateObservationTests
{
    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mentor-template-observation-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private const string EvidenceId = "d67c38ad-3a98-4c9e-b570-740edda90525";
    private const string SessionAlias = "54174a36-0660-4ad6-8773-7b2ab8e719b2";

    private static string Trace(bool summary = true, bool synthetic = false)
    {
        var writer = new StringWriter();
        var sink = new CaptureTraceSink(writer, elapsed: () => TimeSpan.FromSeconds(4));
        sink.WriteHeader(new(DateTimeOffset.UnixEpoch, "private-driver", "2026.10.15.0000.0000",
            Region.Global, "private-device-hash", "0.0.0", OodleMode.FfxivTcp, synthetic));
        void Message(int time, ushort segment, byte[]? payload = null) => sink.Accept(new DecodedMessage(
            "private-session", MessageDirection.Inbound, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(time),
            123456, segment, 0xabcd, payload ?? Encoding.UTF8.GetBytes("private-payload"), "private-connection"));
        Message(0, 3);
        Message(9, 3);
        Message(10, 3);
        Message(10, 7);
        sink.WriteMarker("pop", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(9));
        sink.WriteMarker("pop", DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(10));
        if (summary) sink.WriteSummary(0);
        return writer.ToString();
    }

    private static Protocol.Sharing.CalibrationEvidenceResult Export(string trace)
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        File.WriteAllText(path, trace);
        return TemplateObservationExporter.Export(path, CaptureTraceDigest.Compute(path), EvidenceId, SessionAlias);
    }

    [Fact]
    public void ExactMetadataBucketsPreserveSegmentAndUnionOverlappingWindowsWithoutPublishingLocalIdentity()
    {
        var exported = Export(Trace());
        Assert.True(exported.Available, exported.Reason);
        var root = JsonNode.Parse(exported.EvidenceJson!)!;
        Assert.Equal("TEMPLATE_OBSERVATION", root["kind"]!.GetValue<string>());
        Assert.Equal("GLOBAL", root["target"]!["region"]!.GetValue<string>());
        Assert.Null(root["target"]!["template_profile_id"]);
        Assert.Null(root["target"]!["code_sha256"]);
        var rows = root["observations"]!.AsArray();
        Assert.Equal(2, rows.Count);
        var segment3 = rows.Single(row => row!["segment_type"]!.GetValue<int>() == 3)!;
        Assert.Equal(2, segment3["inside_count"]!.GetValue<int>());
        Assert.Equal(1, segment3["outside_count"]!.GetValue<int>());
        Assert.Equal(2, segment3["event_count"]!.GetValue<int>());
        Assert.Equal(0xabcd, segment3["opcode"]!.GetValue<int>());
        Assert.Equal("HEALTH_UNAVAILABLE", root["sessions"]![0]!["health"]!["silent_reason"]!.GetValue<string>());
        Assert.Empty(root["sessions"]![0]!["checks"]!.AsArray());
        Assert.DoesNotContain("private", exported.EvidenceJson!);
        foreach (var forbidden in new[] { "h12", "at_utc", "t_ms", "conn", "adapter_fingerprint", "epoch\"", "trace_path", "payload" })
            Assert.DoesNotContain("\"" + forbidden, exported.EvidenceJson!);
        Assert.Equal(exported.EvidenceJson, Export(Trace()).EvidenceJson);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("truncated")]
    [InlineData("corrupt")]
    [InlineData("identity")]
    [InlineData("duplicate")]
    [InlineData("oversized-line")]
    public void InvalidOrIncompleteTracesCannotBecomeSubmittable(string failure)
    {
        var trace = failure switch
        {
            "incomplete" => Trace(summary: false),
            "truncated" => Trace().Replace("\"truncated\":false", "\"truncated\":true", StringComparison.Ordinal),
            "corrupt" => Trace() + "{bad\n",
            "identity" => Trace().Replace("2026.10.15.0000.0000", "2026.10", StringComparison.Ordinal),
            "duplicate" => Trace().Replace("\"seg\":3", "\"seg\":3,\"seg\":7", StringComparison.Ordinal),
            _ => new string('x', 128 * 1024 + 1),
        };
        var result = Export(trace);
        Assert.False(result.Available);
        Assert.Null(result.EvidenceJson);
    }

    [Fact]
    public void ClosedFileMustStillMatchTheControllersFrozenDigest()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        File.WriteAllText(path, Trace());
        var digest = CaptureTraceDigest.Compute(path);
        File.AppendAllText(path, "\n");
        var result = TemplateObservationExporter.Export(path, digest, EvidenceId, SessionAlias);
        Assert.Equal("OBSERVATION_HASH_MISMATCH", result.Reason);
        Assert.False(result.Available);
    }

    [Fact]
    public void FileOverSixtyFourMiBLimitIsRejectedBeforeReading()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "trace.jsonl");
        using (var file = File.Create(path)) file.SetLength(CaptureTraceAnalysis.MaxInputBytes + 1);
        Assert.Equal("OBSERVATION_LIMIT", TemplateObservationExporter.Export(path, "", EvidenceId, SessionAlias).Reason);
    }

    [Fact]
    public void LargeMetadataSetIsBoundedAndExplicitlyMarkedOverflowing()
    {
        var writer = new StringWriter();
        var sink = new CaptureTraceSink(writer, elapsed: () => TimeSpan.Zero);
        sink.WriteHeader(new(DateTimeOffset.UnixEpoch, null, "2026.10.15.0000.0000", Region.Global,
            null, "0.0.0", OodleMode.FfxivTcp, false));
        for (ushort opcode = 1; opcode <= 129; opcode++)
            sink.Accept(new("local-session", MessageDirection.Inbound, DateTimeOffset.UnixEpoch, TimeSpan.Zero,
                0, 3, opcode, new byte[12], "local-connection"));
        sink.WriteMarker("pop");
        sink.WriteSummary(0);
        var result = Export(writer.ToString());
        Assert.True(result.Available, result.Reason);
        Assert.True(Encoding.UTF8.GetByteCount(result.EvidenceJson!) <= 48 * 1024);
        var root = JsonNode.Parse(result.EvidenceJson!)!;
        Assert.Equal(128, root["observations"]!.AsArray().Count);
        Assert.True(root["sessions"]![0]!["health"]!["observation_overflow"]!.GetValue<bool>());
        Assert.Empty(root["sessions"]![0]!["checks"]!.AsArray());
    }

    [Fact]
    public void ControllerOnlyExportsFinalizedOwnedSessionAndKeepsRepeatedPreviewIdentityStable()
    {
        using var db = new TestDatabase();
        var source = new FakeCaptureSource();
        var elapsed = TimeSpan.Zero;
        var game = new GameProcessDetection(true, 42, "ffxiv_dx11", DateTimeOffset.UnixEpoch, Region.Global,
            "2026.10.15.0000.0000", 1, @"D:\Game\ffxiv_dx11.exe", Array.Empty<UserText>());
        using var controller = new CaptureValidationController(db.Path, new(), new CaptureValidationServices
        {
            LocateGame = () => game, PollInterval = TimeSpan.FromMilliseconds(10), Elapsed = () => elapsed,
            Trace = new CaptureTraceServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Adapters = new AdapterEnumerator(new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"), new FakeProcessTcpTable()),
                SourceFactory = () => source, TcpConnectionCounter = (_, _) => 0,
            },
        });
        Assert.False(controller.GetTemplateObservation()["available"]!.GetValue<bool>());
        controller.Start("wifi");
        Assert.True(SpinWait.SpinUntil(() => controller.Snapshot()["state"]!.GetValue<string>() == "RECORDING", 5000));
        Assert.Equal("OBSERVATION_ACTIVE", controller.GetTemplateObservation()["reason"]!.GetValue<string>());
        source.PushOpcode(0xabcd);
        controller.AddMarker("pop");
        controller.Stop();
        Assert.True(SpinWait.SpinUntil(() => controller.Snapshot()["state"]!.GetValue<string>() == "COMPLETED", 5000));
        var first = controller.GetTemplateObservation();
        Assert.True(first["available"]!.GetValue<bool>(), first.ToJsonString());
        Assert.Equal(first.ToJsonString(), controller.GetTemplateObservation().ToJsonString());
        var document = JsonNode.Parse(first["evidence_json"]!.GetValue<string>())!;
        Assert.Equal("SYNTHETIC_SOURCE", document["sessions"]![0]!["health"]!["silent_reason"]!.GetValue<string>());
    }
}
