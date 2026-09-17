using System.Text.Json;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>Where a catalogue entry's file came from.</summary>
public enum ProfileOrigin
{
    /// <summary>Shipped with this build, under <see cref="ProfileCatalog.FindDefaultRoot"/>.</summary>
    Shipped,

    /// <summary>Written on this machine by self-calibration, under the managed data directory.</summary>
    Local,

    /// <summary>
    /// Rebuilt on this machine from another player's share code and verified against local
    /// traffic, under <see cref="ProfileCatalog.SharedDirectoryName"/> in the managed data
    /// directory.
    /// </summary>
    Shared,
}

/// <summary>One entry of the catalogue: a file that was looked at and what came of it.</summary>
/// <param name="Path">Absolute path of the profile file.</param>
/// <param name="Report">Validation report for that file.</param>
/// <param name="Status">Effective status, which is AMBIGUOUS when another file claims the same build.</param>
/// <param name="Origin">Which root the file was read from.</param>
/// <param name="Shadowed">
/// True when <see cref="ProfileCatalog.LoadMerged(string?, string?, string?, bool)"/> found a
/// higher-ranked entry for the same region and build whose own binding is usable. A shadowed
/// entry never takes part in recording, and shadowing is decided before the ambiguity check
/// runs, so it never makes the entry that shadowed it ambiguous.
/// </param>
public sealed record ProfileCatalogEntry(
    string Path,
    ProfileValidationReport Report,
    ProfileCompatibilityStatus Status,
    ProfileOrigin Origin = ProfileOrigin.Shipped,
    bool Shadowed = false)
{
    /// <summary>The loaded profile, or null when the file was refused, ambiguous or shadowed.</summary>
    public ProtocolProfile? Profile =>
        Status == ProfileCompatibilityStatus.Ambiguous || Shadowed ? null : Report.Profile;
}

/// <summary>
/// Every profile file under one root directory, with the collisions already resolved.
///
/// The collision rule is deliberately harsh: when two files claim the same region and the
/// same game build within the formal or candidate category, <b>both</b> are refused as
/// AMBIGUOUS. Candidate collisions never disable formal profiles. Picking by file name,
/// modification time or declared status would mean the recorded data depends on which file
/// happened to sort first, which is exactly the kind of silent choice this project refuses
/// to make about protocol constants.
/// </summary>
public sealed class ProfileCatalog
{
    /// <summary>Name of the directory profiles live in.</summary>
    public const string DirectoryName = "protocol-profiles";

    /// <summary>
    /// Name of the directory, under the managed data root, that profiles rebuilt from other
    /// players' share codes live in. Kept apart from the local directory so origin is told by
    /// location, which the schema leaves no room to record.
    /// </summary>
    public const string SharedDirectoryName = "protocol-profiles-shared";

    /// <summary>File name of the schema, which is not itself a profile.</summary>
    public const string SchemaFileName = "profile.schema.json";

    /// <summary>Directory of Oodle signature evidence, which is not a protocol profile tree.</summary>
    public const string OodleSignatureDirectoryName = "oodle-signatures";

    private ProfileCatalog(string? root, string? localRoot, string? sharedRoot, IReadOnlyList<ProfileCatalogEntry> entries)
    {
        Root = root;
        LocalRoot = localRoot;
        SharedRoot = sharedRoot;
        Entries = entries;
    }

    /// <summary>Shipped directory the catalogue was built from, or null when none was found.</summary>
    public string? Root { get; }

    /// <summary>
    /// Local (self-calibration) directory the catalogue was merged with, or null when
    /// <see cref="LoadMerged(string?, string?, bool)"/> was not given one, or when
    /// <see cref="Load"/> built this catalogue.
    /// </summary>
    public string? LocalRoot { get; }

    /// <summary>Shared-calibration directory the catalogue was merged with, or null when none was given.</summary>
    public string? SharedRoot { get; }

    /// <summary>Every file that was inspected, ordered by root (shipped, local, shared) and then by path.</summary>
    public IReadOnlyList<ProfileCatalogEntry> Entries { get; }

    /// <summary>Entries a higher-ranked usable entry shadowed; for diagnostics only.</summary>
    public IReadOnlyList<ProfileCatalogEntry> ShadowedEntries =>
        Entries.Where(entry => entry.Shadowed).ToArray();

    /// <summary>An empty catalogue. Every lookup fails closed.</summary>
    public static ProfileCatalog Empty { get; } = new(null, null, null, Array.Empty<ProfileCatalogEntry>());

