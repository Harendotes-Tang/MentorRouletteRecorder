using Machina.FFXIV.Memory;

namespace MentorRecorder.Collector.Capture;

/// <summary>What the discovery settled on for one signature.</summary>
/// <param name="Type">Machina function being located.</param>
/// <param name="Outcome">How the search for it ended.</param>
/// <param name="Source">Where the accepted pattern came from; null when none was accepted.</param>
/// <param name="Pattern">The accepted pattern, already relaxed where it had to be; null when none.</param>
/// <param name="RelaxedIndex">Index of the one literal byte that was turned into a wildcard, or -1.</param>
/// <param name="ActualByte">The byte the executable has at <paramref name="RelaxedIndex"/>, or -1.</param>
/// <param name="Offset">File offset of the accepted call site, or -1.</param>
public sealed record OodleSignatureFinding(
    SignatureType Type,
    OodleSignatureOutcome Outcome,
    string? Source,
    int[]? Pattern,
    int RelaxedIndex = -1,
    int ActualByte = -1,
    int Offset = -1);

/// <summary>
/// A short run of the executable's machine code around one Oodle call site, written to the
/// local log only as a last resort (see <see cref="OodleSignatureDiscovery.Context"/>).
/// </summary>
/// <param name="Type">Signature the excerpt belongs to.</param>
/// <param name="Kind"><c>site</c> for a place that was accepted, <c>candidate</c> for a near miss.</param>
/// <param name="Source">Pattern source the place was found with.</param>
/// <param name="Offset">File offset of the first byte of <paramref name="Bytes"/>.</param>
/// <param name="SiteOffset">File offset of the call site itself.</param>
/// <param name="Mismatches">Literal bytes that differ from the pattern; zero for a site.</param>
/// <param name="Bytes">The excerpt.</param>
public sealed record OodleSignatureExcerpt(
    SignatureType Type,
    string Kind,
    string Source,
    int Offset,
    int SiteOffset,
    int Mismatches,
    byte[] Bytes);

/// <summary>How the search for one signature ended.</summary>
public enum OodleSignatureOutcome
{
    /// <summary>A known pattern matched exactly once as written.</summary>
    Exact,

    /// <summary>A known pattern matched exactly once after one literal byte was wildcarded.</summary>
    Relaxed,

    /// <summary>No known pattern matched, exactly or with one byte relaxed.</summary>
    Missing,

    /// <summary>Patterns matched, but never at one single place.</summary>
    Ambiguous,
}

/// <summary>The outcome for all twelve signatures.</summary>
/// <param name="Findings">One entry per <see cref="SignatureType"/>, in enum order.</param>
public sealed record OodleSignatureDiscoveryResult(IReadOnlyList<OodleSignatureFinding> Findings)
{
    /// <summary>True when every signature has an accepted pattern.</summary>
    public bool Complete => Findings.Count == Enum.GetValues<SignatureType>().Length &&
        Findings.All(finding => finding.Pattern is not null);

    /// <summary>The accepted patterns; meaningful only when <see cref="Complete"/>.</summary>
    public IReadOnlyDictionary<SignatureType, int[]> Patterns => Findings
        .Where(finding => finding.Pattern is not null)
        .ToDictionary(finding => finding.Type, finding => finding.Pattern!);
}

/// <summary>
/// Looks for the twelve Oodle call sites in an executable no signature profile was written for
/// (a region not supported yet, or a build newer than every table).
///
/// 2026-11-20: a Global tester's 新区服采证 died in its first second because Machina's built-in
/// table missed a signature on Global <c>2026.11.03</c>, there was no Global profile to donate
/// patterns, and nothing said which signature it was. Making a profile needed the maintainer
/// to own that region's client. This does the search on the tester's machine instead, from
/// what the Collector already has:
///
/// <list type="number">
/// <item><description>every known pattern for a signature - Machina's built-in one and the one
/// of each shipped VERIFIED profile, whatever its region - is tried as written;</description></item>
/// <item><description>when none matches, each is tried again allowing exactly one literal byte
/// to differ, which is what a compiler choosing another register does to a call site (the CN
/// <c>Train</c> pair: <c>41 83 fd 01</c> became <c>41 83 fc 01</c>). The byte is then wildcarded
/// and the relaxed pattern must still match exactly once.</description></item>
/// </list>
///
/// The result is a guess, so it is used only where a CANDIDATE profile may be used (an evidence
/// round the player started), it goes through the same install gate as a pattern donor (every
/// signature exactly once in the mapped image), and a wrong guess shows as decode errors in the
/// round's own counters. It reads the executable's bytes off disk, like the profile check; no
/// process is opened.
/// </summary>
public static class OodleSignatureDiscovery
{
    /// <summary>Source name of Machina's own table.</summary>
    public const string BuiltinSource = "machina-builtin";

