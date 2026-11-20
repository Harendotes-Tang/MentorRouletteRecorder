using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Machina.FFXIV.Memory;
using Machina.FFXIV.Oodle;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Capture;

/// <summary>Result of scanning one profile pattern in Machina's mapped executable copy.</summary>
/// <param name="Type">Machina function being located.</param>
/// <param name="MatchCount">Zero, one, or two; two means two or more because scanning stops there.</param>
/// <param name="ResolvedRva">Resolved RVA only when exactly one valid call site matched.</param>
public sealed record OodleSignatureMatch(SignatureType Type, int MatchCount, int? ResolvedRva);

/// <summary>
/// Machina-compatible scanner backed by a checked signature profile.
///
/// Machina's built-in <see cref="SigScan"/> accepts the first matching site. Profile evidence
/// has a stronger rule: every pattern must match exactly once. This implementation therefore
/// ports the same whole-byte wildcard and rel32 resolution semantics while refusing both a
/// missing and an ambiguous pattern. It scans only the executable copy already mapped into
/// this Collector process; it never opens the game process.
/// </summary>
public sealed class OodleSignatureScan : ISigScan
{
    private const int MaximumImageBytes = 512 * 1024 * 1024;
    private readonly string _profileId;
    private readonly IReadOnlyDictionary<SignatureType, int[]> _signatures;
    private IReadOnlyList<OodleSignatureMatch> _lastResults = Array.Empty<OodleSignatureMatch>();

    /// <summary>Creates a scanner from an already validated profile.</summary>
    /// <param name="profile">Exact-executable profile.</param>
    public OodleSignatureScan(OodleSignatureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profileId = profile.Id;
        _signatures = profile.Signatures.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray());
    }

    /// <summary>Results of the most recent scan, ordered by <see cref="SignatureType"/>.</summary>
    public IReadOnlyList<OodleSignatureMatch> LastResults => _lastResults;

    /// <inheritdoc />
    public Dictionary<SignatureType, int> Read(IntPtr library)
    {
        if (library == IntPtr.Zero)
        {
            throw new ArgumentException("the mapped module handle is null", nameof(library));
        }

        var imageSize = ReadImageSize(library);
        var image = new byte[imageSize];
        Marshal.Copy(library, image, 0, image.Length);

        var resolved = new Dictionary<SignatureType, int>();
        var results = new List<OodleSignatureMatch>(Enum.GetValues<SignatureType>().Length);
        foreach (var type in Enum.GetValues<SignatureType>())
        {
            if (!_signatures.TryGetValue(type, out var pattern))
            {
                results.Add(new OodleSignatureMatch(type, 0, null));
                Trace.WriteLine(
                    $"{nameof(OodleSignatureScan)}.{nameof(Read)}: Missing Signature [{type}].",
                    "DEBUG-MACHINA");
                continue;
            }

            var hits = OodleSignaturePattern.FindAll(pattern, image, limit: 2);
            int? target = null;
            if (hits.Count == 1)
            {
                target = OodleSignaturePattern.ResolveRelativeTarget(image, hits[0], pattern.Length);
                if (target is <= 0 || target >= image.Length)
                {
                    target = null;
                }
            }

            results.Add(new OodleSignatureMatch(type, hits.Count, target));
            if (hits.Count == 1 && target is { } rva)
            {
                resolved.Add(type, rva);
                Trace.WriteLine(
                    $"Found Signature [{type}] at offset [{rva:X8}] from profile [{_profileId}]",
                    "DEBUG-MACHINA");
            }
            else if (hits.Count == 0)
            {
                Trace.WriteLine(
                    $"{nameof(OodleSignatureScan)}.{nameof(Read)}: Missing Signature [{type}].",
                    "DEBUG-MACHINA");
            }
            else if (hits.Count > 1)
            {
                Trace.WriteLine(
                    $"{nameof(OodleSignatureScan)}.{nameof(Read)}: Ambiguous Signature [{type}].",
                    "DEBUG-MACHINA");
            }
            else
            {
                Trace.WriteLine(
                    $"{nameof(OodleSignatureScan)}.{nameof(Read)}: Invalid target for Signature [{type}].",
                    "DEBUG-MACHINA");
            }
        }

        _lastResults = results;
        return resolved;
    }

    private static int ReadImageSize(IntPtr library)
    {
        const int dosMagic = 0x5a4d;
        const int peMagic = 0x00004550;
        const int peOffsetField = 0x3c;
        const int coffHeaderBytes = 20;
        const int sizeOfImageOffset = 56;
        const short pe32PlusMagic = 0x20b;

        if ((ushort)Marshal.ReadInt16(library) != dosMagic)
        {
            throw new InvalidDataException("the mapped Oodle image has no DOS header");
        }

        var peOffset = Marshal.ReadInt32(library, peOffsetField);
        if (peOffset <= peOffsetField || peOffset > 16 * 1024 * 1024 ||
            Marshal.ReadInt32(library, peOffset) != peMagic)
        {
            throw new InvalidDataException("the mapped Oodle image has no valid PE header");
        }

        var optionalHeader = checked(peOffset + sizeof(int) + coffHeaderBytes);
        if (Marshal.ReadInt16(library, optionalHeader) != pe32PlusMagic)
        {
            throw new InvalidDataException("the mapped Oodle image is not PE32+");
        }

        var imageSize = Marshal.ReadInt32(library, checked(optionalHeader + sizeOfImageOffset));
        if (imageSize <= 0 || imageSize > MaximumImageBytes)
        {
            throw new InvalidDataException("the mapped Oodle image size is outside the accepted range");
        }

        return imageSize;
    }
}

