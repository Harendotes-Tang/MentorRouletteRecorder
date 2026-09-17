using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// One Oodle signature profile: the twelve call-site byte patterns Machina needs in order to
/// find the Oodle functions inside <em>one exact</em> game executable.
///
/// Machina.FFXIV locates the Oodle network functions by scanning the game image for the byte
/// pattern of each call site and following the trailing <c>e8 rel32</c>.
/// Its built-in table is written against the international client, and a regional build whose
/// compiler picked a different register silently loses a signature -- at which point Machina
/// refuses to decompress <em>anything</em>. On CN <c>2026.08.05.0000.0000</c> ten of the
/// twelve match and both <c>Train</c> entries miss, because the guard
/// <c>cmp r13d, 1</c> is <c>cmp r12d, 1</c> in that build.
///
/// A profile is evidence, never a guess (docs/privacy-boundary.md rule 12). Three gates, and
/// a failure at any of them refuses the whole file rather than using part of it:
///
/// <list type="number">
/// <item><description><c>profile_sha256</c> must equal the canonical hash of the document
/// without that field, computed exactly as <see cref="ProfileLoader.ComputeProfileHash(JsonElement)"/>
/// does for protocol profiles.</description></item>
/// <item><description>Every one of Machina's <see cref="SignatureType"/> names must be
/// present, and every pattern must parse and start with a literal byte.</description></item>
/// <item><description><c>exe_sha256</c> must equal the SHA-256 of the game executable that is
/// actually about to be scanned. A profile for another build is not "close enough"; it is
/// refused and the built-in table is used instead.</description></item>
/// </list>
///
/// Reading the executable's bytes off disk is the same boundary the Oodle decision already
/// draws (docs/privacy-boundary.md section 4.1, <c>DEC-OODLE-01</c>): a file is read, no
/// process is opened and no process memory is touched.
/// </summary>
public sealed class OodleSignatureProfile
{
    /// <summary>Directory, relative to the profile root, that signature profiles live in.</summary>
    public const string DirectoryName = "oodle-signatures";

    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Status of a profile that has not yet decoded a real session.</summary>
    public const string CandidateStatus = "CANDIDATE";

    /// <summary>Status of a profile confirmed by a real session with zero decode errors.</summary>
    public const string VerifiedStatus = "VERIFIED";

    /// <summary>Only provenance accepted by schema version 1.</summary>
    public const string FinderSource = "tools/oodle-signature-finder";

    /// <summary>The finder reproduced Machina's own offsets for this executable.</summary>
    public const string ReproductionChecked = "checked";

    /// <summary>The finder had no known-good reference to compare against.</summary>
    public const string ReproductionUnchecked = "unchecked";

    private static readonly HashSet<string> RequiredRootFields = new(StringComparer.Ordinal)
    {
        "schema_version",
        "region",
        "game_build",
        "exe_sha256",
        "exe_size",
        "generated_at_utc",
        "source",
        "signatures",
        "resolved_rvas",
        "profile_sha256",
        "status",
    };

    /// <summary>
    /// Fields a newer finder may add without changing <c>schema_version</c>. Profiles emitted
    /// before the finder recorded its reproduction check simply lack the field.
    /// </summary>
    private static readonly HashSet<string> OptionalRootFields = new(StringComparer.Ordinal)
    {
        "reproduction",
    };

    private OodleSignatureProfile(
        string path,
        string region,
        string gameBuild,
        string exeSha256,
        long exeSize,
        DateTimeOffset generatedAtUtc,
        string status,
        string profileSha256,
        string? reproduction,
        IReadOnlyDictionary<SignatureType, int[]> signatures,
        IReadOnlyDictionary<SignatureType, int> resolvedRvas)
    {
        Path = path;
        Reproduction = reproduction;
        Region = region;
        GameBuild = gameBuild;
        ExeSha256 = exeSha256;
        ExeSize = exeSize;
        GeneratedAtUtc = generatedAtUtc;
        Status = status;
        ProfileSha256 = profileSha256;
        Signatures = signatures;
        ResolvedRvas = resolvedRvas;
    }

