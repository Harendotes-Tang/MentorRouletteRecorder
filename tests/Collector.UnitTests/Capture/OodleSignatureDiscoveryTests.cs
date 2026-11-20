using System.Buffers.Binary;
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

    /// <summary>File offset where the executable section of <see cref="ImageWith"/> starts.</summary>
    internal const int CodeStart = 0xc0;

    /// <summary>File offset where its data section (readable, not executable) starts.</summary>
    internal const int DataStart = 0x800;

    /// <summary>RVA of the executable section.</summary>
    internal const int CodeRva = 0x1000;

    /// <summary>RVA of the data section.</summary>
    internal const int DataRva = 0x2000;

    /// <summary>
    /// A minimal PE file of 4096 bytes - an executable section from <see cref="CodeStart"/> and a
    /// data section from <see cref="DataStart"/> - holding nothing but the given call sites, each
    /// followed by a rel32 of 0x10, which leads a little further into the section it is in.
    /// </summary>
    internal static byte[] ImageWith(params (int At, string Bytes)[] sites)
    {
        var image = new byte[4096];
        image[0] = 0x4d;
        image[1] = 0x5a;
        const int pe = 0x40;
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3c, 4), pe);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(pe, 4), 0x00004550);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 6, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(pe + 20, 2), 0);
        void Section(int at, int rva, int rawOffset, int size, uint characteristics)
        {
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 8, 4), size);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 12, 4), rva);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 16, 4), size);
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(at + 20, 4), rawOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 36, 4), characteristics);
        }

        // Code (execute, read) and initialised data (read, write); the table ends at 0xa8.
        Section(pe + 24, CodeRva, CodeStart, DataStart - CodeStart, 0x60000020);
        Section(pe + 64, DataRva, DataStart, image.Length - DataStart, 0xc0000040);
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

    /// <summary>
    /// Audit 2026-11-20, A2-1. Machina runs what the set names inside the Collector, so only code
    /// counts: a match in a section the header does not mark executable - exact or one byte off -
    /// is no call site, and neither is a direct call that leads into data.
    /// </summary>
    [Fact]
    public void OnlyACallInsideExecutableCodeIsASite()
    {
        const string pattern = "41 83 fd 01 75 ** 48 8b 0f e8";
        Assert.Equal(OodleSignatureOutcome.Missing,
            Find(ImageWith((0x900, "41 83 fd 01 75 0a 48 8b 0f e8")), ("table", pattern)).Outcome);
        Assert.Equal(OodleSignatureOutcome.Missing,
            Find(ImageWith((0x900, "41 83 fc 01 75 0a 48 8b 0f e8")), ("table", pattern)).Outcome);

        // The variant in code is found. A copy of it in data still makes it ambiguous: the
        // install gate counts every match in the mapped image.
        Assert.Equal(OodleSignatureOutcome.Relaxed,
            Find(ImageWith((0x100, "41 83 fc 01 75 0a 48 8b 0f e8")), ("table", pattern)).Outcome);
        var both = ImageWith((0x100, "41 83 fc 01 75 0a 48 8b 0f e8"), (0x900, "41 83 fc 01 75 0b 48 8b 0f e8"));
        Assert.Equal(OodleSignatureOutcome.Ambiguous, Find(both, ("table", pattern)).Outcome);

        // A direct call from code into the data section.
        var intoData = ImageWith((0x100, "41 83 fd 01 75 0a 48 8b 0f e8"));
        var next = CodeRva + (0x100 + 10 + 4 - CodeStart);
        BinaryPrimitives.WriteInt32LittleEndian(intoData.AsSpan(0x100 + 10, 4), DataRva + 0x10 - next);
        Assert.Equal(OodleSignatureOutcome.Missing, Find(intoData, ("table", pattern)).Outcome);
    }

    /// <summary>Audit 2026-11-20, A2-1. Two signatures settled on one call: neither is used.</summary>
    [Fact]
    public void TwoSignaturesThatSettleOnTheSameCallAreBothRefused()
    {
        var image = ImageWith((0x100, "41 83 fd 01 75 0a 48 8b 0f e8"));
        Assert.True(OodleSignaturePattern.TryParse("41 83 fd 01 75 ** 48 8b 0f e8", out var train, out _));
        Assert.True(OodleSignaturePattern.TryParse("75 ** 48 8b 0f e8", out var suffix, out _));
        var sources = new[]
        {
            ("table", (IReadOnlyDictionary<SignatureType, int[]>)new Dictionary<SignatureType, int[]>
            {
                [SignatureType.OodleNetwork1TCP_Train] = train,
                [SignatureType.OodleNetwork1UDP_Train] = suffix,
            }),
        };

        var findings = OodleSignatureDiscovery.Discover(image, sources).Findings;

        Assert.All(
            findings.Where(f => f.Type is SignatureType.OodleNetwork1TCP_Train or SignatureType.OodleNetwork1UDP_Train),
            f =>
            {
                Assert.Equal(OodleSignatureOutcome.Ambiguous, f.Outcome);
                Assert.Null(f.Pattern);
            });

        // Either one alone is found.
        Assert.Equal(OodleSignatureOutcome.Exact, Find(image, ("table", "41 83 fd 01 75 ** 48 8b 0f e8")).Outcome);
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
        // One rule for both kinds: from N bytes before the place to M bytes past its call operand.
        Assert.Equal(OodleSignatureDiscovery.CandidateBytesAround + 10 + 4 + OodleSignatureDiscovery.CandidateBytesAround,
            candidate.Bytes.Length);
        Assert.Equal(OodleSignatureDiscovery.MaxExcerptBytes, site.Bytes.Length);
    }

    /// <summary>
    /// Audit 2026-11-20, D2-3. The near misses kept are the closest over the whole file, not the
    /// first places in file order: seventy places three bytes off come before the one two bytes
    /// off, and that one still leads.
    /// </summary>
    [Fact]
    public void TheNearMissesKeptAreTheClosestOverTheWholeFile()
    {
        var sites = Enumerable.Range(0, 70)
            .Select(i => (0x100 + (i * 16), "41 83 00 00 00 0a 48 8b 0f e8"))
            .Append((0x700, "41 83 fc 02 75 0a 48 8b 0f e8"))
            .ToArray();
        var image = ImageWith(sites);
        var sources = Sources(("table", "41 83 fd 01 75 ** 48 8b 0f e8"));

        var candidates = OodleSignatureDiscovery.Context(image, OodleSignatureDiscovery.Discover(image, sources), sources);

        Assert.Equal(OodleSignatureDiscovery.CandidateLimit, candidates.Count);
        Assert.Equal((0x700, 2), (candidates[0].SiteOffset, candidates[0].Mismatches));
        // Among equals the earlier place comes first.
        Assert.Equal(
            new[] { 0x100, 0x110, 0x120, 0x130 },
            candidates.Skip(1).Select(candidate => candidate.SiteOffset));
        Assert.All(candidates.Skip(1), candidate => Assert.Equal(3, candidate.Mismatches));
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
