using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Strict, field-by-field reading of a request payload.
///
/// Every accessor rejects the wrong JSON type instead of coercing it, reports the offending
/// path in <c>ERR_BAD_REQUEST.field</c>, and refuses any property the contract does not
/// declare. An unknown key usually means the two sides disagree about the contract, and
/// ignoring it would turn a version mismatch into a subtly wrong write.
/// </summary>
public sealed class PayloadReader
{
    private readonly JsonObject _payload;
    private readonly string _path;

    /// <summary>Wraps a payload object.</summary>
    /// <param name="payload">Payload to read.</param>
    /// <param name="path">Field path prefix used in error messages.</param>
    public PayloadReader(JsonObject payload, string path = "payload")
    {
        ArgumentNullException.ThrowIfNull(payload);
        _payload = payload;
        _path = path;
    }

    /// <summary>Property names present in the payload.</summary>
    public IEnumerable<string> Names => _payload.Select(pair => pair.Key);

    /// <summary>True when the payload has no property at all.</summary>
    public bool IsEmpty => _payload.Count == 0;

    /// <summary>True when <paramref name="name"/> is present, even when its value is null.</summary>
    /// <param name="name">Property name.</param>
    public bool Has(string name) => _payload.ContainsKey(name);

    /// <summary>True only for an explicitly supplied JSON null; omission remains distinct.</summary>
    public bool IsNull(string name) => _payload.TryGetPropertyValue(name, out var value) && value is null;

