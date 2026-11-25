using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Community;

internal static partial class CommunityContent
{
    internal const int MaxSnapshotBytes = 1_250_000;
    internal const int MaxEntries = 400;
    internal const int PageSize = 40;
    internal const int MaxBatch = 20;
    internal const int MaxSubmissionBytes = 30_000;
    private const int MaxMacroLines = 15;
    internal static readonly string[] Fields =
        ["schema_version", "id", "type", "content_id", "region", "language", "title", "body",
         "boss", "phase", "role", "tags", "game_version", "links", "source"];
    internal static readonly string[] PublishedFields =
        ["author", "license", "revision", "updated_at", "status", "issue"];

    internal static bool IsId(string value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && id.ToString("D") == value;

    internal static JsonObject Draft(JsonObject data) =>
        new(Fields.Select(key => KeyValuePair.Create(key, data[key]?.DeepClone())));

    internal static void Validate(JsonObject item, bool published = false) =>
        Validate(item, published, MaxMacroLines);

    internal static void ValidateLocalDraft(JsonObject item) =>
        Validate(item, published: false, maxMacroLines: int.MaxValue);

    private static void Validate(JsonObject item, bool published, int maxMacroLines)
    {
        var keys = published ? Fields.Concat(PublishedFields) : Fields;
        if (item.Count != keys.Count() || !keys.All(item.ContainsKey) ||
            Integer(item, "schema_version") != 1 || !IsId(String(item, "id", 36)) ||
            String(item, "type", 8) is not ("reminder" or "macro") ||
            String(item, "region", 6) is not ("ALL" or "CN" or "TC" or "GLOBAL") ||
            String(item, "language", 7) is not ("zh-Hans" or "zh-Hant" or "en" or "ja") ||
            Integer(item, "content_id") is < 0 or > 65535)
            throw new FormatException("invalid_content");

        if (string.IsNullOrWhiteSpace(String(item, "title", 120)))
            throw new FormatException("invalid_title");
        foreach (var field in new[] { "boss", "phase", "role", "game_version" })
            String(item, field, 80);
        String(item, "source", 240);
        var id = item["id"]!.GetValue<string>();
        Values(item, "tags", 10, value => value.Length is > 0 and <= 40);
        Values(item, "links", 10, value => IsId(value) && value != id);

        var body = String(item, "body", 1600, multiline: true);
        if (string.IsNullOrWhiteSpace(body))
            throw new FormatException("empty_body");
        if (item["type"]!.GetValue<string>() == "macro" && MacroError(body, "macro", maxMacroLines) is { } error)
            throw new FormatException(error);

        if (!published)
            return;
        if (item["author"] is not JsonObject author || author.Count != 2 ||
            !Login().IsMatch(String(author, "login", 39)) || Integer64(author, "id") < 1 ||
            !IsLicense(String(item, "license", 20)) ||
            String(item, "status", 12) is not ("community" or "verified" or "outdated" or "withdrawn") ||
            Integer(item, "revision") < 1 || Integer(item, "issue") < 1 ||
            !DateTimeOffset.TryParseExact(String(item, "updated_at", 20), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            throw new FormatException("invalid_publication");
    }

    internal static bool IsLicense(string value) => value is "CC-BY-4.0" or "CC-BY-SA-4.0";

    internal static string? MacroError(string body, string mode) => MacroError(body, mode, MaxMacroLines);

    private static string? MacroError(string body, string mode, int maxLines)
    {
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (body.Contains('\0') || lines.Any(line => line.Contains('\r')))
            return "invalid_body";
        if (mode == "chat" && (lines.Length != 1 || !ChatCommand().IsMatch(body)))
            return "single_chat_required";
        if (lines.Length > maxLines)
            return "macro_lines";
        // Conservative copy limits in UTF-8. They include the command and preserve full-width text.
        if (lines.Any(line => Encoding.UTF8.GetByteCount(line) > (mode == "chat" ? 500 : 180)))
            return mode == "chat" ? "chat_length" : "macro_length";
        return null;
    }

    internal static JsonObject ParseObject(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
        RejectDuplicateKeys(document.RootElement);
        return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject
            ?? throw new FormatException("invalid_json");
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new FormatException("duplicate_key");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
                RejectDuplicateKeys(child);
        }
    }

    internal static string String(JsonObject data, string key, int limit, bool multiline = false)
    {
        if (data[key] is not JsonValue value || !value.TryGetValue<string>(out var result) ||
            result.Length > limit || !ValidText(result, multiline))
            throw new FormatException("invalid_" + key);
        return result;
    }

    internal static int Integer(JsonObject data, string key) =>
        data[key] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result : throw new FormatException("invalid_" + key);

    internal static long Integer64(JsonObject data, string key)
    {
        if (data[key] is JsonValue value)
        {
            if (value.TryGetValue<long>(out var result))
                return result;
            if (value.TryGetValue<int>(out var integer))
                return integer;
        }
        throw new FormatException("invalid_" + key);
    }

    internal static bool Boolean(JsonObject data, string key) =>
        data[key] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result : throw new FormatException("invalid_" + key);

    private static bool ValidText(string text, bool multiline)
    {
        var normalized = multiline ? text.Replace("\r\n", "\n", StringComparison.Ordinal) : text;
        return !normalized.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\t'));
    }

    private static void Values(JsonObject data, string key, int max, Func<string, bool> validate)
    {
        if (data[key] is not JsonArray values || values.Count > max)
            throw new FormatException("invalid_" + key);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in values)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var str) ||
                !ValidText(str, false) || !validate(str) || !seen.Add(str))
                throw new FormatException("invalid_" + key);
        }
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,38})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Login();

    [GeneratedRegex(@"\A/[pa][ \t]+\S", RegexOptions.CultureInvariant)]
    private static partial Regex ChatCommand();
}
