using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// The share code format: <c>MRC1.</c> + base64url (RFC 4648 §5, no padding) of a raw DEFLATE
/// stream (RFC 1951) holding the payload as canonical JSON.
///
/// Canonical JSON is exactly <see cref="CanonicalJson"/>, which is the grammar of
/// <c>tools/protocol-profile-validator/validate.py</c>'s <c>canonical()</c>: sorted keys, no
/// whitespace, integers only. The identity of a code, <c>code_sha256</c>, is the SHA-256 of that
/// canonical text, not of the code: DEFLATE has many valid encodings of one input, so two
/// compressors produce different codes for one payload and both are the same code.
///
/// Decoding is strict and says why it refused (<see cref="ShareCodeRejection"/>): the inflated
/// text must be the canonical form of the payload it parses to, so one payload has one text and
/// nothing that is not part of the format can ride along. Limits: <see cref="MaxCodeLength"/>
/// characters of code after trimming surrounding whitespace, <see cref="MaxInflatedBytes"/> bytes
/// once inflated. Pure: no IO, no clock, no network.
/// </summary>
public static class ShareCode
{
    /// <summary>Prefix of every code this build writes and reads.</summary>
    public const string Prefix = "MRC1.";

    /// <summary>Payload layout version (<c>v</c>).</summary>
    public const int PayloadVersion = 1;

    /// <summary>Longest code text accepted, in characters, after trimming surrounding whitespace.</summary>
    public const int MaxCodeLength = 4096;

    /// <summary>Largest inflated payload accepted, in bytes.</summary>
    public const int MaxInflatedBytes = 16 * 1024;

    /// <summary>Most selector values a pop may carry (a profile message declares at most 32 fields).</summary>
    public const int MaxSelectorValues = 32;

    private static readonly Regex OtherVersion = new(@"^MRC[0-9]+\.", RegexOptions.CultureInvariant);
    private static readonly Regex BuildPattern = new(@"^[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ProfileIdPattern = new(@"^[a-z0-9][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> TopKeys = new(StringComparer.Ordinal)
    {
        "v", "region", "game_build", "template_profile_id", "template_sha256", "match_source", "pop",
        "zone_opcode", "territory_opcode", "job_opcode",
    };

    private static readonly string[] RequiredKeys =
    {
        "v", "region", "game_build", "template_profile_id", "template_sha256", "match_source", "pop", "zone_opcode",
    };

    private static readonly HashSet<string> PopKeys = new(StringComparer.Ordinal)
    {
        "opcode", "length", "roulette_offset", "selector_values",
    };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>The canonical JSON text of a payload: what is compressed and what is hashed.</summary>
    /// <param name="payload">Payload to render.</param>
    public static string Canonical(ShareCodePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var pop = new JsonObject { ["opcode"] = (int)payload.Pop.Opcode };
        if (payload.Pop.Length is { } length) pop["length"] = length;
        if (payload.Pop.RouletteOffset is { } offset) pop["roulette_offset"] = offset;
        if (payload.Pop.SelectorValues is { } values)
        {
            pop["selector_values"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }

        var node = new JsonObject
        {
            ["v"] = PayloadVersion,
            ["region"] = EnumWire<Region>.Format(payload.Region),
            ["game_build"] = payload.GameBuild,
            ["template_profile_id"] = payload.TemplateProfileId,
            ["template_sha256"] = payload.TemplateSha256,
            ["match_source"] = EnumWire<CalibrationMatchSource>.Format(payload.MatchSource),
            ["pop"] = pop,
            ["zone_opcode"] = (int)payload.ZoneOpcode,
        };
        if (payload.TerritoryOpcode is { } territory) node["territory_opcode"] = (int)territory;
        if (payload.JobOpcode is { } job) node["job_opcode"] = (int)job;

        using var document = JsonDocument.Parse(node.ToJsonString());
        return CanonicalJson.Serialize(document.RootElement);
    }

    /// <summary><c>code_sha256</c>: lowercase hex SHA-256 of the canonical payload.</summary>
    /// <param name="payload">Payload to identify.</param>
    public static string Sha256(ShareCodePayload payload) => Hash(Encoding.UTF8.GetBytes(Canonical(payload)));

    /// <summary>
    /// Why this payload could not be encoded, or null when it can: the very checks a decoder
    /// applies, so nothing that encodes can fail to decode.
    /// </summary>
    /// <param name="payload">Payload to check.</param>
    public static ShareCodeRejection? Check(ShareCodePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        string canonical;
        try
        {
            canonical = Canonical(payload);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "match_source");
        }

        using var document = JsonDocument.Parse(canonical);
        return Read(document.RootElement, out _);
    }

    /// <summary>Encodes a payload. Throws <see cref="ArgumentException"/> when a decoder would refuse it.</summary>
    /// <param name="payload">Payload to encode.</param>
    public static string Encode(ShareCodePayload payload)
    {
        if (Check(payload) is { } rejection)
        {
            throw new ArgumentException(
                "the payload cannot be a share code (" + rejection.Code + " " + rejection.Detail + ")", nameof(payload));
        }

        var bytes = Encoding.UTF8.GetBytes(Canonical(payload));
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(bytes);
        }

        var code = Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return code.Length <= MaxCodeLength
            ? code
            : throw new ArgumentException("the payload does not fit the share code length limit", nameof(payload));
    }

    /// <summary>Decodes a code, never throwing for anything a player could paste.</summary>
    /// <param name="text">Code text; surrounding whitespace is ignored.</param>
    public static ShareCodeDecodeResult Decode(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Refuse(ShareCodeRejection.Empty, "no text");
        }

        if (trimmed.Length > MaxCodeLength)
        {
            return Refuse(ShareCodeRejection.TooLong, "code text over " + MaxCodeLength + " characters");
        }

        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return OtherVersion.IsMatch(trimmed)
                ? Refuse(ShareCodeRejection.Version, "prefix is not " + Prefix)
                : Refuse(ShareCodeRejection.NotACode, "missing prefix");
        }

