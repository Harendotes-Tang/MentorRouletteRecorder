using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Keeps a calibration's evidence on disk between runs of the Collector, so that a restart or
/// a new version of the software does not throw away what the observer has learned and cost
/// the player another evening of play.
///
/// What is written is exactly what <see cref="CalibrationSnapshot"/> holds, which is what the
/// diagnostics report already prints: opcodes, payload lengths, byte offsets, the id-type
/// values a profile would record (roulette, territory, job), redacted connection tags and
/// times. No payload byte has ever been in a snapshot and none is written here
/// (docs/privacy-boundary.md §5.2).
///
/// The file is keyed by region and client build and additionally stamped with the template it
/// was collected under. Evidence collected against a different template describes different
/// shapes, so it is discarded rather than merged - silently, because a player who patched the
/// game is not interested in why yesterday's file did not apply.
/// </summary>
public static class CalibrationEvidenceStore
{
    /// <summary>Directory under the managed data root that holds the files.</summary>
    public const string DirectoryName = "calibration";

    /// <summary>Layout version of the file; anything else is discarded.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Production directory: <c>calibration</c> under the managed data root.</summary>
    public static string RootPath => Path.Combine(DatabasePaths.RootDirectory, DirectoryName);

    /// <summary>File name for a region and build, e.g. <c>cn.2026.09.01.0000.0000.json</c>.</summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build as read from disk.</param>
    public static string FileNameFor(Region region, string gameBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        var builder = new StringBuilder(region == Region.Global ? "global" : "cn").Append('.');
        foreach (var c in gameBuild.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-');
        }

        return builder.Append(".json").ToString();
    }