    /// <summary>Rejects any property outside <paramref name="allowed"/>.</summary>
    /// <param name="allowed">Property names the contract declares.</param>
    public PayloadReader RejectUnknown(params string[] allowed)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        foreach (var (key, _) in _payload)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw CollectorException.BadRequest(
                    CollectorTexts.IpcFieldUnknown(key), Field(key));
            }
        }

        return this;
    }

    /// <summary>Requires the payload to be empty, for messages that take no argument.</summary>
    public void RequireEmpty()
    {
        if (_payload.Count != 0)
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNoParameters(), _path);
        }
    }

    /// <summary>Reads a nested object, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    public PayloadReader? Object(string name)
    {
        if (!_payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonObject nested
            ? new PayloadReader(nested, Field(name))
            : throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotObject(name), Field(name));
    }

    /// <summary>Reads a string, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="maxLength">Longest accepted string.</param>
    public string? String(string name, int maxLength = 4000)
    {
        if (!_payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotString(name), Field(name));
        }

        if (text.Length > maxLength)
        {
            throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldTooLong(name, maxLength), Field(name));
        }

        return text;
    }

    /// <summary>Reads a required non-empty string.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="maxLength">Longest accepted string.</param>
    public string RequiredString(string name, int maxLength = 4000) =>
        String(name, maxLength) is { Length: > 0 } text
            ? text
            : throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing(name), Field(name));

    /// <summary>Reads a required UUID.</summary>
    /// <param name="name">Property name.</param>
    public string RequiredUuid(string name)
    {
        var text = RequiredString(name, 64);
        return Guid.TryParseExact(text, "D", out _)
            ? text
            : throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotUuid(name), Field(name));
    }

    /// <summary>Reads an integer, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    public int? Int(string name, int minimum = int.MinValue, int maximum = int.MaxValue)
    {
        var value = Long(name, minimum, maximum);
        return value is null ? null : (int)value.Value;
    }

    /// <summary>Reads a 64-bit integer, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    public long? Long(string name, long minimum = long.MinValue, long maximum = long.MaxValue)
    {
        if (!_payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonValue value || !value.TryGetValue<long>(out var number))
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotInteger(name), Field(name));
        }

        if (number < minimum || number > maximum)
        {
            throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldOutOfRange(name, minimum, maximum), Field(name));
        }

        return number;
    }

    /// <summary>Reads a required integer.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    public int RequiredInt(string name, int minimum = int.MinValue, int maximum = int.MaxValue) =>
        Int(name, minimum, maximum)
            ?? throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing(name), Field(name));

    /// <summary>Reads a boolean, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    public bool? Bool(string name)
    {
        if (!_payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotBoolean(name), Field(name));
    }

    /// <summary>Reads a UTC timestamp, or null when absent or JSON null.</summary>
    /// <param name="name">Property name.</param>
    public DateTimeOffset? Timestamp(string name)
    {
        var text = String(name, 40);
        if (text is null)
        {
            return null;
        }

        return UtcTimestamp.TryParse(text, out var value)
            ? value
            : throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldNotTimestamp(name), Field(name));
    }

    /// <summary>Reads a required UTC timestamp.</summary>
    /// <param name="name">Property name.</param>
    public DateTimeOffset RequiredTimestamp(string name) =>
        Timestamp(name) ?? throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing(name), Field(name));

    /// <summary>Reads an enum token, or null when absent or JSON null.</summary>
    /// <typeparam name="TEnum">Enum type declared in the domain.</typeparam>
    /// <param name="name">Property name.</param>
    public TEnum? Enum<TEnum>(string name)
        where TEnum : struct, Enum
    {
        var text = String(name, 64);
        if (text is null)
        {
            return null;
        }

        return EnumWire<TEnum>.TryParse(text, out var value)
            ? value
            : throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldNotOneOf(name, string.Join(" / ", EnumWire<TEnum>.AllTokens)), Field(name));
    }

    /// <summary>Reads a required enum token.</summary>
    /// <typeparam name="TEnum">Enum type declared in the domain.</typeparam>
    /// <param name="name">Property name.</param>
    public TEnum RequiredEnum<TEnum>(string name)
        where TEnum : struct, Enum =>
        Enum<TEnum>(name) ?? throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing(name), Field(name));

    /// <summary>Reads an array of integers; an absent property yields an empty list.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="maxItems">Largest accepted array.</param>
    /// <param name="minimum">Smallest accepted element.</param>
    public IReadOnlyList<int> IntArray(string name, int maxItems, int minimum = int.MinValue)
    {
        var array = Array(name, maxItems);
        if (array is null)
        {
            return System.Array.Empty<int>();
        }

        var values = new List<int>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<int>(out var number) || number < minimum)
            {
                throw CollectorException.BadRequest(CollectorTexts.IpcFieldIntegersOnly(name), Field(name));
            }

            values.Add(number);
        }

        return values;
    }

    /// <summary>Longest array element accepted when the caller names no other limit.</summary>
    public const int DefaultArrayElementLength = 200;

    /// <summary>
    /// Reads an array of strings; an absent property yields an empty list.
    ///
    /// Both dimensions are bounded: under an element-count cap alone, a hundred elements of a
    /// megabyte each is a legal request stopped only by the frame limit (review finding L4).
    /// </summary>
    /// <param name="name">Property name.</param>
    /// <param name="maxItems">Largest accepted array.</param>
    /// <param name="maxLength">Longest accepted element.</param>
    /// <param name="minLength">Shortest accepted element; 1 refuses empty strings.</param>
    public IReadOnlyList<string> StringArray(
        string name, int maxItems, int maxLength = DefaultArrayElementLength, int minLength = 0)
    {
        var array = Array(name, maxItems);
        if (array is null)
        {
            return System.Array.Empty<string>();
        }

        var values = new List<string>(array.Count);
        foreach (var node in array)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                throw CollectorException.BadRequest(CollectorTexts.IpcFieldStringsOnly(name), Field(name));
            }

            if (text.Length > maxLength)
            {
                throw CollectorException.BadRequest(
                    CollectorTexts.IpcFieldItemTooLong(name, maxLength), Field(name));
            }

            if (text.Length < minLength)
            {
                throw CollectorException.BadRequest(CollectorTexts.IpcFieldUndeclaredValue(name), Field(name));
            }

            values.Add(text);
        }

        return values;
    }

    /// <summary>Reads an array of objects; an absent property yields an empty list.</summary>
    /// <param name="name">Property name.</param>
    /// <param name="maxItems">Largest accepted array.</param>
    public IReadOnlyList<PayloadReader> ObjectArray(string name, int maxItems)
    {
        var array = Array(name, maxItems);
        if (array is null)
        {
            return System.Array.Empty<PayloadReader>();
        }

        var readers = new List<PayloadReader>(array.Count);
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                throw CollectorException.BadRequest(CollectorTexts.IpcFieldObjectsOnly(name), Field(name));
            }

            readers.Add(new PayloadReader(item, $"{Field(name)}[{index}]"));
        }

        return readers;
    }

    /// <summary>Reads an array of enum tokens; an absent property yields an empty list.</summary>
    /// <typeparam name="TEnum">Enum type declared in the domain.</typeparam>
    /// <param name="name">Property name.</param>
    /// <param name="maxItems">Largest accepted array.</param>
    public IReadOnlyList<TEnum> EnumArray<TEnum>(string name, int maxItems)
        where TEnum : struct, Enum
    {
        var values = new List<TEnum>();
        foreach (var text in StringArray(name, maxItems))
        {
            if (!EnumWire<TEnum>.TryParse(text, out var value))
            {
                throw CollectorException.BadRequest(
                    CollectorTexts.IpcFieldNotOneOf(name, string.Join(" / ", EnumWire<TEnum>.AllTokens)), Field(name));
            }

            values.Add(value);
        }

        return values;
    }

    private JsonArray? Array(string name, int maxItems)
    {
        if (!_payload.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        if (node is not JsonArray array)
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotArray(name), Field(name));
        }

        if (array.Count > maxItems)
        {
            throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldTooManyItems(name, maxItems), Field(name));
        }

        return array;
    }

    private string Field(string name) => _path + "." + name;
}