    /// <summary>Absolute path the profile was read from.</summary>
    public string Path { get; }

    /// <summary>Region token the profile was written for, for diagnostics only.</summary>
    public string Region { get; }

    /// <summary>Client build the profile was written for, for diagnostics only.</summary>
    public string GameBuild { get; }

    /// <summary>SHA-256 of the executable the patterns were derived from, lowercase hex.</summary>
    public string ExeSha256 { get; }

    /// <summary>Size in bytes of the executable the patterns were derived from.</summary>
    public long ExeSize { get; }

    /// <summary>When the finder emitted this evidence, normalized to UTC.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary><c>CANDIDATE</c> or <c>VERIFIED</c>.</summary>
    public string Status { get; }

    /// <summary>Canonical hash of the document without its own hash field.</summary>
    public string ProfileSha256 { get; }

    /// <summary>
    /// Whether the finder's known-good reproduction check ran when this profile was emitted:
    /// <see cref="ReproductionChecked"/>, <see cref="ReproductionUnchecked"/>, or null for a
    /// profile written before the finder recorded it. Evidence only; never a gate.
    /// </summary>
    public string? Reproduction { get; }

    /// <summary>The parsed patterns, one per <see cref="SignatureType"/>.</summary>
    public IReadOnlyDictionary<SignatureType, int[]> Signatures { get; }

    /// <summary>Finder-resolved RVAs. Runtime scanning does not trust these values.</summary>
    public IReadOnlyDictionary<SignatureType, int> ResolvedRvas { get; }

    /// <summary>Short identifier used in log lines, for example <c>cn.2026.08.05</c>.</summary>
    public string Id => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>The signature directory under a profile root, or null when there is none.</summary>
    /// <param name="profileRoot">The <c>protocol-profiles</c> directory, or null.</param>
    public static string? DirectoryUnder(string? profileRoot)
    {
        if (string.IsNullOrWhiteSpace(profileRoot))
        {
            return null;
        }

        var directory = System.IO.Path.Combine(profileRoot, DirectoryName);
        return Directory.Exists(directory) ? directory : null;
    }

