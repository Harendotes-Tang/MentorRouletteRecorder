using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// 从已封存 trace 的单次只读快照提取白名单统计。所有本地关联数据仅在此方法内使用；
/// 没有完整捕获健康报告时明确标记未知，不把操作标记解释为正式协议字段或通关证明。
/// </summary>
internal static class TemplateObservationExporter
{
    private const int MaxLineBytes = 128 * 1024;
    private const int MaxKeys = 4096;
    private const long WindowMs = 3000;
    private static readonly Dictionary<string, string> Events = new(StringComparer.Ordinal)
    {
        ["queued"] = "QUEUE_START", ["pop"] = "MATCH_SHOWN", ["entered"] = "DUTY_ENTER",
        ["victory"] = "DUTY_CLEAR", ["left"] = "DUTY_EXIT",
    };

    internal static CalibrationEvidenceResult Export(string path, string expectedDigest, string evidenceId, string alias)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > CaptureTraceAnalysis.MaxInputBytes) return Fail("OBSERVATION_LIMIT");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(hash, expectedDigest, StringComparison.Ordinal)) return Fail("OBSERVATION_HASH_MISMATCH");
            return Parse(bytes, evidenceId, alias);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { return Fail("OBSERVATION_UNREADABLE"); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or OverflowException)
        { return Fail("OBSERVATION_INVALID"); }
    }

    private static CalibrationEvidenceResult Parse(byte[] bytes, string evidenceId, string alias)
    {
        JsonObject? header = null, summary = null;
        var metadata = new Dictionary<Key, List<long>>();
        var markers = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var messageCount = 0;
        var markerCount = 0;
        // Bounds apply before parsing each line, including malformed or unexpected local files.
        var offset = 0;
        while (offset < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', offset);
            if (end < 0) end = bytes.Length;
            var length = end - offset;
            if (length > MaxLineBytes) return Fail("OBSERVATION_LIMIT");
            var line = bytes.AsSpan(offset, length);
            offset = end + 1;
            if (line.Trim((byte)'\r').IsEmpty) continue;
            using var document = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                return Fail("OBSERVATION_INVALID");
            var node = JsonNode.Parse(root.GetRawText())!.AsObject();
            if (summary is not null) return Fail("OBSERVATION_INVALID");
            if (header is null)
            {
                if (Number(node, "trace_version") != 1) return Fail("OBSERVATION_INVALID");
                header = node;
            }
            else if (node["summary"] is JsonValue flag && flag.TryGetValue<bool>(out var isSummary) && isSummary) summary = node;
            else if (node["marker"] is not null)
            {
                if (++markerCount > CaptureTraceSink.MaxMarkers) return Fail("OBSERVATION_LIMIT");
                var marker = Text(node, "marker");
                if (!Events.TryGetValue(marker, out var eventName)) return Fail("OBSERVATION_INVALID");
                if (!markers.TryGetValue(eventName, out var times)) markers.Add(eventName, times = new());
                times.Add(Time(node));
            }
            else
            {
                if (++messageCount > CaptureTraceAnalysis.MaxMessageRows) return Fail("OBSERVATION_LIMIT");
                var direction = Text(node, "dir");
                var opcodeText = Text(node, "op");
                if (direction is not ("C2S" or "S2C") || opcodeText.Length != 6 || !opcodeText.StartsWith("0x", StringComparison.Ordinal) ||
                    !ushort.TryParse(opcodeText.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var opcode)) return Fail("OBSERVATION_INVALID");
                var key = new Key(direction, UShort(node, "seg"), opcode, UShort(node, "len"));
                if (!metadata.TryGetValue(key, out var times))
                {
                    if (metadata.Count >= MaxKeys) return Fail("OBSERVATION_LIMIT");
                    metadata.Add(key, times = new());
                }
                times.Add(Time(node));
            }
        }
        if (header is null || summary is null || Number(summary, "written") != messageCount ||
            Number(summary, "markers") != markerCount || Number(summary, "messages") != messageCount ||
            Boolean(summary, "truncated")) return Fail("OBSERVATION_INCOMPLETE");
        if (messageCount == 0 || markerCount == 0) return Fail("OBSERVATION_NO_EVENTS");
        if ((long)metadata.Count * markerCount > 4_000_000) return Fail("OBSERVATION_LIMIT");
        var region = Text(header, "region");
        var build = Text(header, "game_build");
        if (region is not ("CN" or "GLOBAL") || build.Length > 24 ||
            !Regex.IsMatch(build, @"\A[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{4}\.[0-9]{4}\z", RegexOptions.CultureInvariant)) return Fail("OBSERVATION_IDENTITY_UNKNOWN");

        var candidates = new List<(Key Key, string Event, int Inside, int Outside, int Hits)>();
        foreach (var (key, times) in metadata)
        {
            times.Sort();
            foreach (var (eventName, eventTimes) in markers)
            {
                eventTimes.Sort();
                var inside = 0;
                var next = 0;
                var hits = 0;
                foreach (var time in eventTimes)
                {
                    var first = LowerBound(times, Math.Max(0, time - WindowMs));
                    var last = LowerBound(times, time + WindowMs + 1);
                    if (last > first) hits++;
                    inside += Math.Max(0, last - Math.Max(first, next));
                    next = Math.Max(next, last);
                }
                if (inside > 0) candidates.Add((key, eventName, inside, times.Count - inside, hits));
            }
        }
        if (candidates.Count == 0) return Fail("OBSERVATION_NO_EVENTS");
        var observations = new JsonArray();
        foreach (var row in candidates.OrderByDescending(row => row.Hits).ThenByDescending(row => row.Inside)
                     .ThenBy(row => row.Event, StringComparer.Ordinal).ThenBy(row => row.Key.Direction, StringComparer.Ordinal)
                     .ThenBy(row => row.Key.Segment).ThenBy(row => row.Key.Opcode).ThenBy(row => row.Key.Length).Take(128))
            observations.Add(new JsonObject
            {
                ["event"] = row.Event, ["direction"] = row.Key.Direction, ["segment_type"] = row.Key.Segment,
                ["opcode"] = row.Key.Opcode, ["length"] = row.Key.Length, ["inside_count"] = row.Inside,
                ["outside_count"] = row.Outside, ["event_count"] = row.Hits,
            });
        var result = new JsonObject
        {
            ["format"] = "MentorRecorder.CalibrationEvidence", ["schema_version"] = 1,
            ["kind"] = "TEMPLATE_OBSERVATION", ["evidence_id"] = evidenceId,
            ["producer"] = new JsonObject { ["app_version"] = Program.Version, ["rules_version"] = CalibrationEvidenceDocument.RulesVersion },
            ["target"] = new JsonObject
            {
                ["region"] = region, ["game_build"] = build, ["template_profile_id"] = null,
                ["template_sha256"] = null, ["code_sha256"] = null, ["quarantine_epoch"] = 0,
            },
            ["sessions"] = new JsonArray(new JsonObject
            {
                ["session_alias"] = alias, ["complete"] = true,
                ["health"] = new JsonObject
                {
                    ["silent_reason"] = Boolean(header, "synthetic") ? "SYNTHETIC_SOURCE" : "HEALTH_UNAVAILABLE",
                    ["preexisting_connections"] = Count(header, "preexisting_connections"),
                    ["adapter_dropped"] = 0, ["decoded_messages_dropped"] = Count(summary, "dropped"),
                    ["damaged_directions"] = Count(summary, "decode_errors"), ["observation_overflow"] = candidates.Count > 128,
                },
                ["checks"] = new JsonArray(),
            }),
            ["observations"] = observations,
        };
        var json = result.ToJsonString();
        return Encoding.UTF8.GetByteCount(json) <= CalibrationEvidenceDocument.MaxBytes
            ? new(true, json, "CANDIDATE_ONLY") : Fail("OBSERVATION_LIMIT");
    }

    private static int LowerBound(List<long> values, long target)
    {
        var low = 0; var high = values.Count;
        while (low < high) { var middle = low + (high - low) / 2; if (values[middle] < target) low = middle + 1; else high = middle; }
        return low;
    }

    private static long Time(JsonObject node) => Number(node, "t_ms") is >= 0 and <= 604800000L and var value ? value : throw new InvalidDataException();
    private static int UShort(JsonObject node, string name) => Number(node, name) is >= 0 and <= 65535 and var value ? (int)value : throw new InvalidDataException();
    private static long Count(JsonObject node, string name) => Number(node, name) is >= 0 and <= 1000000000 and var value ? value : throw new InvalidDataException();
    private static long Number(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : throw new InvalidDataException();
    private static string Text(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new InvalidDataException();
    private static bool Boolean(JsonObject node, string name) => node[name] is JsonValue value && value.TryGetValue<bool>(out var boolean) ? boolean : throw new InvalidDataException();
    private static CalibrationEvidenceResult Fail(string reason) => CalibrationEvidenceResult.Unavailable(reason);
    private sealed record Key(string Direction, int Segment, int Opcode, int Length);
}
