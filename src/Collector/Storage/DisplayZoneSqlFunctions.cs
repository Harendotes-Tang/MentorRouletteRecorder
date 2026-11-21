using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage;

/// <summary>
/// The two SQL functions through which a query cuts days in the 显示时区 (<see cref="DisplayTimeZone"/>)
/// instead of SQLite's <c>'localtime'</c> / <c>'utc'</c> modifiers, which only know the computer's
/// zone. Registered on every connection a <see cref="SqliteDatabase"/> wraps - the writer and the
/// read-only snapshot views exports page through - so <see cref="RunFilterSql"/> expressions run
/// on either.
///
/// The live connection follows the current zone; a read snapshot keeps the zone it started with.
/// Both are registered as non-deterministic so connection mode never affects where SQL uses them. Neither may
/// appear in an index, a generated column, a view or a CHECK constraint (SQLite refuses a
/// non-deterministic function there, and the schema could not be opened without this process);
/// they are used only in the query text RunFilterSql and RunRepository build.
/// </summary>
internal static class DisplayZoneSqlFunctions
{
    /// <summary><c>mr_zone_day(text utcInstant)</c>: the <c>yyyy-MM-dd</c> the instant falls on, or NULL.</summary>
    public const string Day = "mr_zone_day";

    /// <summary><c>mr_zone_day_start(text day)</c>: UTC text of the day's first instant, or NULL.</summary>
    public const string DayStart = "mr_zone_day_start";

    // What SQLite's date functions read besides our own spelling (UtcTimestamp.Format); a value
    // without a zone designator is UTC, as it was to date(..., 'localtime').
    private static readonly string[] LenientInstantFormats =
    {
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd HH:mmK",
    };

    /// <summary>Registers both functions on an open connection.</summary>
    /// <param name="connection">The connection.</param>
    /// <param name="snapshotZone">Fixed zone for a read snapshot; null follows the current zone.</param>
    public static void Register(SqliteConnection connection, TimeZoneInfo? snapshotZone)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateFunction<string?, string?>(Day, instant => DayOf(instant, snapshotZone), isDeterministic: false);
        connection.CreateFunction<string?, string?>(DayStart, day => DayStartOf(day, snapshotZone), isDeterministic: false);
    }

    /// <summary>The body of <see cref="Day"/>.</summary>
    /// <param name="instant">A UTC instant as stored; NULL or anything unreadable gives NULL.</param>
    /// <param name="snapshotZone">Fixed zone for a read snapshot; null follows the current zone.</param>
    internal static string? DayOf(string? instant, TimeZoneInfo? snapshotZone = null)
    {
        if (!TryParseInstant(instant, out var at))
        {
            return null;
        }

        try
        {
            return DisplayTimeZone.DayOf(at, snapshotZone ?? DisplayTimeZone.Current);
        }
        catch (ArgumentOutOfRangeException)
        {
            // 0001-01-01 or 9999-12-31 shifted out of the calendar by the zone's offset.
            return null;
        }
    }

    /// <summary>The body of <see cref="DayStart"/>.</summary>
    /// <param name="day">A calendar day <c>yyyy-MM-dd</c>; NULL or anything else gives NULL.</param>
    /// <param name="snapshotZone">Fixed zone for a read snapshot; null follows the current zone.</param>
    internal static string? DayStartOf(string? day, TimeZoneInfo? snapshotZone = null)
    {
        if (day is null ||
            !DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        try
        {
            return UtcTimestamp.ToText(DisplayTimeZone.DayStartUtc(date, snapshotZone ?? DisplayTimeZone.Current));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryParseInstant(string? text, out DateTimeOffset value)
    {
        if (UtcTimestamp.TryParse(text, out value))
        {
            return true;
        }

        return !string.IsNullOrEmpty(text) &&
            DateTimeOffset.TryParseExact(
                text,
                LenientInstantFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value);
    }
}
