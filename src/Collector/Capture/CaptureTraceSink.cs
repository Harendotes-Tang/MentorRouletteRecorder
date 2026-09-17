using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The facts stamped on the first line of a trace, so a file found later can still say which
/// machine, client and build produced it.
///
/// Everything here is either a version string or a digest. There is no adapter GUID, no
/// address, no path and no character name: a trace is evidence about opcodes, and evidence
/// about opcodes needs none of those (docs/privacy-boundary.md §5).
/// </summary>
/// <param name="StartedAtUtc">When the trace started.</param>
/// <param name="NpcapVersion">Npcap version string, when it could be read.</param>
/// <param name="GameBuild">Client build read from <c>ffxivgame.ver</c>.</param>
/// <param name="Region">Region guessed from the install path.</param>
/// <param name="AdapterFingerprint">Digest of the adapter id, never the id itself.</param>
/// <param name="CollectorVersion">Version of the binary that produced the trace.</param>
/// <param name="Oodle">How the decompressor was obtained (<c>DEC-OODLE-01</c>).</param>
/// <param name="Synthetic">True when the messages came from a fake source rather than a live capture.</param>
public sealed record CaptureTraceHeader(
    DateTimeOffset StartedAtUtc,
    string? NpcapVersion,
    string? GameBuild,
    Region Region,
    string? AdapterFingerprint,
    string CollectorVersion,
    OodleMode Oodle,
    bool Synthetic,
    int PreexistingConnections = 0);

/// <summary>
/// Writes a sanitized, opcode-level trace of a live capture as JSON Lines.
///
/// This is the diagnostics-mode artefact described in docs/privacy-boundary.md §5: it is
/// user-enabled, bounded and short-lived, and it records only what an opcode-level
/// investigation needs. Per message that is: a sequence number, two timestamps, the
/// direction, the segment type, the opcode, the payload <em>length</em>, and a twelve
/// character prefix of the payload's SHA-256.
///
/// What it deliberately never writes:
///
/// <list type="bullet">
///   <item><description>payload bytes, in any encoding -- the digest prefix lets two
///   observations be compared without carrying the bytes that produced them. It is a local
///   correlation identifier, not encryption or an anonymization guarantee;</description></item>
///   <item><description>addresses, of either endpoint -- not even the already-hashed
///   connection key, because a trace does not need to tell connections apart to identify an
///   opcode;</description></item>
///   <item><description>character names, chat, or any other player's data -- none of it is
///   read in the first place;</description></item>
///   <item><description>arbitrary user text -- only the five documented canonical marker
///   tokens are accepted (<see cref="CaptureTraceMarkerText.Sanitize"/>).</description></item>
/// </list>
///
/// Boundedness is enforced here rather than trusted: at most <see cref="MaxLines"/> message
/// lines and <see cref="MaxMarkers"/> marker lines are written, after which the trace keeps
/// <em>counting</em> but stops <em>writing</em>, and the summary line says so. A capture left
/// running overnight therefore produces a file of known maximum size instead of filling the
/// disk.
/// </summary>
public sealed class CaptureTraceSink : IDecodedMessageSink
{
    /// <summary>Schema version of the trace format, on the header line.</summary>
    public const int TraceVersion = 1;

    /// <summary>Default cap on message lines.</summary>
    public const int DefaultMaxLines = 200_000;

    /// <summary>Smallest cap the command line accepts.</summary>
    public const int MinMaxLines = 1;

    /// <summary>Largest cap the command line accepts.</summary>
    public const int MaxMaxLines = 10_000_000;

    /// <summary>Cap on marker lines. A person types a handful; a piped file must not be unbounded.</summary>
    public const int MaxMarkers = 10_000;

    /// <summary>Hex characters of the payload digest kept per message.</summary>
    public const int HashPrefixLength = 12;

    /// <summary>Distinct (direction, opcode) pairs tracked for the summary histogram.</summary>
    public const int MaxTrackedOpcodes = 4096;