        var body = trimmed[Prefix.Length..];
        if (body.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return Refuse(ShareCodeRejection.Characters, "body is not base64url");
        }

        if (FromBase64Url(body) is not { } raw)
        {
            return Refuse(ShareCodeRejection.Base64, "body has an impossible base64 length");
        }

        if (Inflate(raw, out var inflated) is { } inflateRejection)
        {
            return new ShareCodeDecodeResult(null, null, inflateRejection);
        }

        // Text that is not UTF-8 or holds a lone surrogate escape is not JSON a key or string can be read from.
        if (!WellFormedJson.TryParse(inflated, ParseOptions, out var document))
        {
            return Refuse(ShareCodeRejection.Json, "not JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Refuse(ShareCodeRejection.Json, "root is not an object");
            }

            if (DuplicateKey(root) is { } duplicate)
            {
                return Refuse(ShareCodeRejection.DuplicateKey, duplicate);
            }

            if (Read(root, out var payload) is { } rejection)
            {
                return new ShareCodeDecodeResult(null, null, rejection);
            }

            var canonical = Encoding.UTF8.GetBytes(Canonical(payload!));
            return inflated.AsSpan().SequenceEqual(canonical)
                ? new ShareCodeDecodeResult(payload, Hash(canonical), null)
                : Refuse(ShareCodeRejection.NotCanonical, "payload text is not canonical JSON");
        }
    }

    private static ShareCodeDecodeResult Refuse(string code, string detail) =>
        new(null, null, ShareCodeRejection.For(code, detail));

    private static string Hash(byte[] canonical) => Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();

    private static byte[]? FromBase64Url(string body)
    {
        if (body.Length == 0 || body.Length % 4 == 1)
        {
            return null;
        }

        var padded = body.Replace('-', '+').Replace('_', '/') + new string('=', (4 - body.Length % 4) % 4);
        var buffer = new byte[padded.Length / 4 * 3];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? buffer[..written] : null;
    }

    private static ShareCodeRejection? Inflate(byte[] raw, out byte[] inflated)
    {
        inflated = Array.Empty<byte>();
        var buffer = new byte[MaxInflatedBytes + 1];
        var total = 0;
        try
        {
            using var input = new MemoryStream(raw, writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            while (total < buffer.Length)
            {
                var read = deflate.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return ShareCodeRejection.For(ShareCodeRejection.Compression, "not a DEFLATE stream");
        }

        if (total > MaxInflatedBytes)
        {
            return ShareCodeRejection.For(ShareCodeRejection.InflatedTooLong, "inflates past " + MaxInflatedBytes + " bytes");
        }

        inflated = buffer[..total];
        return null;
    }

    private static string? DuplicateKey(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        return property.Name;
                    }

                    if (DuplicateKey(property.Value) is { } nested)
                    {
                        return property.Name + "." + nested;
                    }
                }

                return null;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(DuplicateKey).FirstOrDefault(name => name is not null);
            default:
                return null;
        }
    }

    /// <summary>Reads and validates a payload object; the version first, so a newer layout is named as one.</summary>
    private static ShareCodeRejection? Read(JsonElement root, out ShareCodePayload? payload)
    {
        payload = null;
        if (!root.TryGetProperty("v", out var versionElement))
        {
            return ShareCodeRejection.For(ShareCodeRejection.MissingKey, "v");
        }

        if (!TryInteger(versionElement, out var version))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "v");
        }

        if (version != PayloadVersion)
        {
            return ShareCodeRejection.For(ShareCodeRejection.PayloadVersion, "v");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!TopKeys.Contains(property.Name))
            {
                return ShareCodeRejection.For(ShareCodeRejection.UnknownKey, property.Name);
            }
        }

        if (RequiredKeys.FirstOrDefault(key => !root.TryGetProperty(key, out _)) is { } missing)
        {
            return ShareCodeRejection.For(ShareCodeRejection.MissingKey, missing);
        }

        if (!EnumWire<Region>.TryParse(Text(root, "region"), out var region) || region is not (Region.Cn or Region.Global))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "region");
        }

        var build = Text(root, "game_build");
        var templateId = Text(root, "template_profile_id");
        var templateSha = Text(root, "template_sha256");
        if (build is null || !BuildPattern.IsMatch(build))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "game_build");
        }

        if (templateId is null || !ProfileIdPattern.IsMatch(templateId))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "template_profile_id");
        }

        if (templateSha is null || !ShaPattern.IsMatch(templateSha))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "template_sha256");
        }

        if (!EnumWire<CalibrationMatchSource>.TryParse(Text(root, "match_source"), out var source))
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "match_source");
        }

        if (Opcode(root, "zone_opcode", required: true, out var zone) is { } zoneRejection)
        {
            return zoneRejection;
        }

        if (Opcode(root, "territory_opcode", required: false, out var territory) is { } territoryRejection)
        {
            return territoryRejection;
        }

        if (Opcode(root, "job_opcode", required: false, out var job) is { } jobRejection)
        {
            return jobRejection;
        }

        if (ReadPop(root.GetProperty("pop"), source, out var pop) is { } popRejection)
        {
            return popRejection;
        }

        if (CalibratedShape.RequiresTerritory(source) && territory is null)
        {
            return ShareCodeRejection.For(ShareCodeRejection.TerritoryRequired, "territory_opcode");
        }

        payload = new ShareCodePayload(region, build, templateId, templateSha, source, pop!, zone!.Value, territory, job);
        return null;
    }

    private static ShareCodeRejection? ReadPop(JsonElement element, CalibrationMatchSource source, out ShareCodePop? pop)
    {
        pop = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, "pop");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!PopKeys.Contains(property.Name))
            {
                return ShareCodeRejection.For(ShareCodeRejection.UnknownKey, "pop." + property.Name);
            }
        }

        var inputs = CalibratedShape.InputsFor(source);
        foreach (var (key, input) in new[]
        {
            ("opcode", CalibratedPopInputs.Opcode),
            ("length", CalibratedPopInputs.Length),
            ("roulette_offset", CalibratedPopInputs.RouletteOffset),
            ("selector_values", CalibratedPopInputs.Selectors),
        })
        {
            var present = element.TryGetProperty(key, out _);
            if (present && !inputs.HasFlag(input))
            {
                return ShareCodeRejection.For(ShareCodeRejection.PopKeyForbidden, "pop." + key);
            }

            if (!present && inputs.HasFlag(input))
            {
                return ShareCodeRejection.For(ShareCodeRejection.PopKeyMissing, "pop." + key);
            }
        }

        if (Opcode(element, "opcode", required: true, out var opcode) is { } opcodeRejection)
        {
            return opcodeRejection;
        }

        int? length = null;
        if (element.TryGetProperty("length", out var lengthElement))
        {
            if (!TryInteger(lengthElement, out var value) || value is < 1 or > ushort.MaxValue)
            {
                return ShareCodeRejection.For(ShareCodeRejection.Value, "pop.length");
            }

            length = (int)value;
        }

        int? offset = null;
        if (element.TryGetProperty("roulette_offset", out var offsetElement))
        {
            if (!TryInteger(offsetElement, out var value) || value < 0 || length is null || value >= length)
            {
                return ShareCodeRejection.For(ShareCodeRejection.Value, "pop.roulette_offset");
            }

            offset = (int)value;
        }

        long[]? selectors = null;
        if (element.TryGetProperty("selector_values", out var selectorElement))
        {
            if (selectorElement.ValueKind != JsonValueKind.Array || selectorElement.GetArrayLength() > MaxSelectorValues)
            {
                return ShareCodeRejection.For(ShareCodeRejection.Value, "pop.selector_values");
            }

            selectors = new long[selectorElement.GetArrayLength()];
            var index = 0;
            foreach (var item in selectorElement.EnumerateArray())
            {
                if (!TryInteger(item, out selectors[index++]))
                {
                    return ShareCodeRejection.For(ShareCodeRejection.Value, "pop.selector_values");
                }
            }
        }

        pop = new ShareCodePop(opcode!.Value, length, offset, selectors);
        return null;
    }

    private static ShareCodeRejection? Opcode(JsonElement parent, string key, bool required, out ushort? opcode)
    {
        opcode = null;
        if (!parent.TryGetProperty(key, out var element))
        {
            return required ? ShareCodeRejection.For(ShareCodeRejection.MissingKey, key) : null;
        }

        if (!TryInteger(element, out var value) || value is < 0 or > ushort.MaxValue)
        {
            return ShareCodeRejection.For(ShareCodeRejection.Value, key);
        }

        opcode = (ushort)value;
        return null;
    }

    private static bool TryInteger(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value);
    }

    private static string? Text(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
