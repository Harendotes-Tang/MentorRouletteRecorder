using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// A deliberately small JSON Schema validator: enough of draft 2020-12 to enforce
/// <c>protocol-profiles/profile.schema.json</c> and nothing more.
///
/// Supported keywords: <c>$ref</c> (local pointers only), <c>type</c>, <c>enum</c>,
/// <c>const</c>, <c>required</c>, <c>properties</c>, <c>additionalProperties</c>,
/// <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>minimum</c>, <c>maximum</c>,
/// <c>minLength</c>, <c>maxLength</c> and <c>pattern</c>. An unsupported keyword is ignored
/// rather than silently treated as satisfied by something else, and the schema itself is
/// covered by a test that asserts it uses only these keywords.
///
/// A dedicated implementation is used instead of a package because the profile schema is a
/// hard security boundary: everything the parser is willing to read out of a byte buffer
/// comes through here, so the checking code stays inside this repository and inside its
/// licence.
/// </summary>
public static class JsonSchemaValidator
{
    private const int MaxPatternMilliseconds = 200;

    /// <summary>Every keyword this validator understands.</summary>
    public static IReadOnlyList<string> SupportedKeywords { get; } = new[]
    {
        "$schema", "$id", "$defs", "$ref", "title", "description",
        "type", "enum", "const", "required", "properties", "additionalProperties",
        "items", "minItems", "maxItems", "minimum", "maximum", "minLength", "maxLength", "pattern",
    };

    /// <summary>Validates <paramref name="instance"/> and returns the problems found.</summary>
    /// <param name="instance">Document to check.</param>
    /// <param name="schema">Schema root.</param>
    public static IReadOnlyList<string> Validate(JsonElement instance, JsonElement schema)
    {
        var errors = new List<string>();
        Check(instance, schema, schema, "$", errors);
        return errors;
    }

    private static void Check(
        JsonElement node, JsonElement schema, JsonElement root, string path, List<string> errors)
    {
        if (errors.Count > 200)
        {
            return;
        }

        if (schema.TryGetProperty("$ref", out var reference))
        {
            if (Resolve(root, reference.GetString()) is not { } target)
            {
                errors.Add($"{path}: unresolved $ref {reference.GetString()}");
                return;
            }

            Check(node, target, root, path, errors);
            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && !ContainsValue(allowed, node))
        {
            errors.Add($"{path}: value is not one of the allowed values");
        }

        if (schema.TryGetProperty("const", out var constant) && !SameValue(constant, node))
        {
            errors.Add($"{path}: value is not the required constant");
        }

        if (schema.TryGetProperty("type", out var types) && !MatchesType(node, types))
        {
            errors.Add($"{path}: value has the wrong type");
            return;
        }

        switch (node.ValueKind)
        {
            case JsonValueKind.Number:
                CheckNumber(node, schema, path, errors);
                break;
            case JsonValueKind.String:
                CheckString(node, schema, path, errors);
                break;
            case JsonValueKind.Array:
                CheckArray(node, schema, root, path, errors);
                break;
            case JsonValueKind.Object:
                CheckObject(node, schema, root, path, errors);
                break;
            default:
                break;
        }
    }

    private static void CheckNumber(JsonElement node, JsonElement schema, string path, List<string> errors)
    {
        if (!node.TryGetDecimal(out var value))
        {
            errors.Add($"{path}: number is out of range");
            return;
        }

        if (schema.TryGetProperty("minimum", out var minimum) && value < minimum.GetDecimal())
        {
            errors.Add($"{path}: value is below the minimum");
        }

        if (schema.TryGetProperty("maximum", out var maximum) && value > maximum.GetDecimal())
        {
            errors.Add($"{path}: value is above the maximum");
        }
    }

    private static void CheckString(JsonElement node, JsonElement schema, string path, List<string> errors)
    {
        var text = node.GetString() ?? string.Empty;
        if (schema.TryGetProperty("minLength", out var minLength) && text.Length < minLength.GetInt32())
        {
            errors.Add($"{path}: string is too short");
        }

        if (schema.TryGetProperty("maxLength", out var maxLength) && text.Length > maxLength.GetInt32())
        {
            errors.Add($"{path}: string is too long");
        }

        if (!schema.TryGetProperty("pattern", out var pattern))
        {
            return;
        }

        try
        {
            var regex = new Regex(
                pattern.GetString() ?? string.Empty,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(MaxPatternMilliseconds));
            if (!regex.IsMatch(text))
            {
                errors.Add($"{path}: string does not match the required pattern");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            errors.Add($"{path}: pattern could not be evaluated");
        }
    }

    private static void CheckArray(
        JsonElement node, JsonElement schema, JsonElement root, string path, List<string> errors)
    {
        var length = node.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minItems) && length < minItems.GetInt32())
        {
            errors.Add($"{path}: array has too few items");
        }

        if (schema.TryGetProperty("maxItems", out var maxItems) && length > maxItems.GetInt32())
        {
            errors.Add($"{path}: array has too many items");
        }

        if (!schema.TryGetProperty("items", out var items))
        {
            return;
        }

        var index = 0;
        foreach (var item in node.EnumerateArray())
        {
            Check(item, items, root, string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]"), errors);
            index++;
        }
    }

    private static void CheckObject(
        JsonElement node, JsonElement schema, JsonElement root, string path, List<string> errors)
    {
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray())
            {
                var key = name.GetString();
                if (key is not null && !node.TryGetProperty(key, out _))
                {
                    errors.Add($"{path}: missing required property '{key}'");
                }
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties);
        var additionalAllowed =
            !schema.TryGetProperty("additionalProperties", out var additional) ||
            additional.ValueKind != JsonValueKind.False;

        foreach (var property in node.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Check(property.Value, propertySchema, root, path + "." + property.Name, errors);
            }
            else if (!additionalAllowed)
            {
                errors.Add($"{path}: unexpected property '{property.Name}'");
            }
        }
    }

    private static JsonElement? Resolve(JsonElement root, string? pointer)
    {
        if (pointer is null || !pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var segment in pointer[2..].Split('/'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment.Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal), out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool MatchesType(JsonElement node, JsonElement types)
    {
        if (types.ValueKind == JsonValueKind.String)
        {
            return MatchesType(node, types.GetString());
        }

        foreach (var name in types.EnumerateArray())
        {
            if (MatchesType(node, name.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesType(JsonElement node, string? name) => name switch
    {
        "null" => node.ValueKind == JsonValueKind.Null,
        "boolean" => node.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "integer" => node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out _),
        "number" => node.ValueKind == JsonValueKind.Number,
        "string" => node.ValueKind == JsonValueKind.String,
        "array" => node.ValueKind == JsonValueKind.Array,
        "object" => node.ValueKind == JsonValueKind.Object,
        _ => false,
    };

    private static bool ContainsValue(JsonElement allowed, JsonElement node)
    {
        foreach (var candidate in allowed.EnumerateArray())
        {
            if (SameValue(candidate, node))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameValue(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => string.Equals(
                CanonicalJson.Serialize(left), CanonicalJson.Serialize(right), StringComparison.Ordinal),
        };
    }
}
