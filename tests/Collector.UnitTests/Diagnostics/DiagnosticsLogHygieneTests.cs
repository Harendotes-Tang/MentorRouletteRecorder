using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Pipeline;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What may and may not end up in the local diagnostic log.
///
/// docs/privacy-boundary.md section 5 and docs/capture-diagnostics.md section 8 list what the
/// log is never allowed to contain: raw bytes, hex dumps, chat text, character names, IP and
/// MAC addresses, SIDs, other users' paths, and anything about other players. They also list
/// what it may contain: timestamps, levels, component and event names, opcode numbers,
/// directions, lengths, parsed id fields, profile ids, counts and durations.
///
/// The tests feed the logger every kind of secret those documents name and read the file back.
/// The sanitizer redacts user profile paths, the game's install folder, IPv4 and IPv6 literals,
/// Windows SIDs and long hexadecimal runs. The short SHA-256 prefixes used as identifiers must survive it, or the
/// redaction would cost diagnostic value without buying any privacy. The last section covers
/// two loggers sharing one file.
/// </summary>
public sealed class DiagnosticsLogHygieneTests : IDisposable
{
    // Deliberately recognisable values; each one is something the boundary forbids writing.
    private const string Address = "192.168.31.77";
    private const string Sid = "S-1-5-21-3623811015-3361044348-30300820-1013";
    private const string ProfilePath = @"C:\Users\SomebodyElse\AppData\Local\MentorRecorder\x.db";
    private const string PayloadHex = "2a000000a1bb0d0001350c00a1bb0d000700000001000000";
    private const string IPv6Address = "2001:0db8:85a3:0000:0000:8a2e:0370:7334";
    private const string IPv6Compressed = "fe80::1c2d:5eff:fe4a:9b01";

    private readonly TempDirectory _directory = new("LogHygiene");

    // --------------------------------------------------------------- envelope and paths ----