    /// <summary>
    /// Machina.FFXIV's built-in patterns (Memory/SigScan.cs), the same table
    /// tools/oodle-signature-finder starts from.
    /// </summary>
    public static readonly IReadOnlyDictionary<SignatureType, string> BuiltinPatterns =
        new Dictionary<SignatureType, string>
        {
            [SignatureType.OodleNetwork1_Shared_Size] = "48 83 7b ** 00 75 ** b9 11 00 00 00 e8",
            [SignatureType.OodleNetwork1_Shared_SetWindow] =
                "4c 8b 43 ** 41 b9 00 00 10 00 ba ** 00 00 00 48 89 43 ** 48 8b c8 e8",
            [SignatureType.OodleNetwork1UDP_Train] =
                "41 83 fd 01 75 ** 48 8b 0f e8 ** ** ** ** eb ** 48 8b ** ** e8",
            [SignatureType.OodleNetwork1UDP_Decode] = "74 ** 49 8b ca e8 ** ** ** ** eb ** 48 8b 49 ** e8",
            [SignatureType.OodleNetwork1UDP_State_Size] = "48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8",
            [SignatureType.OodleNetwork1UDP_Encode] = "48 83 c7 02 4d 8b c4 48 89 7c ** ** e8",
            [SignatureType.OodleMalloc] = "41 be 00 00 00 40 ba 10 00 00 00 49 8b ce ff 15",
            [SignatureType.OodleFree] = "48 8b cb f3 ab 4d 85 c0 74 ** 49 8b c8 ff 15",
            [SignatureType.OodleNetwork1TCP_State_Size] =
                "48 8b 7f ** 48 85 f6 75 ** 48 89 ** ** e8 ** ** ** ** 4c ** ** e8",
            [SignatureType.OodleNetwork1TCP_Train] = "41 83 fd 01 75 ** 48 8b 0f e8",
            [SignatureType.OodleNetwork1TCP_Decode] = "4c 8b 11 48 89 6c ** ** 4d 85 d2 74 ** 49 8b ca e8",
            [SignatureType.OodleNetwork1TCP_Encode] =
                "48 8b ** 48 8d ** ** ** c6 44 ** ** ** 49 8b ** 48 89 44 ** ** e8",
        };

    /// <summary>The pattern sources for an executable: the built-in table, then every VERIFIED profile.</summary>
    /// <param name="directory">Signature profile directory, or null.</param>
    public static IReadOnlyList<(string Source, IReadOnlyDictionary<SignatureType, int[]> Patterns)> KnownSources(
        string? directory)
    {
        var sources = new List<(string, IReadOnlyDictionary<SignatureType, int[]>)>();
        var builtin = new Dictionary<SignatureType, int[]>();
        foreach (var (type, text) in BuiltinPatterns)
        {
            if (OodleSignaturePattern.TryParse(text, out var pattern, out _))
            {
                builtin[type] = pattern;
            }
        }

        sources.Add((BuiltinSource, builtin));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return sources;
        }

        foreach (var file in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            if (OodleSignatureProfile.TryLoad(file, out _) is { } profile &&
                string.Equals(profile.Status, OodleSignatureProfile.VerifiedStatus, StringComparison.Ordinal))
            {
                sources.Add((profile.Id, profile.Signatures));
            }
        }

