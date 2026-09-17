using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The Oodle signature profile is the exact-build escape hatch for FfxivTcp decompression, so
/// it is tested on the same fail-closed terms as the protocol profile loader.
/// </summary>
public sealed class OodleSignatureProfileTests : IDisposable
{
    private const string Build = "2026.08.05.0000.0000";
    private const long ProfileExecutableSize = 12345678;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.OodleProfiles", Guid.NewGuid().ToString("N"));

    public OodleSignatureProfileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LoadsAStampedProfile_WithEverySignature()
    {
        var path = WriteProfile("cn.2026.08.05", exeSha256: new string('a', 64));

        var profile = OodleSignatureProfile.TryLoad(path, out var reason);

        Assert.NotNull(profile);
        Assert.Equal(string.Empty, reason);
        Assert.Equal("cn.2026.08.05", profile!.Id);
        Assert.Equal(OodleSignatureProfile.CandidateStatus, profile.Status);
        Assert.Equal(Enum.GetNames<SignatureType>().Length, profile.Signatures.Count);
        Assert.Equal(Enum.GetNames<SignatureType>().Length, profile.ResolvedRvas.Count);
    }

    [Fact]
    public void RefusesAProfileWhoseHashDoesNotMatchItsBody()
    {
        var path = WriteProfile("tampered", exeSha256: new string('b', 64), stamp: false);

        var profile = OodleSignatureProfile.TryLoad(path, out var reason);

        Assert.Null(profile);
        Assert.Contains("profile_sha256", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void FindsOnlyTheProfileWhoseExecutableHashMatches()
    {
        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        WriteProfile("cn.good", directory, new string('c', 64));
        WriteProfile("cn.other", directory, new string('d', 64));

        var match = OodleSignatureProfile.FindForExecutable(
            directory,
            Region.Cn,
            Build,
            new string('c', 64),
            ProfileExecutableSize,
            out var problems);

        Assert.NotNull(match);
        Assert.Empty(problems);
        Assert.Equal("cn.good", match!.Id);
    }

    [Fact]
    public void RefusesDuplicateProfilesClaimingOneExecutable()
    {
        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        WriteProfile("cn.first", directory, new string('e', 64));
        WriteProfile("cn.second", directory, new string('e', 64));

        var match = OodleSignatureProfile.FindForExecutable(
            directory,
            Region.Cn,
            Build,
            new string('e', 64),
            ProfileExecutableSize,
            out var problems);

        Assert.Null(match);
        Assert.Contains(problems, problem => problem.Contains("two or more", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeSelectionUsesTheExactExecutableHash()
    {
        var executable = Path.Combine(_root, "ffxiv_dx11.exe");
        File.WriteAllText(executable, "not the real game, only deterministic bytes", new UTF8Encoding(false));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();

        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        var length = new FileInfo(executable).Length;
        WriteProfile("cn.match", directory, hash, exeSize: length);
        WriteProfile("cn.miss", directory, new string('f', 64), exeSize: length);

        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, Build,
            AllowCandidateOodleSignature: true);
        using var runtime = OodleSignatureRuntime.TryCreate(options, _root);

        Assert.NotNull(runtime);
        Assert.Equal("cn.match", runtime!.Profile.Id);
    }

    [Fact]
    public void NormalCaptureRefusesCandidateProfileWithoutExplicitTraceOptIn()
    {
        var executable = Path.Combine(_root, "ffxiv_dx11.exe");
        File.WriteAllText(executable, "candidate opt-in boundary", new UTF8Encoding(false));
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();
        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        WriteProfile("cn.candidate", directory, hash, exeSize: new FileInfo(executable).Length);

        var options = new CaptureStartOptions(
            "test", 1, null, null, OodleMode.FfxivTcp, null, executable, Region.Cn, Build);
        using var runtime = OodleSignatureRuntime.TryCreate(options, _root);

        Assert.Null(runtime);
    }

    [Fact]
    public void SelectionRefusesWrongBuildEvenWhenExecutableHashMatches()
    {
        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        var hash = new string('1', 64);
        WriteProfile("cn.build", directory, hash);

        var match = OodleSignatureProfile.FindForExecutable(
            directory, Region.Cn, "2026.08.06.0000.0000", hash,
            ProfileExecutableSize, out var problems);

        Assert.Null(match);
        Assert.Empty(problems);
    }

    [Fact]
    public void SelectionRefusesWrongSizeEvenWhenExecutableHashMatches()
    {
        var directory = Path.Combine(_root, ProfileCatalog.OodleSignatureDirectoryName);
        Directory.CreateDirectory(directory);
        var hash = new string('2', 64);
        WriteProfile("cn.size", directory, hash);

        var match = OodleSignatureProfile.FindForExecutable(
            directory, Region.Cn, Build, hash, ProfileExecutableSize + 1, out _);

        Assert.Null(match);
    }

    [Fact]
    public void MissingDirectorySelectsBuiltinByReturningNull()
    {
        var match = OodleSignatureProfile.FindForExecutable(
            Path.Combine(_root, "missing"), Region.Cn, Build, new string('3', 64),
            ProfileExecutableSize, out var problems);

        Assert.Null(match);
        Assert.Empty(problems);
    }

    [Fact]
    public void MachinaPrivateRuntimeContractIsPinnedToTheReferencedPackage()
    {
        Assert.True(OodleSignatureRuntime.ValidateMachinaContract(out var reason), reason);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string WriteProfile(
        string id,
        string? directory = null,
        string? exeSha256 = null,
        bool stamp = true,
        long exeSize = ProfileExecutableSize)
    {
        var targetDirectory = directory ?? Path.Combine(_root, OodleSignatureProfile.DirectoryName);
        Directory.CreateDirectory(targetDirectory);

        var document = new Dictionary<string, object?>
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = Build,
            ["exe_sha256"] = exeSha256 ?? new string('0', 64),
            ["exe_size"] = exeSize,
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
                _ => (object)"00000010"),
            ["profile_sha256"] = new string('0', 64),
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        if (stamp)
        {
            document["profile_sha256"] = ProfileLoader.ComputeProfileHash(json);
            json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }

        var path = Path.Combine(targetDirectory, id + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