/// <summary>
/// Scoped installation of one exact-match signature profile into Machina 2.4.8.3.
///
/// <see cref="OodleFactory"/> stores the native implementation in private static state and
/// returns early whenever that object is already an <see cref="OodleNative_Ffxiv"/>. The
/// override must therefore use the factory's own private lock, bootstrap <c>FfxivTcp</c>,
/// replace the native object before <c>FFXIVNetworkMonitor.Start</c>, and clear it after that
/// monitor stops. Every reflected member and the Machina assembly version are hard gates; a
/// future incompatible package fails closed instead of running with a half-installed scanner.
/// </summary>
public sealed class OodleSignatureRuntime : IDisposable
{
    /// <summary>Exact Machina build whose private factory layout this code supports.</summary>
    public const string SupportedMachinaVersion = "2.4.8.3";

    private readonly string _gameExecutablePath;
    private readonly OodleImplementation _implementation;
    private readonly RotatingFileLogger _logger;
    private readonly OodleSignatureProfile _profile;
    private RuntimeContract? _contract;
    private IOodleNative? _installedNative;
    private readonly List<IOodleNative> _pendingCleanup = new();
    private bool _installed;
    private readonly OodleTempCopyCleaner? _cleaner;
    private readonly bool _requireExactRvas;
    // Set only for a discovered set whose capture knows the Collector's data directory.
    private string? _crashSentinelPath;

    /// <summary>
    /// File name prefix of the marker kept while a discovered set is in force. The rest of the
    /// name is the executable's SHA-256; the file holds that hash and nothing else - no path, no
    /// user, no packet data.
    /// </summary>
    public const string CrashSentinelPrefix = "oodle-discovered-";

    /// <summary>File name extension of that marker.</summary>
    public const string CrashSentinelExtension = ".marker";

    private OodleSignatureRuntime(
        string gameExecutablePath,
        OodleImplementation implementation,
        OodleSignatureProfile profile,
        RotatingFileLogger logger, OodleTempCopyCleaner? cleaner)
        : this(gameExecutablePath, implementation, profile, logger, cleaner, requireExactRvas: true)
    {
    }

    private OodleSignatureRuntime(
        string gameExecutablePath,
        OodleImplementation implementation,
        OodleSignatureProfile profile,
        RotatingFileLogger logger, OodleTempCopyCleaner? cleaner,
        bool requireExactRvas)
    {
        _gameExecutablePath = gameExecutablePath;
        _implementation = implementation;
        _profile = profile;
        _logger = logger;
        _cleaner = cleaner;
        _requireExactRvas = requireExactRvas;
    }

    /// <summary>
    /// True when the profile is a pattern donor for an executable it was not generated from
    /// (a client build with no signature profile of its own). The install then requires every
    /// signature to hit exactly once but does not compare RVAs, and a failure falls back to
    /// Machina's built-in table instead of refusing capture.
    /// </summary>
    public bool IsPatternFallback => !_requireExactRvas;

