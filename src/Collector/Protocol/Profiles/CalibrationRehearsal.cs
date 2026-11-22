using System.Text.Json;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// A maintainer's rehearsal of self-calibration on a client build the software already ships a profile for.
///
/// Self-calibration only ever runs after a game update, on a build no profile names, so a template's rules
/// cannot be tried on real traffic until the next patch. A rehearsal makes this machine treat one shipped
/// build as unknown: the merged catalogue withholds the shipped profile of exactly that region and build from
/// selection (<see cref="ProfileCatalog.WithheldEntries"/>), so calibration arms as it would after a patch, and
/// the profile it writes for the build is the one that records. Calibration templates are read from the shipped
/// directory alone (<see cref="ProfileCatalog.LoadDefault"/>), so the withheld profile remains the template.
///
/// The switch is a file, <see cref="FileName"/>, in the managed data directory, holding
/// <c>{"region": "GLOBAL", "game_build": "2026.11.03.0000.0000"}</c>. Nothing in the software creates it and
/// no setting shows it (docs/protocol-profile-format.md section 11.8). It names one build so that it lapses by
/// itself: after the next update the client reports another build and the shipped profiles apply as usual.
/// A file that is missing, larger than <see cref="MaxFileBytes"/>, not JSON, or that names no known region
/// or no build is no rehearsal at all.
/// </summary>
/// <param name="Region">Region of the rehearsed build: CN, GLOBAL or TC.</param>
/// <param name="GameBuild">The rehearsed client build, compared without regard to case.</param>
public sealed record CalibrationRehearsal(Region Region, string GameBuild)
{
    /// <summary>Name of the switch file under <see cref="DatabasePaths.RootDirectory"/>.</summary>
    public const string FileName = "calibration-rehearsal.json";

    /// <summary>Largest switch file read; anything longer is not one.</summary>
    public const int MaxFileBytes = 4096;

    private static readonly Regex BuildPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);

    /// <summary>Where the switch file of this installation lives, whether or not it exists.</summary>
    public static string DefaultPath => Path.Combine(DatabasePaths.RootDirectory, FileName);

    /// <summary>The rehearsal the switch file of this installation names, or null.</summary>
    public static CalibrationRehearsal? ReadDefault() => Read(DefaultPath);

    /// <summary>True when <paramref name="profile"/> is the formal profile this rehearsal withholds.</summary>
    /// <param name="profile">A profile read from the shipped directory.</param>
    public bool Withholds(ProtocolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Status != ProfileCompatibilityStatus.Candidate && profile.Region == Region &&
            string.Equals(profile.GameBuild, GameBuild, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a switch file. Never throws; anything but a well-formed switch is null.</summary>
    /// <param name="path">File to read.</param>
    public static CalibrationRehearsal? Read(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes)
            {
                return null;
            }

            // Read as text, not bytes: the file is written by hand, and Windows PowerShell writes UTF-16 or UTF-8
            // with a byte order mark, either of which a byte-level parse refuses.
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("region", out var regionText) || regionText.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("game_build", out var buildText) || buildText.ValueKind != JsonValueKind.String ||
                !EnumWire<Region>.TryParse(regionText.GetString()!.Trim().ToUpperInvariant(), out var region) ||
                region is not (Region.Cn or Region.Global or Region.Tc))
            {
                return null;
            }

            var build = buildText.GetString()!.Trim();
            return BuildPattern.IsMatch(build) ? new CalibrationRehearsal(region, build) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // InvalidOperationException: a string GetString cannot read (a lone surrogate escape).
            return null;
        }
    }
}
