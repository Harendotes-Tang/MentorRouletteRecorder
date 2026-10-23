using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The share code format: <c>MRC1.</c> + base64url of the
/// raw-DEFLATE canonical JSON payload, identified by the SHA-256 of that canonical payload.
/// The vectors under <c>tests/Fixtures/shared-calibration</c> are produced by the Python
/// validator's own <c>canonical()</c> and Python's zlib, so passing them is the guarantee that
/// the Python port reproduces this format.
/// </summary>
public sealed class ShareCodeTests
{
    private static readonly Lazy<JsonDocument> Vectors = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "shared-calibration", "vectors.json"))));

    private static JsonElement Vector(string list, string name) =>
        Vectors.Value.RootElement.GetProperty(list).EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);

    private static TheoryData<string> Names(string list)
    {
        var data = new TheoryData<string>();
        foreach (var item in Vectors.Value.RootElement.GetProperty(list).EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<string> ValidNames() => Names("valid");

    public static TheoryData<string> InvalidNames() => Names("invalid");

    /// <summary>The canonical text of a valid reply-state payload, for tests that damage it in one place.</summary>
    internal static string ValidCanonical() => ShareCode.Canonical(
        Payload(CalibrationMatchSource.ReplyState, new ShareCodePop(0xC002, SelectorValues: new long[] { 3 }), territory: 0xA108));

    /// <summary>A code around arbitrary inflated bytes: raw DEFLATE and base64url, with nothing checked.</summary>
    /// <param name="inflated">What the code inflates to.</param>
    internal static string RawCode(byte[] inflated)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(inflated);
        }

        return ShareCode.Prefix + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>A code whose payload is valid except that its build is a lone surrogate escape.</summary>
    internal static string IllFormedCode() => RawCode(Encoding.UTF8.GetBytes(
        ValidCanonical().Replace("\"game_build\":\"2026.09.01.0000.0000\"", "\"game_build\":\"\\ud800\"", StringComparison.Ordinal)));

    public static TheoryData<string, string, string> LoneSurrogates() => new()
    {
        // why, canonical text to replace, what replaces it (JSON escapes, not characters); test_sharecode.py mirrors these
        { "a key of the payload", "{\"game_build\"", "{\"\\ud800\":1,\"game_build\"" },
        { "a key inside the pop", "\"pop\":{\"opcode\"", "\"pop\":{\"\\ud800\":1,\"opcode\"" },
        { "a string value", "\"game_build\":\"2026.09.01.0000.0000\"", "\"game_build\":\"\\ud800\"" },
        { "a lone low surrogate", "\"match_source\":\"REPLY_STATE\"", "\"match_source\":\"\\udc00\"" },
        { "a pair in the wrong order", "\"region\":\"CN\"", "\"region\":\"\\udc00\\ud800\"" },
        { "a high surrogate before a letter", "\"region\":\"CN\"", "\"region\":\"\\ud800N\"" },
        { "an array element", "\"selector_values\":[3]", "\"selector_values\":[\"\\ud800\"]" },
        { "the value of a repeated key", "{\"game_build\"", "{\"game_build\":\"\\ud800\",\"game_build\"" },
    };

    /// <summary>
    /// .NET cannot read a key or string holding a lone surrogate, so the whole text is refused before any part
    /// of it is inspected: a pasted code never throws, and the Python port names the same token.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoneSurrogates))]
    public void ALoneSurrogateAnywhereIsRefusedAsJson(string why, string anchor, string replacement)
    {
        var canonical = ValidCanonical();
        Assert.Contains(anchor, canonical, StringComparison.Ordinal);

        var decoded = ShareCode.Decode(RawCode(Encoding.UTF8.GetBytes(canonical.Replace(anchor, replacement, StringComparison.Ordinal))));

        Assert.True(decoded.Rejection?.Code == ShareCodeRejection.Json, why + ": " + (decoded.Rejection?.Code ?? "valid"));
        Assert.Null(decoded.Payload);
        Assert.Null(decoded.CodeSha256);
    }

    public static TheoryData<string, string, byte[]> NotUtf8() => new()
    {
        { "inside a key", "{\"game_build\"", Bytes("{\"", 0xFF, "\":1,\"game_build\"") },
        { "inside a string value", "\"region\":\"CN\"", Bytes("\"region\":\"C", 0xFF, "\"") },
        { "a surrogate written as UTF-8", "\"region\":\"CN\"", Bytes("\"region\":\"", 0xED, 0xA0, 0x80, "\"") },
        { "a truncated sequence", "\"region\":\"CN\"", Bytes("\"region\":\"", 0xE4, 0xB8, "\"") },
    };

    [Theory]
    [MemberData(nameof(NotUtf8))]
    public void BytesThatAreNotUtf8AreRefusedAsJsonWhereverTheyAre(string why, string anchor, byte[] replacement)
    {
        var canonical = ValidCanonical();
        var at = canonical.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(at >= 0, why);
        var inflated = Encoding.UTF8.GetBytes(canonical[..at]).Concat(replacement)
            .Concat(Encoding.UTF8.GetBytes(canonical[(at + anchor.Length)..])).ToArray();

        var decoded = ShareCode.Decode(RawCode(inflated));

        Assert.True(decoded.Rejection?.Code == ShareCodeRejection.Json, why + ": " + (decoded.Rejection?.Code ?? "valid"));
    }

    [Fact]
    public void AWellFormedSurrogatePairIsOrdinaryText()
    {
        var paired = ValidCanonical().Replace("{\"game_build\"", "{\"\\ud83d\\ude00\":1,\"game_build\"", StringComparison.Ordinal);

        Assert.Equal(ShareCodeRejection.UnknownKey, ShareCode.Decode(RawCode(Encoding.UTF8.GetBytes(paired))).Rejection?.Code);
    }

    [Fact]
    public void APayloadHoldingALoneSurrogateIsRefusedByCheckInsteadOfThrowing()
    {
        var payload = Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 64)) with { GameBuild = "2026\ud800" };

        Assert.Equal(ShareCodeRejection.Value, ShareCode.Check(payload)?.Code);
        Assert.Throws<ArgumentException>(() => ShareCode.Encode(payload));
    }

    private static byte[] Bytes(params object[] parts) => parts.SelectMany(part => part switch
    {
        string text => Encoding.UTF8.GetBytes(text),
        int value => new[] { (byte)value },
        _ => throw new ArgumentException("text or a byte", nameof(parts)),
    }).ToArray();

    [Fact]
    public void TheVectorsAreThere()
    {
        Assert.True(Vectors.Value.RootElement.GetProperty("valid").GetArrayLength() >= 5);
        Assert.True(Vectors.Value.RootElement.GetProperty("invalid").GetArrayLength() >= 20);
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void AValidVectorDecodesToItsPayloadCanonicalFormAndHash(string name)
    {
        var vector = Vector("valid", name);
        var canonical = vector.GetProperty("canonical").GetString()!;

        // The C# canonical writer agrees with Python's on the payload itself...
        Assert.Equal(canonical, CanonicalJson.Serialize(vector.GetProperty("payload")));

        // ...and a code compressed by Python decodes to the same payload and identity.
        var decoded = ShareCode.Decode(vector.GetProperty("code").GetString());
        Assert.Null(decoded.Rejection);
        Assert.NotNull(decoded.Payload);
        Assert.Equal(canonical, ShareCode.Canonical(decoded.Payload!));
        Assert.Equal(vector.GetProperty("code_sha256").GetString(), decoded.CodeSha256);
        Assert.Equal(decoded.CodeSha256, ShareCode.Sha256(decoded.Payload!));
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public void EncodingAPayloadRoundTripsToTheSameIdentity(string name)
    {
        var vector = Vector("valid", name);
        var payload = ShareCode.Decode(vector.GetProperty("code").GetString()).Payload!;

        var code = ShareCode.Encode(payload);

        Assert.StartsWith(ShareCode.Prefix, code, StringComparison.Ordinal);
        Assert.True(code.Length <= ShareCode.MaxCodeLength);
        Assert.Matches("^MRC1\\.[A-Za-z0-9_-]+$", code);
        var again = ShareCode.Decode(code);
        Assert.Equal(vector.GetProperty("code_sha256").GetString(), again.CodeSha256);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public void AnInvalidVectorIsRefusedForItsOwnReason(string name)
    {
        var vector = Vector("invalid", name);

        var decoded = ShareCode.Decode(vector.GetProperty("code").GetString());

        Assert.Null(decoded.Payload);
        Assert.Null(decoded.CodeSha256);
        Assert.NotNull(decoded.Rejection);
        Assert.Equal(vector.GetProperty("reason").GetString(), decoded.Rejection!.Code);
        // The message is shown to the player: Chinese text, never an opcode or a hex digit string.
        Assert.False(string.IsNullOrWhiteSpace(decoded.Rejection.Message));
        Assert.DoesNotContain("0x", decoded.Rejection.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("[\\u4e00-\\u9fff]", decoded.Rejection.Message);
    }

    [Fact]
    public void ANullCodeIsEmpty()
    {
        Assert.Equal("E_SHARE_CODE_EMPTY", ShareCode.Decode(null).Rejection!.Code);
    }

    [Fact]
    public void SurroundingWhitespaceFromAPasteIsIgnored()
    {
        var code = Vector("valid", "reply_state").GetProperty("code").GetString()!;

        var decoded = ShareCode.Decode("\r\n  " + code + " \t\n");

        Assert.Null(decoded.Rejection);
        Assert.Equal(Vector("valid", "reply_state").GetProperty("code_sha256").GetString(), decoded.CodeSha256);
    }

    [Fact]
    public void ThePayloadCarriesOnlyTheLearnedValues()
    {
        var payload = ShareCode.Decode(Vector("valid", "reply_state").GetProperty("code").GetString()).Payload!;

        Assert.Equal(Region.Cn, payload.Region);
        Assert.Equal("2026.09.01.0000.0000", payload.GameBuild);
        Assert.Equal("cn.2026.08.05", payload.TemplateProfileId);
        Assert.Equal(CalibrationMatchSource.ReplyState, payload.MatchSource);
        Assert.Equal(49154, payload.Pop.Opcode);
        Assert.Equal(new long[] { 3 }, payload.Pop.SelectorValues);
        Assert.Null(payload.Pop.Length);
        Assert.Null(payload.Pop.RouletteOffset);
        Assert.Equal(41223, payload.ZoneOpcode);
        Assert.Equal((ushort)41224, payload.TerritoryOpcode);
        Assert.Equal((ushort)41225, payload.JobOpcode);
        using var canonical = JsonDocument.Parse(ShareCode.Canonical(payload));
        Assert.Equal(
            new[] { "game_build", "job_opcode", "match_source", "pop", "region", "template_profile_id",
                "template_sha256", "territory_opcode", "v", "zone_opcode" },
            canonical.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    public static TheoryData<string, ShareCodePayload> Unencodable() => new()
    {
        { "queue request without territory", Payload(CalibrationMatchSource.QueueRequest, new ShareCodePop(0xC001)) },
        { "reply state with a length", Payload(CalibrationMatchSource.ReplyState,
            new ShareCodePop(0xC002, Length: 40, SelectorValues: new long[] { 3 }), territory: 0xA108) },
        { "announcement without a length", Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D)) },
        { "marker offset outside the pop", Payload(CalibrationMatchSource.MarkerOffset,
            new ShareCodePop(0xF00D, Length: 24, RouletteOffset: 30)) },
        { "unknown region", Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 64)) with
            { Region = Region.Unknown } },
        { "uppercase template hash", Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 64)) with
            { TemplateSha256 = new string('A', 64) } },
        { "build with a space", Payload(CalibrationMatchSource.Announcement, new ShareCodePop(0xF00D, Length: 64)) with
            { GameBuild = "2026 09" } },
        { "too many selector values", Payload(CalibrationMatchSource.ReplyState,
            new ShareCodePop(0xC002, SelectorValues: Enumerable.Range(0, ShareCode.MaxSelectorValues + 1).Select(i => (long)i).ToArray())) },
    };

    [Theory]
    [MemberData(nameof(Unencodable))]
    public void APayloadTheDecoderWouldRefuseCannotBeEncoded(string why, ShareCodePayload payload)
    {
        var error = Assert.Throws<ArgumentException>(() => ShareCode.Encode(payload));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), why);
    }

    [Fact]
    public void TheLargestPayloadTheFormatAllowsStillFitsTheCodeLimit()
    {
        var payload = Payload(CalibrationMatchSource.ReplyState,
            new ShareCodePop(ushort.MaxValue,
                SelectorValues: Enumerable.Repeat(long.MinValue, ShareCode.MaxSelectorValues).ToArray()),
            territory: ushort.MaxValue, job: ushort.MaxValue) with
        {
            GameBuild = new string('9', 128),
            TemplateProfileId = "a" + new string('9', 63),
        };

        var code = ShareCode.Encode(payload);

        Assert.True(code.Length <= ShareCode.MaxCodeLength);
        Assert.Null(ShareCode.Decode(code).Rejection);
    }

    [Fact]
    public void TwoEncodingsOfOnePayloadShareOneIdentity()
    {
        var payload = Payload(CalibrationMatchSource.MarkerOffset, new ShareCodePop(0xF00D, Length: 24, RouletteOffset: 8));

        var first = ShareCode.Decode(ShareCode.Encode(payload));
        var second = ShareCode.Decode(ShareCode.Encode(payload with { }));

        Assert.Equal(first.CodeSha256, second.CodeSha256);
        Assert.Matches("^[0-9a-f]{64}$", first.CodeSha256!);
    }

    internal static ShareCodePayload Payload(
        CalibrationMatchSource source, ShareCodePop pop, ushort? territory = null, ushort? job = null) => new(
        Region.Cn, "2026.09.01.0000.0000", "cn.template", new string('0', 64), source, pop, 0xA107, territory, job);
}