    /// <summary>Wire token of where the signatures in force came from.</summary>
    public string SourceToken => IsPatternFallback ? PatternFallbackSource : ProfileSource;

    /// <summary>Source token of an exactly matching profile.</summary>
    public const string ProfileSource = "profile";

    /// <summary>Source token of a donor profile re-scanned against a newer executable.</summary>
    public const string PatternFallbackSource = "pattern-fallback";

    /// <summary>The exact-match profile selected for this capture.</summary>
    public OodleSignatureProfile Profile => _profile;

    /// <summary>
    /// Selects a profile only when region, build, byte length and executable SHA-256 all match.
    /// Returning null means Machina's built-in scanner remains in effect.
    /// </summary>
    /// <param name="options">The already detected game and requested Oodle mode.</param>
    /// <param name="profileRoot">Profile root, or null to use the installed/default root.</param>
    /// <param name="logger">Local diagnostics.</param>
    /// <param name="cleaner">Owner of Machina's temp copies, or null.</param>
    /// <param name="dataDirectory">
    /// The Collector's data directory, where the marker of a discovered set in force is kept;
    /// null keeps no marker (audit 2026-11-20, A2-1).
    /// </param>
    public static OodleSignatureRuntime? TryCreate(
        CaptureStartOptions options,
        string? profileRoot = null,
        RotatingFileLogger? logger = null, OodleTempCopyCleaner? cleaner = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var localLogger = logger ?? RotatingFileLogger.Disabled;
        if (options.Oodle != OodleMode.FfxivTcp ||
            string.IsNullOrWhiteSpace(options.GameExecutablePath))
        {
            return null;
        }

        var executableHash = GameExecutableHash.Compute(options.GameExecutablePath);
        long executableSize = 0;
        try
        {
            var file = new FileInfo(options.GameExecutablePath);
            executableSize = file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            executableHash = null;
        }

        var root = profileRoot ?? ProfileCatalog.FindDefaultRoot();
        var directory = OodleSignatureProfile.DirectoryUnder(root);
        OodleSignatureProfile? profile = null;
        IReadOnlyList<string> problems = Array.Empty<string>();
        if (!string.IsNullOrWhiteSpace(executableHash) && executableSize > 0)
        {
            profile = OodleSignatureProfile.FindForExecutable(
                directory,
                options.Region,
                options.GameBuild,
                executableHash,
                executableSize,
                out problems);
        }

        if (profile is null && !string.IsNullOrWhiteSpace(executableHash) && executableSize > 0 &&
            OodleSignatureProfile.FindPatternDonor(directory, options.Region) is { } donor)
        {
            // A patch changes the executable, so no profile can match it by hash, but the
            // call-site patterns are wildcarded and normally survive. Re-scan them against the
            // new image; the built-in table is known not to find the CN training functions
            // (protocol-profiles/oodle-signatures/README.md), so this is what keeps a patched
            // client decodable long enough to calibrate.
            if (donor.PatternsFitExecutable(options.GameExecutablePath, out var donorProblem))
            {
                localLogger.Write(
                    LogLevel.Info,
                    "capture",
                    "oodle_signature_source",
                    new Dictionary<string, object?>
                    {
                        ["source"] = PatternFallbackSource,
                        ["profile_id"] = donor.Id,
                        ["donor_game_build"] = donor.GameBuild,
                        ["region"] = EnumWire<Region>.Format(options.Region),
                        ["game_build"] = options.GameBuild,
                        ["exe_hash_prefix"] = GameExecutableHash.Prefix(executableHash),
                    });
                return new OodleSignatureRuntime(
                    options.GameExecutablePath, OodleImplementation.FfxivTcp, donor, localLogger, cleaner,
                    requireExactRvas: false);
            }

            localLogger.Write(
                LogLevel.Warn,
                "capture",
                "oodle_signature_donor_rejected",
                new Dictionary<string, object?>
                {
                    ["profile_id"] = donor.Id,
                    ["game_build"] = options.GameBuild,
                    ["reason"] = donorProblem,
                });
        }

        var searchAllowed = profile is null && options.AllowCandidateOodleSignature &&
            !string.IsNullOrWhiteSpace(executableHash) && executableSize > 0;
        var sentinel = searchAllowed ? CrashSentinelPath(dataDirectory, executableHash!) : null;
        // A marker left behind: the last round that used a set found on this machine for this
        // executable never stopped cleanly, most likely because that set ended the Collector.
        // The code around its call sites is logged, as the round itself could not, and no set
        // is searched for this executable again; the marker stays.
        var crashed = sentinel is not null && File.Exists(sentinel);
        if (crashed)
        {
            LogContextFromFile(localLogger, options.GameExecutablePath, directory, "crashed");
        }

        if (searchAllowed && !crashed &&
            TryDiscover(options, directory, executableHash!, executableSize, localLogger) is { } discovered)
        {
            // No profile and no usable donor of this region - none at all, or one whose patterns
            // no longer fit this file (oodle_signature_donor_rejected above): a round the player
            // started may use what this machine can find by itself, like any other CANDIDATE.
            return new OodleSignatureRuntime(
                options.GameExecutablePath, OodleImplementation.FfxivTcp, discovered, localLogger, cleaner,
                requireExactRvas: false)
            {
                _crashSentinelPath = sentinel,
            };
        }

        if (profile is null)
        {
            localLogger.Write(
                LogLevel.Info,
                "capture",
                "oodle_signature_source",
                new Dictionary<string, object?>
                {
                    ["source"] = "builtin",
                    ["region"] = EnumWire<Region>.Format(options.Region),
                    ["game_build"] = options.GameBuild,
                    ["exe_hash_prefix"] = GameExecutableHash.Prefix(executableHash),
                    ["fallback_reason"] = string.IsNullOrWhiteSpace(executableHash)
                        ? "executable hash unavailable"
                        : crashed
                            ? "a signature set found on this machine did not end its last round cleanly; not searched again"
                            : directory is null
                                ? "signature profile directory missing"
                                : "no exact region/build/size/hash match",
                    ["refused_profile_count"] = problems.Count,
                });
            return null;
        }

        if (profile.Status == OodleSignatureProfile.CandidateStatus &&
            !options.AllowCandidateOodleSignature)
        {
            localLogger.Write(
                LogLevel.Info,
                "capture",
                "oodle_signature_source",
                new Dictionary<string, object?>
                {
                    ["source"] = "builtin",
                    ["profile_id"] = profile.Id,
                    ["region"] = profile.Region,
                    ["game_build"] = profile.GameBuild,
                    ["exe_hash_prefix"] = GameExecutableHash.Prefix(profile.ExeSha256),
                    ["fallback_reason"] = "candidate profile requires explicit trace opt-in",
                });
            return null;
        }

        localLogger.Write(
            LogLevel.Info,
            "capture",
            "oodle_signature_source",
            new Dictionary<string, object?>
            {
                ["source"] = "profile",
                ["profile_id"] = profile.Id,
                ["status"] = profile.Status,
                ["region"] = profile.Region,
                ["game_build"] = profile.GameBuild,
                ["exe_hash_prefix"] = GameExecutableHash.Prefix(profile.ExeSha256),
            });
        return new OodleSignatureRuntime(
            options.GameExecutablePath, OodleImplementation.FfxivTcp, profile, localLogger, cleaner);
    }

