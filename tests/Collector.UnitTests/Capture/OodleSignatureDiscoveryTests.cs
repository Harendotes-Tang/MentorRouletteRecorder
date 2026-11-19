using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 2026-11-20. A client no signature profile was written for: the search this machine can do
/// with the patterns it already has, and what it refuses to call found.
/// </summary>
public sealed class OodleSignatureDiscoveryTests
{
    private const SignatureType Subject = SignatureType.OodleNetwork1TCP_Train;

    /// <summary>An image that is nothing but one call site at <paramref name="at"/>.</summary>
    private static byte[] ImageWith(params (int At, string Bytes)[] sites)
    {
        var image = new byte[4096];
        foreach (var (at, bytes) in sites)
        {
            var parts = bytes.Split(' ');
            for (var i = 0; i < parts.Length; i++)
            {
                image[at + i] = Convert.ToByte(parts[i], 16);
            }

            // rel32 -> a target well inside the image.
            image[at + parts.Length] = 0x10;
        }

        return image;
    }

    private static IReadOnlyList<(string, IReadOnlyDictionary<SignatureType, int[]>)> Sources(
        params (string Source, string Pattern)[] patterns) =>
        patterns.Select(item =>
        {
            Assert.True(OodleSignaturePattern.TryParse(item.Pattern, out var parsed, out var problem), problem);
            return (item.Source, (IReadOnlyDictionary<SignatureType, int[]>)new Dictionary<SignatureType, int[]>
            {
                [Subject] = parsed,
            });
        }).ToArray();

    private static OodleSignatureFinding Find(byte[] image, params (string Source, string Pattern)[] patterns) =>
        OodleSignatureDiscovery.Discover(image, Sources(patterns)).Findings.Single(f => f.Type == Subject);

    [Fact]
    public void AKnownPatternThatMatchesOnceIsTakenAsWrittenFromTheFirstSourceThatHasIt()
    {
        var image = ImageWith((0x100, "41 83 fd 01 75 0a 48 8b 0f e8"));

        var finding = Find(image, ("first", "41 83 fc 01 75 ** 48 8b 0f e8"), ("second", "41 83 fd 01 75 ** 48 8b 0f e8"));

        Assert.Equal(OodleSignatureOutcome.Exact, finding.Outcome);
        Assert.Equal("second", finding.Source);
        Assert.Equal(-1, finding.RelaxedIndex);
    }

    /// <summary>The CN case: the compiler chose r12 where the table says r13.</summary>
    [Fact]
    public void OneDifferentRegisterByteIsWildcardedAndReported()
    {
        var image = ImageWith((0x100, "41 83 fc 01 75 0a 48 8b 0f e8"));

        var finding = Find(image, (OodleSignatureDiscovery.BuiltinSource, "41 83 fd 01 75 ** 48 8b 0f e8"));

        Assert.Equal(OodleSignatureOutcome.Relaxed, finding.Outcome);
        Assert.Equal(OodleSignatureDiscovery.BuiltinSource, finding.Source);
        Assert.Equal(2, finding.RelaxedIndex);
        Assert.Equal(0xfc, finding.ActualByte);
        Assert.Equal("41 83 ** 01 75 ** 48 8b 0f e8", OodleSignaturePattern.Render(finding.Pattern!));
    }

    [Theory]
    // Two bytes off is another piece of code, not another register.
    [InlineData("41 83 fc 02 75 0a 48 8b 0f e8")]
    // The call itself is what makes the place a call site.
    [InlineData("41 83 fd 01 75 0a 48 8b 0f e9")]
    // The first byte is never the one that differs.
    [InlineData("40 83 fd 01 75 0a 48 8b 0f e8")]
    public void AnythingFurtherOffIsMissing(string site)
    {
        var finding = Find(ImageWith((0x100, site)), ("table", "41 83 fd 01 75 ** 48 8b 0f e8"));

        Assert.Equal(OodleSignatureOutcome.Missing, finding.Outcome);
        Assert.Null(finding.Pattern);
    }

    [Fact]
    public void TwoPlacesAreNeverChosenBetween()
    {
        var exactTwice = ImageWith(
            (0x100, "41 83 fd 01 75 0a 48 8b 0f e8"), (0x200, "41 83 fd 01 75 0b 48 8b 0f e8"));
        Assert.Equal(OodleSignatureOutcome.Ambiguous,
            Find(exactTwice, ("table", "41 83 fd 01 75 ** 48 8b 0f e8")).Outcome);

        var nearTwice = ImageWith(
            (0x100, "41 83 fc 01 75 0a 48 8b 0f e8"), (0x200, "41 83 fe 01 75 0a 48 8b 0f e8"));
        Assert.Equal(OodleSignatureOutcome.Ambiguous,
            Find(nearTwice, ("table", "41 83 fd 01 75 ** 48 8b 0f e8")).Outcome);
    }

