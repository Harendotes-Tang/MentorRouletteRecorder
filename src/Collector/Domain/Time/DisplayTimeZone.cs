using System.Globalization;

namespace MentorRecorder.Collector.Domain.Time;

/// <summary>
/// The 显示时区 the Desktop shows record times in (docs/ui-design.md, 显示时区), held by the
/// Collector for every day and clock time it computes itself: the days of a month that have
/// records (<c>QueryRunCalendarDays</c>), the sorting anchor of a date-only imported record, and
/// the HH:mm inside its sentences.
///
/// It is process state, not a setting: the Desktop tells it with <c>SetDisplayTimeZone</c> after
/// every connection and whenever the player picks another zone. A Collector that starts follows
/// the computer's zone (<see cref="TimeZoneInfo.Local"/>) until it is told otherwise, which is
/// exactly what it did before this class existed. Every conversion goes from a UTC instant
/// through the zone's own rules, so daylight saving is exact.
///
/// What deliberately stays elsewhere: stored and transmitted times are UTC, trends are cut in UTC
/// (docs/statistics-definitions.md), CSV/JSON exports are UTC, and log lines, file names and the
/// parent-process watchdog use the computer's clock.
///
/// Thread-safe: the zone in force is one immutable snapshot swapped atomically; every method
/// reads it once.
/// </summary>
public static class DisplayTimeZone
{
    /// <summary>The longest zone id accepted (contracts/ipc-v1.schema.json, SetDisplayTimeZoneRequest).</summary>
    public const int MaxIdLength = 100;

    private static State _state = State.System;

    /// <summary>The zone in force: the chosen one, or the computer's while none is chosen.</summary>
    public static TimeZoneInfo Current => Volatile.Read(ref _state).Zone;

    /// <summary>The chosen zone id; null while the computer's zone is followed.</summary>
    public static string? Id => Volatile.Read(ref _state).Id;

    /// <summary>
    /// Puts a zone in force. Null, empty or blank follows the computer's zone again.
    /// </summary>
    /// <param name="ianaId">An IANA zone id this computer knows, such as <c>Asia/Tokyo</c>.</param>
    /// <returns>The zone now in force.</returns>
    /// <exception cref="TimeZoneNotFoundException">
    /// The id is longer than <see cref="MaxIdLength"/> or names no zone this computer knows; the zone
    /// in force is left as it was. The IPC layer turns it into <c>ERR_BAD_REQUEST</c>.
    /// </exception>
    public static TimeZoneInfo Set(string? ianaId)
    {
        // The runtime caches the computer's zone for the life of the process, so a Windows zone
        // changed since start would otherwise never be seen. Every SetDisplayTimeZone - the
        // Desktop sends one after each connection and on each 显示时区 change - drops that cache.
        TimeZoneInfo.ClearCachedData();
        var next = Resolve(ianaId);
        Volatile.Write(ref _state, next);
        return next.Zone;
    }

    /// <summary>
    /// Puts a zone in force until the returned scope is disposed, then restores the one that was in
    /// force before. For tests: the zone is process-wide, so a test that changes it must restore it
    /// and must not run beside tests that read it.
    /// </summary>
    /// <param name="ianaId">As for <see cref="Set"/>.</param>
    public static IDisposable Use(string? ianaId)
    {
        var next = Resolve(ianaId);
        var previous = Interlocked.Exchange(ref _state, next);
        return new Restore(previous);
    }

    /// <summary>The instant as a wall clock in the zone in force.</summary>
    /// <param name="instant">Any instant; its own offset is irrelevant.</param>
    public static DateTimeOffset Convert(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Current);

    /// <summary>The calendar day, <c>yyyy-MM-dd</c>, the instant falls on in the zone in force.</summary>
    /// <param name="instant">Any instant.</param>
    public static string DayOf(DateTimeOffset instant) =>
        DayOf(instant, Current);