    /// <summary>Opcode rows carried on the summary line.</summary>
    public const int TopOpcodeCount = 40;

    /// <summary>Direction token for a server-to-client message.</summary>
    public const string InboundToken = "S2C";

    /// <summary>Direction token for a client-to-server message.</summary>
    public const string OutboundToken = "C2S";

    private readonly TextWriter _writer;
    private readonly IClock _clock;
    private readonly Func<TimeSpan> _elapsed;
    private readonly object _gate = new();
    private readonly Dictionary<(string Direction, ushort Opcode), OpcodeTally> _tallies = new();

    private long _seq;
    private readonly Dictionary<string, ConnectionTally> _connections = new(StringComparer.Ordinal);
    private long _untrackedConnections;
    private long _written;
    private long _markers;
    private long _decodeErrors;
    private long _untracked;
    private bool _headerWritten;
    private bool _summaryWritten;

    /// <summary>Creates a sink writing to <paramref name="writer"/>.</summary>
    /// <param name="writer">Destination; the caller owns and disposes it.</param>
    /// <param name="clock">Wall clock for <c>at_utc</c>; the system clock when null.</param>
    /// <param name="elapsed">
    /// Monotonic reading since the trace started, used for <c>t_ms</c>. A stopwatch started
    /// here when null; tests pass a deterministic function instead.
    /// </param>
    /// <param name="maxLines">Cap on message lines, clamped into the documented range.</param>
    public CaptureTraceSink(
        TextWriter writer,
        IClock? clock = null,
        Func<TimeSpan>? elapsed = null,
        int maxLines = DefaultMaxLines)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
        _clock = clock ?? SystemClock.Instance;
        MaxLines = Math.Clamp(maxLines, MinMaxLines, MaxMaxLines);

