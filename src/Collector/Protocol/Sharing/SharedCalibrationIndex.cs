using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>One published code as the public repository's index lists it.</summary>
/// <param name="Region">CN or GLOBAL.</param>
/// <param name="GameBuild">Client build the code was learned on.</param>
/// <param name="CodeSha256">Identity of the code: SHA-256 of its canonical payload.</param>
/// <param name="MatchSource">Kind of evidence that named the match; must agree with the code itself.</param>
/// <param name="Submitters">Distinct accounts that submitted this same code, at least 1.</param>
/// <param name="FirstPublishedAtUtc">When the code was first published.</param>
/// <param name="Path">Repository path of the code file; always <see cref="SharedCalibrationIndex.CodePath"/>.</param>
/// <param name="Commit">Commit that added the file; codes are downloaded by it, so no cache serves other content.</param>
/// <param name="Revoked">True when the maintainer withdrew the code.</param>
/// <param name="Conflicting">
/// True when another published code of the same build, template and match source differs from this one
/// (plan §18.6): at least one of them is wrong, so both are picked last. Optional in the index; absent reads as false.
/// </param>
public sealed record SharedIndexEntry(
    Region Region,
    string GameBuild,
    string CodeSha256,
    CalibrationMatchSource MatchSource,
    int Submitters,
    DateTimeOffset FirstPublishedAtUtc,
    string Path,
    string Commit,
    bool Revoked,
    bool Conflicting = false);

/// <summary>An index entry that was not used, and why.</summary>
/// <param name="Position">Zero-based position in <c>entries</c>.</param>
/// <param name="Reason">
/// <c>NOT_AN_OBJECT</c>, <c>DUPLICATE_KEY</c>, <c>MISSING:&lt;field&gt;</c> or <c>INVALID:&lt;field&gt;</c>.
/// Never repeats content from the index.
/// </param>
public sealed record SharedIndexSkip(int Position, string Reason);

/// <summary>What reading an index produced.</summary>
/// <param name="Entries">Entries that passed every check, in index order.</param>
/// <param name="Skipped">Entries that did not, with reasons.</param>
/// <param name="Refusal">
/// Why the whole index was refused (<c>NOT_JSON</c>, which includes bytes that are not UTF-8 and a key or string
/// holding a lone surrogate escape; <c>NOT_AN_OBJECT</c>, <c>DUPLICATE_KEY</c>, <c>SCHEMA_VERSION</c>,
/// <c>NO_ENTRIES</c>, <c>TOO_MANY_ENTRIES</c>), or null when it was read.
/// </param>
public sealed record SharedIndexReadResult(
    IReadOnlyList<SharedIndexEntry> Entries,
    IReadOnlyList<SharedIndexSkip> Skipped,
    string? Refusal)
{
    /// <summary>True when the index itself was usable, even if some entries were skipped.</summary>
    public bool IsReadable => Refusal is null;

    internal static SharedIndexReadResult Refuse(string reason) =>
        new(Array.Empty<SharedIndexEntry>(), Array.Empty<SharedIndexSkip>(), reason);
}

/// <summary>
/// Reads the public repository's <c>index.json</c> and chooses what to download.
///
/// The index is untrusted input from the network, so it is read the way a share code is: a fixed
/// layout (<c>{"schema_version": 1, "entries": [...]}</c>), at most <see cref="MaxEntries"/> entries,
/// every field required and checked against a fixed format, unknown fields ignored. An entry that
/// fails is skipped with a reason; nothing is repaired or guessed. A code's path is not taken from
/// the index at face value: it must be exactly <see cref="CodePath"/> for the entry's region, build
/// and hash, which rules out traversal and any file the layout does not define. Pure: no IO, no clock.
/// </summary>
public static class SharedCalibrationIndex
{
    /// <summary>The one index layout this build reads; an incompatible layout must use another file.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Most entries an index may list before it is refused whole.</summary>
    public const int MaxEntries = 512;

    /// <summary>Most codes verified at once for one region and build (plan §3.2).</summary>
    public const int MaxCandidates = 8;

    /// <summary>Extension of a code file.</summary>
    public const string CodeExtension = ".mrc";

    private static readonly string[] Fields =
    {
        "region", "game_build", "code_sha256", "match_source", "submitters", "first_published_at", "path", "commit", "revoked",
    };

