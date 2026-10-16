using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Tests;

/// <summary>
/// Renders a <c>$defs/TextRef</c> into its Simplified Chinese with contracts/collector-texts.json, the
/// way a Desktop that knows the key does in zh-CN (contracts/collector-texts.md §3). A reference is
/// parallel to its Chinese field exactly when this rendering equals that field, which is what the
/// wire tests assert: the key exists, every argument is there with the kind the catalogue declares,
/// and the sentence is byte for byte the one an older Desktop shows.
/// </summary>
internal static class CollectorTextCatalogue
{
    private static readonly Lazy<JsonObject> Texts = new(Load);

    /// <summary>The Chinese a reference renders to.</summary>
    /// <param name="reference">A <c>$defs/TextRef</c>.</param>
    public static string Render(JsonNode? reference)
    {
        var node = Assert.IsType<JsonObject>(reference);
        var key = node["key"]!.GetValue<string>();
        var entry = Texts.Value[key] ?? throw new Xunit.Sdk.XunitException("the catalogue has no key " + key);
        var template = entry is JsonValue plain ? plain.GetValue<string>() : entry["zh"]!.GetValue<string>();
        var kinds = entry is JsonObject declared ? declared["args"] as JsonObject : null;
        var args = node["args"] as JsonObject;
        Assert.Equal(kinds?.Count ?? 0, args?.Count ?? 0);

        var builder = new StringBuilder();
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open);
            builder.Append(template, index, open - index);
            var name = template.Substring(open + 1, close - open - 1);
            var kind = kinds![name]!.GetValue<string>();
            var value = args![name] ?? throw new Xunit.Sdk.XunitException(key + ": no value for {" + name + "}");
            builder.Append(kind switch
            {
                "number" => value.GetValue<long>().ToString(CultureInfo.InvariantCulture),
                "clock" => DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture)
                    .ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
                "text_ref" => Render(value),
                _ => value.GetValue<string>(),
            });
            index = close + 1;
        }

        return builder.ToString();
    }

    /// <summary>Asserts that a reference stands for exactly this Chinese; both absent counts too.</summary>
    /// <param name="chinese">The legacy Chinese field, or null.</param>
    /// <param name="reference">Its companion <c>*_ref</c> field, or null.</param>
    public static void AssertParallel(JsonNode? chinese, JsonNode? reference)
    {
        if (chinese is null)
        {
            Assert.Null(reference);
            return;
        }

        Assert.Equal(chinese.GetValue<string>(), Render(reference));
    }

    /// <summary>Asserts that a <c>$defs/TextRefList</c> is as long as its Chinese list and in the same order.</summary>
    /// <param name="chinese">The Chinese list.</param>
    /// <param name="references">Its companion <c>*_refs</c> list.</param>
    public static void AssertParallelList(JsonNode? chinese, JsonNode? references)
    {
        var texts = Assert.IsType<JsonArray>(chinese);
        var refs = Assert.IsType<JsonArray>(references);
        Assert.Equal(texts.Count, refs.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            AssertParallel(texts[i], refs[i]);
        }
    }

    private static JsonObject Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "contracts", "collector-texts.json");
            if (File.Exists(path))
            {
                return JsonNode.Parse(File.ReadAllText(path))!["texts"]!.AsObject();
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("contracts/collector-texts.json not found above " + AppContext.BaseDirectory);
    }
}
