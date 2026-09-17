using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The diagnostics snapshot and the sanitized report the user is invited to hand to a
/// maintainer. The report is the one artefact of this project that is meant to leave the
/// machine, so what it may not contain is asserted directly.
/// </summary>
public sealed class CaptureDiagnosticsTests
{
    private const string IPv4Pattern = @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b";
    private const string WindowsPathPattern = @"[A-Za-z]:\\";

    /// <summary>Sixteen hex characters in a row: eight bytes of somebody's traffic.</summary>
    private const string HexDumpPattern = "[0-9a-fA-F]{16,}";

    [Fact]
    public void SanitizedReportCarriesNoAddressNoPathAndNoPayload()
    {
        var json = SanitizedDiagnosticsReport
            .Build(Loaded(), "0.1.0", DateTimeOffset.UnixEpoch)
            .ToJsonString();

        Assert.DoesNotMatch(IPv4Pattern, json);
        Assert.DoesNotMatch(WindowsPathPattern, json);

        // Not even the masked form the UI shows, and never the adapter's own GUID.
        Assert.DoesNotContain("192.168", json, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.31.x", json, StringComparison.Ordinal);
        Assert.DoesNotContain("{7C4A8D09-1111-2222-3333-444455556666}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SdoA", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ffxiv_dx11.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DEADBEEF", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PopulatedCalibrationEvidenceExportsCountsWithoutPayloadOrTimeline()
    {
        var template = CalibrationObserverTests.Template();
        var observer = new CalibrationObserver(template, Region.Cn, "calibration-session");
        var marker = System.Text.Encoding.ASCII.GetBytes("CALIBRATION-PRIVATE");
        foreach (var message in CalibrationObserverTests.Session1(popState: 9))
        {
            var payload = message.Payload.ToArray();
            if (payload.Length == 40)
            {
                marker.CopyTo(payload, 20);
            }

            observer.Accept(message with { Payload = payload, ConnectionKey = "203.0.113.9:5500-private" });
        }

        observer.Flush();
        var snapshot = observer.Snapshot();
        var draft = CalibrationDraft.Derive(snapshot, template);
        var calibration = new CalibrationStatusSnapshot(
            CalibrationState.Ready, "2026.09.01.0000.0000", template.Source.ProfileId,
            draft.Progress, draft.Blockers, draft.Events, null, null,
            CalibrationEvidenceSummary.From(snapshot, template));
        var report = SanitizedDiagnosticsReport.Build(
            Loaded() with { Calibration = calibration }, "0.3.4", DateTimeOffset.UnixEpoch);
        var json = report.ToJsonString();
        var calibrationReport = report["calibration"]!.AsObject();
        var evidence = calibrationReport["evidence"]!;

        Assert.NotEmpty(evidence["finder_states"]!.AsArray());
        Assert.NotEmpty(evidence["finder_lengths"]!.AsArray());
        Assert.NotEmpty(evidence["zone_once_only"]!.AsArray());
        Assert.False(calibrationReport.ContainsKey("events"));
        Assert.DoesNotContain("CALIBRATION-PRIVATE", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(marker), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(marker), json, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.9", json, StringComparison.Ordinal);
        Assert.DoesNotContain("connection_tag", json, StringComparison.Ordinal);
        // Refusal counters may name roulette_id, but the played id and timeline never leave.
        Assert.DoesNotContain("\"roulette_id\":", calibrationReport.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"territory_id\":", calibrationReport.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizedReportKeepsWhatDiagnosisActuallyNeeds()
    {
        var report = SanitizedDiagnosticsReport.Build(Loaded(), "0.1.0", DateTimeOffset.UnixEpoch);

        Assert.Equal("0.1.0", report["collector_version"]!.GetValue<string>());
        Assert.Equal(CaptureDiagnosticsSnapshot.LiveCaptureStatus, report["live_capture_status"]!.GetValue<string>());
        Assert.Equal("WinPCap", report["boundary"]!["monitor_type"]!.GetValue<string>());
        Assert.False(report["boundary"]!["injected_hook_enabled"]!.GetValue<bool>());
        Assert.False(report["boundary"]!["reads_game_process_memory"]!.GetValue<bool>());
        Assert.Equal(0, report["boundary"]!["listening_ports"]!.GetValue<int>());
        // Report version 2 replaces the constant outbound_connections with what the request did.
        Assert.False(report["boundary"]!.AsObject().ContainsKey("outbound_connections"));
        var outbound = report["boundary"]!["outbound"]!;
        Assert.True(outbound["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.False(outbound["kill_switch"]!.GetValue<bool>());
        Assert.Null(outbound["last_fetch_utc"]);
        Assert.Null(outbound["last_fetch_status"]);
        Assert.Equal("2024.06.18.0000.0000", report["game"]!["game_build"]!.GetValue<string>());
        Assert.Equal(17, report["counters"]!["packets_observed"]!.GetValue<long>());
        Assert.Equal("NONE", report["profile"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public void AdapterFingerprintIsStableAndIrreversible()
    {
        const string id = "{7C4A8D09-1111-2222-3333-444455556666}";

        var fingerprint = SanitizedDiagnosticsReport.Fingerprint(id);

        Assert.Equal(fingerprint, SanitizedDiagnosticsReport.Fingerprint(id));
        Assert.NotEqual(fingerprint, SanitizedDiagnosticsReport.Fingerprint("{OTHER}"));
        Assert.Equal(SanitizedDiagnosticsReport.AdapterFingerprintLength, fingerprint!.Length);
        Assert.DoesNotContain("7C4A8D09", fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SanitizedDiagnosticsReport.Fingerprint(null));
    }

    [Fact]
    public void DisclosesWhetherTheGameExecutableIsRead()
    {
        var reading = SanitizedDiagnosticsReport.Build(Loaded(), "0.1.0", DateTimeOffset.UnixEpoch);
        var notReading = SanitizedDiagnosticsReport.Build(
            Loaded() with { Oodle = OodleMode.LibraryTcp, ReadsGameExecutable = false },
            "0.1.0",
            DateTimeOffset.UnixEpoch);

        // DEC-OODLE-01 requires the user to be told which of the two modes is in force.
        Assert.True(reading["boundary"]!["reads_game_executable"]!.GetValue<bool>());
        Assert.Equal("FfxivTcp", reading["boundary"]!["oodle_mode"]!.GetValue<string>());
        Assert.False(notReading["boundary"]!["reads_game_executable"]!.GetValue<bool>());
        Assert.Equal("LibraryTcp", notReading["boundary"]!["oodle_mode"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(CaptureControllerState.Unavailable, CaptureState.Stopped)]
    [InlineData(CaptureControllerState.Idle, CaptureState.Stopped)]
    [InlineData(CaptureControllerState.Stopping, CaptureState.Stopped)]
    [InlineData(CaptureControllerState.Starting, CaptureState.Starting)]
    [InlineData(CaptureControllerState.Running, CaptureState.Running)]
    [InlineData(CaptureControllerState.Faulted, CaptureState.Failed)]
    public void MapsEveryControllerStateOntoAContractState(
        CaptureControllerState state, CaptureState expected) =>
        Assert.Equal(expected, (Loaded() with { State = state }).ContractState);

    [Fact]
    public void ReportsDegraded_WhenRunningAfterADrop()
    {
        var snapshot = Loaded() with { State = CaptureControllerState.Running, DroppedCount = 1 };

        Assert.True(snapshot.IsDegraded);
        Assert.Equal(CaptureState.Degraded, snapshot.ContractState);
    }

    [Fact]
    public void ReportsDegraded_WhenTheQueueIsPastFourFifths()
    {
        var snapshot = Loaded() with
        {
            State = CaptureControllerState.Running,
            QueueCapacity = 100,
            QueueDepth = 81,
        };

        Assert.True(snapshot.IsDegraded);
    }

    [Fact]
    public void IsNotDegradedWhileStopped() =>
        Assert.False((Loaded() with { State = CaptureControllerState.Idle, DroppedCount = 99 }).IsDegraded);

    [Fact]
    public void CaptureStatusRendersTheBoundaryConstants()
    {
        var status = CaptureWire.CaptureStatus(Loaded());

        Assert.Equal("WinPCap", status["monitor_type"]!.GetValue<string>());
        Assert.False(status["injected_hook_enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void CaptureStatusPassesThroughOnlyMaskedAddresses()
    {
        var adapters = new AdapterEnumerator(
                new FakeAdapterProvider().Add("{ABC}", "Wi-Fi", addresses: "192.168.31.77"),
                new FakeProcessTcpTable())
            .List(null);

        var json = CaptureWire.Adapters(Loaded(), adapters).ToJsonString();

        Assert.Contains("192.168.31.x", json, StringComparison.Ordinal);
        Assert.DoesNotMatch(IPv4Pattern, json);
    }

    [Fact]
    public void LastErrorCodeIsUnavailable_WhenCaptureCannotRunAtAll()
    {
        var snapshot = Loaded() with { State = CaptureControllerState.Unavailable, LastErrorCode = null };

        Assert.Equal("UNAVAILABLE", CaptureWire.LastErrorCode(snapshot));
    }

    [Fact]
    public void LastErrorCodeIsNull_WhileCaptureIsHealthy() =>
        Assert.Null(CaptureWire.LastErrorCode(
            Loaded() with { State = CaptureControllerState.Running, LastErrorCode = null }));

    [Fact]
    public void CaptureStatusRendersEveryPipelineCounter()
    {
        var status = CaptureWire.CaptureStatus(Loaded() with
        {
            ParseOkCount = 900,
            ParseFailCount = 12,
            DuplicateCount = 5,
            IgnoredCount = 41207,
            LastValidEventAtUtc = DateTimeOffset.UnixEpoch,
            LastValidEventKind = "DUTY_RESULT",
        });

        Assert.Equal("DUTY_RESULT", status["last_valid_event_kind"]!.GetValue<string>());
        Assert.Null(CaptureWire.CaptureStatus(Loaded())["last_valid_event_kind"]);
        Assert.True(CaptureWire.CaptureStatus(Loaded()).AsObject().ContainsKey("last_valid_event_kind"));

        // The counters the diagnostics page shows; without them it cannot print numbers at all
        // (contracts/CHANGELOG.md entry 14).
        Assert.Equal(17, status["packets_observed"]!.GetValue<long>());
        Assert.Equal(16, status["messages_decoded"]!.GetValue<long>());
        Assert.Equal(1, status["decode_errors"]!.GetValue<long>());
        Assert.Equal(900, status["parse_ok_count"]!.GetValue<long>());
        Assert.Equal(12, status["parse_fail_count"]!.GetValue<long>());
        Assert.Equal(5, status["duplicate_count"]!.GetValue<long>());
        Assert.Equal(41207, status["ignored_count"]!.GetValue<long>());
        Assert.Equal(2, status["connection_count"]!.GetValue<int>());
        Assert.Equal(0, status["packets_dropped"]!.GetValue<long>());
        Assert.Equal(3, status["queue_depth"]!.GetValue<int>());
        Assert.Equal(4096, status["queue_capacity"]!.GetValue<int>());
        Assert.Equal(12.5, status["message_rate_per_second"]!.GetValue<double>());
        Assert.Equal(1234, status["uptime_ms"]!.GetValue<long>());
        Assert.Equal(
            "1970-01-01T00:00:00.000Z", status["last_valid_event_at_utc"]!.GetValue<string>());
        Assert.Empty(status["recent_parser_errors"]!.AsArray());
    }

    [Fact]
    public void CaptureStatusRendersAParserErrorAsCodeHexOpcodeAndDirection()
    {
        var status = CaptureWire.CaptureStatus(Loaded() with
        {
            RecentParserErrors = new[]
            {
                new ParserErrorView(
                    DateTimeOffset.UnixEpoch,
                    "E_LEN_MISMATCH",
                    0x01A3,
                    "S2C",
                    "CONTENT_FINDER_POP: payload length 8 violates the declared length rule"),
            },
        });

        var row = Assert.Single(status["recent_parser_errors"]!.AsArray())!.AsObject();
        Assert.Equal("1970-01-01T00:00:00.000Z", row["at_utc"]!.GetValue<string>());
        Assert.Equal("E_LEN_MISMATCH", row["code"]!.GetValue<string>());
        Assert.Equal("0x01A3", row["opcode"]!.GetValue<string>());
        Assert.Equal("S2C", row["direction"]!.GetValue<string>());
        Assert.Contains("length rule", row["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheLastTwentyParserErrorsEverReachTheWire()
    {
        // The parser keeps a hundred; a broken profile must not turn every status frame into
        // a kilobyte of the same sentence repeated.
        var errors = Enumerable.Range(0, 100)
            .Select(i => new ParserErrorView(
                DateTimeOffset.UnixEpoch,
                "E_UNKNOWN_OPCODE",
                (ushort)i,
                "S2C",
                "no profile message claims this opcode"))
            .ToArray();

        var rendered = CaptureWire.ParserErrors(errors);

        Assert.Equal(CaptureDiagnosticsSnapshot.MaxRecentParserErrors, rendered.Count);

        // The tail, oldest first: the twenty most recent refusals are opcodes 80..99.
        Assert.Equal("0x0050", rendered[0]!["opcode"]!.GetValue<string>());
        Assert.Equal("0x0063", rendered[^1]!["opcode"]!.GetValue<string>());
    }

    [Fact]
    public void AParserRefusalNeverCarriesOneByteOfThePayload()
    {
        // Driven through the real parser rather than a hand-written view: the guarantee is
        // about what ProfileMessageParser writes, not about what the fixture leaves out
        // (docs/capture-diagnostics.md section 5.3).
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.ParserErrors", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var profile = ProfileLoader.Load(
                ProfileTestFiles.Write(directory, "leak-profile", ProfileTestFiles.Valid()));
            var parser = new ProfileMessageParser(profile, new DiscardingSemanticSink());

            // Every byte is recognisable, so any transcription of the payload shows up.
            var payload = new byte[64];
            Array.Fill(payload, (byte)0xDE);
            payload[1] = 0xAD;
            payload[2] = 0xBE;
            payload[3] = 0xEF;

            // A length no declared message accepts, then an opcode no message claims. The
            // second is ignored rather than refused, so only one refusal reaches the ring.
            parser.Accept(DecodedFor(100, payload));
            parser.Accept(DecodedFor(60000, payload));

            var stats = parser.GetParserStats();
            Assert.Equal(1, stats.ParseFailed);
            Assert.Equal(1, stats.Ignored);

            var json = CaptureWire.ParserErrors(stats.RecentErrors
                    .Select(error => new ParserErrorView(
                        error.AtUtc, error.Kind, error.Opcode, "S2C", error.Message))
                    .ToArray())
                .ToJsonString();

            Assert.DoesNotContain("DEADBEEF", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("dead", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("beef", json, StringComparison.OrdinalIgnoreCase);

            // Nothing that even looks like a hex dump, an address or a filesystem path may
            // reach the wire.
            Assert.DoesNotMatch(HexDumpPattern, json);
            Assert.DoesNotMatch(IPv4Pattern, json);
            Assert.DoesNotMatch(WindowsPathPattern, json);

            // What it does carry: a timestamp, a code and an opcode number.
            Assert.Contains("E_LEN_MISMATCH", json, StringComparison.Ordinal);
            Assert.Contains("0x0064", json, StringComparison.Ordinal);
            Assert.DoesNotContain("E_UNKNOWN_OPCODE", json, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp debris only.
            }
        }
    }

    [Fact]
    public void EveryParserRefusalIsStamped()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 4, 7, 30, 0, TimeSpan.Zero));
        var parser = new ProfileMessageParser(null, new DiscardingSemanticSink(), clock);

        parser.Accept(DecodedFor(1, new byte[] { 1, 2 }));

        var error = Assert.Single(parser.GetParserStats().RecentErrors);
        Assert.Equal(ParserErrorCode.ProfileUnsupported, error.Code);
        Assert.Equal(clock.UtcNow, error.AtUtc);
    }

    private static DecodedMessage DecodedFor(int opcode, byte[] payload) => new(
        "20000000-0000-4000-8000-000000000099",
        MessageDirection.Inbound,
        new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero),
        TimeSpan.Zero,
        1000,
        0,
        (ushort)opcode,
        payload,
        "test-connection");

    private sealed class DiscardingSemanticSink : ISemanticEventSink
    {
        public void Accept(SemanticEvent semanticEvent)
        {
        }
    }

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
        AdapterMaskedIPv4 = new[] { "192.168.31.x" },
        ConnectionCount = 2,
        PacketsObserved = 17,
        MessagesDecoded = 16,
        DecodeErrors = 1,
        DroppedCount = 0,
        QueueDepth = 3,
        QueueCapacity = 4096,
        MessageRatePerSecond = 12.5,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
        UptimeMs = 1234,
        Oodle = OodleMode.FfxivTcp,
        ReadsGameExecutable = true,
        GameExecutableKnown = true,
        Warnings = new[] { "游戏安装路径为 D:\\SdoA\\FFXIV" },
    };

    [Fact]
    public void SanitizedReportDropsWarningsRatherThanRiskingWhatIsInThem()
    {
        // Warnings are free text assembled from several places. The report whitelists fields
        // instead of filtering them, so a warning that happens to carry a path cannot leak.
        var json = SanitizedDiagnosticsReport
            .Build(Loaded(), "0.1.0", DateTimeOffset.UnixEpoch)
            .ToJsonString();

        Assert.DoesNotContain("warnings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(WindowsPathPattern, json);
    }
}
