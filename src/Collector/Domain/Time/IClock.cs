using System.Diagnostics;

namespace MentorRecorder.Collector.Domain.Time;

/// <summary>
/// Time source for the Collector.
///
/// Two distinct notions of time are exposed on purpose (docs/architecture.md §4, item 6):
/// <list type="bullet">
///   <item><description><see cref="UtcNow"/> is wall-clock UTC and is the only thing ever persisted.</description></item>
///   <item><description><see cref="Elapsed"/> is a monotonic reading. Every duration written to
///   <c>mentor_runs.duration_ms</c> for a live-observed run is the difference of two
///   monotonic readings, never the difference of two wall-clock timestamps.</description></item>
/// </list>
/// </summary>
public interface IClock
{
    /// <summary>Current wall-clock time in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Monotonic elapsed time since an arbitrary fixed origin. Only differences are meaningful.
    /// Unaffected by system clock adjustments, time zone changes and daylight saving.
    /// </summary>
    TimeSpan Elapsed { get; }
}

/// <summary>Production clock: the OS wall clock plus a process-wide <see cref="Stopwatch"/>.</summary>
public sealed class SystemClock : IClock
{
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();

    /// <summary>Shared instance; the class is stateless.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public TimeSpan Elapsed => Monotonic.Elapsed;
}