    [Fact]
    public void ACallThatLeadsOutOfTheImageIsNotASite()
    {
        var image = ImageWith((0x100, "41 83 fd 01 75 0a 48 8b 0f e8"));
        // rel32 = 0x7fffff10: far beyond the file.
        image[0x100 + 10 + 3] = 0x7f;

        Assert.Equal(OodleSignatureOutcome.Missing,
            Find(image, ("table", "41 83 fd 01 75 ** 48 8b 0f e8")).Outcome);
    }

    [Fact]
    public void TheResultIsCompleteOnlyWithAllTwelve()
    {
        var image = ImageWith((0x100, "41 83 fd 01 75 0a 48 8b 0f e8"));

        var result = OodleSignatureDiscovery.Discover(image, Sources(("table", "41 83 fd 01 75 ** 48 8b 0f e8")));

        Assert.Equal(Enum.GetValues<SignatureType>().Length, result.Findings.Count);
        Assert.False(result.Complete);
        Assert.Single(result.Patterns);
    }

    /// <summary>
    /// The last resort keeps the code around a site that was found, and for one that was not,
    /// only the few places that come closest - nothing else of the executable.
    /// </summary>
    [Fact]
    public void TheLastResortKeepsOnlyTheSurroundingsOfCallSitesAndNearMisses()
    {
        var found = ImageWith((0x400, "41 83 fd 01 75 0a 48 8b 0f e8"));
        var result = OodleSignatureDiscovery.Discover(found, Sources(("table", "41 83 fd 01 75 ** 48 8b 0f e8")));
        var site = Assert.Single(OodleSignatureDiscovery.Context(
            found, result, Sources(("table", "41 83 fd 01 75 ** 48 8b 0f e8"))));
        Assert.Equal(("site", Subject, 0x400, 0), (site.Kind, site.Type, site.SiteOffset, site.Mismatches));
        Assert.Equal(0x400 - OodleSignatureDiscovery.SiteBytesBefore, site.Offset);
        Assert.Equal(OodleSignatureDiscovery.SiteBytesBefore + OodleSignatureDiscovery.SiteBytesAfter, site.Bytes.Length);

        // Two bytes off: not accepted, but the place a maintainer would look at.
        var missed = ImageWith((0x400, "41 83 fc 02 75 0a 48 8b 0f e8"), (0x800, "41 83 00 00 00 0a 00 8b 0f e8"));
        var sources = Sources(("table", "41 83 fd 01 75 ** 48 8b 0f e8"));
        var candidate = Assert.Single(OodleSignatureDiscovery.Context(
            missed, OodleSignatureDiscovery.Discover(missed, sources), sources));
        Assert.Equal(("candidate", 0x400, 2), (candidate.Kind, candidate.SiteOffset, candidate.Mismatches));
        Assert.Equal(0x400 - OodleSignatureDiscovery.CandidateBytesAround, candidate.Offset);
        Assert.True(candidate.Bytes.Length < 128);
    }

    [Fact]
    public void TheSectionTableOfAnExecutableIsReadAndAnythingElseGivesNone()
    {
        var sqlite = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll"));

        var sections = OodleSignatureRuntime.PeSections(sqlite);

        Assert.NotEmpty(sections);
        Assert.All(sections, section => Assert.True(section.VirtualAddress > 0 && section.RawOffset >= 0));
        Assert.Empty(OodleSignatureRuntime.PeSections(new byte[256]));
        Assert.Empty(OodleSignatureRuntime.PeSections(Array.Empty<byte>()));
    }

    /// <summary>The built-in table is Machina's own: every signature, every pattern parseable.</summary>
    [Fact]
    public void TheBuiltinTableCoversEverySignature()
    {
        var builtin = OodleSignatureDiscovery.KnownSources(directory: null).Single();

        Assert.Equal(OodleSignatureDiscovery.BuiltinSource, builtin.Source);
        Assert.Equal(
            Enum.GetValues<SignatureType>().OrderBy(type => type),
            builtin.Patterns.Keys.OrderBy(type => type));
    }

    [Theory]
    [InlineData("OodleNative_Ffxiv.Initialize: Missing Signature [OodleNetwork1TCP_Train].", "OodleNetwork1TCP_Train")]
    [InlineData("OodleSignatureScan.Read: Missing Signature [OodleFree].", "OodleFree")]
    // Only an enum name is ever written to the log: not a number, not arbitrary text.
    [InlineData("x: Missing Signature [7].", null)]
    [InlineData("x: Missing Signature [C:\\Users\\someone].", null)]
    [InlineData("Found Signature [OodleFree] at offset [00000010]", null)]
    [InlineData("x: Missing Signature [OodleFree", null)]
    public void OnlyAMissingSignaturesOwnNameIsReadFromATraceLine(string trace, string? expected)
    {
        Assert.Equal(expected, MachinaCaptureSource.MissingSignatureName(trace));
    }
}
