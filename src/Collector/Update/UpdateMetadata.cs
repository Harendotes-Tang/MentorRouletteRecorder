using System.Text.Json;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Update;

/// <summary>What reading a published metadata document produced.</summary>
/// <param name="Version">The version it names, or null when it was refused.</param>
/// <param name="Refusal">
/// Why the document was refused (<c>NOT_JSON</c>, which includes bytes that are not UTF-8 and a key
/// or string holding a lone surrogate escape; <c>NOT_AN_OBJECT</c>, <c>DUPLICATE_KEY</c>,
/// <c>MISSING:version</c>, <c>INVALID:version</c>), or null when it was read. Never repeats the
/// document's content.
/// </param>
public sealed record UpdateMetadataReadResult(string? Version, string? Refusal)
{
    /// <summary>True when the document was usable.</summary>
    public bool IsReadable => Refusal is null;

    internal static UpdateMetadataReadResult Refuse(string reason) => new(null, reason);
}

/// <summary>
/// Reads the <c>BUILD-METADATA.json</c> published beside a release.
///
/// The document is untrusted input from the network, so it is read the way the shared-calibration
/// index is: strict UTF-8 with a byte-order mark tolerated, a JSON object at the root, at most one
/// fact taken from it - a <c>version</c> of three numbers - and every other key ignored. Nothing is
/// repaired, guessed or executed; the version is only ever compared and displayed. Pure: no IO, no
/// clock.
/// </summary>
public static class UpdateMetadata
{
    /// <summary>Layout a version must have before it is looked at further.</summary>
    public const string VersionPattern = @"^\d+\.\d+\.\d+$";

    private static readonly Regex Version = new(@"^[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant);

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Reads a document. Never throws; every failure is a refusal on the result.</summary>
    /// <param name="utf8">The document's bytes, as downloaded.</param>
    public static UpdateMetadataReadResult Read(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith(Utf8Bom))
        {
            utf8 = utf8[Utf8Bom.Length..];
        }

        if (!WellFormedJson.TryParse(utf8, ParseOptions, out var document))
        {
            return UpdateMetadataReadResult.Refuse("NOT_JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateMetadataReadResult.Refuse("NOT_AN_OBJECT");
            }

            if (HasDuplicateKey(root))
            {
                return UpdateMetadataReadResult.Refuse("DUPLICATE_KEY");
            }

            if (!root.TryGetProperty("version", out var version))
            {
                return UpdateMetadataReadResult.Refuse("MISSING:version");
            }

            var text = version.ValueKind == JsonValueKind.String ? version.GetString() : null;
            return text is not null && Version.IsMatch(text)
                ? new UpdateMetadataReadResult(text, null)
                : UpdateMetadataReadResult.Refuse("INVALID:version");
        }
    }

    private static bool HasDuplicateKey(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return true;
            }
        }

        return false;
    }
}
