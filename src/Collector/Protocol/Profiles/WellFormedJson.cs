using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// Parses JSON that must be readable as text all the way through.
///
/// <see cref="JsonDocument"/> accepts a key or a string holding a lone surrogate escape (<c>"\ud800"</c>, a low
/// surrogate first, a high surrogate not followed by a low one) and bytes inside a string that are not UTF-8; it
/// throws <see cref="InvalidOperationException"/> only later, from whichever <see cref="JsonProperty.Name"/> or
/// <see cref="JsonElement.GetString"/> reads that text first. A reader of untrusted input that promises never to
/// throw parses through here instead: such text is refused exactly like text that is not JSON, before anything in
/// it is looked at, so what a document is taken to say never depends on where the damage sits. This is the rule of
/// <c>strict_json</c> in <c>tools/shared-calibration/sharecode.py</c>. Pure: no IO.
/// </summary>
internal static class WellFormedJson
{
    /// <summary>
    /// Parses UTF-8 JSON, or returns false when it is not JSON under <paramref name="options"/> or any key or
    /// string in it (a repeated key and its value included) cannot be read as text.
    /// </summary>
    /// <param name="utf8">The JSON bytes.</param>
    /// <param name="options">Parse options, as the caller would pass to <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/>.</param>
    /// <param name="document">The parsed document, owned by the caller; null when false is returned.</param>
    internal static bool TryParse(ReadOnlyMemory<byte> utf8, JsonDocumentOptions options, [NotNullWhen(true)] out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(utf8, options);
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }

        if (IsReadable(document.RootElement))
        {
            return true;
        }

        document.Dispose();
        document = null;
        return false;
    }

    /// <summary>True when every key and string under <paramref name="root"/> can be read as text.</summary>
    private static bool IsReadable(JsonElement root)
    {
        try
        {
            ReadAllText(root);
            return true;
        }
        catch (InvalidOperationException)
        {
            // The runtime's own verdict: exactly the keys and strings a later reader would throw on.
            return false;
        }
    }

    /// <summary>Reads every key and string once; depth is bounded by the options the document was parsed with.</summary>
    private static void ReadAllText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    _ = property.Name;
                    ReadAllText(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    ReadAllText(item);
                }

                break;
            case JsonValueKind.String:
                _ = element.GetString();
                break;
        }
    }
}