    /// <summary>
    /// Searches the executable for the twelve call sites with every pattern this installation
    /// knows (<see cref="OodleSignatureDiscovery.IsCallSite"/> decides what counts as one), and
    /// writes what it found to the local log whether or not it found them all: the
    /// log is among the files a tester returns, and the patterns, the executable's hash and its
    /// size are what a maintainer needs to write the region's profile without owning its client.
    /// </summary>
    private static OodleSignatureProfile? TryDiscover(
        CaptureStartOptions options,
        string? directory,
        string executableHash,
        long executableSize,
        RotatingFileLogger logger)
    {
        byte[] image;
        try
        {
            image = File.ReadAllBytes(options.GameExecutablePath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var sources = OodleSignatureDiscovery.KnownSources(directory);
        var result = OodleSignatureDiscovery.Discover(image, sources);
        static string Names(IEnumerable<OodleSignatureFinding> findings) =>
            string.Join(',', findings.Select(finding => finding.Type.ToString()));
        var accepted = result.Findings.Where(finding => finding.Pattern is not null).ToArray();
        logger.Write(
            result.Complete ? LogLevel.Info : LogLevel.Warn,
            "capture",
            "oodle_signature_discovery",
            new Dictionary<string, object?>
            {
                ["complete"] = result.Complete,
                ["region"] = EnumWire<Region>.Format(options.Region),
                ["game_build"] = options.GameBuild,
                // In groups of eight: the logger redacts sixteen hexadecimal digits in a row,
                // and a profile is bound to the whole hash. It is the game file's, not a person's.
                ["exe_sha256"] = string.Join(' ', Enumerable.Range(0, executableHash.Length / 8)
                    .Select(group => executableHash.Substring(group * 8, 8))),
                ["exe_size"] = executableSize,
                ["missing"] = Names(result.Findings.Where(f => f.Outcome == OodleSignatureOutcome.Missing)),
                ["ambiguous"] = Names(result.Findings.Where(f => f.Outcome == OodleSignatureOutcome.Ambiguous)),
                ["sources"] = string.Join(',', accepted.Select(f => $"{f.Type}={f.Source}")),
                ["relaxed"] = string.Join(',', accepted
                    .Where(f => f.Outcome == OodleSignatureOutcome.Relaxed)
                    .Select(f => $"{f.Type}@{f.RelaxedIndex}={f.ActualByte:x2}")),
                ["signatures"] = string.Join(';', accepted
                    .Select(f => $"{f.Type}={OodleSignaturePattern.Render(f.Pattern!)}")),
            });
        if (!result.Complete)
        {
            LogContext(logger, image, result, sources, "incomplete");
            return null;
        }

        var profile = OodleSignatureProfile.Discovered(
            options.Region, options.GameBuild ?? string.Empty, executableHash, executableSize, result.Patterns);
        logger.Write(
            LogLevel.Info,
            "capture",
            "oodle_signature_source",
            new Dictionary<string, object?>
            {
                ["source"] = PatternFallbackSource,
                ["profile_id"] = profile.Id,
                ["status"] = profile.Status,
                ["region"] = profile.Region,
                ["game_build"] = profile.GameBuild,
                ["exe_hash_prefix"] = GameExecutableHash.Prefix(executableHash),
            });
        return profile;
    }

    /// <summary>True when the signatures in force were found on this machine, not read from a profile.</summary>
    public bool IsDiscovered => _profile.Id.StartsWith("discovered.", StringComparison.Ordinal);

    /// <summary>
    /// Writes the code around the call sites to the local log after a round that used a
    /// discovered set and decoded nothing: the set was wrong somewhere, and without this the
    /// tester would have to be asked for another run. Never called for a set that worked.
    /// </summary>
    /// <param name="profileRoot">Profile root, or null for the installed one.</param>
    public void LogContextAfterFailedDecode(string? profileRoot = null)
    {
        if (!IsDiscovered)
        {
            return;
        }

        LogContextFromFile(
            _logger,
            _gameExecutablePath,
            OodleSignatureProfile.DirectoryUnder(profileRoot ?? ProfileCatalog.FindDefaultRoot()),
            "decode_failed");
    }

    /// <summary>
    /// Removes the marker of this discovered set: the capture that used it stopped cleanly.
    /// Does nothing for any other set. A marker that cannot be removed is only logged; the next
    /// round then treats the set as one that ended the Collector, which costs a search, not data.
    /// </summary>
    public void ClearCrashSentinel()
    {
        if (_crashSentinelPath is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.WriteError("capture", "oodle_signature_sentinel_failed", ex);
        }
    }

    /// <summary>Writes the marker before a discovered set is handed to Machina.</summary>
    private void ArmCrashSentinel()
    {
        if (_crashSentinelPath is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _profile.ExeSha256 + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without a marker a crash is not remembered: the round goes on as it would have
            // before markers existed, and the failure is in the log.
            _logger.WriteError("capture", "oodle_signature_sentinel_failed", ex);
        }
    }

    /// <summary>
    /// Where the marker of a discovered set for <paramref name="executableHash"/> is kept, or
    /// null when there is no data directory or the hash is not plain lowercase hexadecimal.
    /// </summary>
    /// <param name="dataDirectory">The Collector's data directory.</param>
    /// <param name="executableHash">SHA-256 of the executable, lowercase hex.</param>
    public static string? CrashSentinelPath(string? dataDirectory, string executableHash)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || executableHash.Length != 64 ||
            !executableHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return null;
        }

        return Path.Combine(dataDirectory, CrashSentinelPrefix + executableHash + CrashSentinelExtension);
    }

