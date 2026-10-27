using System.Security.Cryptography;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;

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
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = OodleSignatureTestFiles.Write(
            Path.Combine(_root, OodleSignatureProfile.DirectoryName),
            Guid.NewGuid().ToString("N"), Build, hash, bytes.LongLength, resolvedRva: DeclaredRva);

        return OodleSignatureProfile.TryLoad(path, out var reason)
            ?? throw new InvalidOperationException(reason);
    }
}
