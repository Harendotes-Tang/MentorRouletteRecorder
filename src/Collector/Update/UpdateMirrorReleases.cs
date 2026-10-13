using System.Text.Json;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Update;

/// <summary>Reads a bounded mirror listing without trusting any asset URL supplied by the server.</summary>
internal static class UpdateMirrorReleases
{
    internal const int MaxBytes = 256 * 1024;
    private const int MaxReleases = 20;

    /// <summary>Returns complete formal releases newest first; null denotes malformed or ambiguous input.</summary>
    internal static IReadOnlyList<string>? Read(ReadOnlyMemory<byte> bytes)
    {
        if (!WellFormedJson.TryParse(bytes, new JsonDocumentOptions { MaxDepth = 16 }, out var document)) return null;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaxReleases || HasDuplicates(root)) return null;
            var versions = new Dictionary<string, UpdateVersion>(StringComparer.Ordinal);
            foreach (var release in root.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) return null;
                if (Flag(release, "draft") || Flag(release, "prerelease")) continue;
                var text = tag.GetString()!;
                if (!text.StartsWith('v') || !UpdateVersion.TryParse(text[1..], out var parsed)) continue;
                var version = text[1..];
                if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
                if (assets.GetArrayLength() > 100) return null;
                var names = new HashSet<string>(StringComparer.Ordinal);
                var allNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
                    if (!allNames.Add(name.GetString()!)) return null;
                    if (!asset.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "attach") continue;
                    names.Add(name.GetString()!);
                }
                var installer = "MentorRecorder-" + version + "-setup.exe";
                if (names.Contains(UpdateCheckClient.AssetFileName) && names.Contains(installer) && names.Contains(installer + UpdateCheckClient.ChecksumSuffix))
                {
                    if (!versions.TryAdd(version, parsed)) return null;
                }
            }
            return versions.OrderByDescending(pair => pair.Value.Major).ThenByDescending(pair => pair.Value.Minor)
                .ThenByDescending(pair => pair.Value.Patch).Select(pair => pair.Key).ToArray();
        }
    }

    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var flag) && flag.ValueKind != JsonValueKind.False;

    private static bool HasDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
                if (!keys.Add(property.Name) || HasDuplicates(property.Value)) return true;
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) if (HasDuplicates(value)) return true;
        return false;
    }
}