        if (elapsed is not null)
        {
            _elapsed = elapsed;
        }
        else
        {
            var stopwatch = Stopwatch.StartNew();
            _elapsed = () => stopwatch.Elapsed;
        }
    }

    /// <summary>Cap on message lines actually in force.</summary>
    public int MaxLines { get; }

    /// <summary>Messages observed, including those the line cap stopped us from writing.</summary>
    public long MessageCount => Interlocked.Read(ref _seq);

    /// <summary>Message lines actually written.</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>Marker lines written.</summary>
    public long MarkerCount => Interlocked.Read(ref _markers);

    /// <summary>Framing or upstream bundle-decompression failures counted by the capture source.</summary>
    public long DecodeErrorCount => Interlocked.Read(ref _decodeErrors);

    /// <summary>True when a cap stopped a line from being written.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Writes the header line. Must be called once, before anything else.</summary>
    /// <param name="header">Facts about the machine, client and build.</param>
    public void WriteHeader(CaptureTraceHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);

        lock (_gate)
        {
            if (_headerWritten)
            {
                return;
            }

            _headerWritten = true;
            WriteLine(new JsonObject
            {
                ["trace_version"] = TraceVersion,
                ["synthetic"] = header.Synthetic,
                ["started_at_utc"] = UtcTimestamp.ToText(header.StartedAtUtc),
                ["npcap_version"] = header.NpcapVersion,
                ["game_build"] = header.GameBuild,
                ["region"] = EnumWire<Region>.Format(header.Region),
                ["adapter_fingerprint"] = header.AdapterFingerprint,
                ["collector_version"] = header.CollectorVersion,
                ["oodle_mode"] = header.Oodle.ToString(),
                // > 0 means the trace attached to a session that already had connections
                // open; their Oodle state is incomplete and their lines are not evidence.
                ["midstream"] = header.PreexistingConnections > 0,
                ["preexisting_connections"] = header.PreexistingConnections,

                // A trace is raw material for a profile, never a profile. The status the whole
                // repository is gated on is repeated on the line itself so a file that outlives
                // its context cannot be mistaken for a verified result.
                ["live_capture_status"] = CaptureDiagnosticsSnapshot.LiveCaptureStatus,
            });
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Called from the queue's single parser thread. Markers arrive from another thread, so
    /// the writer is guarded; the lock is held only for the duration of one formatted line.
    /// </remarks>
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var direction = message.Direction == MessageDirection.Inbound ? InboundToken : OutboundToken;
        var length = message.Payload.Length;
        var digest = HashPrefix(message.Payload.Span);

        var connection = ConnectionTag(message.ConnectionKey);
        var tMs = Milliseconds(message.Mono);

        lock (_gate)
        {
            var seq = ++_seq;
            Tally(direction, message.Opcode, length);
            TallyConnection(connection, tMs, message.Opcode);

            if (_written >= MaxLines || _summaryWritten)
            {
                Truncated = true;
                return;
            }

            WriteLine(new JsonObject
            {
                ["seq"] = seq,
                // The queue may be briefly backlogged. Use the timestamp captured with the
                // message rather than the later file-write time, otherwise an opcode can be
                // shifted into or out of the user's marker window under load.
                ["t_ms"] = tMs,
                // Local clock since 0.2.3 (review finding H-8); traces recorded before that
                // carry the server bundle epoch here instead. The epoch itself is written
                // alongside so a report can still line the two generations of trace up.
                ["at_utc"] = UtcTimestamp.ToText(message.ObservedAtUtc),
                ["epoch"] = message.Epoch,
                ["conn"] = connection,
                ["dir"] = direction,
                ["seg"] = message.SegmentType,
                ["op"] = FormatOpcode(message.Opcode),
                ["len"] = length,
                ["h12"] = digest,
            });
            _written++;
        }
    }

    /// <summary>
    /// Writes one user marker, so the analysis step can line an in-game moment up with the
    /// traffic around it.
    /// </summary>
    /// <param name="text">Raw text as typed; written only when it names an allowed marker.</param>
    /// <returns>True when a line was written.</returns>
    public bool WriteMarker(string? text) => WriteMarker(text, _clock.UtcNow, _elapsed());

    /// <summary>写入已接收的标记；异步调用方必须传入接收时刻，不能用磁盘恢复后的写入时刻。</summary>
    /// <param name="text">规范标记；不在白名单时返回 false。</param>
    /// <param name="receivedAtUtc">标记被接受时的 UTC 时间。</param>
    /// <param name="receivedElapsed">接受时距会话起点的单调时间，负值按零处理。</param>
    /// <returns>实际写入一行时为 true；达到上限或已总结时为 false。</returns>
    public bool WriteMarker(string? text, DateTimeOffset receivedAtUtc, TimeSpan receivedElapsed)
    {
        var sanitized = CaptureTraceMarkerText.Sanitize(text);
        if (sanitized is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_markers >= MaxMarkers || _summaryWritten)
            {
                Truncated = true;
                return false;
            }

            WriteLine(new JsonObject
            {
                ["marker"] = sanitized,
                ["t_ms"] = Milliseconds(receivedElapsed),
                ["at_utc"] = UtcTimestamp.ToText(receivedAtUtc),
            });
            _markers++;
            return true;
        }
    }

    /// <summary>Counts one framing or bundle-decompression failure reported by the capture source.</summary>
    public void CountDecodeError() => Interlocked.Increment(ref _decodeErrors);

    /// <summary>Writes the summary line and flushes. Must be called last; idempotent.</summary>
    /// <param name="dropped">Messages the bounded queue discarded.</param>
    public void WriteSummary(long dropped)
    {
        lock (_gate)
        {
            if (_summaryWritten)
            {
                return;
            }

            var summary = new JsonObject
            {
                ["summary"] = true,
                ["messages"] = _seq,
                ["decode_errors"] = Interlocked.Read(ref _decodeErrors),
                ["dropped"] = dropped,
                ["duration_ms"] = Milliseconds(),
                ["written"] = _written,
                ["markers"] = _markers,
                ["truncated"] = Truncated,
                ["max_lines"] = MaxLines,
                ["untracked_opcodes"] = _untracked,
                ["top_opcodes"] = TopOpcodes(),
                ["connections"] = Connections(),
                ["untracked_connections"] = _untrackedConnections,
            };

            _summaryWritten = true;
            WriteLine(summary);
            _writer.Flush();
        }
    }

    /// <summary>Renders an opcode the way every line and every report spells it.</summary>
    /// <param name="opcode">Opcode value.</param>
    public static string FormatOpcode(ushort opcode) =>
        "0x" + opcode.ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>The twelve hex characters of SHA-256 that stand in for a payload.</summary>
    /// <param name="payload">Payload bytes; they are hashed and immediately forgotten.</param>
    public static string HashPrefix(ReadOnlySpan<byte> payload)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, digest);
        return Convert.ToHexString(digest).ToLowerInvariant()[..HashPrefixLength];
    }

    private JsonArray TopOpcodes()
    {
        var rows = new JsonArray();
        foreach (var entry in _tallies
                     .OrderByDescending(pair => pair.Value.Count)
                     .ThenBy(pair => pair.Key.Direction, StringComparer.Ordinal)
                     .ThenBy(pair => pair.Key.Opcode)
                     .Take(TopOpcodeCount))
        {
            rows.Add(new JsonObject
            {
                ["dir"] = entry.Key.Direction,
                ["op"] = FormatOpcode(entry.Key.Opcode),
                ["count"] = entry.Value.Count,
                ["len_min"] = entry.Value.MinLength,
                ["len_max"] = entry.Value.MaxLength,
            });
        }

        return rows;
    }

    /// <summary>Distinct connections the summary keeps apart; the rest are only counted.</summary>
    public const int MaxTrackedConnections = 64;

    /// <summary>Characters of the (already hashed) connection key that identify a connection in the file.</summary>
    public const int ConnectionTagLength = 8;

    /// <summary>
    /// The tag written per line. The connection key coming out of the capture layer is a
    /// hash prefix of the TCP tuple, never an address; the trace shortens it further.
    /// </summary>
    /// <param name="connectionKey">Opaque key from <see cref="DecodedMessage.ConnectionKey"/>.</param>
    public static string ConnectionTag(string? connectionKey)
    {
        if (string.IsNullOrEmpty(connectionKey))
        {
            return "-";
        }

        return connectionKey.Length <= ConnectionTagLength
            ? connectionKey
            : connectionKey[..ConnectionTagLength];
    }

    private void TallyConnection(string connection, long tMs, ushort opcode)
    {
        if (_connections.TryGetValue(connection, out var tally))
        {
            _connections[connection] = tally.With(tMs, opcode);
            return;
        }

        if (_connections.Count >= MaxTrackedConnections)
        {
            _untrackedConnections++;
            return;
        }

        _connections[connection] = ConnectionTally.First(tMs, opcode);
    }

    private JsonArray Connections()
    {
        var array = new JsonArray();
        foreach (var (connection, tally) in _connections.OrderBy(pair => pair.Value.FirstMs))
        {
            array.Add(new JsonObject
            {
                ["conn"] = connection,
                ["messages"] = tally.Messages,
                ["first_t_ms"] = tally.FirstMs,
                ["last_t_ms"] = tally.LastMs,
                ["distinct_opcodes"] = tally.Opcodes.Count,
            });
        }

        return array;
    }

    private readonly record struct ConnectionTally(
        long Messages, long FirstMs, long LastMs, HashSet<ushort> Opcodes)
    {
        public static ConnectionTally First(long tMs, ushort opcode) =>
            new(1, tMs, tMs, new HashSet<ushort> { opcode });

        public ConnectionTally With(long tMs, ushort opcode)
        {
            Opcodes.Add(opcode);
            return this with { Messages = Messages + 1, LastMs = Math.Max(LastMs, tMs) };
        }
    }

    private void Tally(string direction, ushort opcode, int length)
    {
        var key = (direction, opcode);
        if (_tallies.TryGetValue(key, out var tally))
        {
            _tallies[key] = tally.With(length);
            return;
        }

        if (_tallies.Count >= MaxTrackedOpcodes)
        {
            _untracked++;
            return;
        }

        _tallies[key] = new OpcodeTally(1, length, length);
    }

    private long Milliseconds() => Milliseconds(_elapsed());

    private static long Milliseconds(TimeSpan elapsed) =>
        Math.Max(0, (long)elapsed.TotalMilliseconds);

    /// <summary>
    /// Writes one JSON object as one line. The newline is written explicitly rather than
    /// through <c>WriteLine</c> so the file is LF-terminated JSON Lines on every platform and
    /// whatever the caller's writer was configured with.
    /// </summary>
    /// <param name="node">Object to serialise.</param>
    private void WriteLine(JsonObject node)
    {
        _writer.Write(node.ToJsonString(Ipc.Wire.JsonOptions));
        _writer.Write('\n');
    }

    private readonly record struct OpcodeTally(long Count, int MinLength, int MaxLength)
    {
        public OpcodeTally With(int length) =>
            new(Count + 1, Math.Min(MinLength, length), Math.Max(MaxLength, length));
    }
}

