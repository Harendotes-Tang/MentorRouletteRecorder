using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The gate that decides whether a profile-backed Oodle scan may be installed.
///
/// Review finding M-9. Besides the zero-match path, the dangerous cases are a pattern that
/// matches twice and a pattern that matches once at the wrong address. Either installs a
/// mislocated decoder, which corrupts every bundle it touches while looking, from the outside,
/// like a capture that simply decodes nothing. The comparison is therefore asserted directly,
/// one broken condition at a time.
/// </summary>
public sealed class OodleSignatureInstallGateTests : IDisposable
{
    private const string Build = "2026.08.05.0000.0000";
    private const int DeclaredRva = 0x00000010;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.OodleGate", Guid.NewGuid().ToString("N"));

    public OodleSignatureInstallGateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void AScanThatMatchesEverySignatureExactlyOnceAtTheDeclaredRvaIsAccepted()
    {
        var profile = Profile();
        var results = Results(type => new OodleSignatureMatch(type, 1, DeclaredRva));

        Assert.True(OodleSignatureRuntime.ScanMatchesProfile(results, profile));
    }

    [Fact]
    public void AResolvedRvaThatDiffersFromTheProfileIsRefused()
    {
        var profile = Profile();
        var broken = Enum.GetValues<SignatureType>()[0];
        var results = Results(type => new OodleSignatureMatch(
            type, 1, type == broken ? DeclaredRva + 4 : DeclaredRva));

        Assert.False(OodleSignatureRuntime.ScanMatchesProfile(results, profile));
    }

    [Fact]
    public void AnAmbiguousSignatureIsRefused()
    {
        var profile = Profile();
        var ambiguous = Enum.GetValues<SignatureType>()[0];
        var results = Results(type => new OodleSignatureMatch(
            type, type == ambiguous ? 2 : 1, DeclaredRva));

        Assert.False(OodleSignatureRuntime.ScanMatchesProfile(results, profile));
    }

    [Fact]
    public void AMissingSignatureIsRefused()
    {
        var profile = Profile();
        var missing = Enum.GetValues<SignatureType>()[0];
        var results = Results(type => new OodleSignatureMatch(
            type, type == missing ? 0 : 1, type == missing ? null : DeclaredRva));

        Assert.False(OodleSignatureRuntime.ScanMatchesProfile(results, profile));
    }

    [Fact]
    public void AShortResultListIsRefusedEvenWhenEveryEntryItDoesCarryIsPerfect()
    {
        var profile = Profile();
        var results = Results(type => new OodleSignatureMatch(type, 1, DeclaredRva))
            .Skip(1)
            .ToArray();

        Assert.False(OodleSignatureRuntime.ScanMatchesProfile(results, profile));
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

    private static IReadOnlyList<OodleSignatureMatch> Results(
        Func<SignatureType, OodleSignatureMatch> build) =>
        Enum.GetValues<SignatureType>().Select(build).ToArray();

    private OodleSignatureProfile Profile()
    {
        var executable = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(executable, [0x4d, 0x5a]);
        var bytes = File.ReadAllBytes(executable);

        var document = new Dictionary<string, object?>
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = Build,
            ["exe_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["exe_size"] = bytes.LongLength,
            ["generated_at_utc"] = "2026-09-05T00:00:00Z",
            ["source"] = OodleSignatureProfile.FinderSource,
            ["status"] = OodleSignatureProfile.CandidateStatus,
            ["signatures"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                type => (object)(type is SignatureType.OodleMalloc or SignatureType.OodleFree
                    ? "48 8b ff 15"
                    : "48 8b 00 e8")),
            ["resolved_rvas"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                _ => (object)DeclaredRva.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
            ["profile_sha256"] = new string('0', 64),
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        document["profile_sha256"] = ProfileLoader.ComputeProfileHash(
            JsonSerializer.Serialize(document, options));

        var directory = Path.Combine(_root, OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, options), new UTF8Encoding(false));

        return OodleSignatureProfile.TryLoad(path, out var reason)
            ?? throw new InvalidOperationException(reason);
    }
}
