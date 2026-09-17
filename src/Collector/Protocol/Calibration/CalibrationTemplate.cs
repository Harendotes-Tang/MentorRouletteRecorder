using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// The shape knowledge one shipped profile lends to calibration on a build it does not match:
/// the four message structures, the roulette request shape, the mentor roulette id and the
/// match window. Nothing here carries an opcode into the new build; opcodes are exactly what
/// calibration re-learns.
/// </summary>
public sealed class CalibrationTemplate
{
    private CalibrationTemplate(ProtocolProfile source, ProfileCalibration calibration)
    {
        Source = source;
        Calibration = calibration;
        Pop = source.Message("CONTENT_FINDER_POP")!;
        ZoneInitialization = source.Message("ZONE_INITIALIZATION")!;
        ZoneTerritory = source.Message("ZONE_TERRITORY");
        PlayerJob = source.Message("PLAYER_JOB");
    }

    /// <summary>The profile the template was taken from.</summary>
    public ProtocolProfile Source { get; }

    /// <summary>The request shape and echo window declared by the template.</summary>
    public ProfileCalibration Calibration { get; }

    /// <summary>Pop structure; every template has one.</summary>
    public ProfileMessage Pop { get; }

    /// <summary>Zone-change marker structure; every template has one.</summary>
    public ProfileMessage ZoneInitialization { get; }

    /// <summary>Territory structure, when the template declares one.</summary>
    public ProfileMessage? ZoneTerritory { get; }

    /// <summary>Job structure, when the template declares one.</summary>
    public ProfileMessage? PlayerJob { get; }

    /// <summary>Mentor roulette id inherited by every calibrated profile.</summary>
    public int MentorRouletteId => Source.MentorRouletteId!.Value;

    /// <summary>Match window inherited by every calibrated profile.</summary>
    public TimeSpan MatchWindow => Source.MatchWindow;

    /// <summary>Region the template describes.</summary>
    public Region Region => Source.Region;

    /// <summary>
    /// Wraps one profile as a template, or returns null when it cannot serve as one: no
    /// calibration section, no roulette id, or a missing required message.
    /// </summary>
    /// <param name="profile">Candidate template.</param>
    public static CalibrationTemplate? From(ProtocolProfile? profile)
    {
        if (profile?.Calibration is not { } calibration || profile.MentorRouletteId is null ||
            profile.Message("CONTENT_FINDER_POP") is null || profile.Message("ZONE_INITIALIZATION") is null ||
            profile.Message("CONTENT_FINDER_POP")!.ExpectedLength is null ||
            profile.Message("ZONE_INITIALIZATION")!.ExpectedLength is null)
        {
            return null;
        }

        return new CalibrationTemplate(profile, calibration);
    }

    /// <summary>
    /// Picks the template for a region from a catalogue: the VERIFIED profile with the highest
    /// build (ordinal comparison) that carries a calibration section. Two profiles tied on the
    /// same build are refused rather than ordered by path, mirroring the catalogue's own
    /// ambiguity rule. SYNTHETIC profiles take part only when asked, for offline fixtures.
    /// </summary>
    /// <param name="catalog">Catalogue to search; the shipped one in production.</param>
    /// <param name="region">Region of the running client.</param>
    /// <param name="allowSynthetic">Whether SYNTHETIC templates may be chosen.</param>
    public static CalibrationTemplate? Select(ProfileCatalog catalog, Region region, bool allowSynthetic = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var eligible = catalog.UsableFor(region)
            .Where(profile => profile.Calibration is not null)
            .Where(profile => profile.Status == ProfileCompatibilityStatus.Verified ||
                (allowSynthetic && profile.Status == ProfileCompatibilityStatus.Synthetic))
            .Select(From)
            .Where(template => template is not null)
            .Select(template => template!)
            .OrderByDescending(template => template.Source.GameBuild, StringComparer.Ordinal)
            .ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var best = eligible[0];
        var tied = eligible.Count(template =>
            string.Equals(template.Source.GameBuild, best.Source.GameBuild, StringComparison.Ordinal));
        return tied == 1 ? best : null;
    }
}
