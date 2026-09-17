using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The <c>ExportDiagnosticsReport</c> writer.
///
/// This report is the one file the project invites a user to hand to a stranger, so the
/// assertions state what may not be in it. They run over the bytes actually written rather
/// than over the object that was built, because a formatting step can reintroduce a path.
/// </summary>
public sealed class ExportDiagnosticsReportTests : IDisposable
{
    private const string IPv4Pattern = @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b";
    /// <summary>
    /// Colon-separated hex groups. The lookahead requires at least one a-f digit, which is
    /// what keeps a wall-clock time such as <c>13:45:00</c> from reading as an address.
    /// </summary>
    private const string IPv6Pattern =
        @"\b(?=[0-9a-fA-F:]*[a-fA-F])[0-9a-fA-F]{0,4}(?::[0-9a-fA-F]{0,4}){2,}\b";
    private const string SidPattern = @"\bS-1-\d+(?:-\d+)+\b";
    private const string WindowsPathPattern = @"[A-Za-z]:\\|\\\\[A-Za-z0-9]";
    private const string HexDumpPattern = "[0-9a-fA-F]{16,}";
    private const string EnvironmentPlaceholderPattern = "%[A-Za-z]+%";

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 4, 13, 45, 0, TimeSpan.Zero));
    private readonly string _directory;

    public ExportDiagnosticsReportTests()
    {
        // A writable local checkout may live outside the user's profile, so the fixture uses a
        // unique directory of its own rather than relying on OS-protected folders.
        _directory = Path.Combine(
            AppContext.BaseDirectory, "MentorRecorder.DiagReport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp debris only.
        }
    }

    [Fact]
    public void WritesTheGeneratedNameUnderTheDatabaseFolderWhenNoPathIsGiven()
    {
        var export = NewExport();

        var result = export.Write(Loaded(), "0.1.0");

        Assert.Equal(
            Path.Combine(export.DefaultDirectory, "diag_20260904_1345.json"), result.TargetPath);
        Assert.True(File.Exists(result.TargetPath));
        Assert.Equal(new FileInfo(result.TargetPath).Length, result.ByteCount);
        Assert.Equal(_clock.UtcNow, result.CompletedAtUtc);
    }

    [Fact]
    public void WritesIntoADirectoryTheCallerNames()
    {
        var target = Path.Combine(_directory, "chosen");
        Directory.CreateDirectory(target);

        var result = NewExport().Write(Loaded(), "0.1.0", target);

        Assert.Equal(Path.Combine(target, "diag_20260904_1345.json"), result.TargetPath);
    }

    [Fact]
    public void ReplacesAnEarlierReportOfItsOwnGeneratedName()
    {
        // A report is a fresh observation, so replacing an older file the exporter itself
        // named is the expected behaviour rather than a conflict the user has to resolve.
        // Only that name, though: see RefusesToReplaceAFileItDidNotName.
        var target = Path.Combine(_directory, "diag_20260904_1345.json");
        File.WriteAllText(target, "stale");

        var result = NewExport().Write(Loaded(), "0.1.0", target);

        Assert.Equal(target, result.TargetPath);
        Assert.DoesNotContain("stale", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToReplaceAFileItDidNotName()
    {
        // "diag.json" is a name a person would plausibly choose, and it is not one this
        // exporter produces. Replacing it unasked is how a diagnostics report ends up
        // destroying something the user cannot get back (review finding H3).
        var target = Path.Combine(_directory, "diag.json");
        File.WriteAllText(target, "stale");

        var failure = Assert.Throws<CollectorException>(
            () => NewExport().Write(Loaded(), "0.1.0", target));

        Assert.Equal(ErrorCodes.ExportFailed, failure.Code);
        Assert.Equal("stale", File.ReadAllText(target));
    }

    [Fact]
    public void RefusesANetworkDestination()
    {
        var failure = Assert.Throws<CollectorException>(
            () => NewExport().Write(Loaded(), "0.1.0", @"\\mentor-test.invalid\reports\mentor-diag.json"));

        Assert.Equal(ErrorCodes.ExportFailed, failure.Code);
    }

    [Fact]
    public void TheWrittenFileCarriesNoAddressNoSidNoPathAndNoPayload()
    {
        var path = NewExport().Write(Loaded(), "0.1.0").TargetPath;

        var text = File.ReadAllText(path);

        Assert.DoesNotMatch(IPv4Pattern, text);
        Assert.DoesNotMatch(IPv6Pattern, text);
        Assert.DoesNotMatch(SidPattern, text);
        Assert.DoesNotMatch(WindowsPathPattern, text);
        Assert.DoesNotMatch(HexDumpPattern, text);
        Assert.DoesNotMatch(EnvironmentPlaceholderPattern, text);

        // The specific values the loaded snapshot carries, each of which would identify this
        // machine, its network or its owner.
        Assert.DoesNotContain("192.168", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.31.x", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fe80", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S-1-5-21", text, StringComparison.Ordinal);
        Assert.DoesNotContain("{7C4A8D09-1111-2222-3333-444455556666}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SdoA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ffxiv_dx11.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warnings", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheWrittenFileCarriesEverythingADiagnosisNeeds()
    {
        var path = NewExport().Write(Loaded(), "0.4.2").TargetPath;
        var report = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        Assert.Equal("0.4.2", report["app_version"]!.GetValue<string>());
        Assert.Equal("0.4.2", report["collector_version"]!.GetValue<string>());
        Assert.Equal(IpcEnvelope.ProtocolVersion, report["ipc_protocol_version"]!.GetValue<int>());
        Assert.Equal(CaptureDiagnosticsSnapshot.LiveCaptureStatus, report["live_capture_status"]!.GetValue<string>());
        Assert.True(report["public_distribution_ready"]!.GetValue<bool>());

        Assert.Equal("READY", report["npcap"]!["status"]!.GetValue<string>());
        Assert.NotNull(report["npcap"]!["version"]);

        Assert.True(report["game"]!["running"]!.GetValue<bool>());
        Assert.Equal(4321, report["game"]!["process_id"]!.GetValue<int>());
        Assert.Equal("CN", report["game"]!["region"]!.GetValue<string>());
        Assert.Equal("2024.06.18.0000.0000", report["game"]!["game_build"]!.GetValue<string>());

        // The adapter is a digest plus a count, never the identifier or the addresses.
        Assert.True(report["adapter"]!["selected"]!.GetValue<bool>());
        Assert.Equal(
            SanitizedDiagnosticsReport.AdapterFingerprintLength,
            report["adapter"]!["fingerprint"]!.GetValue<string>().Length);
        Assert.Equal(2, report["adapter"]!["address_count"]!.GetValue<int>());

        Assert.Equal("Running", report["capture"]!["state"]!.GetValue<string>());
        Assert.Equal(17, report["counters"]!["packets_observed"]!.GetValue<long>());
        Assert.Equal(900, report["counters"]!["parse_ok"]!.GetValue<long>());
        Assert.Equal(12, report["counters"]!["parse_fail"]!.GetValue<long>());
        Assert.Equal("NONE", report["profile"]!["status"]!.GetValue<string>());
        Assert.Equal("IDLE", report["run"]!["state"]!.GetValue<string>());

        var error = Assert.Single(report["recent_parser_errors"]!.AsArray())!.AsObject();
        Assert.Equal("E_UNKNOWN_OPCODE", error["code"]!.GetValue<string>());
        Assert.Equal("0x01A3", error["opcode"]!.GetValue<string>());
        Assert.Equal("S2C", error["direction"]!.GetValue<string>());
    }

    private DiagnosticsReportExport NewExport() =>
        new(Path.Combine(_directory, "mentor_recorder.db"), _clock);

    /// <summary>A snapshot carrying every kind of value the report must not leak.</summary>
    private static CaptureDiagnosticsSnapshot Loaded() => new()
    {
        State = CaptureControllerState.Running,
        CaptureSessionId = "5a5f0a5c-0000-4000-8000-000000000001",
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()).Detect(),
        Game = new GameProcessDetection(
            true,
            4321,
            "ffxiv_dx11",
            DateTimeOffset.UnixEpoch,
            Region.Cn,
            "2024.06.18.0000.0000",
            1,
            @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe",
            Array.Empty<string>()),
        Profile = NoProfileStatusProvider.Instance.Current,
        AdapterId = "{7C4A8D09-1111-2222-3333-444455556666}",
        AdapterName = "Wi-Fi",
        AdapterMaskedIPv4 = new[] { "192.168.31.x", "10.0.0.x" },
        ConnectionCount = 2,
        PacketsObserved = 17,
        MessagesDecoded = 16,
        DecodeErrors = 1,
        ParseOkCount = 900,
        ParseFailCount = 12,
        DuplicateCount = 5,
        DroppedCount = 0,
        QueueDepth = 3,
        QueueCapacity = 4096,
        MessageRatePerSecond = 12.5,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        UptimeMs = 1234,
        Oodle = OodleMode.FfxivTcp,
        ReadsGameExecutable = true,
        GameExecutableKnown = true,
        RecentParserErrors = new[]
        {
            new ParserErrorView(
                DateTimeOffset.UnixEpoch,
                "E_UNKNOWN_OPCODE",
                0x01A3,
                "S2C",
                "no profile message claims this opcode"),
        },
        Warnings = new[]
        {
            @"游戏安装路径为 D:\SdoA\FFXIV",
            "本机地址 192.168.31.77 / fe80::1，登录用户 S-1-5-21-1111-2222-3333-1001",
            "最近一次解码失败的前八字节 DEADBEEFDEADBEEF",
        },
    };
}