    [Fact]
    public void AUserProfilePathIsRedactedWhereverItAppears()
    {
        var text = Write(logger => logger.Write(LogLevel.Warn, "capture", "temp_copy_left", new Dictionary<string, object?>()
        {
            ["path"] = ProfilePath,
            ["message"] = "could not delete " + ProfilePath + " because it is still mapped",
        }));

        Assert.DoesNotContain("SomebodyElse", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);

        // The non-identifying tail is kept: a redacted log still has to be useful.
        Assert.Contains("MentorRecorder", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RedactsAnyUsersProfileFolderNotJustTheCurrentOne()
    {
        var text = @"failed to open C:\Users\SomebodyElse\Documents\notes.txt";

        var sanitized = RotatingFileLogger.Sanitize(text);

        Assert.DoesNotContain("SomebodyElse", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", sanitized, StringComparison.Ordinal);
        Assert.Contains(@"\Documents\notes.txt", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_RedactsTheCurrentUsersProfileFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile), "Windows always has a user profile");

        var sanitized = RotatingFileLogger.Sanitize(profile + @"\AppData\Local\x.db");

        Assert.StartsWith("%USERPROFILE%", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_LeavesTextWithoutAProfilePathAlone() =>
        Assert.Equal("queue depth 12 of 4096", RotatingFileLogger.Sanitize("queue depth 12 of 4096"));

    [Theory]
    [InlineData(@"Copied from D:\APPS\FF14\最终幻想XIV\game\ffxiv_dx11.exe.")]
    [InlineData(@"Copied from E:\Square Enix\game\ffxiv_dx11.EXE.")]
    [InlineData(@"Copied from D:\John's Games\game\ffxiv_dx11.exe.")]
    [InlineData(@"Copied from \\server\share\FF XIV\game\ffxiv_dx11.exe.")]
    [InlineData("Copied from F:/Games/FFXIV/game/ffxiv_dx11.exe.")]
    public void SanitizerRemovesTheWholeGameInstallPath(string message)
    {
        var sanitized = RotatingFileLogger.Sanitize(message);

        Assert.Equal("Copied from [game-dir].", sanitized);
        Assert.DoesNotContain("ffxiv_dx11", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Logger_WritesNoPayloadAndNoUserName()
    {
        // A folder of its own that must come away whole: a log file the logger left open fails the test.
        using var directory = new TempDirectory("LogTests", strict: true);
        var line = Write(logger => logger.Write(LogLevel.Info, "ipc", "connection_accepted", new Dictionary<string, object?>
        {
            ["count"] = 3,
            ["path"] = @"C:\Users\SomebodyElse\AppData\Local\MentorRecorder\x.db",
        }), directory.Path);

        Assert.Contains("\"event\":\"connection_accepted\"", line, StringComparison.Ordinal);
        Assert.Contains("\"ts\":\"2026-09-04T07:00:00.000Z\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SomebodyElse", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnExceptionIsRecordedByTypeAndMessageOnlyAndItsMessageIsRedactedToo()
    {
        var text = Write(logger => logger.WriteError(
            "ipc",
            "connection_failed",
            new IOException("cannot open " + ProfilePath)));

        var line = JsonNode.Parse(text.Trim())!.AsObject();
        Assert.Equal("IOException", line["error_type"]!.GetValue<string>());
        Assert.DoesNotContain("SomebodyElse", text, StringComparison.OrdinalIgnoreCase);

        // A stack trace names methods, files and, on a developer machine, absolute paths.
        // None of it is in the contract, and none of it is written.
        Assert.Null(line["stack_trace"]);
        Assert.DoesNotContain("   at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLineIsOneJsonObjectWithTheDeclaredEnvelope()
    {
        var text = Write(logger =>
        {
            logger.Write(LogLevel.Info, "capture", "session_started", new Dictionary<string, object?>() { ["queue_capacity"] = 4096 });
            logger.Write(LogLevel.Warn, "capture", "packets_dropped", new Dictionary<string, object?>() { ["dropped"] = 17 });
            logger.Write(LogLevel.Error, "ipc", "accept_failed");
        });

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        foreach (var raw in lines)
        {
            var line = JsonNode.Parse(raw)!.AsObject();
            Assert.NotNull(line["ts"]);
            Assert.NotNull(line["level"]);
            Assert.NotNull(line["component"]);
            Assert.NotNull(line["event"]);
        }

        Assert.Equal("INFO", JsonNode.Parse(lines[0])!["level"]!.GetValue<string>());
        Assert.Equal("WARN", JsonNode.Parse(lines[1])!["level"]!.GetValue<string>());
        Assert.Equal("ERROR", JsonNode.Parse(lines[2])!["level"]!.GetValue<string>());
    }

    [Fact]
    public void TheCountersDiagnosticsActuallyNeedsAreAllowedThrough()
    {
        var text = Write(logger => logger.Write(LogLevel.Info, "capture", "session_closed", new Dictionary<string, object?>()
        {
            ["opcode"] = 61441,
            ["direction"] = "SERVER_TO_CLIENT",
            ["length"] = 8,
            ["content_id"] = 900001,
            ["profile_id"] = "synthetic-v1",
            ["messages_decoded"] = 143119,
            ["duration_ms"] = 1250,
        }));

        var line = JsonNode.Parse(text.Trim())!.AsObject();
        Assert.Equal(61441, line["opcode"]!.GetValue<int>());
        Assert.Equal(8, line["length"]!.GetValue<int>());
        Assert.Equal("synthetic-v1", line["profile_id"]!.GetValue<string>());
        Assert.Equal(143119, line["messages_decoded"]!.GetValue<int>());
    }

    // ------------------------------------------------------------------------ rotation ----

    [Fact]
    public void RotationKeepsAtMostFiveFilesOfAtMostTwoMebibytesEach()
    {
        using var logger = new RotatingFileLogger(_directory.Path, Clock());

        // Roughly 24 MiB of text, which is more than twice what the policy may keep.
        var filler = new string('x', 64 * 1024);
        for (var index = 0; index < 384; index++)
        {
            logger.Write(LogLevel.Info, "soak", "filler", new Dictionary<string, object?>() { ["blob"] = filler });
        }

        var files = Directory.GetFiles(_directory.Path, "collector-*.log");

        Assert.True(
            files.Length <= RotatingFileLogger.MaxFiles,
            $"rotation kept {files.Length} files; the policy is at most {RotatingFileLogger.MaxFiles}");

        foreach (var file in files)
        {
            var length = new FileInfo(file).Length;

            // A file is rotated when it has reached the limit, so the last line written before
            // the check can push it one line past. Allow exactly that and nothing more.
            Assert.True(
                length <= RotatingFileLogger.MaxFileBytes + (128 * 1024),
                $"{Path.GetFileName(file)} is {length} bytes; the per-file limit is " +
                $"{RotatingFileLogger.MaxFileBytes}");
        }

        var total = files.Sum(file => new FileInfo(file).Length);
        Assert.True(
            total <= (RotatingFileLogger.MaxFiles * RotatingFileLogger.MaxFileBytes) + (128 * 1024),
            $"the log directory holds {total} bytes; the policy caps one day at " +
            $"{RotatingFileLogger.MaxFiles} x {RotatingFileLogger.MaxFileBytes}");
    }

    [Fact]
    public void Logger_DeletesDaysThatHaveAgedOut()
    {
        // A folder of its own that must come away whole: a log file the logger left open fails the test.
        using var directory = new TempDirectory("LogTests", strict: true);
        var stale = directory.Combine("collector-20260101.log");
        var recent = directory.Combine("collector-20260903.log");
        File.WriteAllText(stale, "old\n");
        File.WriteAllText(recent, "yesterday\n");

        using var logger = new RotatingFileLogger(directory.Path, Clock());
        logger.Write(LogLevel.Info, "startup", "database_ready");

        Assert.False(File.Exists(stale), "a log from eight months ago should have been pruned");
        Assert.True(File.Exists(recent), "yesterday's log is still within the retention window");
        Assert.True(File.Exists(logger.CurrentPath));
    }

    [Fact]
    public void AFailureToWriteTheLogNeverPropagates()
    {
        // Diagnostics are optional; the data is not. A log directory that cannot be created
        // must cost a log line, not the process.
        var impossible = _directory.Combine("file-not-a-directory");
        File.WriteAllText(impossible, "this is a file, so it can never be a log directory");

        using var logger = new RotatingFileLogger(Path.Combine(impossible, "logs"), Clock());

        var exception = Record.Exception(() =>
        {
            logger.Write(LogLevel.Info, "startup", "database_ready");
            logger.WriteError("ipc", "accept_failed", new IOException("boom"));
        });

        Assert.Null(exception);
    }

    // ------------------------------------------------------ the rest of section 5 ----
    // docs/privacy-boundary.md section 5 forbids addresses, SIDs and hex dumps in the
    // diagnostic log as firmly as it forbids other users' paths.

    /// <summary>
    /// An IPv4 address, a SID and a hex dump are each replaced by their token. A value is looked
    /// for regardless of case: a hex dump in capitals is the same dump.
    /// </summary>
    /// <param name="value">The forbidden value written into a field.</param>
    /// <param name="fragment">A part of it that must not survive either.</param>
    /// <param name="token">What the sanitizer writes instead.</param>
    [Theory]
    [InlineData(Address, Address, "[ip]")]
    [InlineData(Sid, "S-1-5-21-", "[sid]")]
    [InlineData(PayloadHex, PayloadHex, "[hex]")]
    public void AForbiddenValueIsNeverWrittenInClear(string value, string fragment, string token)
    {
        var text = Write(logger => logger.Write(LogLevel.Info, "capture", "value_seen", new Dictionary<string, object?>()
        {
            ["value"] = value,
        }));

        Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fragment, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(token, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIPv6AddressIsNeverWrittenInClearInEitherForm()
    {
        var text = Write(logger => logger.Write(LogLevel.Info, "capture", "adapter_selected", new Dictionary<string, object?>()
        {
            ["address"] = IPv6Address,
            ["link_local"] = IPv6Compressed,
            ["message"] = "no traffic seen from " + IPv6Compressed + " on this adapter",
        }));

        Assert.DoesNotContain(IPv6Address, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(IPv6Compressed, text, StringComparison.OrdinalIgnoreCase);

        // The compressed form appears twice, alone and inside a sentence; both are redacted.
        Assert.DoesNotContain("fe80:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[ip6]", text, StringComparison.Ordinal);

        // The surrounding sentence survives: a redacted line still has to be readable.
        Assert.Contains("on this adapter", text, StringComparison.Ordinal);
    }

    // Sanitisation must cover the component and event names and every value that only becomes
    // text when rendered, not string fields alone (review finding L2).

    [Fact]
    public void TheComponentAndEventNamesAreSanitisedLikeEveryOtherString()
    {
        var text = Write(logger => logger.Write(LogLevel.Warn, "ipc 10.1.2.3", "peer S-1-5-21-1-2-3-1013"));

        Assert.DoesNotContain("10.1.2.3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-1-2-3-1013", text, StringComparison.Ordinal);
        Assert.Contains("[ip]", text, StringComparison.Ordinal);
        Assert.Contains("[sid]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFieldThatIsNotAStringUntilItIsRenderedIsSanitisedToo()
    {
        // Testing `value is string` is not enough: anything that only becomes text when the
        // line is rendered -- a DirectoryInfo, an IPAddress, any object with a revealing
        // ToString -- must be sanitised as well.
        var text = Write(logger => logger.Write(LogLevel.Warn, "capture", "adapter", new Dictionary<string, object?>
        {
            ["folder"] = new DirectoryInfo(@"C:\Users\someone\AppData"),
            ["peer"] = System.Net.IPAddress.Parse("fe80::1"),
        }));

        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fe80::1", text, StringComparison.Ordinal);
        Assert.Contains("[ip6]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShortHashPrefixesUsedAsIdentifiersSurviveTheRedaction()
    {
        // The adapter fingerprint is a twelve-character SHA-256 prefix, which is why the hex
        // rule starts at sixteen rather than eight; a UUID's groups are shorter still.
        // Redacting either would leave a log that cannot tell two adapters or two sessions
        // apart, and would hide nothing in exchange.
        var fingerprint = SanitizedDiagnosticsReport.Fingerprint(@"\Device\NPF_{2C1E4A55}")!;
        const string sessionId = "8f14e45f-ceea-467a-9c4e-1c1d0f8b2a37";

        var text = Write(logger => logger.Write(LogLevel.Info, "capture", "session_started", new Dictionary<string, object?>()
        {
            ["adapter_fingerprint"] = fingerprint,
            ["capture_session_id"] = sessionId,
            ["profile_id"] = "synthetic-v1",
        }));

        Assert.Equal(SanitizedDiagnosticsReport.AdapterFingerprintLength, fingerprint.Length);
        Assert.Contains(fingerprint, text, StringComparison.Ordinal);
        Assert.Contains(sessionId, text, StringComparison.Ordinal);
        Assert.Contains("synthetic-v1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[hex]", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------ the CN clear signal ----
    // Duty-result brief, decision 7: a line may say that the signal was seen, for which run, and how
    // long before the exit. No payload, no hash, no opcode, no duty number.

    [Fact]
    public void TheClearLogLinesCarryOnlyTheirFields()
    {
        const string runId = "8f14e45f-ceea-467a-9c4e-1c1d0f8b2a37";
        var signal = new DutyClearNote(DutyClearNoteKind.Signal, runId, Domain.RunState.EnteredDuty, true, null, 3, 1);
        var exit = new DutyClearNote(DutyClearNoteKind.Exit, runId, Domain.RunState.Idle, true, 13_723, 3, 1);

        var text = Write(logger =>
        {
            logger.Write(LogLevel.Info, "protocol", signal.LogEvent, signal.LogFields());
            logger.Write(LogLevel.Info, "protocol", exit.LogEvent, exit.LogFields());
        });

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(raw => JsonNode.Parse(raw)!.AsObject())
            .ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            new[] { "completed", "completions", "component", "event", "level", "run_id", "signals", "state_before", "ts" },
            lines[0].Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("protocol", lines[0]["component"]!.GetValue<string>());
        Assert.Equal("duty_clear_signal", lines[0]["event"]!.GetValue<string>());
        Assert.Equal(runId, lines[0]["run_id"]!.GetValue<string>());
        Assert.Equal("ENTERED_DUTY", lines[0]["state_before"]!.GetValue<string>());
        Assert.True(lines[0]["completed"]!.GetValue<bool>());
        Assert.Equal(3, lines[0]["signals"]!.GetValue<long>());
        Assert.Equal(1, lines[0]["completions"]!.GetValue<long>());

        Assert.Equal(
            new[] { "clear_to_exit_ms", "component", "event", "level", "run_id", "ts" },
            lines[1].Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("duty_clear_exit", lines[1]["event"]!.GetValue<string>());
        Assert.Equal(runId, lines[1]["run_id"]!.GetValue<string>());
        Assert.Equal(13_723, lines[1]["clear_to_exit_ms"]!.GetValue<long>());

        // The sanitiser leaves the run id alone: it has nothing to hide, and a log that cannot name the
        // run is no use.
        Assert.DoesNotContain("[hex]", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The host writes the pipeline's notes to the diagnostic log as they happen. Fed a clear on an opcode
    /// no profile declares: the line names neither that opcode nor the duty's number, and carries no bytes.
    /// </summary>
    [Fact]
    public void TheHostWritesTheClearToTheDiagnosticLog()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero));
        using var logger = new RotatingFileLogger(_directory.Combine("logs"), clock);
        string logPath;
        using (var host = OpenSyntheticHost(clock, logger))
        {
            var pipeline = host.LiveProtocol!;
            const string session = "30000000-0000-4000-8000-000000000031";
            pipeline.OnCaptureStarted(session);
            pipeline.Accept(new Protocol.Decoded.DecodedMessage(
                session, Protocol.Decoded.MessageDirection.Inbound, clock.UtcNow, TimeSpan.FromSeconds(1), 1000,
                3, 61455, DirectorBodies.Body(DirectorBodies.ClearCommand), "zone"));
            logPath = logger.CurrentPath;
        }

        var line = Assert.Single(
            File.ReadAllLines(logPath, new UTF8Encoding(false)),
            raw => raw.Contains("\"duty_clear_signal\"", StringComparison.Ordinal));
        var fields = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("protocol", fields["component"]!.GetValue<string>());
        Assert.Null(fields["run_id"]);
        Assert.Equal("IDLE", fields["state_before"]!.GetValue<string>());
        Assert.False(fields["completed"]!.GetValue<bool>());
        Assert.Equal(1, fields["signals"]!.GetValue<long>());
        Assert.Equal(0, fields["completions"]!.GetValue<long>());
        Assert.DoesNotContain("61455", line, StringComparison.Ordinal);
        Assert.DoesNotContain("f00d", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("61453", line, StringComparison.Ordinal);
        Assert.DoesNotContain("6d0000", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Audit 2026-11-03 (C-7 / X3): a confirmed calibration that could not be written or selected is told to the player
    /// in a catalogue sentence without the system's words, and the host writes those words to the local log instead -
    /// redacted like every line, so the profile's path under another Windows user's folder never reaches the file.
    /// </summary>
    [Fact]
    public void TheHostWritesACalibrationFaultToTheDiagnosticLogWithItsPathRedacted()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 3, 3, 0, 0, TimeSpan.Zero));
        using var logger = new RotatingFileLogger(_directory.Combine("logs"), clock);
        string logPath;
        using (var host = OpenSyntheticHost(clock, logger))
        {
            // The event is raised only by the pipeline itself; its backing delegate is what a raise calls.
            var raise = (Action<string, string?, Exception?>?)typeof(LiveProtocolPipeline)
                .GetField("CalibrationFault", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(host.LiveProtocol!);
            Assert.NotNull(raise);
            raise!("calibration_write_failed", null, new IOException("The process cannot access the file '" + ProfilePath + "'."));
            raise("calibration_written_not_selected", "no profile matches this build", null);
            logPath = logger.CurrentPath;
        }

        var lines = File.ReadAllLines(logPath, new UTF8Encoding(false));
        var failed = JsonNode.Parse(Assert.Single(lines, raw => raw.Contains("\"calibration_write_failed\"", StringComparison.Ordinal)))!;
        Assert.Equal("calibration", failed["component"]!.GetValue<string>());
        Assert.Equal("ERROR", failed["level"]!.GetValue<string>());
        Assert.Equal(nameof(IOException), failed["error_type"]!.GetValue<string>());
        Assert.Contains("%USERPROFILE%", failed["error_message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("SomebodyElse", string.Join("\n", lines), StringComparison.OrdinalIgnoreCase);
        var notSelected = JsonNode.Parse(Assert.Single(lines, raw => raw.Contains("\"calibration_written_not_selected\"", StringComparison.Ordinal)))!;
        Assert.Equal("no profile matches this build", notSelected["reason"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------- sharing ----
    // Two writers, one log file. --capture-trace, a departing process and the serving instance
    // can all hold the same day's file open, so the append must permit shared writes: a line the
    // loser cannot append is dropped into a catch block, and a log that silently loses lines is
    // worse than no log because it is trusted (review finding M7). Loss that still occurs is
    // counted, and the count is reachable from GetStatus.

    [Fact]
    public async Task TwoLoggersOnTheSameFileBothGetTheirLinesIn()
    {
        const int perLogger = 60;
        var clock = Clock();
        using var first = new RotatingFileLogger(_directory.Path, clock);
        using var second = new RotatingFileLogger(_directory.Path, clock);

        using var start = new Barrier(2);
        var writers = new[] { first, second }.Select(logger => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perLogger; i++)
            {
                logger.Write(LogLevel.Info, "ipc", "probe");
            }
        })).ToArray();

        await Task.WhenAll(writers);

        var lines = File.ReadAllLines(first.CurrentPath);
        Assert.Equal(0, first.SuppressedWrites + second.SuppressedWrites);
        Assert.Equal(2 * perLogger, lines.Length);
    }

    [Fact]
    public void AWriteThatCannotBeMadeIsCountedRatherThanForgotten()
    {
        using var logger = new RotatingFileLogger(_directory.Path, Clock());
        logger.Write(LogLevel.Info, "ipc", "probe");

        // A directory in place of the file makes every append fail permanently, which is the
        // case the counter has to survive.
        var path = logger.CurrentPath;
        File.Delete(path);
        Directory.CreateDirectory(path);

        logger.Write(LogLevel.Warn, "ipc", "probe");

        Assert.True(logger.SuppressedWrites > 0);
    }

    private static TestClock Clock() => new(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero));

    /// <summary>Writes with a fresh logger and returns the file text.</summary>
    /// <param name="write">What the logger writes.</param>
    /// <param name="directory">The log folder; this test class's folder when null.</param>
    private string Write(Action<RotatingFileLogger> write, string? directory = null)
    {
        using var logger = new RotatingFileLogger(directory ?? _directory.Path, Clock());
        write(logger);
        return File.ReadAllText(logger.CurrentPath, new UTF8Encoding(false));
    }

    /// <summary>A host on a database in this test's directory, reading with the synthetic CN-shape profile.</summary>
    private CollectorHost OpenSyntheticHost(TestClock clock, RotatingFileLogger logger)
    {
        var selection = Protocol.Profiles.ProfileSelector.SelectExplicit(
            Path.Combine(AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-cn-shape-v1.json"),
            allowSynthetic: true);
        return CollectorHost.Open(_directory.Combine("host.db"), clock, new CaptureServices(), logger, _ => selection);
    }

    public void Dispose() => _directory.Dispose();
}
