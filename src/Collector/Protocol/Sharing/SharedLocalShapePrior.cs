using System.Globalization;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// The shape prior this machine computes for itself (plan §20.5, security review 2026-10-15): does a code name the match
/// with the shape the calibration this machine itself verified for the previous build had?
///
/// The public index carries a <c>shape_prior</c> of its own, but it is computed from whatever the repository holds, and
/// one account can publish a code for an invented earlier build to plant it. A repository value must never decide which
/// code binds on the login burst, so the consensus breaks a tied match only with this prior: one read from profiles this
/// machine verified - its own calibrations, written only after the player confirmed the timeline, and other players'
/// calibrations it verified, recorded a whole duty with and settled (plan §18.4). The index value only orders downloads.
///
/// The rule follows the publishing tool's (<c>tools/shared-calibration/index.py</c>, <c>_shape_priors</c>): the
/// comparable profiles are those of the same region, read as calibrated shapes of the template in force - the template
/// line, whatever version of it they were written under - naming the match the same way, for a build dated strictly
/// earlier (two builds of one day cannot be ordered and are not compared). Only the latest such build counts, ordered by
/// date and then by build text. The prior is true when one of its profiles has the candidate's shape, false when none
/// has, and null when there is no such build or the candidate has no shape. The shape is what a patch leaves alone while
/// it reshuffles opcodes: an announcement's length, a marker's length and roulette offset, a reply state's selector
/// values. A queue request has none. Pure: no IO, no clock.
/// </summary>
public static class SharedLocalShapePrior
{
    private static readonly Regex DatedBuild = new(
        @"^([0-9]{4})\.([0-9]{2})\.([0-9]{2})\.[0-9]{4}\.[0-9]{4}\z", RegexOptions.CultureInvariant);

    /// <summary>The calendar date a client build carries, or null when it does not have that shape or the date is not real.</summary>
    /// <param name="gameBuild">Client build, e.g. <c>2026.09.01.0000.0000</c>.</param>
    public static DateOnly? BuildDate(string? gameBuild)
    {
        if (gameBuild is null || DatedBuild.Match(gameBuild) is not { Success: true } match)
        {
            return null;
        }

        var (year, month, day) = (Number(match.Groups[1].Value), Number(match.Groups[2].Value), Number(match.Groups[3].Value));
        return year >= 1 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    /// <summary>
    /// What a code's match looks like apart from its opcode, as one comparable value carrying the match source; null for a
    /// queue request, which has no shape.
    /// </summary>
    /// <param name="payload">A decoded or recovered payload.</param>
    public static string? Shape(ShareCodePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var pop = payload.Pop;
        string? shape = payload.MatchSource switch
        {
            CalibrationMatchSource.Announcement => Text(pop.Length),
            CalibrationMatchSource.MarkerOffset => Text(pop.Length) + "," + Text(pop.RouletteOffset),
            CalibrationMatchSource.ReplyState => string.Join(
                ",", (pop.SelectorValues ?? Array.Empty<long>()).Select(value => value.ToString(CultureInfo.InvariantCulture))),
            _ => null,
        };
        return shape is null ? null : EnumWire<CalibrationMatchSource>.Format(payload.MatchSource) + ":" + shape;
    }

    /// <summary>
    /// The payloads of profiles this machine verified, read through the template in force. A profile that is not exactly a
    /// calibrated shape of this template - another region, another template line, a shipped profile of another layout -
    /// is left out.
    /// </summary>
    /// <param name="profiles">Profiles the caller vouches this machine verified.</param>
    /// <param name="template">The template in force.</param>
    public static IReadOnlyList<ShareCodePayload> Verified(IEnumerable<ProtocolProfile> profiles, CalibrationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(template);
        return profiles
            .OfType<ProtocolProfile>()
            .Select(profile => SharedProfileBuilder.RecoverPayload(profile, template))
            .OfType<ShareCodePayload>()
            .ToArray();
    }

    /// <summary>The prior of one candidate against what this machine verified.</summary>
    /// <param name="candidate">The candidate's payload.</param>
    /// <param name="verified">Payloads of the profiles this machine verified (<see cref="Verified"/>).</param>
    public static bool? For(ShareCodePayload candidate, IEnumerable<ShareCodePayload> verified)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(verified);
        if (Shape(candidate) is not { } shape || BuildDate(candidate.GameBuild) is not { } date)
        {
            return null;
        }

        var comparable = verified
            .Where(other => other.Region == candidate.Region && other.MatchSource == candidate.MatchSource &&
                            string.Equals(other.TemplateSha256, candidate.TemplateSha256, StringComparison.Ordinal))
            .Select(other => (Date: BuildDate(other.GameBuild), other.GameBuild, Shape: Shape(other)))
            .Where(other => other.Date is { } earlier && earlier < date)
            .ToArray();
        if (comparable.Length == 0)
        {
            return null;
        }

        var latest = comparable
            .OrderByDescending(other => other.Date)
            .ThenByDescending(other => other.GameBuild, StringComparer.Ordinal)
            .First();
        return comparable
            .Where(other => other.Date == latest.Date && string.Equals(other.GameBuild, latest.GameBuild, StringComparison.Ordinal))
            .Any(other => string.Equals(other.Shape, shape, StringComparison.Ordinal));
    }

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "-";
}
