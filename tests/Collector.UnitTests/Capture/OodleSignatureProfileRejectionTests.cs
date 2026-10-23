using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Every fail-closed gate in <see cref="OodleSignatureProfile.TryLoad"/>, one mutation each.
///
/// The loader has around twenty distinct refusal reasons and only one of them was covered
/// (review finding M-8). This profile decides which bytes are executed as the Oodle
/// decompressor's entry points, so a gate that stops refusing does not fail loudly - it
/// accepts a wrong profile. Each case starts from a document known to load and breaks one
/// thing at a time, so a gate that disappears takes a named test with it.
/// </summary>
public sealed class OodleSignatureProfileRejectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.OodleReject", Guid.NewGuid().ToString("N"));

    public OodleSignatureProfileRejectionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TheUnmutatedDocumentLoads()
    {
        var profile = OodleSignatureProfile.TryLoad(Write(Valid()), out var reason);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    // Root field set.
    [InlineData("extra-root-field", "unexpected or duplicate root field")]
    [InlineData("missing-root-field", "required root fields are missing")]
    // Scalars.
    [InlineData("schema-version", "schema_version must be")]
    [InlineData("region-unknown", "region must be CN or GLOBAL")]
    [InlineData("exe-hash-short", "exe_sha256 must be 64 hexadecimal characters")]
    [InlineData("exe-hash-not-hex", "exe_sha256 must be 64 hexadecimal characters")]
    [InlineData("exe-size-zero", "exe_size must be positive")]
    [InlineData("source-foreign", "source must be")]
    [InlineData("generated-at-unparsable", "generated_at_utc must be a UTC timestamp")]
    [InlineData("status-unknown", "status must be")]
    [InlineData("reproduction-unknown", "reproduction must be")]
    [InlineData("reproduction-not-text", "reproduction must be")]
    [InlineData("wrong-type", "a required field is missing or has the wrong type")]
    // Hash.
    [InlineData("hash-mismatch", "profile_sha256 does not match the document")]
    // Signature table.
    [InlineData("signatures-not-object", "signatures must be an object")]
    [InlineData("signatures-extra-entry", "unexpected or duplicate entry")]
    [InlineData("signatures-missing-entry", "exactly one entry for every SignatureType")]
    [InlineData("signatures-no-call-tail", "does not end at a resolvable call operand")]
    [InlineData("signatures-unparsable", "signatures.")]
    // Resolved RVA table.
    [InlineData("rvas-not-object", "resolved_rvas must be an object")]
    [InlineData("rvas-short-hex", "must be eight hex digits")]
    [InlineData("rvas-zero", "must be eight hex digits")]
    [InlineData("rvas-missing-entry", "exactly one entry for every SignatureType")]
    [InlineData("rvas-extra-entry", "invalid entry")]
    public void EachMutationIsRefusedWithItsOwnReason(string mutation, string expected)
    {
        var document = Valid();
        Mutate(document, mutation);

        var profile = OodleSignatureProfile.TryLoad(Write(document, restamp: mutation != "hash-mismatch"), out var reason);

        Assert.Null(profile);
        Assert.Contains(expected, reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OodleSignatureProfile.ReproductionChecked)]
    [InlineData(OodleSignatureProfile.ReproductionUnchecked)]
    public void ARecordedReproductionCheckIsReadBackVerbatim(string reproduction)
    {
        var document = Valid();
        document["reproduction"] = reproduction;

        var profile = OodleSignatureProfile.TryLoad(Write(document), out var reason);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(reproduction, profile!.Reproduction);
    }

    [Fact]
    public void AProfileWrittenBeforeTheFinderRecordedReproductionStillLoads()
    {
        var profile = OodleSignatureProfile.TryLoad(Write(Valid()), out var reason);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, reason);
        Assert.Null(profile!.Reproduction);
    }

    [Fact]
    public void AFileThatIsNotJsonIsRefusedRatherThanThrown()
    {
        var path = Path.Combine(_root, "broken.json");
        File.WriteAllText(path, "{not json", new UTF8Encoding(false));

        Assert.Null(OodleSignatureProfile.TryLoad(path, out var reason));
        Assert.NotEqual(string.Empty, reason);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Mutate(JsonObject document, string mutation)
    {
        var signatures = document["signatures"]!.AsObject();
        var rvas = document["resolved_rvas"]!.AsObject();
        var firstSignature = Enum.GetNames<SignatureType>()[0];

        switch (mutation)
        {
            case "extra-root-field": document["comment"] = "not in the schema"; break;
            case "missing-root-field": document.Remove("game_build"); break;
            case "schema-version": document["schema_version"] = 2; break;
            case "region-unknown": document["region"] = "UNKNOWN"; break;
            case "exe-hash-short": document["exe_sha256"] = new string('a', 63); break;
            case "exe-hash-not-hex": document["exe_sha256"] = new string('z', 64); break;
            case "exe-size-zero": document["exe_size"] = 0; break;
            case "source-foreign": document["source"] = "somebody-elses-tool"; break;
            case "generated-at-unparsable": document["generated_at_utc"] = "not a timestamp"; break;
            case "status-unknown": document["status"] = "TRUSTED"; break;
            case "reproduction-unknown": document["reproduction"] = "verified"; break;
            case "reproduction-not-text": document["reproduction"] = true; break;
            case "wrong-type": document["game_build"] = 20260805; break;
            case "hash-mismatch": document["profile_sha256"] = new string('b', 64); break;
            case "signatures-not-object": document["signatures"] = "48 8b 00 e8"; break;
            case "signatures-extra-entry": signatures["NotASignature"] = "48 8b 00 e8"; break;
            case "signatures-missing-entry": signatures.Remove(firstSignature); break;
            case "signatures-no-call-tail": signatures[firstSignature] = "48 8b 00 90"; break;
            case "signatures-unparsable": signatures[firstSignature] = "48 zz 00 e8"; break;
            case "rvas-not-object": document["resolved_rvas"] = "00000010"; break;
            case "rvas-short-hex": rvas[firstSignature] = "0010"; break;
            case "rvas-zero": rvas[firstSignature] = "00000000"; break;
            case "rvas-missing-entry": rvas.Remove(firstSignature); break;
            case "rvas-extra-entry": rvas["NotASignature"] = "00000010"; break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unknown mutation");
        }
    }

    private static JsonObject Valid()
    {
        var signatures = new JsonObject();
        var rvas = new JsonObject();
        foreach (var type in Enum.GetValues<SignatureType>())
        {
            signatures[type.ToString()] =
                type is SignatureType.OodleMalloc or SignatureType.OodleFree
                    ? "48 8b ff 15"
                    : "48 8b 00 e8";
            rvas[type.ToString()] = "00000010";
        }

        return new JsonObject
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = "2026.08.05.0000.0000",
            ["exe_sha256"] = new string('a', 64),
            ["exe_size"] = 12345678,
            ["generated_at_utc"] = "2026-09-05T00:00:00Z",
            ["source"] = OodleSignatureProfile.FinderSource,
            ["status"] = OodleSignatureProfile.CandidateStatus,
            ["signatures"] = signatures,
            ["resolved_rvas"] = rvas,
            ["profile_sha256"] = new string('0', 64),
        };
    }

    private string Write(JsonObject document, bool restamp = true)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        if (restamp)
        {
            document["profile_sha256"] = new string('0', 64);
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(
                document.ToJsonString(options));
        }

        var path = Path.Combine(_root, "cn.mutation." + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, document.ToJsonString(options), new UTF8Encoding(false));
        return path;
    }
}
