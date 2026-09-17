using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Reference;

/// <summary>
/// Roulette names the player corrected on this machine, kept across runs.
///
/// The shipped table maps a roulette id to a name for display, and it can be wrong: the names
/// come from a community export of the game's own text, and a patch can renumber or rename
/// what it likes.
///
/// Without an override, a wrong NAME and a wrong MESSAGE look identical in the timeline the
/// player confirms, so the only way to report "that is not what I queued" is to mark the line
/// wrong, which throws away a correctly identified opcode along with it. A name the player can
/// correct costs one click and costs the calibration nothing.
///
/// A correction is the player's own testimony about their own client and outranks the shipped
/// table for that id permanently. It is display only, exactly like the table it overrides: no
/// override identifies anything on the wire, and none reaches a protocol profile.
/// </summary>
public static class RouletteNameOverrides
{
    /// <summary>Directory under the managed data root that holds the file.</summary>
    public const string DirectoryName = "reference";

    /// <summary>File name; one file covers every region.</summary>
    public const string FileName = "roulette-names.json";

    /// <summary>Longest name accepted from a client, in characters.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Corrections kept at once. A region has ten or so roulettes; this is not a cache.</summary>
    public const int MaxEntries = 128;

    /// <summary>Production path: <c>reference/roulette-names.json</c> under the data root.</summary>
    public static string DefaultPath =>
        Path.Combine(DatabasePaths.RootDirectory, DirectoryName, FileName);

    /// <summary>
    /// Reads the corrections, or an empty map when there are none or the file cannot be read.
    /// Never throws: a correction is a convenience and may never stop the Collector starting.
    /// </summary>
    /// <param name="path">File to read.</param>
    public static IReadOnlyDictionary<(Region Region, int RouletteId), string> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = new Dictionary<(Region, int), string>();
        try
        {
            if (!File.Exists(path))
            {
                return result;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("names", out var names) ||
                names.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var item in names.EnumerateArray())
            {
                if (result.Count >= MaxEntries ||
                    !item.TryGetProperty("region", out var region) ||
                    !item.TryGetProperty("roulette_id", out var id) ||
                    !item.TryGetProperty("name", out var name) ||
                    region.ValueKind != JsonValueKind.String ||
                    id.ValueKind != JsonValueKind.Number ||
                    name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var text = name.GetString();
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaxNameLength)
                {
                    continue;
                }

                result[(EnumWire<Region>.Parse(region.GetString()), id.GetInt32())] = text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException)
        {
            return new Dictionary<(Region, int), string>();
        }

        return result;
    }

    /// <summary>
    /// Records one correction, replacing any earlier one for the same region and id. Returns
    /// false when it could not be written; the caller has already accepted the player's
    /// timeline either way, so a failed write costs a label and nothing else.
    /// </summary>
    /// <param name="path">File to write.</param>
    /// <param name="region">Region the correction belongs to.</param>
    /// <param name="rouletteId">Roulette id the player named.</param>
    /// <param name="name">Name the player chose.</param>
    /// <param name="atUtc">When the correction was made.</param>
    public static bool Record(string path, Region region, int rouletteId, string name, DateTimeOffset atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaxNameLength)
        {
            return false;
        }

        var current = new Dictionary<(Region, int), string>(Load(path))
        {
            [(region, rouletteId)] = name,
        };
        if (current.Count > MaxEntries)
        {
            return false;
        }

        var rows = new JsonArray();
        foreach (var ((entryRegion, entryId), entryName) in current
            .OrderBy(entry => entry.Key.Item1).ThenBy(entry => entry.Key.Item2))
        {
            rows.Add(new JsonObject
            {
                ["region"] = EnumWire<Region>.Format(entryRegion),
                ["roulette_id"] = entryId,
                ["name"] = entryName,
            });
        }

        var document = new JsonObject
        {
            ["schema_version"] = 1,
            ["updated_at_utc"] = atUtc.ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["note"] = "本机玩家在校准核对时改正的随机任务名称。只用于显示，" +
                       "不参与任何判定，也不写进协议档案。删掉本文件即恢复随包名称。",
            ["names"] = rows,
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                temporary,
                document.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
}