        return sources;
    }

    /// <summary>Searches <paramref name="image"/> for every signature.</summary>
    /// <param name="image">Bytes of the executable file.</param>
    /// <param name="sources">Known patterns, most trusted first.</param>
    public static OodleSignatureDiscoveryResult Discover(
        ReadOnlySpan<byte> image,
        IReadOnlyList<(string Source, IReadOnlyDictionary<SignatureType, int[]> Patterns)> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var findings = new List<OodleSignatureFinding>();
        foreach (var type in Enum.GetValues<SignatureType>())
        {
            findings.Add(FindOne(type, image, sources));
        }

        return new OodleSignatureDiscoveryResult(findings);
    }

    private static OodleSignatureFinding FindOne(
        SignatureType type,
        ReadOnlySpan<byte> image,
        IReadOnlyList<(string Source, IReadOnlyDictionary<SignatureType, int[]> Patterns)> sources)
    {
        var ambiguous = false;
        foreach (var (source, patterns) in sources)
        {
            if (!patterns.TryGetValue(type, out var pattern))
            {
                continue;
            }

            var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);
            ambiguous |= hits.Count > 1;
            if (hits.Count == 1 && TargetInside(image, hits[0], pattern.Length))
            {
                return new OodleSignatureFinding(type, OodleSignatureOutcome.Exact, source, pattern, Offset: hits[0]);
            }
        }

        // One literal byte off, at one single place across every known pattern.
        (string Source, int[] Pattern, int Index, int Offset)? near = null;
        foreach (var (source, patterns) in sources)
        {
            if (!patterns.TryGetValue(type, out var pattern))
            {
                continue;
            }

            foreach (var (offset, index) in FindOneByteOff(pattern, image, limit: 2))
            {
                if (near is { } seen && seen.Offset != offset)
                {
                    return new OodleSignatureFinding(type, OodleSignatureOutcome.Ambiguous, null, null);
                }

                near ??= (source, pattern, index, offset);
            }
        }

        if (near is not { } found)
        {
            return new OodleSignatureFinding(
                type, ambiguous ? OodleSignatureOutcome.Ambiguous : OodleSignatureOutcome.Missing, null, null);
        }

        var relaxed = (int[])found.Pattern.Clone();
        relaxed[found.Index] = OodleSignaturePattern.Wildcard;
        var relaxedHits = OodleSignaturePattern.FindAll(relaxed, image, limit: 2);
        if (relaxedHits.Count != 1 || !TargetInside(image, relaxedHits[0], relaxed.Length))
        {
            return new OodleSignatureFinding(type, OodleSignatureOutcome.Ambiguous, null, null);
        }

        return new OodleSignatureFinding(
            type, OodleSignatureOutcome.Relaxed, found.Source, relaxed, found.Index,
            image[found.Offset + found.Index], found.Offset);
    }

    /// <summary>Bytes kept before an accepted call site.</summary>
    public const int SiteBytesBefore = 96;

    /// <summary>Bytes kept from an accepted call site on: the finder derives the two
    /// <c>Train</c> patterns from the instructions that follow <c>TCP_State_Size</c>.</summary>
    public const int SiteBytesAfter = 288;

    /// <summary>Bytes kept on each side of a near miss.</summary>
    public const int CandidateBytesAround = 32;

    /// <summary>Near misses kept per signature.</summary>
    public const int CandidateLimit = 5;

    /// <summary>Most literal bytes a place may differ by and still be called a near miss.</summary>
    public const int CandidateMismatches = 3;

    /// <summary>
    /// The last resort (owner decision 2026-11-20: not to be used lightly). When the search
    /// could not settle all twelve, or settled them and the round then decoded nothing, the
    /// maintainer cannot write the region's profile from pattern names alone, and the only
    /// other way to get further is to ask the tester for yet another run. These excerpts are
    /// what the finder tool would look at on the maintainer's own machine: the code around
    /// each call site that was found, and for each one that was not, the few places that come
    /// closest. They are machine code of the game executable - no packet, no address, no
    /// character - and they are written only in those two cases.
    /// </summary>
    /// <param name="image">Bytes of the executable file.</param>
    /// <param name="result">What <see cref="Discover"/> found.</param>
    /// <param name="sources">The same sources the search used.</param>
    public static IReadOnlyList<OodleSignatureExcerpt> Context(
        ReadOnlySpan<byte> image,
        OodleSignatureDiscoveryResult result,
        IReadOnlyList<(string Source, IReadOnlyDictionary<SignatureType, int[]> Patterns)> sources)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sources);
        var excerpts = new List<OodleSignatureExcerpt>();
        foreach (var finding in result.Findings)
        {
            if (finding.Pattern is not null && finding.Offset >= 0)
            {
                excerpts.Add(Excerpt(
                    image, finding.Type, "site", finding.Source ?? string.Empty, finding.Offset, 0,
                    SiteBytesBefore, SiteBytesAfter));
                continue;
            }

            var near = new List<(int Offset, int Mismatches, string Source, int Length)>();
            foreach (var (source, patterns) in sources)
            {
                if (patterns.TryGetValue(finding.Type, out var pattern))
                {
                    near.AddRange(FindNear(pattern, image, CandidateMismatches, limit: 64)
                        .Select(hit => (hit.Offset, hit.Mismatches, source, pattern.Length)));
                }
            }

            foreach (var hit in near
                         .GroupBy(item => item.Offset)
                         .Select(group => group.OrderBy(item => item.Mismatches).First())
                         .OrderBy(item => item.Mismatches)
                         .ThenBy(item => item.Offset)
                         .Take(CandidateLimit))
            {
                excerpts.Add(Excerpt(
                    image, finding.Type, "candidate", hit.Source, hit.Offset, hit.Mismatches,
                    CandidateBytesAround, hit.Length + sizeof(int) + CandidateBytesAround));
            }
        }

        return excerpts;
    }

    private static OodleSignatureExcerpt Excerpt(
        ReadOnlySpan<byte> image, SignatureType type, string kind, string source, int site, int mismatches,
        int before, int after)
    {
        var start = Math.Max(0, site - before);
        var end = (int)Math.Min(image.Length, (long)site + after);
        return new OodleSignatureExcerpt(type, kind, source, start, site, mismatches, image[start..end].ToArray());
    }

    /// <summary>Places that differ from <paramref name="pattern"/> in at most a few literal bytes.</summary>
    private static List<(int Offset, int Mismatches)> FindNear(
        int[] pattern, ReadOnlySpan<byte> image, int maximumMismatches, int limit)
    {
        var hits = new List<(int, int)>();
        if (pattern.Length < OodleSignaturePattern.MinimumBytes || image.Length < pattern.Length)
        {
            return hits;
        }

        var fixedTail = pattern[^1] == 0x15 && pattern.Length >= 2 && pattern[^2] == 0xff ? 2 : 1;
        var last = image.Length - pattern.Length;
        var first = (byte)pattern[0];
        for (var at = 0; at <= last; at++)
        {
            if (image[at] != first)
            {
                continue;
            }

            var mismatches = 0;
            for (var j = 1; j < pattern.Length && mismatches <= maximumMismatches; j++)
            {
                if (pattern[j] != OodleSignaturePattern.Wildcard && pattern[j] != image[at + j])
                {
                    mismatches = j >= pattern.Length - fixedTail ? maximumMismatches + 1 : mismatches + 1;
                }
            }

            if (mismatches > maximumMismatches)
            {
                continue;
            }

            hits.Add((at, mismatches));
            if (hits.Count >= limit)
            {
                return hits;
            }
        }

        return hits;
    }

    private static bool TargetInside(ReadOnlySpan<byte> image, int site, int patternLength) =>
        OodleSignaturePattern.ResolveRelativeTarget(image, site, patternLength) is { } target &&
        target > 0 && target < image.Length;

    /// <summary>
    /// Offsets where <paramref name="pattern"/> matches except for exactly one literal byte.
    /// The first byte and the call opcode the pattern ends on (<c>e8</c>, or <c>ff 15</c>) must
    /// match as written: the first keeps the scan fast, the last is what makes the place a call.
    /// </summary>
    private static List<(int Offset, int Index)> FindOneByteOff(int[] pattern, ReadOnlySpan<byte> image, int limit)
    {
        var hits = new List<(int, int)>();
        if (pattern.Length < OodleSignaturePattern.MinimumBytes || image.Length < pattern.Length)
        {
            return hits;
        }

        var fixedTail = pattern[^1] == 0x15 && pattern.Length >= 2 && pattern[^2] == 0xff ? 2 : 1;
        var last = image.Length - pattern.Length;
        var first = (byte)pattern[0];
        for (var at = 0; at <= last; at++)
        {
            if (image[at] != first)
            {
                continue;
            }

            var mismatch = -1;
            var matched = true;
            for (var j = 1; j < pattern.Length; j++)
            {
                if (pattern[j] == OodleSignaturePattern.Wildcard || pattern[j] == image[at + j])
                {
                    continue;
                }

                if (mismatch >= 0 || j >= pattern.Length - fixedTail)
                {
                    matched = false;
                    break;
                }

                mismatch = j;
            }

            if (!matched || mismatch < 0)
            {
                continue;
            }

            hits.Add((at, mismatch));
            if (hits.Count >= limit)
            {
                return hits;
            }
        }

        return hits;
    }
}