    private static readonly Regex BuildPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ShaPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CommitPattern = new(@"^(?:[0-9a-f]{40}|[0-9a-f]{64})\z", RegexOptions.CultureInvariant);
    private static readonly Regex CodePathPattern = new(
        @"^(?:cn|global)/[A-Za-z0-9][A-Za-z0-9._-]{0,127}/[0-9a-f]{12}\.mrc\z", RegexOptions.CultureInvariant);
    private static readonly Regex StampPattern = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?Z\z", RegexOptions.CultureInvariant);

    private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>True for a client build a path may carry: starts with a letter or digit, then letters, digits, <c>._-</c>.</summary>
    /// <param name="text">Candidate build.</param>
    public static bool IsBuild([NotNullWhen(true)] string? text) => text is not null && BuildPattern.IsMatch(text);

    /// <summary>True for 64 lowercase hex digits.</summary>
    /// <param name="text">Candidate hash.</param>
    public static bool IsSha256([NotNullWhen(true)] string? text) => text is not null && ShaPattern.IsMatch(text);

    /// <summary>True for a full lowercase git commit id (40 or 64 hex digits).</summary>
    /// <param name="text">Candidate commit.</param>
    public static bool IsCommit([NotNullWhen(true)] string? text) => text is not null && CommitPattern.IsMatch(text);

    /// <summary>True for a path of the shape <see cref="CodePath"/> produces.</summary>
    /// <param name="text">Candidate path.</param>
    public static bool IsCodePath([NotNullWhen(true)] string? text) => text is not null && CodePathPattern.IsMatch(text);

    /// <summary>Directory name of a region in the repository and in the local store: <c>cn</c> or <c>global</c>.</summary>
    /// <param name="region">CN or GLOBAL; anything else throws.</param>
    public static string RegionDirectory(Region region) => region switch
    {
        Region.Cn => "cn",
        Region.Global => "global",
        _ => throw new ArgumentException("only CN and GLOBAL have shared calibrations", nameof(region)),
    };

    /// <summary>Repository path of a code: <c>&lt;region&gt;/&lt;build&gt;/&lt;first 12 hex digits&gt;.mrc</c>.</summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build (<see cref="IsBuild"/>).</param>
    /// <param name="codeSha256">Identity of the code (<see cref="IsSha256"/>).</param>
    public static string CodePath(Region region, string gameBuild, string codeSha256)
    {
        var directory = RegionDirectory(region);
        if (!IsBuild(gameBuild))
        {
            throw new ArgumentException("not a client build", nameof(gameBuild));
        }

        if (!IsSha256(codeSha256))
        {
            throw new ArgumentException("not a lowercase hex SHA-256", nameof(codeSha256));
        }

        return directory + "/" + gameBuild + "/" + codeSha256[..12] + CodeExtension;
    }

    /// <summary>Reads an index. Never throws for any input.</summary>
    /// <param name="utf8">The downloaded bytes; a leading UTF-8 byte order mark is ignored.</param>
    public static SharedIndexReadResult Read(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Span.StartsWith(Utf8Bom))
        {
            utf8 = utf8[Utf8Bom.Length..];
        }

