using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Capture;

/// <summary>One decoded-message line read back from a trace.</summary>
/// <param name="Seq">Sequence number within the trace.</param>
/// <param name="TimeMs">Milliseconds since the trace started.</param>
/// <param name="Direction">Direction token, <c>S2C</c> or <c>C2S</c>.</param>
/// <param name="Opcode">Opcode as written, <c>0xNNNN</c>.</param>
/// <param name="Length">Payload length in bytes.</param>
/// <param name="Hash">Twelve hex characters of the payload digest.</param>
public sealed record TraceMessageRow(
    long Seq, long TimeMs, string Direction, string Opcode, int Length, string Hash);

/// <summary>One marker line read back from a trace.</summary>
/// <param name="Name">Sanitized marker text.</param>
/// <param name="TimeMs">Milliseconds since the trace started.</param>
public sealed record TraceMarkerRow(string Name, long TimeMs);

/// <summary>One row of the opcode histogram.</summary>
/// <param name="Direction">Direction token.</param>
/// <param name="Opcode">Opcode as written.</param>
/// <param name="Count">Messages seen.</param>
/// <param name="MinLength">Shortest payload.</param>
/// <param name="MaxLength">Longest payload.</param>
/// <param name="ModeLength">Most common payload length.</param>
public sealed record TraceOpcodeRow(
    string Direction, string Opcode, long Count, int MinLength, int MaxLength, int ModeLength);

/// <summary>
/// One opcode that appeared near every occurrence of one marker.
///
/// A candidate is a <em>question</em>, never an answer: it says "this opcode showed up each
/// time you typed <c>pop</c>, and rarely otherwise". Turning it into a protocol constant
/// requires the evidence procedure in docs/protocol-profile-format.md §5, which this table
/// cannot and does not perform.
/// </summary>
/// <param name="Marker">Marker name the candidate belongs to.</param>
/// <param name="Direction">Direction token.</param>
/// <param name="Opcode">Opcode as written.</param>
/// <param name="MarkerHits">Marker occurrences whose window contained this opcode.</param>
/// <param name="MarkerCount">Marker occurrences in total.</param>
/// <param name="InsideCount">Messages of this opcode inside any of those windows.</param>
/// <param name="TotalCount">Messages of this opcode in the whole trace.</param>
/// <param name="Ratio">Inside over total; 1.0 means it never appeared anywhere else.</param>
public sealed record TraceCandidateRow(
    string Marker,
    string Direction,
    string Opcode,
    int MarkerHits,
    int MarkerCount,
    long InsideCount,
    long TotalCount,
    double Ratio);

/// <summary>
/// A trace file, parsed back into rows, plus the two questions the live-validation guide asks
/// of it: what appeared, and what appeared next to a marker.
///
/// Reading is deliberately forgiving: a trace can be cut short by a power failure or by the
/// process being killed, and a truncated last line must not cost the analyst the whole
/// session. Unreadable lines are counted, not thrown.
/// </summary>
public sealed class CaptureTraceAnalysis
{
    /// <summary>Largest trace the in-memory report path will open.</summary>
    public const long MaxInputBytes = 64L * 1024 * 1024;

    /// <summary>Largest message collection materialized by the report path.</summary>
    public const int MaxMessageRows = 250_000;

    private readonly List<TraceMessageRow> _messages;
    private readonly List<TraceMarkerRow> _markers;

    private CaptureTraceAnalysis(
        string path,
        JsonObject? header,
        JsonObject? summary,
        List<TraceMessageRow> messages,
        List<TraceMarkerRow> markers,
        int unreadableLines)
    {
        Path = path;
        Header = header;
        Summary = summary;
        _messages = messages;
        _markers = markers;
        UnreadableLines = unreadableLines;
    }

    /// <summary>File the rows were read from.</summary>
    public string Path { get; }

    /// <summary>Header line, when the file had one.</summary>
    public JsonObject? Header { get; }

    /// <summary>Summary line, when the trace finished cleanly.</summary>
    public JsonObject? Summary { get; }

    /// <summary>Lines that could not be parsed; a non-zero count means the file was damaged.</summary>
    public int UnreadableLines { get; }

    /// <summary>Message rows, in file order.</summary>
    public IReadOnlyList<TraceMessageRow> Messages => _messages;

    /// <summary>Marker rows, in file order.</summary>
    public IReadOnlyList<TraceMarkerRow> Markers => _markers;