    /// <summary>
    /// Why a run started from nothing. "OK" is the only answer that is not a loss of evidence;
    /// the others separate an absent file from a stale or unreadable one, which the report's
    /// bare carried = 0 cannot.
    /// </summary>
    /// <param name="root">Directory holding the files.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Hash of the template the evidence must have been collected under.</param>
    public static string Explain(string root, Region region, string gameBuild, string templateSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var path = Path.Combine(root, FileNameFor(region, gameBuild));
        try
        {
            if (!File.Exists(path))
            {
                return "NO_FILE";
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var node = document.RootElement;
            if (Int32(node, "schema_version") != SchemaVersion)
            {
                return "OLD_LAYOUT";
            }

            if (!string.Equals(Text(node, "game_build"), gameBuild, StringComparison.Ordinal))
            {
                return "OTHER_BUILD";
            }

            return string.Equals(Text(node, "template_sha256"), templateSha256, StringComparison.Ordinal)
                ? "OK"
                : "OTHER_TEMPLATE";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return "UNREADABLE";
        }
    }

    /// <summary>
    /// Reads the evidence for a build, or null when there is none, it was collected under a
    /// different template, or the file cannot be understood. Never throws: a corrupt file is
    /// the same situation as no file, and neither may stop the Collector from starting.
    /// </summary>
    /// <param name="root">Directory holding the files.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Hash of the template the evidence must have been collected under.</param>
    public static CalibrationSnapshot? Load(string root, Region region, string gameBuild, string templateSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var path = Path.Combine(root, FileNameFor(region, gameBuild));
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var node = document.RootElement;
            if (Int32(node, "schema_version") != SchemaVersion ||
                !string.Equals(Text(node, "game_build"), gameBuild, StringComparison.Ordinal) ||
                !string.Equals(Text(node, "template_sha256"), templateSha256, StringComparison.Ordinal))
            {
                return null;
            }

            return Read(node);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or OverflowException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the evidence atomically. Returns false when it could not be written; the caller
    /// carries on observing in memory exactly as before, because losing the file costs the
    /// player nothing they had before this existed.
    /// </summary>
    /// <param name="root">Directory to write into.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Hash of the template the evidence was collected under.</param>
    /// <param name="snapshot">Evidence to write.</param>
    public static bool Save(
        string root, Region region, string gameBuild, string templateSha256, CalibrationSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(snapshot);
        var path = Path.Combine(root, FileNameFor(region, gameBuild));
        // Never trade an evening of evidence for a minute of it. An observer that failed to
        // adopt - an unreadable file, a template that did not match, anything at all - starts
        // empty, and the periodic save would then write that emptiness over the good file and
        // make the loss permanent.
        //
        // Discarding is not affected: 重新观察 deletes the file first, so the next save has
        // nothing to be poorer than.
        if (StoredMessages(path) > snapshot.MessagesSeen)
        {
            return false;
        }

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(root);
            var json = Write(snapshot, region, gameBuild, templateSha256).ToJsonString(new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
            }

            return false;
        }
    }

    /// <summary>
    /// How many messages the file at this path claims, or -1 when there is no readable file.
    /// Reads the one number, not the whole snapshot.
    /// </summary>
    /// <param name="path">Full path of the evidence file.</param>
    private static int StoredMessages(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return -1;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return Int32(document.RootElement, "messages_seen");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Removes the file for a build. Used when the player throws the evidence away and when a
    /// profile has been written, so neither decision is quietly undone by the next restart.
    /// </summary>
    /// <param name="root">Directory holding the files.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    public static void Delete(string root, Region region, string gameBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        try
        {
            var path = Path.Combine(root, FileNameFor(region, gameBuild));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------------------- writing

    private static JsonObject Write(
        CalibrationSnapshot snapshot, Region region, string gameBuild, string templateSha256) => new()
    {
        ["schema_version"] = SchemaVersion,
        ["region"] = EnumWire<Region>.Format(region),
        ["game_build"] = gameBuild,
        ["template_sha256"] = templateSha256,
        ["capture_session_id"] = snapshot.CaptureSessionId,
        ["sessions"] = snapshot.SessionCount,
        ["messages_seen"] = snapshot.MessagesSeen,
        ["overflow"] = snapshot.OverflowCount,
        ["pop_shapes"] = snapshot.PopShapeSeen,
        ["marker_overflow"] = snapshot.MarkerOverflow,
        ["diagnostics_overflow"] = snapshot.DiagnosticsOverflow,
        ["first_at"] = Stamp(snapshot.FirstMessageAtUtc),
        ["last_at"] = Stamp(snapshot.LastMessageAtUtc),
        ["pairs"] = Array(snapshot.Pairs, pair => new JsonObject
        {
            ["tag"] = pair.ConnectionTag,
            ["request"] = pair.RequestOpcode,
            ["reply"] = pair.ReplyOpcode,
            ["roulette"] = pair.RouletteId,
            ["request_t"] = pair.RequestTMs,
            ["reply_t"] = pair.ReplyTMs,
            ["at"] = Stamp(pair.ReplyAtUtc),
        }),
        ["pops"] = Array(snapshot.Pops, pop => new JsonObject
        {
            ["tag"] = pop.ConnectionTag,
            ["opcode"] = pop.Opcode,
            ["roulette"] = pop.RouletteId,
            ["t"] = pop.TMs,
            ["at"] = Stamp(pop.AtUtc),
            ["echo"] = pop.WithinEcho,
            ["selectors"] = Array(pop.Selectors, reading => new JsonObject
            {
                ["field"] = reading.Field,
                ["value"] = reading.Value,
            }),
        }),
        ["clusters"] = Array(snapshot.Clusters, cluster => new JsonObject
        {
            ["index"] = cluster.Index,
            ["tag"] = cluster.ConnectionTag,
            ["start_t"] = cluster.StartTMs,
            ["end_t"] = cluster.EndTMs,
            ["started_at"] = Stamp(cluster.StartedAtUtc),
            ["ended_at"] = Stamp(cluster.EndedAtUtc),
            ["load_t"] = cluster.LoadStartTMs,
            ["load_at"] = Stamp(cluster.LoadStartedAtUtc),
            ["overflow"] = cluster.OverflowCount,
            // Both added for shared calibration without changing the layout version, so every
            // earlier file still loads; absent, they read back as "no readings" and "not known".
            ["lobby"] = cluster.Lobby,
            ["territory_readings"] = Array(cluster.TerritoryReadings, reading => new JsonObject
            {
                ["opcode"] = reading.Opcode,
                ["valid"] = reading.Valid,
                ["invalid"] = reading.Invalid,
                ["known_duty"] = reading.KnownDuty,
            }),
            ["members"] = Keys(cluster.Members),
            ["territories"] = Array(cluster.TerritoryHits, hit => new JsonObject
            {
                ["opcode"] = hit.Opcode,
                ["length"] = hit.Length,
                ["territory"] = hit.TerritoryId,
                ["duty"] = hit.DutyName,
            }),
            ["jobs"] = Array(
                cluster.JobValues.Keys.Concat(cluster.JobViolations.Keys).Distinct().OrderBy(opcode => opcode),
                opcode => new JsonObject
                {
                    ["opcode"] = opcode,
                    ["values"] = new JsonArray(
                        (cluster.JobValues.TryGetValue(opcode, out var values) ? values : System.Array.Empty<long>())
                        .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["violations"] = cluster.JobViolations.TryGetValue(opcode, out var violations) ? violations : 0,
                }),
        }),
        ["outside"] = Keys(snapshot.OutsideCounts),
        ["opcodes"] = Array(snapshot.OpcodeCounts, entry => new JsonObject
        {
            ["inbound"] = entry.Key.Direction == PacketDirection.ServerToClient,
            ["opcode"] = entry.Key.Opcode,
            ["count"] = entry.Value,
        }),
        ["job_violations"] = Array(snapshot.JobViolations, entry => new JsonObject
        {
            ["opcode"] = entry.Key,
            ["count"] = entry.Value,
        }),
        ["pop_refusals"] = Array(snapshot.PopRefusals, entry => new JsonObject
        {
            ["opcode"] = entry.Key.Opcode,
            ["field"] = entry.Key.Field,
            ["count"] = entry.Value,
        }),
        ["finder_lengths"] = Array(snapshot.FinderLengths, entry => new JsonObject
        {
            ["opcode"] = entry.Key.Opcode,
            ["length"] = entry.Key.Length,
            ["count"] = entry.Value,
        }),
        ["roulette_echoes"] = Array(snapshot.RouletteEchoes, entry => new JsonObject
        {
            ["opcode"] = entry.Key.Opcode,
            ["length"] = entry.Key.Length,
            ["count"] = entry.Value,
        }),
        ["echo_hits"] = Array(snapshot.RouletteEchoHits, hit => new JsonObject
        {
            ["tag"] = hit.ConnectionTag,
            ["opcode"] = hit.Opcode,
            ["length"] = hit.Length,
            ["roulette"] = hit.RouletteId,
            ["t"] = hit.TMs,
            ["at"] = Stamp(hit.AtUtc),
        }),
        ["finder_selectors"] = Array(snapshot.FinderSelectors, entry => new JsonObject
        {
            ["opcode"] = entry.Key.Opcode,
            ["field"] = entry.Key.Field,
            ["value"] = entry.Key.Value,
            ["count"] = entry.Value,
        }),
        ["markers"] = Array(snapshot.Markers, marker => new JsonObject
        {
            ["opcode"] = marker.Opcode,
            ["length"] = marker.Length,
            ["offset"] = marker.Offset,
            ["hits"] = marker.Hits,
            ["tag"] = marker.ConnectionTag,
            ["many"] = marker.ManyConnections,
            ["complete"] = marker.SightingsComplete,
            ["roulettes"] = new JsonArray(marker.RouletteIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["sightings"] = Array(marker.Sightings, sighting => new JsonObject
            {
                ["at"] = Stamp(sighting.AtUtc),
                ["roulette"] = sighting.RouletteId,
                ["tag"] = sighting.ConnectionTag,
                ["t"] = sighting.TMs,
            }),
        }),
        ["marker_shapes"] = Array(snapshot.MarkerShapeTotals, entry => new JsonObject
        {
            ["opcode"] = entry.Key.Opcode,
            ["length"] = entry.Key.Length,
            ["count"] = entry.Value,
        }),
        // The timing tables. They need two duties on two different roulettes before they can
        // say anything, so they are the slowest evidence of all to collect and the most
        // expensive to lose; absent from a file written by an older version, which reads back
        // as "nothing timed yet" and simply starts collecting.
        ["timing_overflow"] = snapshot.TimingOverflow,
        ["timed_dead"] = Array(snapshot.TimedDead, dead => new JsonObject
        {
            ["opcode"] = dead.Opcode,
            ["length"] = dead.Length,
        }),
        ["timed_shapes"] = Array(snapshot.TimedShapes, shape => new JsonObject
        {
            ["opcode"] = shape.Opcode,
            ["length"] = shape.Length,
            ["total"] = shape.Total,
            ["in_queue"] = shape.InQueue,
            ["pre_duty"] = shape.PreDuty,
            ["stray"] = shape.Stray,
            ["complete"] = shape.SightingsComplete,
            ["sightings"] = Array(shape.Sightings, Sighting),
            ["pending"] = Array(shape.Pending, Sighting),
        }),
    };

    private static JsonObject Sighting(TimedSighting sighting) => new()
    {
        ["at"] = Stamp(sighting.AtUtc),
        ["t"] = sighting.TMs,
        ["tag"] = sighting.ConnectionTag,
    };

    private static JsonArray Keys(IReadOnlyDictionary<MessageKey, int> counts) =>
        Array(counts, entry => new JsonObject
        {
            ["inbound"] = entry.Key.Direction == PacketDirection.ServerToClient,
            ["opcode"] = entry.Key.Opcode,
            ["length"] = entry.Key.Length,
            ["count"] = entry.Value,
        });

    private static JsonArray Array<T>(IEnumerable<T> items, Func<T, JsonObject> map) =>
        new(items.Select(item => (JsonNode?)map(item)).ToArray());

    private static string? Stamp(DateTimeOffset? at) => at is { } value
        ? value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)
        : null;

    private static string Stamp(DateTimeOffset at) => Stamp((DateTimeOffset?)at)!;

    // ------------------------------------------------------------------------------- reading

    private static CalibrationSnapshot Read(JsonElement node)
    {
        var clusters = Items(node, "clusters").Select(item => new ZoneCluster(
            Int32(item, "index"),
            Text(item, "tag") ?? string.Empty,
            Int64(item, "start_t"),
            Int64(item, "end_t"),
            At(item, "started_at"),
            At(item, "ended_at"),
            KeyCounts(item, "members"),
            Items(item, "territories").Select(hit => new TerritoryHit(
                (ushort)Int32(hit, "opcode"), Int32(hit, "length"), Int64(hit, "territory"),
                Text(hit, "duty") ?? string.Empty)).ToArray(),
            Items(item, "jobs")
                .Select(job => (Opcode: (ushort)Int32(job, "opcode"), Values: Values(job, "values").ToArray()))
                .Where(job => job.Values.Length > 0)
                .ToDictionary(job => job.Opcode, job => (IReadOnlyList<long>)job.Values),
            Int32(item, "overflow"))
        {
            LoadStartTMs = Int64(item, "load_t"),
            LoadStartedAtUtc = At(item, "load_at"),
            TerritoryReadings = Items(item, "territory_readings").Select(reading => new TerritoryReading(
                (ushort)Int32(reading, "opcode"), Int32(reading, "valid"), Int32(reading, "invalid"),
                Int32(reading, "known_duty"))).ToArray(),
            Lobby = BoolOrNull(item, "lobby"),
            // Files written before 2026-09-15 carry no per-burst violations; they read as none.
            JobViolations = Items(item, "jobs")
                .Select(job => (Opcode: (ushort)Int32(job, "opcode"), Count: Int32(job, "violations")))
                .Where(job => job.Count > 0)
                .ToDictionary(job => job.Opcode, job => job.Count),
        }).ToArray();

        return new CalibrationSnapshot(
            Text(node, "capture_session_id") ?? string.Empty,
            Items(node, "pairs").Select(item => new FinderPairHit(
                Text(item, "tag") ?? string.Empty, (ushort)Int32(item, "request"), (ushort)Int32(item, "reply"),
                Int64(item, "roulette"), Int64(item, "request_t"), Int64(item, "reply_t"), At(item, "at"))).ToArray(),
            Items(node, "pops").Select(item => new PopHit(
                Text(item, "tag") ?? string.Empty, (ushort)Int32(item, "opcode"), Int64(item, "roulette"),
                Int64(item, "t"), At(item, "at"), Bool(item, "echo"),
                Items(item, "selectors")
                    .Select(reading => new CalibrationSelectorReading(
                        Text(reading, "field") ?? string.Empty, Int64(reading, "value")))
                    .ToArray())).ToArray(),
            clusters,
            KeyCounts(node, "outside"),
            Items(node, "opcodes").ToDictionary(
                item => (Bool(item, "inbound") ? PacketDirection.ServerToClient : PacketDirection.ClientToServer,
                    (ushort)Int32(item, "opcode")),
                item => Int32(item, "count")),
            Items(node, "job_violations").ToDictionary(
                item => (ushort)Int32(item, "opcode"), item => Int32(item, "count")),
            Int32(node, "overflow"),
            Int32(node, "messages_seen"),
            Math.Max(1, Int32(node, "sessions")))
        {
            PopShapeSeen = Int32(node, "pop_shapes"),
            PopRefusals = Items(node, "pop_refusals").ToDictionary(
                item => ((ushort)Int32(item, "opcode"), Text(item, "field") ?? string.Empty),
                item => Int32(item, "count")),
            FinderLengths = Items(node, "finder_lengths").ToDictionary(
                item => ((ushort)Int32(item, "opcode"), Int32(item, "length")), item => Int32(item, "count")),
            RouletteEchoes = Items(node, "roulette_echoes").ToDictionary(
                item => ((ushort)Int32(item, "opcode"), Int32(item, "length")), item => Int32(item, "count")),
            RouletteEchoHits = Items(node, "echo_hits").Select(item => new RouletteEchoHit(
                Text(item, "tag") ?? string.Empty, (ushort)Int32(item, "opcode"), Int32(item, "length"),
                Int64(item, "roulette"), Int64(item, "t"), At(item, "at"))).ToArray(),
            FinderSelectors = Items(node, "finder_selectors").ToDictionary(
                item => ((ushort)Int32(item, "opcode"), Text(item, "field") ?? string.Empty, Int64(item, "value")),
                item => Int32(item, "count")),
            Markers = Items(node, "markers").Select(item => new MarkerCandidate(
                (ushort)Int32(item, "opcode"), Int32(item, "length"), Int32(item, "offset"), Int32(item, "hits"),
                Values(item, "roulettes").ToArray(), Text(item, "tag") ?? string.Empty, Bool(item, "many"),
                Items(item, "sightings").Select(sighting => new MarkerSighting(
                    At(sighting, "at"), Int64(sighting, "roulette"), Text(sighting, "tag") ?? string.Empty,
                    Int64(sighting, "t"))).ToArray(),
                Bool(item, "complete"))).ToArray(),
            MarkerShapeTotals = Items(node, "marker_shapes").ToDictionary(
                item => ((ushort)Int32(item, "opcode"), Int32(item, "length")), item => Int32(item, "count")),
            TimedShapes = Items(node, "timed_shapes").Select(item => new TimedShape(
                (ushort)Int32(item, "opcode"), Int32(item, "length"), Int32(item, "total"),
                Int32(item, "in_queue"), Int32(item, "pre_duty"), Int32(item, "stray"),
                Sightings(item, "sightings"), Sightings(item, "pending"), Bool(item, "complete"))).ToArray(),
            TimedDead = Items(node, "timed_dead")
                .Select(item => ((ushort)Int32(item, "opcode"), Int32(item, "length"))).ToArray(),
            TimingOverflow = Int32(node, "timing_overflow"),
            MarkerOverflow = Int32(node, "marker_overflow"),
            DiagnosticsOverflow = Int32(node, "diagnostics_overflow"),
            FirstMessageAtUtc = AtOrNull(node, "first_at"),
            LastMessageAtUtc = AtOrNull(node, "last_at"),
        };
    }

    private static IReadOnlyList<TimedSighting> Sightings(JsonElement node, string name) =>
        Items(node, name).Select(item => new TimedSighting(
            At(item, "at"), Int64(item, "t"), Text(item, "tag") ?? string.Empty)).ToArray();

    private static Dictionary<MessageKey, int> KeyCounts(JsonElement node, string name) =>
        Items(node, name).ToDictionary(
            item => new MessageKey(
                Bool(item, "inbound") ? PacketDirection.ServerToClient : PacketDirection.ClientToServer,
                (ushort)Int32(item, "opcode"), Int32(item, "length")),
            item => Int32(item, "count"));

    private static IEnumerable<JsonElement> Items(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static IEnumerable<long> Values(JsonElement node, string name) =>
        Items(node, name).Select(item => item.GetInt64());

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Int32(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static long Int64(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    private static bool Bool(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool? BoolOrNull(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static DateTimeOffset At(JsonElement node, string name) =>
        AtOrNull(node, name) ?? DateTimeOffset.UnixEpoch;

    private static DateTimeOffset? AtOrNull(JsonElement node, string name) =>
        Text(node, name) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;
}