    /// <summary>The instant's calendar day in a given zone, including a frozen query zone.</summary>
    /// <param name="instant">Any instant.</param>
    /// <param name="zone">The zone to use.</param>
    internal static string DayOf(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The wall-clock time <c>HH:mm</c> of the instant in the zone in force.</summary>
    /// <param name="instant">Any instant.</param>
    public static string ClockOf(DateTimeOffset instant) =>
        Convert(instant).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The zone's offset from UTC at <paramref name="instant"/>, in whole minutes.</summary>
    /// <param name="instant">Any instant.</param>
    public static int UtcOffsetMinutes(DateTimeOffset instant) =>
        (int)Current.GetUtcOffset(instant).TotalMinutes;

    /// <summary>
    /// The first instant of <paramref name="day"/> in the zone in force, in UTC: the instant of its
    /// 00:00. A day whose midnight does not exist, because a daylight-saving change skips it, starts
    /// at the first instant that does - the moment of the change, shown as 01:00 or whatever the
    /// clock jumps to. A midnight that happens twice starts at its first occurrence.
    /// </summary>
    /// <param name="day">Calendar day in the zone in force.</param>
    /// <exception cref="ArgumentOutOfRangeException">The day's start lies outside the range of <see cref="DateTimeOffset"/>.</exception>
    public static DateTimeOffset DayStartUtc(DateOnly day) => DayStartUtc(day, Current);

    /// <summary><see cref="DayStartUtc(DateOnly)"/> in a given zone.</summary>
    /// <param name="day">Calendar day in <paramref name="zone"/>.</param>
    /// <param name="zone">The zone.</param>
    internal static DateTimeOffset DayStartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // The day starts with the offset in force just before or just after its midnight; any
        // other offset of the zone cannot apply to that instant. Of the candidates that do fall
        // on the day, the earliest is its first instant: in a gap the earlier-offset candidate
        // lands on the transition itself, at a repeated midnight it is the first occurrence.
        DateTimeOffset? first = null;
        DateTimeOffset? fallback = null;
        foreach (var probe in new[] { -1, 0, 1 })
        {
            if (!TryCandidate(midnight, probe, zone, out var candidate))
            {
                continue;
            }

            fallback ??= candidate;
            if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(candidate, zone).DateTime) == day &&
                (first is null || candidate < first))
            {
                first = candidate;
            }
        }

        // A day the zone skipped entirely (Pacific/Apia, 2011-12-30) has no instant of its own:
        // it starts where the next day starts, which the earliest-offset candidate names.
        return first ?? fallback ?? throw new ArgumentOutOfRangeException(nameof(day));
    }

    private static bool TryCandidate(DateTime midnight, int probeDays, TimeZoneInfo zone, out DateTimeOffset candidate)
    {
        try
        {
            var offset = zone.GetUtcOffset(midnight.AddDays(probeDays));
            candidate = new DateTimeOffset(midnight - offset, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // The first and last days of the calendar: a probe or the start leaves DateTime's range.
            candidate = default;
            return false;
        }
    }

    private static State Resolve(string? ianaId)
    {
        var id = ianaId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return State.System;
        }

        if (id.Length > MaxIdLength)
        {
            throw new TimeZoneNotFoundException("The time zone id is too long.");
        }

        try
        {
            return new State(id, TimeZoneInfo.FindSystemTimeZoneById(id));
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new TimeZoneNotFoundException("The time zone data of this computer is damaged.", ex);
        }
        catch (System.Security.SecurityException ex)
        {
            throw new TimeZoneNotFoundException("The time zone data of this computer cannot be read.", ex);
        }
    }

    /// <summary>One zone in force: <see cref="Id"/> null means the computer's zone.</summary>
    private sealed class State
    {
        public static readonly State System = new(null, null);

        private readonly TimeZoneInfo? _zone;

        public State(string? id, TimeZoneInfo? zone)
        {
            Id = id;
            _zone = zone;
        }

        public string? Id { get; }

        // Read on every use: the runtime refreshes its view of the computer's zone when
        // TimeZoneInfo.ClearCachedData() runs, which Set does on every call, and a chosen zone
        // never changes.
        public TimeZoneInfo Zone => _zone ?? TimeZoneInfo.Local;
    }

    private sealed class Restore(State previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref _state, previous);
            }
        }
    }
}