    /// <summary>Reads a trace file.</summary>
    /// <param name="path">JSON Lines file written by <c>--capture-trace</c>.</param>
    public static CaptureTraceAnalysis Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (file.Length > MaxInputBytes)
        {
            throw new InvalidDataException(string.Create(
                CultureInfo.InvariantCulture,
                $"trace is larger than the {MaxInputBytes} byte report limit"));
        }

        JsonObject? header = null;
        JsonObject? summary = null;
        var messages = new List<TraceMessageRow>();
        var markers = new List<TraceMarkerRow>();
        var unreadable = 0;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? node;
            try
            {
                node = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                node = null;
            }

            if (node is null)
            {
                unreadable++;
                continue;
            }

            Classify(node, ref header, ref summary, messages, markers, ref unreadable);
        }

        return new CaptureTraceAnalysis(path, header, summary, messages, markers, unreadable);
    }

    /// <summary>Counts per direction and opcode, with the length range and the commonest length.</summary>
    public IReadOnlyList<TraceOpcodeRow> Histogram()
    {
        var rows = new List<TraceOpcodeRow>();
        foreach (var group in _messages.GroupBy(message => (message.Direction, message.Opcode)))
        {
            var lengths = group.GroupBy(message => message.Length)
                .OrderByDescending(bucket => bucket.Count())
                .ThenBy(bucket => bucket.Key)
                .First();
            rows.Add(new TraceOpcodeRow(
                group.Key.Direction,
                group.Key.Opcode,
                group.LongCount(),
                group.Min(message => message.Length),
                group.Max(message => message.Length),
                lengths.Key));
        }

        return rows
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Direction, StringComparer.Ordinal)
            .ThenBy(row => row.Opcode, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Messages within a window of one marker occurrence, in time order.</summary>
    /// <param name="marker">Marker occurrence to look around.</param>
    /// <param name="windowMs">Half-width of the window in milliseconds.</param>
    public IReadOnlyList<TraceMessageRow> Around(TraceMarkerRow marker, int windowMs)
    {
        ArgumentNullException.ThrowIfNull(marker);

        return _messages
            .Where(message => Math.Abs(message.TimeMs - marker.TimeMs) <= windowMs)
            .OrderBy(message => message.TimeMs)
            .ThenBy(message => message.Seq)
            .ToList();
    }

    /// <summary>
    /// Opcodes that appeared inside the window of <em>every</em> occurrence of a marker name,
    /// ranked by how rarely they appeared anywhere else.
    ///
    /// The "every occurrence" rule is what makes the table worth reading: an opcode that
    /// missed one of three pops is not a candidate for the pop, it is noise that happened to
    /// be nearby. The ratio then separates a message that only occurs at the pop from one
    /// that occurs constantly and was therefore also present at the pop.
    /// </summary>
    /// <param name="windowMs">Half-width of the window in milliseconds.</param>
    /// <param name="markerName">Restrict to one marker name, or null for all of them.</param>
    public IReadOnlyList<TraceCandidateRow> Candidates(int windowMs, string? markerName = null)
    {
        var totals = _messages
            .GroupBy(message => (message.Direction, message.Opcode))
            .ToDictionary(group => group.Key, group => group.LongCount());

        var rows = new List<TraceCandidateRow>();
        foreach (var group in _markers.GroupBy(marker => marker.Name, StringComparer.Ordinal))
        {
            if (markerName is not null && !string.Equals(group.Key, markerName, StringComparison.Ordinal))
            {
                continue;
            }

            rows.AddRange(CandidatesFor(group.Key, group.ToList(), windowMs, totals));
        }

        return rows
            .OrderByDescending(row => row.Ratio)
            .ThenByDescending(row => row.InsideCount)
            .ThenBy(row => row.Opcode, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<TraceCandidateRow> CandidatesFor(
        string name,
        IReadOnlyList<TraceMarkerRow> occurrences,
        int windowMs,
        IReadOnlyDictionary<(string Direction, string Opcode), long> totals)
    {
        var hits = new Dictionary<(string Direction, string Opcode), int>();
        var inside = new Dictionary<(string Direction, string Opcode), HashSet<long>>();

        foreach (var occurrence in occurrences)
        {
            var seen = new HashSet<(string Direction, string Opcode)>();
            foreach (var message in Around(occurrence, windowMs))
            {
                var key = (message.Direction, message.Opcode);
                seen.Add(key);
                if (!inside.TryGetValue(key, out var sequences))
                {
                    sequences = new HashSet<long>();
                    inside[key] = sequences;
                }

                sequences.Add(message.Seq);
            }

            foreach (var key in seen)
            {
                hits[key] = hits.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        foreach (var pair in hits.Where(entry => entry.Value == occurrences.Count))
        {
            var total = totals.TryGetValue(pair.Key, out var value) ? value : 0;
            long insideCount = inside[pair.Key].Count;
            yield return new TraceCandidateRow(
                name,
                pair.Key.Direction,
                pair.Key.Opcode,
                pair.Value,
                occurrences.Count,
                insideCount,
                total,
                total == 0 ? 0 : (double)insideCount / total);
        }
    }

    private static void Classify(
        JsonObject node,
        ref JsonObject? header,
        ref JsonObject? summary,
        List<TraceMessageRow> messages,
        List<TraceMarkerRow> markers,
        ref int unreadable)
    {
        if (node.ContainsKey("trace_version"))
        {
            header ??= node;
            return;
        }

        if (node.ContainsKey("summary"))
        {
            summary ??= node;
            return;
        }

        if (Text(node, "marker") is { } marker)
        {
            markers.Add(new TraceMarkerRow(marker, Number(node, "t_ms")));
            return;
        }

        if (Text(node, "op") is { } opcode && Text(node, "dir") is { } direction)
        {
            if (messages.Count >= MaxMessageRows)
            {
                throw new InvalidDataException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"trace has more than {MaxMessageRows} message rows"));
            }

            messages.Add(new TraceMessageRow(
                Number(node, "seq"),
                Number(node, "t_ms"),
                direction,
                opcode,
                (int)Number(node, "len"),
                Text(node, "h12") ?? string.Empty));
            return;
        }

        unreadable++;
    }

    private static string? Text(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue text
            ? text.TryGetValue<string>(out var result) ? result : null
            : null;

    private static long Number(JsonObject node, string name) =>
        node.TryGetPropertyValue(name, out var value) && value is JsonValue number &&
        number.TryGetValue<long>(out var result)
            ? result
            : 0;
}

/// <summary>
/// The <c>--trace-report</c> mode: read a trace back and print what it suggests.
///
/// Offline in the strictest sense -- it opens one file and prints text. No capture, no game,
/// no Npcap, no database, so it runs on a machine that has never seen the game.
/// </summary>
public static class CaptureTraceReport
{
    /// <summary>Default half-width of a marker window, in milliseconds.</summary>
    public const int DefaultWindowMs = 5000;

    /// <summary>Smallest window the command line accepts.</summary>
    public const int MinWindowMs = 1;

    /// <summary>Largest window the command line accepts.</summary>
    public const int MaxWindowMs = 600_000;

    /// <summary>Histogram rows printed before the rest is summarised.</summary>
    public const int MaxHistogramRows = 60;

    /// <summary>Messages printed per marker occurrence.</summary>
    public const int MaxWindowRows = 60;

    /// <summary>Candidate rows printed per marker name.</summary>
    public const int MaxCandidateRows = 20;

    /// <summary>The sentence that keeps a candidate a candidate.</summary>
    public const string CandidateWarning =
        "「候选」只表示时间上相关，不表示已验证。按 docs/protocol-profile-format.md §5，" +
        "必须再有一次独立会话复现、并留下脱敏固件与 SHA-256，才能写进协议档案。";

    /// <summary>Reads a trace and prints the report. Returns 0 unless the file was unusable.</summary>
    /// <param name="path">Trace file.</param>
    /// <param name="markerName">Restrict the marker sections to this name, or null for all.</param>
    /// <param name="windowMs">Half-width of the marker window in milliseconds.</param>
    /// <param name="output">Where to print; standard output when null.</param>
    public static int Run(
        string path, string? markerName = null, int windowMs = DefaultWindowMs, TextWriter? output = null)
    {
        var writer = output ?? Console.Out;
        var analysis = CaptureTraceAnalysis.Load(path);
        var window = Math.Clamp(windowMs, MinWindowMs, MaxWindowMs);

        WriteOverview(writer, analysis, window, markerName);
        WriteHistogram(writer, analysis);
        WriteWindows(writer, analysis, window, markerName);
        WriteCandidates(writer, analysis, window, markerName);
        writer.Flush();
        return 0;
    }

    private static void WriteOverview(
        TextWriter writer, CaptureTraceAnalysis analysis, int windowMs, string? markerName)
    {
        writer.WriteLine("抓包取证报告 / capture trace report");
        writer.WriteLine("  文件:            " + analysis.Path);
        writer.WriteLine("  trace_version:   " + Field(analysis.Header, "trace_version"));
        writer.WriteLine("  synthetic:       " + Field(analysis.Header, "synthetic"));
        writer.WriteLine("  started_at_utc:  " + Field(analysis.Header, "started_at_utc"));
        writer.WriteLine("  region:          " + Field(analysis.Header, "region"));
        writer.WriteLine("  game_build:      " + Field(analysis.Header, "game_build"));
        writer.WriteLine("  npcap_version:   " + Field(analysis.Header, "npcap_version"));
        writer.WriteLine("  collector:       " + Field(analysis.Header, "collector_version"));
        writer.WriteLine("  oodle_mode:      " + Field(analysis.Header, "oodle_mode"));
        writer.WriteLine("  live_capture:    " + Field(analysis.Header, "live_capture_status"));
        writer.WriteLine();
        var only = markerName is null ? string.Empty : "  仅看标记 " + markerName;
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  消息行 {analysis.Messages.Count}  标记 {analysis.Markers.Count}  " +
            $"无法解析的行 {analysis.UnreadableLines}  窗口 ±{windowMs} ms{only}"));
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  summary: messages={Field(analysis.Summary, "messages")} " +
            $"decode_errors={Field(analysis.Summary, "decode_errors")} " +
            $"dropped={Field(analysis.Summary, "dropped")} " +
            $"duration_ms={Field(analysis.Summary, "duration_ms")} " +
            $"truncated={Field(analysis.Summary, "truncated")}"));

        if (analysis.Summary is null)
        {
            writer.WriteLine("  ⚠ 文件没有 summary 行：这次取证没有正常结束，统计可能不完整。");
        }
    }

    private static void WriteHistogram(TextWriter writer, CaptureTraceAnalysis analysis)
    {
        var rows = analysis.Histogram();
        writer.WriteLine();
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"opcode 直方图（共 {rows.Count} 个 方向+opcode 组合）"));
        writer.WriteLine("  方向  opcode   条数     len_min  len_max  len_mode");

        foreach (var row in rows.Take(MaxHistogramRows))
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {row.Direction,-4}  {row.Opcode,-7}  {row.Count,-8}  " +
                $"{row.MinLength,-7}  {row.MaxLength,-7}  {row.ModeLength}"));
        }

        if (rows.Count > MaxHistogramRows)
        {
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  …… 其余 {rows.Count - MaxHistogramRows} 行未显示"));
        }
    }

    private static void WriteWindows(
        TextWriter writer, CaptureTraceAnalysis analysis, int windowMs, string? markerName)
    {
        foreach (var marker in analysis.Markers)
        {
            if (markerName is not null && !string.Equals(marker.Name, markerName, StringComparison.Ordinal))
            {
                continue;
            }

            var rows = analysis.Around(marker, windowMs);
            writer.WriteLine();
            writer.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"标记 \"{marker.Name}\" @ t={marker.TimeMs} ms  ±{windowMs} ms  命中 {rows.Count} 条"));
            writer.WriteLine("  Δms      方向  opcode   len    h12");

            foreach (var row in rows.Take(MaxWindowRows))
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {row.TimeMs - marker.TimeMs,-7}  {row.Direction,-4}  " +
                    $"{row.Opcode,-7}  {row.Length,-5}  {row.Hash}"));
            }

            if (rows.Count > MaxWindowRows)
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"  …… 其余 {rows.Count - MaxWindowRows} 条未显示"));
            }
        }
    }

    private static void WriteCandidates(
        TextWriter writer, CaptureTraceAnalysis analysis, int windowMs, string? markerName)
    {
        writer.WriteLine();
        writer.WriteLine("候选 opcode（在同名标记的**每一次**窗口内都出现，且在别处越少越靠前）");
        writer.WriteLine("  标记            方向  opcode   命中/次数  窗口内  全程   比例   判定");

        var candidates = analysis.Candidates(windowMs, markerName);
        if (candidates.Count == 0)
        {
            writer.WriteLine("  （没有任何 opcode 满足条件。标记太少或窗口太宽时这是正常的。）");
        }

        foreach (var group in candidates.GroupBy(row => row.Marker, StringComparer.Ordinal))
        {
            foreach (var row in group.Take(MaxCandidateRows))
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {row.Marker,-14}  {row.Direction,-4}  {row.Opcode,-7}  " +
                    $"{row.MarkerHits}/{row.MarkerCount,-7}  {row.InsideCount,-6}  " +
                    $"{row.TotalCount,-5}  {row.Ratio,-5:F2}  候选"));
            }
        }

        writer.WriteLine();
        writer.WriteLine(CandidateWarning);
    }

    private static string Field(JsonObject? node, string name)
    {
        if (node is null || !node.TryGetPropertyValue(name, out var value) || value is null)
        {
            return "<无>";
        }

        return value.ToJsonString(Ipc.Wire.JsonOptions).Trim('"');
    }
}
