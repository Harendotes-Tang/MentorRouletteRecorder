using Machina.FFXIV;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The signature profiles this build ships, and the two upstream contracts they depend on.
///
/// <c>protocol-profiles/oodle-signatures/cn.2026.08.05.json</c> is copied to the test output
/// and must be loaded and asserted here (review finding M-7). Otherwise a hand edit or a bad
/// merge can break its hash, move an RVA or promote its status with every test still green,
/// and the only symptom on a user's machine is that capture does not start.
/// </summary>
public sealed class OodleSignatureShippedProfileTests
{
    /// <summary>
    /// The ten offsets <see cref="OodleSignaturePatternTests"/> pins against Machina's own
    /// built-in table. The two <c>*_Train</c> entries are not here on purpose: they are this
    /// project's own derivation and have no upstream value to be compared with.
    /// </summary>
    private static readonly IReadOnlyDictionary<SignatureType, int> KnownMachinaOffsets =
        OodleSignaturePatternTests.Known.ToDictionary(pair => pair.Key, pair => pair.Value.Target);

    private static string SignatureDirectory =>
        Path.Combine(AppContext.BaseDirectory, ProfileCatalog.DirectoryName, OodleSignatureProfile.DirectoryName);

    [Fact]
    public void EveryShippedSignatureProfileLoadsAndIsSelfConsistent()
    {
        var files = Directory.GetFiles(SignatureDirectory, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var profile = OodleSignatureProfile.TryLoad(file, out var reason);
            Assert.True(profile is not null, $"{Path.GetFileName(file)} was refused: {reason}");

            // The loader recomputes the canonical hash; equality with the declared one is
            // what makes an edited profile impossible to load unnoticed.
            Assert.Equal(64, profile!.ProfileSha256.Length);
            Assert.Equal(Enum.GetValues<SignatureType>().Length, profile.Signatures.Count);
            Assert.Equal(Enum.GetValues<SignatureType>().Length, profile.ResolvedRvas.Count);
            Assert.Contains(
                profile.Status,
                new[] { OodleSignatureProfile.CandidateStatus, OodleSignatureProfile.VerifiedStatus });
        }
    }

    [Fact]
    public void TheCnProfileDeclaresTheVerifiedStatusAndTheKnownMachinaOffsets()
    {
        var path = Path.Combine(SignatureDirectory, "cn.2026.08.05.json");
        Assert.True(File.Exists(path), path);

        var profile = OodleSignatureProfile.TryLoad(path, out var reason);
        Assert.True(profile is not null, reason);

        // Promoting a profile to VERIFIED is a claim about a live session, so the claim is
        // pinned here rather than left to the next editor of the file.
        Assert.Equal(OodleSignatureProfile.VerifiedStatus, profile!.Status);
        Assert.Equal(Region.Cn, EnumWire<Region>.Parse(profile.Region));
        Assert.Equal("2026.08.05.0000.0000", profile.GameBuild);
        Assert.Equal(51952384, profile.ExeSize);
        Assert.Equal(
            "e06704e3fa9c3bd43a0c8d238945163d7be14c5700cc4d350618b8ec1dd4e1a6", profile.ExeSha256);

        foreach (var (type, expected) in KnownMachinaOffsets)
        {
            Assert.True(profile.ResolvedRvas.TryGetValue(type, out var actual), type.ToString());
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// The GLOBAL profile was not made by the finder tool: its patterns and call targets are what
    /// a tester's 1.9.1-beta.2 found on GLOBAL 2026.11.03 and then decoded 41,965 messages with,
    /// none failing. The file says where it came from, and the claim of that session is pinned
    /// here like the CN one. Only the digest of the evidence trace is kept in the repository:
    /// the trace holds the tester's timeline.
    /// </summary>
    [Fact]
    public void TheGlobalProfileIsVerifiedAndSaysItWasFoundOnAPlayersMachine()
    {
        var path = Path.Combine(SignatureDirectory, "global.2026.11.03.json");
        Assert.True(File.Exists(path), path);

        var profile = OodleSignatureProfile.TryLoad(path, out var reason);
        Assert.True(profile is not null, reason);

        Assert.Equal(OodleSignatureProfile.VerifiedStatus, profile!.Status);
        Assert.Equal(OodleSignatureProfile.DiscoverySource, profile.Source);
        Assert.Equal(OodleSignatureProfile.ReproductionUnchecked, profile.Reproduction);
        Assert.Equal(Region.Global, EnumWire<Region>.Parse(profile.Region));
        Assert.Equal("2026.11.03.0000.0000", profile.GameBuild);
        Assert.Equal(51963136, profile.ExeSize);
        Assert.Equal(
            "ceb4260b2574476acf72e7b6105d0a4f8d1bd78b4e7cab5d8115c453f2f39339", profile.ExeSha256);
        Assert.Equal(0x01E58AE0, profile.ResolvedRvas[SignatureType.OodleNetwork1TCP_Train]);
        Assert.Equal(0x01E59780, profile.ResolvedRvas[SignatureType.OodleNetwork1UDP_Train]);
    }

    [Fact]
    public void MachinaAssemblyVersionIsTheOnlyOneThisBuildIsWiredFor()
    {
        var version = typeof(FFXIVNetworkMonitor).Assembly.GetName().Version;

        Assert.Equal(OodleSignatureRuntime.SupportedMachinaVersion, version?.ToString());
    }

}
