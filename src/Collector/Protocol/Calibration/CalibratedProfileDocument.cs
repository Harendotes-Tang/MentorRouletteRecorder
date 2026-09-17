using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The JSON a calibrated profile is written as, shared by <see cref="LocalProfileWriter"/> and the
/// shared-calibration builder so the two cannot drift apart: one message writer, one evidence
/// entry, one timestamp format, one stamping step. Output of the local writer is pinned byte for
/// byte by its golden tests, so nothing here may change what it produces.
/// </summary>
internal static class CalibratedProfileDocument
{
    /// <summary>How a calibrated profile is laid out on disk.</summary>
    internal static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One message as the profile format spells it.</summary>
    /// <param name="message">Message to write.</param>
    internal static JsonObject Message(ProfileMessage message)
    {
        var fields = new JsonArray();
        foreach (var field in message.Fields)
        {
            fields.Add(Field(field));
        }

        var node = new JsonObject
        {
            ["name"] = message.Name,
            ["opcode"] = message.Opcode,
            ["direction"] = message.Direction == PacketDirection.ServerToClient ? "SERVER_TO_CLIENT" : "CLIENT_TO_SERVER",
        };
        if (message.SegmentType is { } segment) node["segment_type"] = segment;
        if (message.ExpectedLength is { } expected) node["expected_length"] = expected;
        if (message.MinLength is { } min) node["min_length"] = min;
        if (message.MaxLength is { } max) node["max_length"] = max;
        if (message.VictoryValues.Count > 0) node["victory_values"] = new JsonArray(message.VictoryValues.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
        node["fields"] = fields;
        return node;
    }

    /// <summary>The <c>messages</c> array for a list of messages, in order.</summary>
    /// <param name="messages">Messages to write.</param>
    internal static JsonArray Messages(IEnumerable<ProfileMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            array.Add(Message(message));
        }

        return array;
    }

    /// <summary>One <c>provenance.evidence</c> entry.</summary>
    /// <param name="field">Evidence key, e.g. <c>messages.ZONE_INITIALIZATION.opcode</c>.</param>
    /// <param name="method">Evidence method token.</param>
    /// <param name="recorded">Timestamp in the profile format.</param>
    /// <param name="samples">Sample count.</param>
    /// <param name="note">Plain-language note.</param>
    internal static JsonObject Evidence(string field, string method, string recorded, int samples, string note) => new()
    {
        ["field"] = field,
        ["method"] = method,
        ["recorded_at_utc"] = recorded,
        ["sample_count"] = samples,
        ["note"] = note,
    };

    /// <summary>A timestamp as the profile schema requires it: UTC, millisecond precision, trailing Z.</summary>
    /// <param name="at">Instant to render.</param>
    internal static string Timestamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Stamps <c>profile_sha256</c> into a document built by the caller and returns the text to
    /// write. The hash covers the canonical form, so it does not depend on the layout.
    /// </summary>
    /// <param name="document">Document with a placeholder hash; the caller's own, freshly built object.</param>
    internal static string Stamp(JsonObject document)
    {
        using (var parsed = JsonDocument.Parse(document.ToJsonString()))
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(parsed.RootElement);
        }

        return document.ToJsonString(Indented);
    }

    /// <summary>Runs the ordinary loader over profile text as if it were the file at <paramref name="path"/>.</summary>
    /// <param name="path">Path the file would have; its stem and directory take part in the rules.</param>
    /// <param name="json">Profile text.</param>
    internal static ProfileValidationReport Validate(string path, string json)
    {
        using var parsed = JsonDocument.Parse(json);
        return ProfileLoader.Validate(path, parsed.RootElement);
    }

    private static JsonObject Field(ProfileField field)
    {
        var node = new JsonObject
        {
            ["name"] = field.Name,
            ["offset"] = field.Offset,
            ["type"] = field.Type.ToString().ToLowerInvariant(),
        };
        if (field.Type == ProfileFieldType.Bytes) node["length"] = field.Length;
        node["endian"] = field.Endian == ProfileEndian.Little ? "little" : "big";
        if (field.Role == ProfileFieldRole.Selector) node["role"] = "selector";
        var constraints = new JsonObject();
        if (field.Constraints.Min is { } min) constraints["min"] = min;
        if (field.Constraints.Max is { } max) constraints["max"] = max;
        if (field.Constraints.In is { Count: > 0 } values)
        {
            constraints["in"] = new JsonArray(values.Select(v => JsonValue.Create(v)).ToArray<JsonNode?>());
        }

        if (constraints.Count > 0) node["constraints"] = constraints;
        return node;
    }
}