/// <summary>
/// Reduces user input to one of the five canonical event markers. Rejecting every other
/// string makes it impossible for a pasted address, path, packet fragment or character name
/// to enter the trace through the marker prompt.
/// </summary>
public static class CaptureTraceMarkerText
{
    /// <summary>
    /// The complete set of markers this build accepts, in prompt order.
    ///
    /// The single source of the allowlist: the validation controller, the contract schema and
    /// the Desktop all derive it from here, so a new marker cannot end up silently unreachable,
    /// or accepted over IPC and then dropped on the way to disk (review finding M-18). A
    /// contract test asserts the schema enum still matches.
    /// </summary>
    public static readonly IReadOnlyList<string> Allowed = new[]
    {
        "queued", "pop", "entered", "victory", "left",
    };

    /// <summary>
    /// Returns the canonical lowercase marker, or null when the input is not on the allowlist.
    /// </summary>
    /// <param name="text">Raw text as typed.</param>
    public static string? Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var canonical = text.Trim().ToLowerInvariant();
        foreach (var allowed in Allowed)
        {
            if (string.Equals(canonical, allowed, StringComparison.Ordinal))
            {
                return allowed;
            }
        }

        return null;
    }
}

/// <summary>
/// The SHA-256 sidecar that makes a trace citable.
///
/// docs/protocol-profile-format.md §5 refuses a constant without evidence, and evidence that
/// cannot be shown to be the same file twice is not evidence. Every trace therefore ends with
/// a <c>&lt;name&gt;.sha256</c> next to it, in the same shape the profile validator's stamps
/// use, so a candidate opcode can be tied to the exact bytes it came from.
/// </summary>
public static class CaptureTraceDigest
{
    /// <summary>Extension appended to the trace path.</summary>
    public const string SidecarExtension = ".sha256";

    /// <summary>Hashes a file and writes its sidecar next to it.</summary>
    /// <param name="path">Trace file that has already been closed.</param>
    /// <returns>The lowercase hexadecimal digest that was written.</returns>
    public static string WriteSidecar(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var digest = Compute(path);
        var name = Path.GetFileName(path);
        File.WriteAllText(
            path + SidecarExtension,
            string.Create(CultureInfo.InvariantCulture, $"{digest}  {name}\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return digest;
    }

    /// <summary>Computes the SHA-256 of a file as lowercase hexadecimal.</summary>
    /// <param name="path">File to hash.</param>
    public static string Compute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
