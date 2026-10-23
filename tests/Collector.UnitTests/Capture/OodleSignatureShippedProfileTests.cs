using System.Text;
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
        new Dictionary<SignatureType, int>
        {
            [SignatureType.OodleNetwork1_Shared_Size] = 0x01E4C930,
            [SignatureType.OodleNetwork1_Shared_SetWindow] = 0x01E4C800,
            [SignatureType.OodleNetwork1UDP_Decode] = 0x01E4A910,
            [SignatureType.OodleNetwork1UDP_State_Size] = 0x01E4AFB0,
            [SignatureType.OodleNetwork1UDP_Encode] = 0x01E445E0,
            [SignatureType.OodleMalloc] = 0x028E69C8,
            [SignatureType.OodleFree] = 0x028E69D0,
            [SignatureType.OodleNetwork1TCP_State_Size] = 0x01E4A7B0,
            [SignatureType.OodleNetwork1TCP_Decode] = 0x01E4A770,
            [SignatureType.OodleNetwork1TCP_Encode] = 0x01E43FD0,
        };

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

    [Fact]
    public void MachinaAssemblyVersionIsTheOnlyOneThisBuildIsWiredFor()
    {
        var version = typeof(FFXIVNetworkMonitor).Assembly.GetName().Version;

        Assert.Equal(OodleSignatureRuntime.SupportedMachinaVersion, version?.ToString());
    }

    [Fact]
    public void MachinaDecodeFailureTraceContractIsPinnedToTheReferencedPackage()
    {
        // decode_error_count is derived from Machina's own debug trace text: 2.4.7.7 reports a
        // failed bundle decompression by writing a line and returning null rather than by
        // raising the message. A reworded string would leave the counter at 0 for ever with
        // every test still green, and docs/live-validation-guide.md section 3.5.3 treats "zero
        // decode errors" as evidence for promoting a signature profile to VERIFIED, so the
        // literals are pinned against the referenced assembly. Managed string literals live in
        // the metadata #US heap as UTF-16 and no reflection API exposes a method body's string
        // constants, hence the search over the file's bytes (review finding H-3).
        var location = typeof(FFXIVNetworkMonitor).Assembly.Location;
        Assert.True(File.Exists(location), location);

        var assembly = File.ReadAllBytes(location);
        foreach (var marker in MachinaCaptureSource.DecodeFailureTraceMarkers)
        {
            Assert.True(
                Contains(assembly, Encoding.Unicode.GetBytes(marker)),
                $"Machina no longer contains the trace text '{marker}'. decode_error_count " +
                "would silently stay at 0; re-derive the markers before upgrading the package.");
        }
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;
}
