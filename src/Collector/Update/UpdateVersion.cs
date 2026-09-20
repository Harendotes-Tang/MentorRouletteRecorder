using System.Globalization;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// A release version as the update check compares them: three numbers, plus the prerelease
/// label the version they came from carried, if any.
///
/// The two sides are read by different entry points on purpose. <see cref="TryParseLocal"/> reads
/// this build's own stamp, which may carry a <c>-beta.1</c> or <c>+commit</c> suffix;
/// <see cref="TryParse"/> reads the published one, which comes off the network and is accepted
/// only as three plain numbers, so nothing a published file says is ever interpreted. Pure: no
/// clock, no IO.
/// </summary>
/// <param name="Major">Major component.</param>
/// <param name="Minor">Minor component.</param>
/// <param name="Patch">Patch component.</param>
/// <param name="Prerelease">True when the version carried a <c>-</c> suffix; build metadata is not one.</param>
/// <param name="PrereleaseLabel">
/// The <c>-</c> suffix without its leading dash and without any <c>+</c> build metadata
/// (<c>beta.1</c> for <c>1.4.0-beta.1+2f3a4b5</c>), or null when there was none. Kept so two
/// prereleases of the same release can be ordered against each other.
/// </param>
public readonly record struct UpdateVersion(
    int Major, int Minor, int Patch, bool Prerelease = false, string? PrereleaseLabel = null)
{
    /// <summary>Most digits one component may have, so a component can never overflow.</summary>
    public const int MaxComponentDigits = 6;

    /// <summary>Longest prerelease label kept; anything past this is truncated, never parsed.</summary>
    public const int MaxPrereleaseLabelLength = 64;

    private static readonly Regex Pattern = new(
        @"^([0-9]{1,6})\.([0-9]{1,6})\.([0-9]{1,6})\z", RegexOptions.CultureInvariant);

    private static readonly Regex NumericIdentifier = new(
        @"^[0-9]{1,9}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads a published version: exactly three numbers of at most
    /// <see cref="MaxComponentDigits"/> digits, with no suffix and no surrounding space.
    /// </summary>
    /// <param name="text">Candidate version.</param>
    /// <param name="version">The version read; default when false is returned.</param>
    public static bool TryParse(string? text, out UpdateVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return false;
        }

        version = new UpdateVersion(Number(match, 1), Number(match, 2), Number(match, 3));
        return true;
    }

    /// <summary>
    /// Reads this build's own version: the same three numbers, with surrounding space and a
    /// <c>-</c> prerelease or <c>+</c> build suffix allowed. A suffix must not be empty. The
    /// prerelease label is kept (without its build metadata); build metadata is discarded,
    /// because it takes no part in precedence.
    /// </summary>
    /// <param name="text">Candidate version.</param>
    /// <param name="version">The version read; default when false is returned.</param>
    public static bool TryParseLocal(string? text, out UpdateVersion version)
    {
        version = default;
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();
        var marker = trimmed.IndexOfAny(new[] { '-', '+' });
        var prerelease = false;
        string? label = null;
        if (marker >= 0)
        {
            if (marker == trimmed.Length - 1)
            {
                return false;
            }

            prerelease = trimmed[marker] == '-';
            if (prerelease)
            {
                var suffix = trimmed[(marker + 1)..];
                var build = suffix.IndexOf('+');
                if (build >= 0)
                {
                    suffix = suffix[..build];
                }

                if (suffix.Length == 0)
                {
                    return false;
                }

                label = suffix.Length > MaxPrereleaseLabelLength
                    ? suffix[..MaxPrereleaseLabelLength]
                    : suffix;
            }

            trimmed = trimmed[..marker];
        }

        if (!TryParse(trimmed, out var core))
        {
            return false;
        }

        version = core with { Prerelease = prerelease, PrereleaseLabel = label };
        return true;
    }

    /// <summary>
    /// True when <paramref name="published"/> is an upgrade this build should be told about.
    ///
    /// Strictly newer by <see cref="Compare"/>, with one refusal on top of it: a prerelease is
    /// never an upgrade target for a stable build, however high its numbers. A test build is
    /// handed out on purpose, not offered to everyone; the published feed is
    /// <c>releases/latest</c>, which skips GitHub prereleases, and <see cref="TryParse"/> refuses
    /// a suffix outright, so this is the last of three layers rather than the only one.
    /// </summary>
    /// <param name="published">Version the published metadata names.</param>
    /// <param name="running">Version this build reports.</param>
    public static bool IsNewer(UpdateVersion published, UpdateVersion running) =>
        (!published.Prerelease || running.Prerelease) && Compare(published, running) > 0;

    /// <summary>
    /// Orders two versions the way semantic versioning does: by the three numbers, then a
    /// prerelease below the release it was cut towards (<c>1.4.0-beta.1</c> &lt; <c>1.4.0</c>),
    /// then label identifier by label identifier - numerically where an identifier is all
    /// digits, so <c>beta.10</c> comes after <c>beta.9</c>. Build metadata is not compared.
    /// </summary>
    /// <param name="left">Left version.</param>
    /// <param name="right">Right version.</param>
    public static int Compare(UpdateVersion left, UpdateVersion right)
    {
        var numbers = left.Major != right.Major
            ? left.Major.CompareTo(right.Major)
            : left.Minor != right.Minor
                ? left.Minor.CompareTo(right.Minor)
                : left.Patch.CompareTo(right.Patch);
        if (numbers != 0)
        {
            return numbers;
        }

        if (left.Prerelease != right.Prerelease)
        {
            return left.Prerelease ? -1 : 1;
        }

        return left.Prerelease ? CompareLabels(left.PrereleaseLabel, right.PrereleaseLabel) : 0;
    }

    /// <inheritdoc />
    public override string ToString() => Prerelease && PrereleaseLabel is { } label
        ? string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}-{3}", Major, Minor, Patch, label)
        : string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);

    /// <summary>
    /// Compares two prerelease labels identifier by identifier, as semantic versioning does:
    /// two numeric identifiers compare as numbers, a numeric identifier ranks below an
    /// alphanumeric one, anything else compares by ordinal, and a label that runs out of
    /// identifiers first ranks lower.
    /// </summary>
    private static int CompareLabels(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : -1) : 1;
        }

        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var order = CompareIdentifier(leftParts[index], rightParts[index]);
            if (order != 0)
            {
                return order;
            }
        }

        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = NumericIdentifier.IsMatch(left);
        var rightNumeric = NumericIdentifier.IsMatch(right);
        if (leftNumeric && rightNumeric)
        {
            return int.Parse(left, NumberStyles.None, CultureInfo.InvariantCulture)
                .CompareTo(int.Parse(right, NumberStyles.None, CultureInfo.InvariantCulture));
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static int Number(Match match, int group) =>
        int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
}