    /// <summary>
    /// The newest VERIFIED profile of a region, used as a pattern donor when no profile
    /// matches the running executable: its wildcard patterns are re-scanned against the new
    /// image, its resolved RVAs are ignored. Two profiles tied on the same build refuse each
    /// other, like every other selection in this project.
    /// </summary>
    /// <param name="directory">Signature profile directory.</param>
    /// <param name="region">Region of the running client.</param>
    public static OodleSignatureProfile? FindPatternDonor(string? directory, Region region)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var candidates = new List<OodleSignatureProfile>();
        foreach (var file in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var profile = TryLoad(file, out _);
            if (profile is not null &&
                string.Equals(profile.Region, EnumWire<Region>.Format(region), StringComparison.Ordinal) &&
                string.Equals(profile.Status, VerifiedStatus, StringComparison.Ordinal))
            {
                candidates.Add(profile);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = candidates.OrderByDescending(profile => profile.GameBuild, StringComparer.Ordinal).ToArray();
        var best = ordered[0];
        return ordered.Count(profile => string.Equals(profile.GameBuild, best.GameBuild, StringComparison.Ordinal)) == 1
            ? best
            : null;
    }

    /// <summary>
    /// Checks a donor's patterns against the bytes of an executable file: every signature
    /// must hit exactly once and resolve to a target inside the file. A file offset is not a
    /// mapped RVA, so this is a plausibility gate before the real scan at install time, not
    /// the scan itself.
    /// </summary>
    /// <param name="executablePath">Executable to check.</param>
    /// <param name="problem">Why the donor cannot be used, when it cannot.</param>
    public bool PatternsFitExecutable(string executablePath, out string problem)
    {
        byte[] image;
        try
        {
            image = File.ReadAllBytes(executablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = "executable unreadable: " + ex.GetType().Name;
            return false;
        }

        foreach (var type in Enum.GetValues<SignatureType>())
        {
            if (!Signatures.TryGetValue(type, out var pattern))
            {
                problem = $"donor lacks signature {type}";
                return false;
            }

            var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);
            if (hits.Count != 1)
            {
                problem = hits.Count == 0 ? $"signature {type} not found" : $"signature {type} ambiguous";
                return false;
            }

            var target = OodleSignaturePattern.ResolveRelativeTarget(image, hits[0], pattern.Length);
            if (target is not > 0 || target >= image.Length)
            {
                problem = $"signature {type} resolves outside the image";
                return false;
            }
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>
    /// The profile written for one specific executable, or null when none matches.
    ///
    /// Two profiles claiming the same executable is a contradiction, not a preference: both
    /// are refused, the same way <c>ProfileCatalog</c> refuses two protocol profiles that
    /// claim one build.
    /// </summary>
    /// <param name="directory">Signature profile directory.</param>
    /// <param name="region">Detected client region.</param>
    /// <param name="gameBuild">Detected client build.</param>
    /// <param name="exeSha256">SHA-256 of the executable about to be scanned, lowercase hex.</param>
    /// <param name="exeSize">Size of that executable in bytes.</param>
    /// <param name="problems">Every file that was refused, with the reason.</param>
    public static OodleSignatureProfile? FindForExecutable(
        string? directory,
        Region region,
        string? gameBuild,
        string exeSha256,
        long exeSize,
        out IReadOnlyList<string> problems)
    {
        ArgumentException.ThrowIfNullOrEmpty(exeSha256);
        var refused = new List<string>();
        problems = refused;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var matches = new List<OodleSignatureProfile>();
        foreach (var file in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var profile = TryLoad(file, out var reason);
            if (profile is null)
            {
                refused.Add($"{System.IO.Path.GetFileName(file)}: {reason}");
                continue;
            }

            var regionMatches = string.Equals(
                profile.Region, EnumWire<Region>.Format(region), StringComparison.Ordinal);
            var buildMatches = string.Equals(
                profile.GameBuild, gameBuild, StringComparison.Ordinal);
            var executableMatches = profile.ExeSize == exeSize && string.Equals(
                profile.ExeSha256, exeSha256, StringComparison.OrdinalIgnoreCase);

            if (regionMatches && buildMatches && executableMatches)
            {
                matches.Add(profile);
            }
        }

        if (matches.Count > 1)
        {
            refused.Add(
                "two or more signature profiles claim this executable; all of them were refused");
            return null;
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Reads and validates one signature profile file.</summary>
    /// <param name="path">File to read.</param>
    /// <param name="reason">Why the file was refused, when it was.</param>
    public static OodleSignatureProfile? TryLoad(string path, out string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            return Validate(System.IO.Path.GetFullPath(path), document.RootElement, out reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or JsonException or ArgumentException)
        {
            reason = ex.Message;
            return null;
        }
    }

    private static OodleSignatureProfile? Validate(string path, JsonElement root, out string reason)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            reason = "the document is not a JSON object";
            return null;
        }

        var seenRootFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var known = RequiredRootFields.Contains(property.Name) ||
                OptionalRootFields.Contains(property.Name);
            if (!known || !seenRootFields.Add(property.Name))
            {
                reason = $"unexpected or duplicate root field '{property.Name}'";
                return null;
            }
        }

        if (!RequiredRootFields.IsSubsetOf(seenRootFields))
        {
            reason = "one or more required root fields are missing";
            return null;
        }

        string? reproduction = null;
        if (root.TryGetProperty("reproduction", out var reproductionElement))
        {
            var reproductionText = reproductionElement.ValueKind == JsonValueKind.String
                ? reproductionElement.GetString()
                : null;
            if (reproductionText is not (ReproductionChecked or ReproductionUnchecked))
            {
                reason = $"reproduction must be {ReproductionChecked} or {ReproductionUnchecked}";
                return null;
            }

            reproduction = reproductionText;
        }

        if (!TryInteger(root, "schema_version", out var schemaVersion) ||
            schemaVersion != SupportedSchemaVersion)
        {
            reason = $"schema_version must be {SupportedSchemaVersion}";
            return null;
        }

        if (!TryText(root, "region", out var region) ||
            !TryText(root, "game_build", out var gameBuild) ||
            !TryText(root, "exe_sha256", out var exeSha256) ||
            !TryText(root, "generated_at_utc", out var generatedAtText) ||
            !TryText(root, "source", out var source) ||
            !TryText(root, "status", out var status) ||
            !TryText(root, "profile_sha256", out var declaredHash) ||
            !TryInteger(root, "exe_size", out var exeSize))
        {
            reason = "a required field is missing or has the wrong type";
            return null;
        }

        if (exeSha256.Length != 64 || !IsHex(exeSha256))
        {
            reason = "exe_sha256 must be 64 hexadecimal characters";
            return null;
        }

        if (exeSize <= 0)
        {
            reason = "exe_size must be positive";
            return null;
        }

        if (!EnumWire<Region>.TryParse(region, out var parsedRegion) ||
            parsedRegion == MentorRecorder.Collector.Domain.Region.Unknown)
        {
            reason = "region must be CN or GLOBAL";
            return null;
        }

        if (!string.Equals(source, FinderSource, StringComparison.Ordinal))
        {
            reason = $"source must be {FinderSource}";
            return null;
        }

        if (!DateTimeOffset.TryParse(
                generatedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var generatedAtUtc) || generatedAtUtc.Offset != TimeSpan.Zero)
        {
            reason = "generated_at_utc must be a UTC timestamp";
            return null;
        }

        if (status is not (CandidateStatus or VerifiedStatus))
        {
            reason = $"status must be {CandidateStatus} or {VerifiedStatus}";
            return null;
        }

        string expectedHash;
        try
        {
            expectedHash = ProfileLoader.ComputeProfileHash(root);
        }
        catch (InvalidDataException ex)
        {
            reason = ex.Message;
            return null;
        }

        if (declaredHash.Length != 64 || !IsHex(declaredHash) ||
            !string.Equals(declaredHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "profile_sha256 does not match the document";
            return null;
        }

        var signatures = ReadSignatures(root, out reason);
        if (signatures is null)
        {
            return null;
        }


        var resolvedRvas = ReadResolvedRvas(root, out reason);
        if (resolvedRvas is null)
        {
            return null;
        }

        reason = string.Empty;
        return new OodleSignatureProfile(
            path, region, gameBuild, exeSha256.ToLowerInvariant(), exeSize,
            generatedAtUtc, status, expectedHash, reproduction, signatures, resolvedRvas);
    }

    private static Dictionary<SignatureType, int[]>? ReadSignatures(JsonElement root, out string reason)
    {
        if (!root.TryGetProperty("signatures", out var table) ||
            table.ValueKind != JsonValueKind.Object)
        {
            reason = "signatures must be an object";
            return null;
        }

        var parsed = new Dictionary<SignatureType, int[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in table.EnumerateObject())
        {
            if (!Enum.TryParse<SignatureType>(property.Name, ignoreCase: false, out _) ||
                !seen.Add(property.Name))
            {
                reason = $"signatures contains unexpected or duplicate entry '{property.Name}'";
                return null;
            }
        }

        if (seen.Count != Enum.GetValues<SignatureType>().Length)
        {
            reason = "signatures must contain exactly one entry for every SignatureType";
            return null;
        }

        foreach (var name in Enum.GetNames<SignatureType>())
        {
            if (!table.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                reason = $"signatures.{name} is missing";
                return null;
            }

            if (!OodleSignaturePattern.TryParse(value.GetString(), out var pattern, out var problem))
            {
                reason = $"signatures.{name}: {problem}";
                return null;
            }

            var type = Enum.Parse<SignatureType>(name);
            var hasCallTail = type is SignatureType.OodleMalloc or SignatureType.OodleFree
                ? pattern.Length >= 2 && pattern[^2] == 0xff && pattern[^1] == 0x15
                : pattern[^1] == 0xe8;
            if (!hasCallTail)
            {
                reason = $"signatures.{name} does not end at a resolvable call operand";
                return null;
            }

            parsed[type] = pattern;
        }

        reason = string.Empty;
        return parsed;
    }

    private static Dictionary<SignatureType, int>? ReadResolvedRvas(
        JsonElement root, out string reason)
    {
        if (!root.TryGetProperty("resolved_rvas", out var table) ||
            table.ValueKind != JsonValueKind.Object)
        {
            reason = "resolved_rvas must be an object";
            return null;
        }

        var parsed = new Dictionary<SignatureType, int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in table.EnumerateObject())
        {
            if (!Enum.TryParse<SignatureType>(property.Name, ignoreCase: false, out var type) ||
                !seen.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
            {
                reason = $"resolved_rvas contains an invalid entry '{property.Name}'";
                return null;
            }

            var text = property.Value.GetString() ?? string.Empty;
            if (text.Length != 8 || !int.TryParse(
                    text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rva) ||
                rva <= 0)
            {
                reason = $"resolved_rvas.{property.Name} must be eight hex digits";
                return null;
            }

            parsed[type] = rva;
        }

        if (seen.Count != Enum.GetValues<SignatureType>().Length)
        {
            reason = "resolved_rvas must contain exactly one entry for every SignatureType";
            return null;
        }

        reason = string.Empty;
        return parsed;
    }

    private static bool TryText(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryInteger(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value);
    }

    private static bool IsHex(string text)
    {
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Machina's signature pattern grammar, parsed the same way <c>SigScan</c> parses it: pairs of
/// hexadecimal digits, with <c>**</c> (or <c>??</c>) meaning "any byte", represented as -1.
///
/// Two rules are enforced here that Machina's own parser leaves implicit, because both turn a
/// typo into a silently wrong scan rather than a refusal: the pattern must be long enough to
/// mean anything, and it must start with a literal byte -- a leading wildcard would make the
/// scan match at the first offset it looks at.
/// </summary>
public static class OodleSignaturePattern
{
    /// <summary>Shortest pattern that is allowed to be called a signature.</summary>
    public const int MinimumBytes = 4;

    /// <summary>Upper bound that keeps a malformed profile from forcing excessive scans.</summary>
    public const int MaximumBytes = 256;

    /// <summary>Byte value standing for "any byte".</summary>
    public const int Wildcard = -1;

    /// <summary>Parses one pattern, or explains why it cannot be parsed.</summary>
    /// <param name="text">Pattern text, for example <c>41 83 ** 01 75 ** 48 8b 0f e8</c>.</param>
    /// <param name="pattern">Parsed bytes, with -1 for wildcards.</param>
    /// <param name="problem">Explanation when parsing failed.</param>
    public static bool TryParse(string? text, out int[] pattern, out string problem)
    {
        pattern = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            problem = "the pattern is empty";
            return false;
        }

        var packed = text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("??", "**", StringComparison.Ordinal);
        if (packed.Length % 2 != 0)
        {
            problem = "the pattern has an odd number of hexadecimal digits";
            return false;
        }

        var bytes = new int[packed.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var pair = packed.AsSpan(i * 2, 2);
            if (pair is "**")
            {
                bytes[i] = Wildcard;
                continue;
            }

            if (!byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            {
                problem = $"'{pair}' is not a hexadecimal byte";
                return false;
            }

            bytes[i] = value;
        }

        if (bytes.Length < MinimumBytes)
        {
            problem = $"a signature must be at least {MinimumBytes} bytes";
            return false;
        }

        if (bytes.Length > MaximumBytes)
        {
            problem = $"a signature must be at most {MaximumBytes} bytes";
            return false;
        }

        if (bytes[0] == Wildcard)
        {
            problem = "a signature must start with a literal byte";
            return false;
        }

        pattern = bytes;
        problem = string.Empty;
        return true;
    }

    /// <summary>Every offset in <paramref name="image"/> where <paramref name="pattern"/> matches.</summary>
    /// <param name="pattern">Parsed pattern.</param>
    /// <param name="image">Bytes to search.</param>
    /// <param name="limit">Stop after this many matches; zero means find them all.</param>
    public static IReadOnlyList<int> FindAll(int[] pattern, ReadOnlySpan<byte> image, int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var hits = new List<int>();
        if (pattern.Length == 0 || pattern[0] == Wildcard || image.Length < pattern.Length)
        {
            return hits;
        }

        var last = image.Length - pattern.Length;
        for (var at = 0; at <= last; at++)
        {
            if (image[at] != pattern[0])
            {
                continue;
            }

            var matched = true;
            for (var j = 1; j < pattern.Length; j++)
            {
                if (pattern[j] != Wildcard && pattern[j] != image[at + j])
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
            {
                continue;
            }

            hits.Add(at);
            if (limit > 0 && hits.Count >= limit)
            {
                return hits;
            }
        }

        return hits;
    }

    /// <summary>Resolves the rel32 immediately following a matched call-site pattern.</summary>
    /// <param name="image">Mapped image bytes.</param>
    /// <param name="site">Pattern start offset.</param>
    /// <param name="patternLength">Pattern length, ending on <c>e8</c> or <c>ff 15</c>.</param>
    /// <returns>Resolved image-relative target, or null when the operand is out of range.</returns>
    public static int? ResolveRelativeTarget(
        ReadOnlySpan<byte> image, int site, int patternLength)
    {
        var operand = (long)site + patternLength;
        if (site < 0 || patternLength <= 0 || operand < 0 || operand + sizeof(int) > image.Length)
        {
            return null;
        }

        var relative = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            image.Slice((int)operand, sizeof(int)));
        var target = operand + sizeof(int) + relative;
        return target is > 0 and <= int.MaxValue ? (int)target : null;
    }
}

/// <summary>
/// SHA-256 of a game executable, cached so that starting a capture does not re-hash fifty
/// megabytes every time.
///
/// The cache key is path plus last-write time plus length, so a patched client is never
/// matched against a stale digest: the moment the file changes, the key changes and the hash
/// is recomputed. Only the file is read; no process is opened.
/// </summary>
public static class GameExecutableHash
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime WriteTimeUtc, long Length, string Hash)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lowercase hex SHA-256 of a file, or null when it cannot be read.</summary>
    /// <param name="path">File to hash.</param>
    public static string? Compute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            var key = file.FullName;
            var writeTimeUtc = file.LastWriteTimeUtc;
            var length = file.Length;
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached) &&
                    cached.WriteTimeUtc == writeTimeUtc &&
                    cached.Length == length)
                {
                    return cached.Hash;
                }
            }

            string hash;
            using (var stream = new FileStream(
                       key, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       bufferSize: 1 << 20, FileOptions.SequentialScan))
            {
                hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }

            file.Refresh();
            if (!file.Exists || file.LastWriteTimeUtc != writeTimeUtc || file.Length != length)
            {
                return null;
            }

            lock (Gate)
            {
                Cache[key] = (writeTimeUtc, length, hash);
            }

            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The first twelve characters of a digest: enough to tell builds apart, never a secret.</summary>
    /// <param name="hash">Full digest, or null.</param>
    public static string Prefix(string? hash) =>
        string.IsNullOrEmpty(hash) ? "unknown" : hash[..Math.Min(12, hash.Length)];
}
