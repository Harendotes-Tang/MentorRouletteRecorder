using System.Globalization;

namespace MentorRecorder.Collector.Domain.Time;

/// <summary>
/// The single place that knows how a timestamp is spelled.
///
/// Every persisted and transmitted timestamp is UTC ISO-8601 with exactly three
/// fractional digits and a literal <c>Z</c>, for example <c>2026-09-04T11:22:33.456Z</c>
/// (docs/data-model.md §0, <c>$defs/UtcTimestamp</c>). Local time is never stored.
/// </summary>
public static class UtcTimestamp
{
    /// <summary>The one and only accepted format string.</summary>
    public const string Format = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>Formats an instant, converting to UTC first.</summary>
    public static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    /// <summary>Formats a nullable instant; null maps to null.</summary>
    public static string? ToTextOrNull(DateTimeOffset? value) =>
        value is null ? null : ToText(value.Value);

    /// <summary>
    /// Strictly parses a timestamp. Anything that is not exactly <see cref="Format"/>
    /// is rejected rather than coerced, so a client cannot smuggle in local time.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        if (!string.IsNullOrEmpty(text) &&
            DateTimeOffset.TryParseExact(
                text,
                Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Parses a timestamp read back from our own database, or throws.</summary>
    public static DateTimeOffset Parse(string? text) =>
        TryParse(text, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a UTC ISO-8601 timestamp with milliseconds");

    /// <summary>Parses a nullable timestamp read back from our own database.</summary>
    public static DateTimeOffset? ParseOrNull(string? text) =>
        string.IsNullOrEmpty(text) ? null : Parse(text);

    /// <summary>Truncates an instant to millisecond precision so round-tripping is lossless.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(
            value.UtcDateTime.Ticks - (value.UtcDateTime.Ticks % TimeSpan.TicksPerMillisecond),
            TimeSpan.Zero);
}