        // Text that is not UTF-8 or holds a lone surrogate escape is refused whole, before any entry is read.
        if (!WellFormedJson.TryParse(utf8, ParseOptions, out var document))
        {
            return SharedIndexReadResult.Refuse("NOT_JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return SharedIndexReadResult.Refuse("NOT_AN_OBJECT");
            }

            if (HasDuplicateKey(root))
            {
                return SharedIndexReadResult.Refuse("DUPLICATE_KEY");
            }

            if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != SchemaVersion)
            {
                return SharedIndexReadResult.Refuse("SCHEMA_VERSION");
            }

            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                return SharedIndexReadResult.Refuse("NO_ENTRIES");
            }

            if (entries.GetArrayLength() > MaxEntries)
            {
                return SharedIndexReadResult.Refuse("TOO_MANY_ENTRIES");
            }

            var read = new List<SharedIndexEntry>();
            var skipped = new List<SharedIndexSkip>();
            var position = 0;
            foreach (var item in entries.EnumerateArray())
            {
                if (ReadEntry(item, out var entry) is { } reason)
                {
                    skipped.Add(new SharedIndexSkip(position, reason));
                }
                else
                {
                    read.Add(entry!);
                }

                position++;
            }

            return new SharedIndexReadResult(read, skipped, null);
        }
    }

    /// <summary>
    /// What to download for a region and build: entries that are not revoked (a revocation of a code
    /// anywhere in the index wins over every other entry for it), each code once, ordered by
    /// submitters descending, then first published ascending, then hash; at most <see cref="MaxCandidates"/>.
    /// </summary>
    /// <param name="entries">Entries of a readable index.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    public static IReadOnlyList<SharedIndexEntry> Select(IEnumerable<SharedIndexEntry> entries, Region region, string gameBuild)
    {
        var forBuild = ForBuild(entries, region, gameBuild);
        var revoked = forBuild.Where(entry => entry.Revoked).Select(entry => entry.CodeSha256).ToHashSet(StringComparer.Ordinal);
        return forBuild
            .Where(entry => !revoked.Contains(entry.CodeSha256))
            .OrderBy(entry => entry.Conflicting)
            .ThenByDescending(entry => entry.Submitters)
            .ThenBy(entry => entry.FirstPublishedAtUtc)
            .ThenBy(entry => entry.CodeSha256, StringComparer.Ordinal)
            .DistinctBy(entry => entry.CodeSha256, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToArray();
    }

    /// <summary>Codes the index revokes for a region and build, sorted, each once.</summary>
    /// <param name="entries">Entries of a readable index.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    public static IReadOnlyList<string> Revoked(IEnumerable<SharedIndexEntry> entries, Region region, string gameBuild) =>
        ForBuild(entries, region, gameBuild)
            .Where(entry => entry.Revoked)
            .Select(entry => entry.CodeSha256)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>The bytes without a leading UTF-8 byte order mark.</summary>
    internal static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Utf8Bom) ? bytes[Utf8Bom.Length..] : bytes;

    private static SharedIndexEntry[] ForBuild(IEnumerable<SharedIndexEntry> entries, Region region, string gameBuild)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries
            .Where(entry => entry.Region == region && string.Equals(entry.GameBuild, gameBuild, StringComparison.Ordinal))
            .ToArray();
    }

    private static string? ReadEntry(JsonElement item, out SharedIndexEntry? entry)
    {
        entry = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return "NOT_AN_OBJECT";
        }

        if (HasDuplicateKey(item))
        {
            return "DUPLICATE_KEY";
        }

        if (Fields.FirstOrDefault(field => !item.TryGetProperty(field, out _)) is { } missing)
        {
            return "MISSING:" + missing;
        }

        if (!EnumWire<Region>.TryParse(Text(item, "region"), out var region) || region is not (Region.Cn or Region.Global))
        {
            return "INVALID:region";
        }

        var build = Text(item, "game_build");
        if (!IsBuild(build))
        {
            return "INVALID:game_build";
        }

        var sha = Text(item, "code_sha256");
        if (!IsSha256(sha))
        {
            return "INVALID:code_sha256";
        }

        if (!EnumWire<CalibrationMatchSource>.TryParse(Text(item, "match_source"), out var source))
        {
            return "INVALID:match_source";
        }

        var submitters = item.GetProperty("submitters");
        if (submitters.ValueKind != JsonValueKind.Number || !submitters.TryGetInt32(out var count) || count < 1)
        {
            return "INVALID:submitters";
        }

        if (Stamp(Text(item, "first_published_at")) is not { } published)
        {
            return "INVALID:first_published_at";
        }

        var path = Text(item, "path");
        if (path is null || !string.Equals(path, CodePath(region, build, sha), StringComparison.Ordinal))
        {
            return "INVALID:path";
        }

        var commit = Text(item, "commit");
        if (!IsCommit(commit))
        {
            return "INVALID:commit";
        }

        var revoked = item.GetProperty("revoked");
        if (revoked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return "INVALID:revoked";
        }

        // Optional (plan §18.6): an index written before the field existed reads as "no conflict".
        var conflicting = false;
        if (item.TryGetProperty("conflicting", out var conflict))
        {
            if (conflict.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return "INVALID:conflicting";
            }

            conflicting = conflict.GetBoolean();
        }

        entry = new SharedIndexEntry(region, build, sha, source, count, published, path, commit, revoked.GetBoolean(), conflicting);
        return null;
    }

    private static bool HasDuplicateKey(JsonElement element)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !seen.Add(property.Name));
    }

    private static DateTimeOffset? Stamp(string? text) =>
        text is not null && StampPattern.IsMatch(text) &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    private static string? Text(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
