using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Checks the byte grammar and the exact offsets reported by the CN finder run.</summary>
public sealed class OodleSignaturePatternTests
{
    private static readonly IReadOnlyDictionary<SignatureType, (string Pattern, int Target)> Known =
        new Dictionary<SignatureType, (string, int)>
        {
            [SignatureType.OodleNetwork1_Shared_Size] =
                ("48 83 7b ** 00 75 ** b9 11 00 00 00 e8", 0x01E4C930),
            [SignatureType.OodleNetwork1_Shared_SetWindow] =
                ("4c 8b 43 ** 41 b9 00 00 10 00 ba ** 00 00 00 48 89 43 ** 48 8b c8 e8", 0x01E4C800),
            [SignatureType.OodleNetwork1UDP_Decode] =
                ("74 ** 49 8b ca e8 ** ** ** ** eb ** 48 8b 49 ** e8", 0x01E4A910),
            [SignatureType.OodleNetwork1UDP_State_Size] =
                ("48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8", 0x01E4AFB0),
            [SignatureType.OodleNetwork1UDP_Encode] =
                ("48 83 c7 02 4d 8b c4 48 89 7c ** ** e8", 0x01E445E0),
            [SignatureType.OodleMalloc] =
                ("41 be 00 00 00 40 ba 10 00 00 00 49 8b ce ff 15", 0x028E69C8),
            [SignatureType.OodleFree] =
                ("48 8b cb f3 ab 4d 85 c0 74 ?? 49 8b c8 ff 15", 0x028E69D0),
            [SignatureType.OodleNetwork1TCP_State_Size] =
                ("48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8 ** ** ** ** 4c ** ** e8", 0x01E4A7B0),
            [SignatureType.OodleNetwork1TCP_Decode] =
                ("4c 8b 11 48 89 6c ** ** 4d 85 d2 74 ** 49 8b ca e8", 0x01E4A770),
            [SignatureType.OodleNetwork1TCP_Encode] =
                ("48 8b ** 48 8d ** ** ** c6 44 ** ** ** 49 8b ** 48 89 44 ** ** e8", 0x01E43FD0),
        };

    [Fact]
    public void ParserAcceptsBothWildcardSpellings()
    {
        Assert.True(OodleSignaturePattern.TryParse(
            "48 ** ?? e8", out var pattern, out var problem), problem);

        Assert.Equal(new[] { 0x48, -1, -1, 0xe8 }, pattern);
    }

    [Fact]
    public void MatcherReportsAmbiguityInsteadOfChoosingTheFirstHit()
    {
        Assert.True(OodleSignaturePattern.TryParse(
            "48 8b ** e8", out var pattern, out var problem), problem);
        var image = new byte[]
        {
            0x48, 0x8b, 0x01, 0xe8, 0, 0, 0, 0,
            0x48, 0x8b, 0x02, 0xe8, 0, 0, 0, 0,
        };

        var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);

        Assert.Equal(new[] { 0, 8 }, hits);
    }

    [Fact]
    public void SyntheticMappedImageReproducesTheTenKnownMachinaOffsets()
    {
        var image = Enumerable.Repeat((byte)0xcc, 4096).ToArray();
        var sites = new Dictionary<SignatureType, int>
        {
            [SignatureType.OodleNetwork1_Shared_Size] = 64,
            [SignatureType.OodleNetwork1_Shared_SetWindow] = 320,
            [SignatureType.OodleNetwork1UDP_Decode] = 640,
            [SignatureType.OodleNetwork1UDP_State_Size] = 960,
            [SignatureType.OodleNetwork1TCP_State_Size] = 960,
            [SignatureType.OodleNetwork1UDP_Encode] = 1280,
            [SignatureType.OodleMalloc] = 1600,
            [SignatureType.OodleFree] = 1920,
            [SignatureType.OodleNetwork1TCP_Decode] = 2240,
            [SignatureType.OodleNetwork1TCP_Encode] = 2560,
        };

        // The longer TCP-state pattern contains the shorter UDP-state pattern at the same
        // call site, exactly as it does in the real image. Plant it first, then give the
        // embedded first call its own rel32 target.
        var tcpState = Parse(Known[SignatureType.OodleNetwork1TCP_State_Size].Pattern);
        Plant(image, sites[SignatureType.OodleNetwork1TCP_State_Size], tcpState,
            Known[SignatureType.OodleNetwork1TCP_State_Size].Target);
        var udpState = Parse(Known[SignatureType.OodleNetwork1UDP_State_Size].Pattern);
        WriteRelativeTarget(image, sites[SignatureType.OodleNetwork1UDP_State_Size], udpState.Length,
            Known[SignatureType.OodleNetwork1UDP_State_Size].Target);

        foreach (var (type, evidence) in Known)
        {
            if (type is SignatureType.OodleNetwork1TCP_State_Size or
                SignatureType.OodleNetwork1UDP_State_Size)
            {
                continue;
            }

            Plant(image, sites[type], Parse(evidence.Pattern), evidence.Target);
        }

        foreach (var (type, evidence) in Known)
        {
            var pattern = Parse(evidence.Pattern);
            var hit = Assert.Single(OodleSignaturePattern.FindAll(pattern, image));
            Assert.Equal(sites[type], hit);
            Assert.Equal(
                evidence.Target,
                OodleSignaturePattern.ResolveRelativeTarget(image, hit, pattern.Length));
        }
    }

    [Theory]
    [InlineData(@"Copied from D:\APPS\FF14\最终幻想XIV\game\ffxiv_dx11.exe.")]
    [InlineData(@"Copied from E:\Square Enix\game\ffxiv_dx11.EXE.")]
    [InlineData(@"Copied from D:\John's Games\game\ffxiv_dx11.exe.")]
    [InlineData(@"Copied from \\server\share\FF XIV\game\ffxiv_dx11.exe.")]
    [InlineData("Copied from F:/Games/FFXIV/game/ffxiv_dx11.exe.")]
    public void SanitizerRemovesTheWholeGameInstallPath(string message)
    {
        var sanitized = RotatingFileLogger.Sanitize(message);

        Assert.Equal("Copied from [game-dir].", sanitized);
        Assert.DoesNotContain("ffxiv_dx11", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", sanitized, StringComparison.Ordinal);
    }

    private static int[] Parse(string text)
    {
        Assert.True(OodleSignaturePattern.TryParse(text, out var pattern, out var problem), problem);
        return pattern;
    }

    private static void Plant(byte[] image, int site, int[] pattern, int target)
    {
        for (var index = 0; index < pattern.Length; index++)
        {
            image[site + index] = pattern[index] < 0
                ? (byte)(0x30 + index % 0x40)
                : (byte)pattern[index];
        }

        WriteRelativeTarget(image, site, pattern.Length, target);
    }

    private static void WriteRelativeTarget(byte[] image, int site, int patternLength, int target)
    {
        var operand = site + patternLength;
        var relative = checked(target - (operand + sizeof(int)));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            image.AsSpan(operand, sizeof(int)), relative);
    }
}
