using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// The one canonical byte form of a profile document, used to compute and verify
/// <c>profile_sha256</c>.
///
/// The grammar is deliberately narrow so that this writer and the one in
/// <c>tools/protocol-profile-validator/validate.py</c> cannot drift apart: object keys are
/// sorted with an ordinal comparison, there is no whitespace, every number must be an
/// integer, and strings use the short escapes plus <c>\u00xx</c> for the remaining control
/// characters and nothing else. A profile carrying a fractional number is rejected rather
/// than rounded, because a hash that depends on a formatting choice is not a hash.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Writes one element in canonical form.</summary>
    /// <param name="element">Element to write.</param>
    public static string Serialize(JsonElement element)
    {
        var builder = new StringBuilder(1024);
        Write(builder, element);
        return builder.ToString();
    }

    /// <summary>
    /// Canonical form of <paramref name="document"/> with the named top-level properties
    /// removed. Used to hash a profile without its own hash field.
    /// </summary>
    /// <param name="document">Document root; must be an object.</param>
    /// <param name="excludedProperties">Top-level property names to leave out.</param>
    public static string SerializeWithout(JsonElement document, params string[] excludedProperties)
    {
        ArgumentNullException.ThrowIfNull(excludedProperties);
        if (document.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("a profile document must be a JSON object");
        }

        var excluded = new HashSet<string>(excludedProperties, StringComparer.Ordinal);
        var builder = new StringBuilder(1024);
        WriteObject(builder, document, excluded);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(builder, element, null);
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    Write(builder, item);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(builder, element.GetString() ?? string.Empty);
                break;

            case JsonValueKind.Number:
                if (!element.TryGetInt64(out var number))
                {
                    throw new InvalidDataException(
                        "a protocol profile may not contain a non-integer number");
                }

                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                throw new InvalidDataException("unsupported JSON node in a protocol profile");
        }
    }

    private static void WriteObject(StringBuilder builder, JsonElement element, HashSet<string>? excluded)
    {
        var properties = new List<JsonProperty>();
        foreach (var property in element.EnumerateObject())
        {
            if (excluded is null || !excluded.Contains(property.Name))
            {
                properties.Add(property);
            }
        }

        properties.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        builder.Append('{');
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            WriteString(builder, properties[i].Name);
            builder.Append(':');
            Write(builder, properties[i].Value);
        }

        builder.Append('}');
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }
}