    /// <summary>Loads every profile under <paramref name="root"/>.</summary>
    /// <param name="root">Directory to scan; a missing directory yields an empty catalogue.</param>
    /// <param name="allowCandidate">显式允许候选档案进入观察目录；默认不保留任何候选。</param>
    public static ProfileCatalog Load(string? root, bool allowCandidate = false)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new ProfileCatalog(root is null ? null : Path.GetFullPath(root), null, null,
                Array.Empty<ProfileCatalogEntry>());
        }

        var fullRoot = Path.GetFullPath(root);
        var reports = LoadReports(fullRoot, allowCandidate);
        var entries = BuildEntries(
            reports.Select(report => (Report: report, Origin: ProfileOrigin.Shipped, Shadowed: false)));
        return new ProfileCatalog(fullRoot, null, null, entries);
    }

    /// <summary>
    /// Loads the shipped root and, when given, a local (self-calibration) root, with no shared
    /// root. Same as <see cref="LoadMerged(string?, string?, string?, bool)"/> with a null
    /// shared root.
    /// </summary>
    /// <param name="shippedRoot">Shipped profile directory; typically <see cref="FindDefaultRoot"/>.</param>
    /// <param name="localRoot">Local (self-calibration) profile directory, or null to skip it.</param>
    /// <param name="allowCandidate">显式允许候选档案进入观察目录；默认不保留任何候选。</param>
    public static ProfileCatalog LoadMerged(string? shippedRoot, string? localRoot, bool allowCandidate = false) =>
        LoadMerged(shippedRoot, localRoot, null, allowCandidate);

    /// <summary>
    /// Loads the shipped root and, when given, the local (self-calibration) and shared-calibration
    /// roots, and merges them with precedence before ambiguity.
    ///
    /// Within each (region, build) among formal (non-candidate) entries, only an entry whose own
    /// binding is usable (<see cref="ProtocolProfile.ToBinding"/>) may shadow anything. The usable
    /// entries are ranked - a profile that reads the server's announcement before one that infers
    /// the match from the queue, then shipped before local before shared - and the best-ranked
    /// ones shadow every other formal entry of that build, usable or not. A local profile the
    /// binding refuses therefore never hides a usable shared one, and the two never refuse each
    /// other as AMBIGUOUS (review finding 1). When no entry of a build is usable, nothing is
    /// shadowed.
    ///
    /// Whatever survives - several equally ranked usable entries, or entries of a build nothing
    /// usable claims - then goes through the same "two files, same region and build, refuse both"
    /// rule <see cref="Load"/> uses. Candidates are never ranked, shadowed or shadowing.
    /// </summary>
    /// <param name="shippedRoot">Shipped profile directory; typically <see cref="FindDefaultRoot"/>.</param>
    /// <param name="localRoot">Local (self-calibration) profile directory, or null to skip it.</param>
    /// <param name="sharedRoot">Shared-calibration profile directory, or null to skip it.</param>
    /// <param name="allowCandidate">显式允许候选档案进入观察目录；默认不保留任何候选。</param>
    public static ProfileCatalog LoadMerged(
        string? shippedRoot, string? localRoot, string? sharedRoot, bool allowCandidate = false)
    {
        var tagged = LoadReports(shippedRoot, allowCandidate).Select(report => (Report: report, Origin: ProfileOrigin.Shipped))
            .Concat(LoadReports(localRoot, allowCandidate).Select(report => (Report: report, Origin: ProfileOrigin.Local)))
            .Concat(LoadReports(sharedRoot, allowCandidate).Select(report => (Report: report, Origin: ProfileOrigin.Shared)))
            .ToArray();
        var shadowed = Shadow(tagged);
        var entries = BuildEntries(tagged.Select((item, index) => (item.Report, item.Origin, shadowed.Contains(index))));
        return new ProfileCatalog(FullOrNull(shippedRoot), FullOrNull(localRoot), FullOrNull(sharedRoot), entries);
    }

    private static string? FullOrNull(string? root) => string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);

    /// <summary>
    /// Indices of the tagged reports that a higher-ranked usable entry of the same region and
    /// build shadows. "Usable" is the binding's own verdict, the one the state machine acts on.
    /// </summary>
    private static HashSet<int> Shadow((ProfileValidationReport Report, ProfileOrigin Origin)[] tagged)
    {
        var shadowed = new HashSet<int>();
        var formal = Enumerable.Range(0, tagged.Length)
            .Where(index => tagged[index].Report.Profile is { } profile && profile.Status != ProfileCompatibilityStatus.Candidate)
            .GroupBy(index => (tagged[index].Report.Profile!.Region, Build: tagged[index].Report.Profile!.GameBuild.ToUpperInvariant()));
        foreach (var build in formal)
        {
            var usable = build.Where(index => tagged[index].Report.Profile!.ToBinding().IsUsable).ToArray();
            if (usable.Length == 0)
            {
                continue;
            }

            var best = usable.Min(index => Rank(tagged[index].Report.Profile!, tagged[index].Origin));
            foreach (var index in build)
            {
                if (!usable.Contains(index) || Rank(tagged[index].Report.Profile!, tagged[index].Origin) != best)
                {
                    shadowed.Add(index);
                }
            }
        }

        return shadowed;
    }

    /// <summary>Lower ranks first: reading the match beats inferring it, then shipped, local, shared.</summary>
    private static int Rank(ProtocolProfile profile, ProfileOrigin origin) =>
        (profile.MatchFromQueue ? 10 : 0) + origin switch
        {
            ProfileOrigin.Shipped => 0,
            ProfileOrigin.Local => 1,
            _ => 2,
        };

    /// <summary>
    /// Reads and validates every profile file directly under one root. A missing or blank
    /// root yields no reports rather than throwing, so callers never have to special-case
    /// "no local directory yet".
    /// </summary>
    private static ProfileValidationReport[] LoadReports(string? root, bool allowCandidate)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<ProfileValidationReport>();
        }

        var files = Directory
            .EnumerateFiles(Path.GetFullPath(root), "*.json", SearchOption.AllDirectories)
            .Where(IsProtocolProfileFile)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        return files.Select(ProfileLoader.Validate)
            .Where(report => allowCandidate || report.Status != "CANDIDATE")
            .ToArray();
    }

    /// <summary>
    /// Runs the "two files, same region and build (within the same candidate/formal
    /// category), refuse both" rule over every tagged report that is not already shadowed,
    /// then builds the catalogue entries. Shared by <see cref="Load"/> (where nothing is ever
    /// shadowed) and the merged loads (where shadowing was decided first).
    /// </summary>
    private static List<ProfileCatalogEntry> BuildEntries(
        IEnumerable<(ProfileValidationReport Report, ProfileOrigin Origin, bool Shadowed)> tagged)
    {
        var list = tagged.ToArray();
        var ambiguous = list
            .Where(entry => !entry.Shadowed && entry.Report.Profile is not null)
            .GroupBy(entry => (entry.Report.Profile!.Region, entry.Report.Profile.GameBuild.ToUpperInvariant(),
                Candidate: entry.Report.Profile.Status == ProfileCompatibilityStatus.Candidate))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => entry.Report.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<ProfileCatalogEntry>(list.Length);
        foreach (var (report, origin, shadowed) in list)
        {
            var status = report.Profile is null
                ? ProfileCompatibilityStatus.Unsupported
                : ambiguous.Contains(report.Path)
                    ? ProfileCompatibilityStatus.Ambiguous
                    : report.Profile.Status;
            entries.Add(new ProfileCatalogEntry(report.Path, report, status, origin, shadowed));
        }

        return entries;
    }

    private static bool IsProtocolProfileFile(string path)
    {
        if (string.Equals(Path.GetFileName(path), SchemaFileName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(directory))
        {
            if (string.Equals(
                    Path.GetFileName(directory),
                    OodleSignatureDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return true;
    }

    /// <summary>Loads the catalogue from <see cref="FindDefaultRoot"/>.</summary>
    /// <param name="allowCandidate">只有用户开启候选验证时才设为 true。</param>
    public static ProfileCatalog LoadDefault(bool allowCandidate = false) => Load(FindDefaultRoot(), allowCandidate);

    /// <summary>
    /// Locates the <c>protocol-profiles</c> directory next to the executable, next to the
    /// working directory, or in any ancestor of either. Returns null when there is none.
    /// </summary>
    public static string? FindDefaultRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, DirectoryName);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    /// <summary>
    /// Path of the local (self-calibration) profile directory under the managed data
    /// directory, whether or not it exists yet. Callers that are about to write to it use
    /// this; callers that only want to read from it use <see cref="FindLocalRoot"/>.
    /// </summary>
    public static string LocalRootPath => Path.Combine(DatabasePaths.RootDirectory, DirectoryName);

    /// <summary>
    /// Locates the local (self-calibration) profile directory under the managed data
    /// directory (<see cref="DatabasePaths.RootDirectory"/>). Returns null when it does not
    /// exist yet -- a machine that has never calibrated has nothing to merge.
    /// </summary>
    public static string? FindLocalRoot() => Directory.Exists(LocalRootPath) ? LocalRootPath : null;

    /// <summary>
    /// Path of the shared-calibration profile directory under the managed data directory,
    /// whether or not it exists yet.
    /// </summary>
    public static string SharedRootPath => Path.Combine(DatabasePaths.RootDirectory, SharedDirectoryName);

    /// <summary>The shared-calibration profile directory, or null when it does not exist yet.</summary>
    public static string? FindSharedRoot() => Directory.Exists(SharedRootPath) ? SharedRootPath : null;

    /// <summary>
    /// Keeps only the newest <paramref name="keepPerRegion"/> profile files in each region
    /// subdirectory of one calibrated root, "newest" meaning the greatest <c>generated_at</c> the
    /// file declares, falling back to the file's own last-write time when that cannot be read.
    /// Call it once per root (local and shared): each root keeps its own quota, so shared
    /// profiles never push out local ones or the reverse. Never touches anything that is not a
    /// <c>.json</c> profile file, and never touches <see cref="SchemaFileName"/>. Pure directory
    /// hygiene: it does no scheduling and is safe to call from a startup path.
    /// </summary>
    /// <param name="localRoot">Calibrated profile directory; a missing one deletes nothing.</param>
    /// <param name="keepPerRegion">How many files to keep per region subdirectory.</param>
    /// <returns>Paths of the files that were deleted.</returns>
    public static IReadOnlyList<string> PruneLocalProfiles(string localRoot, int keepPerRegion = 3)
    {
        ArgumentException.ThrowIfNullOrEmpty(localRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(keepPerRegion);

        if (!Directory.Exists(localRoot))
        {
            return Array.Empty<string>();
        }

        var deleted = new List<string>();
        foreach (var regionDirectory in Directory.EnumerateDirectories(localRoot))
        {
            var newestFirst = Directory
                .EnumerateFiles(regionDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), SchemaFileName, StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path: path, GeneratedAt: ReadGeneratedAtOrFileTime(path)))
                .OrderByDescending(file => file.GeneratedAt)
                .ToArray();

            foreach (var file in newestFirst.Skip(keepPerRegion))
            {
                try
                {
                    File.Delete(file.Path);
                    deleted.Add(file.Path);
                }
                catch (IOException)
                {
                    // A file the OS is still holding open is left for the next run; retention
                    // is a best-effort sweep, never a hard requirement of startup.
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// Reads <c>generated_at</c> out of a profile file for retention ordering only. Any
    /// failure -- missing file, bad JSON, missing or unparsable property -- falls back to the
    /// file's own last-write time rather than throwing; retention must never crash startup
    /// over one damaged file.
    /// </summary>
    private static DateTimeOffset ReadGeneratedAtOrFileTime(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("generated_at", out var value) &&
                value.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var generatedAt))
            {
                return generatedAt;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall back to the file time below.
        }

        return File.GetLastWriteTimeUtc(path);
    }

    /// <summary>
    /// The profiles that could be used for <paramref name="region"/>, ambiguity and shadowing
    /// already removed. Ordered by profile id so the result is stable.
    /// </summary>
    /// <param name="region">Region to filter by.</param>
    public IReadOnlyList<ProtocolProfile> UsableFor(Region region) => Entries
        .Where(entry => entry.Profile is { } profile && profile.Region == region)
        .Select(entry => entry.Profile!)
        .OrderBy(profile => profile.ProfileId, StringComparer.Ordinal)
        .ToArray();

    /// <summary>True when two or more files claim <paramref name="region"/> and <paramref name="build"/>.</summary>
    /// <param name="region">Region to check.</param>
    /// <param name="build">Game build to check.</param>
    /// <param name="candidate">检查候选类别；默认只检查正式档案类别。</param>
    public bool IsAmbiguous(Region region, string build, bool candidate = false) => Entries
        .Any(entry => entry.Status == ProfileCompatibilityStatus.Ambiguous &&
                      entry.Report.Profile is { } profile &&
                      (profile.Status == ProfileCompatibilityStatus.Candidate) == candidate &&
                      profile.Region == region &&
                      string.Equals(profile.GameBuild, build, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Origin of a profile this catalogue returned, or null when it did not come from this
    /// catalogue's entries at all (for example, an explicitly selected file).
    /// </summary>
    /// <param name="profile">Profile previously returned by <see cref="UsableFor"/>.</param>
    public ProfileOrigin? OriginOf(ProtocolProfile profile) => Entries
        .Where(entry => entry.Profile is not null && entry.Profile == profile)
        .Select(entry => (ProfileOrigin?)entry.Origin)
        .FirstOrDefault();
}
