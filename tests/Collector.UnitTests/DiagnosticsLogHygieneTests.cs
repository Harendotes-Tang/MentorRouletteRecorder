using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;

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
/// The sanitizer redacts user profile paths, IPv4 and IPv6 literals, Windows SIDs and long
/// hexadecimal runs. The short SHA-256 prefixes used as identifiers must survive it, or the
/// redaction would cost diagnostic value without buying any privacy.
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

    private readonly string _directory;

    public DiagnosticsLogHygieneTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.LogHygiene", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

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
        var clock = new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero));
        using var logger = new RotatingFileLogger(_directory, clock);

        // Roughly 24 MiB of text, which is more than twice what the policy may keep.
        var filler = new string('x', 64 * 1024);
        for (var index = 0; index < 384; index++)
        {
            logger.Write(LogLevel.Info, "soak", "filler", new Dictionary<string, object?>() { ["blob"] = filler });
        }

        var files = Directory.GetFiles(_directory, "collector-*.log");

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
    public void AFailureToWriteTheLogNeverPropagates()
    {
        // Diagnostics are optional; the data is not. A log directory that cannot be created
        // must cost a log line, not the process.
        var impossible = Path.Combine(_directory, "file-not-a-directory");
        File.WriteAllText(impossible, "this is a file, so it can never be a log directory");

        using var logger = new RotatingFileLogger(
            Path.Combine(impossible, "logs"),
            new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));

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

    [Fact]
    public void AnIPv4AddressIsNeverWrittenInClear()
    {
        var text = Write(logger => logger.Write(LogLevel.Info, "capture", "adapter_selected", new Dictionary<string, object?>()
        {
            ["address"] = Address,
        }));

        Assert.DoesNotContain(Address, text, StringComparison.Ordinal);
        Assert.Contains("[ip]", text, StringComparison.Ordinal);
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

    [Fact]
    public void AUserSidIsNeverWrittenInClear()
    {
        var text = Write(logger => logger.Write(LogLevel.Info, "ipc", "pipe_created", new Dictionary<string, object?>()
        {
            ["owner"] = Sid,
        }));

        Assert.DoesNotContain(Sid, text, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-", text, StringComparison.Ordinal);
        Assert.Contains("[sid]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHexPayloadDumpIsNeverWrittenInClear()
    {
        var text = Write(logger => logger.Write(LogLevel.Warn, "capture", "decode_error", new Dictionary<string, object?>()
        {
            ["payload"] = PayloadHex,
        }));

        Assert.DoesNotContain(PayloadHex, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[hex]", text, StringComparison.Ordinal);
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

    /// <summary>Writes with a fresh logger in this test's directory and returns the file text.</summary>
    private string Write(Action<RotatingFileLogger> write)
    {
        var clock = new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero));
        using var logger = new RotatingFileLogger(_directory, clock);
        write(logger);
        return File.ReadAllText(logger.CurrentPath, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // The temp cleaner will get it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
