using System.Globalization;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// A release version as the update check compares them: three numbers, plus whether the version
/// they came from was a prerelease.
///
/// The two sides are read by different entry points on purpose. <see cref="TryParseLocal"/> reads
/// this build's own stamp, which may carry a <c>-rc.1</c> or <c>+commit</c> suffix;
/// <see cref="TryParse"/> reads the published one, which comes off the network and is accepted
/// only as three plain numbers, so nothing a published file says is ever interpreted. Pure: no
/// clock, no IO.
/// </summary>
/// <param name="Major">Major component.</param>
/// <param name="Minor">Minor component.</param>
/// <param name="Patch">Patch component.</param>
/// <param name="Prerelease">True when the version carried a <c>-</c> suffix; build metadata is not one.</param>
public readonly record struct UpdateVersion(int Major, int Minor, int Patch, bool Prerelease = false)
{
    /// <summary>Most digits one component may have, so a component can never overflow.</summary>
    public const int MaxComponentDigits = 6;

    private static readonly Regex Pattern = new(
        @"^([0-9]{1,6})\.([0-9]{1,6})\.([0-9]{1,6})\z", RegexOptions.CultureInvariant);

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
    /// <c>-</c> prerelease or <c>+</c> build suffix allowed. A suffix must not be empty.
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
        if (marker >= 0)
        {
            if (marker == trimmed.Length - 1)
            {
                return false;
            }

            prerelease = trimmed[marker] == '-';
            trimmed = trimmed[..marker];
        }

        if (!TryParse(trimmed, out var core))
        {
            return false;
        }

        version = core with { Prerelease = prerelease };
        return true;
    }

    /// <summary>
    /// True when <paramref name="published"/> is strictly newer than <paramref name="running"/>.
    /// Equal numbers with a running prerelease count as newer: <c>1.2.3</c> is the release
    /// <c>1.2.3-rc.1</c> was cut towards.
    /// </summary>
    /// <param name="published">Version the published metadata names.</param>
    /// <param name="running">Version this build reports.</param>
    public static bool IsNewer(UpdateVersion published, UpdateVersion running) => Compare(published, running) > 0;

    /// <inheritdoc />
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture, "{0}.{1}.{2}", Major, Minor, Patch);

    private static int Compare(UpdateVersion left, UpdateVersion right)
    {
        var numbers = left.Major != right.Major
            ? left.Major.CompareTo(right.Major)
            : left.Minor != right.Minor
                ? left.Minor.CompareTo(right.Minor)
                : left.Patch.CompareTo(right.Patch);
        return numbers != 0 ? numbers : right.Prerelease.CompareTo(left.Prerelease);
    }

    private static int Number(Match match, int group) =>
        int.Parse(match.Groups[group].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
}