    /// <summary>Re-reads the executable and logs the last-resort excerpts for it.</summary>
    private static void LogContextFromFile(
        RotatingFileLogger logger, string executablePath, string? directory, string reason)
    {
        byte[] image;
        try
        {
            image = File.ReadAllBytes(executablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }

        var sources = OodleSignatureDiscovery.KnownSources(directory);
        LogContext(logger, image, OodleSignatureDiscovery.Discover(image, sources), sources, reason);
    }

    private static void LogContext(
        RotatingFileLogger logger,
        byte[] image,
        OodleSignatureDiscoveryResult result,
        IReadOnlyList<(string Source, IReadOnlyDictionary<SignatureType, int[]> Patterns)> sources,
        string reason)
    {
        // File offsets become RVAs with the section table, which is all this adds to them.
        logger.Write(LogLevel.Warn, "capture", "oodle_signature_sections", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["sections"] = string.Join(',', PeSections(image)
                .Select(section => $"{section.VirtualAddress:X8}:{section.RawOffset:X8}:{section.RawSize:X8}")),
        });
        foreach (var excerpt in OodleSignatureDiscovery.Context(image, result, sources))
        {
            logger.Write(LogLevel.Warn, "capture", "oodle_signature_context", new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["signature"] = excerpt.Type.ToString(),
                ["kind"] = excerpt.Kind,
                ["source"] = excerpt.Source,
                ["mismatches"] = excerpt.Mismatches,
                ["file_offset"] = excerpt.Offset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                ["site_offset"] = excerpt.SiteOffset.ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
                ["bytes"] = string.Join(' ', excerpt.Bytes.Select(value =>
                    value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))),
            });
        }
    }

    /// <summary>The section table of a PE file: where each section lies in memory and in the file.</summary>
    /// <param name="image">Bytes of the executable file.</param>
    public static IReadOnlyList<PeSection> PeSections(ReadOnlySpan<byte> image)
    {
        var sections = new List<PeSection>();
        const int sectionHeaderBytes = 40;
        if (image.Length < 0x40 || image[0] != 0x4d || image[1] != 0x5a)
        {
            return sections;
        }

        var pe = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(0x3c, 4));
        if (pe <= 0 || (long)pe + 24 > image.Length ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(pe, 4)) != 0x00004550)
        {
            return sections;
        }

        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(pe + 6, 2));
        var optionalSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(pe + 20, 2));
        var table = pe + 24 + optionalSize;
        for (var i = 0; i < count && i < 96; i++)
        {
            var at = table + (i * sectionHeaderBytes);
            if ((long)at + sectionHeaderBytes > image.Length)
            {
                break;
            }

            sections.Add(new PeSection(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(at + 12, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(at + 8, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(at + 20, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(image.Slice(at + 16, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(at + 36, 4))));
        }

        return sections;
    }

    /// <summary>Confirms that the private Machina contract still has the supported shape.</summary>
    /// <param name="reason">Stable non-sensitive refusal reason.</param>
    public static bool ValidateMachinaContract(out string reason) =>
        TryGetRuntimeContract(out _, out reason);

    /// <summary>
    /// Installs and initializes the custom scanner. Call before attaching the per-capture
    /// Machina trace listener and before <c>FFXIVNetworkMonitor.Start</c>.
    /// </summary>
    public void Install()
    {
        if (_pendingCleanup.Count > 0) throw Refused(CollectorTexts.CaptureOodlePreviousNotReleased());
        if (_installed)
        {
            return;
        }

        if (!TryGetRuntimeContract(out var contract, out var refusal) || contract is null)
        {
            _logger.Write(
                LogLevel.Error,
                "capture",
                "oodle_signature_runtime_incompatible",
                new Dictionary<string, object?> { ["reason"] = refusal });
            throw Refused(CollectorTexts.CaptureOodleMachinaIncompatible());
        }

        var scan = new OodleSignatureScan(_profile);
        var native = new OodleNative_Ffxiv(scan);
        // From here on Machina may run the functions the set names inside this process.
        ArmCrashSentinel();
        try
        {
            lock (contract.SyncRoot)
            {
                // The lock is Monitor-based and therefore re-entrant on this thread. Calling
                // the public factory here both follows Machina's initialization contract and
                // sets its private implementation enum without a race window.
                if (_cleaner is null) OodleFactory.SetImplementation(_implementation, _gameExecutablePath);
                else _cleaner.TrackFactoryInitialization(() => OodleFactory.SetImplementation(_implementation, _gameExecutablePath));

                if (contract.NativeField.GetValue(null) is IOodleNative previous)
                {
                    previous.UnInitialize();
                }

                // Never leave the just-uninitialized built-in object visible in static state.
                contract.NativeField.SetValue(null, null);
                contract.NativeField.SetValue(null, native);
                if (_cleaner is null) native.Initialize(_gameExecutablePath);
                else _cleaner.TrackInitialization(native, () => native.Initialize(_gameExecutablePath));
                var scanComplete = ScanMatchesProfile(scan.LastResults, _profile, _requireExactRvas);
                if (!native.Initialized || !scanComplete)
                {
                    throw new InvalidOperationException(
                        "the profile-backed Oodle scan was incomplete or ambiguous");
                }

                _contract = contract;
                _installedNative = native;
                _installed = true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The set was refused without ending the process: it is not in force.
            ClearCrashSentinel();
            try
            {
                lock (contract.SyncRoot)
                {
                    var visibleNative = contract.NativeField.GetValue(null) as IOodleNative;
                    _contract = contract;
                    if (visibleNative is not null) _pendingCleanup.Add(visibleNative);
                    if (!ReferenceEquals(visibleNative, native)) _pendingCleanup.Add(native);
                    // Retain each partially initialized object until UnInitialize succeeds.
                    // The capture owner holds the lease when this rollback fails.
                    Dispose();
                }
            }
            catch (Exception cleanupError)
                when (cleanupError is not OutOfMemoryException and not StackOverflowException)
            {
                _logger.WriteError(
                    "capture", "oodle_signature_override_cleanup_failed", cleanupError);
            }

            LogScanResults(scan);
            // Any failure lands here - a full temp folder, an antivirus scan of the copy, a profile
            // that no longer fits - so the player's sentence names none of them for certain; the
            // tool a maintainer regenerates the profile with is named in the local log only
            // (audit 2026-10-20, A2-4).
            _logger.WriteError("capture", "oodle_signature_override_failed", ex,
                "if the profile no longer fits, regenerate and verify it with tools/oodle-signature-finder");
            throw Refused(CollectorTexts.CaptureOodleProfileLoadFailed(), ex);
        }

        LogScanResults(scan);
    }

    /// <summary>
    /// The fail-closed comparison between what the scanner found in the mapped image and what
    /// the profile claims it would find.
    ///
    /// Every signature must match exactly once <em>and</em> resolve to exactly the RVA the
    /// profile declares. Both halves matter: dropping the count check accepts an ambiguous
    /// pattern, and inverting the RVA comparison accepts a scanner that located the wrong
    /// bytes, installing a mislocated Oodle decoder that corrupts every bundle -- the precise
    /// failure this profile mechanism exists to prevent. A separate method so the decision can
    /// be tested directly rather than only through a live install (review finding M-9).
    /// </summary>
    /// <param name="results">What the scanner reported, one entry per signature type.</param>
    /// <param name="profile">Profile whose declared RVAs are the expectation.</param>
    public static bool ScanMatchesProfile(
        IReadOnlyList<OodleSignatureMatch> results, OodleSignatureProfile profile, bool requireExactRvas = true)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(profile);

        return results.Count == Enum.GetValues<SignatureType>().Length &&
            results.All(result =>
                result.MatchCount == 1 &&
                result.ResolvedRva is { } actualRva &&
                (!requireExactRvas ||
                 (profile.ResolvedRvas.TryGetValue(result.Type, out var expectedRva) && actualRva == expectedRva)));
    }

    private void LogScanResults(OodleSignatureScan scan)
    {
        var missing = scan.LastResults
            .Where(result => result.MatchCount == 0 || result.ResolvedRva is null)
            .Select(result => result.Type.ToString())
            .ToArray();
        var ambiguous = scan.LastResults
            .Where(result => result.MatchCount > 1)
            .Select(result => result.Type.ToString())
            .ToArray();
        var mismatched = scan.LastResults
            .Where(result =>
                result.MatchCount == 1 &&
                result.ResolvedRva is { } actualRva &&
                _profile.ResolvedRvas.TryGetValue(result.Type, out var expectedRva) &&
                actualRva != expectedRva)
            .Select(result => result.Type.ToString())
            .ToArray();
        var found = scan.LastResults
            .Where(result => result.MatchCount == 1 && result.ResolvedRva is not null)
            .Select(result => result.Type.ToString())
            .ToArray();
        _logger.Write(
            LogLevel.Info,
            "capture",
            "oodle_signature_scan",
            new Dictionary<string, object?>
            {
                ["profile_id"] = _profile.Id,
                ["found"] = string.Join(',', found),
                ["missing"] = string.Join(',', missing),
                ["ambiguous"] = string.Join(',', ambiguous),
                ["rva_mismatch"] = string.Join(',', mismatched),
                // Where each call resolved in the mapped image: with the patterns above, all a
                // profile for this executable needs.
                ["rvas"] = string.Join(',', scan.LastResults
                    .Where(result => result.MatchCount == 1 && result.ResolvedRva is not null)
                    .Select(result => $"{result.Type}={result.ResolvedRva:X8}")),
            });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var contract = _contract;
        if (contract is null)
        {
            return;
        }

        try
        {
            lock (contract.SyncRoot)
            {
                if (_installedNative is { } installed && !_pendingCleanup.Contains(installed))
                    _pendingCleanup.Add(installed);
                while (_pendingCleanup.Count > 0)
                {
                    var native = _pendingCleanup[0];
                    native.UnInitialize();
                    if (ReferenceEquals(contract.NativeField.GetValue(null), native))
                        contract.NativeField.SetValue(null, null);
                    _pendingCleanup.RemoveAt(0);
                    if (ReferenceEquals(_installedNative, native)) _installedNative = null;
                }
                _contract = null;
                _installed = false;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.WriteError("capture", "oodle_signature_override_cleanup_failed", ex);
            throw new CollectorException(ErrorCodes.Internal, "Oodle 原生资源释放失败，已保留资源供停止操作重试。", inner: ex);
        }
    }

    private static bool TryGetRuntimeContract(out RuntimeContract? contract, out string reason)
    {
        contract = null;
        var assemblyVersion = typeof(OodleFactory).Assembly.GetName().Version?.ToString();
        if (!string.Equals(assemblyVersion, SupportedMachinaVersion, StringComparison.Ordinal))
        {
            reason = "unsupported Machina assembly version";
            return false;
        }

        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var lockField = typeof(OodleFactory).GetField("_lock", flags);
        var nativeField = typeof(OodleFactory).GetField("_oodleNative", flags);
        var implementationField = typeof(OodleFactory).GetField("_oodleImplementation", flags);
        if (lockField?.FieldType != typeof(object) || lockField.GetValue(null) is not object syncRoot ||
            nativeField?.FieldType != typeof(IOodleNative) ||
            implementationField?.FieldType != typeof(OodleImplementation))
        {
            reason = "OodleFactory private layout changed";
            return false;
        }

        var constructor = typeof(OodleNative_Ffxiv).GetConstructor(new[] { typeof(ISigScan) });
        var initialized = typeof(OodleNative_Ffxiv).GetProperty(
            nameof(OodleNative_Ffxiv.Initialized), BindingFlags.Instance | BindingFlags.Public);
        if (constructor is null || initialized?.PropertyType != typeof(bool))
        {
            reason = "OodleNative_Ffxiv contract changed";
            return false;
        }

        contract = new RuntimeContract(syncRoot, nativeField);
        reason = string.Empty;
        return true;
    }

    /// <param name="message">The whole refusal, its advice to regenerate the signature profile included.</param>
    /// <param name="inner">Underlying exception, if any.</param>
    private static CollectorException Refused(UserText message, Exception? inner = null) =>
        new(ErrorCodes.Internal, message, inner: inner);

    private sealed record RuntimeContract(object SyncRoot, FieldInfo NativeField);
}
