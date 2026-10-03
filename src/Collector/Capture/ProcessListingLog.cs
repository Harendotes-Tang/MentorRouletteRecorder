using System.Diagnostics;
using MentorRecorder.Collector.Diagnostics;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Records a process listing that keeps failing, without flooding the log.
///
/// A failed listing is not "the game exited" anywhere (docs/state-machine.md 3.6): the selection
/// keeps its pin, a trace keeps recording. That also made it silent, and a process-table read that
/// fails for an hour looked exactly like a quiet session. The first failure in a row is logged,
/// then at most one line per <see cref="RepeatInterval"/> while it lasts, and one line when a
/// listing is read again (audit 2026-10-03, CS1-X1). Nothing about any process is written: only
/// counts.
/// </summary>
internal sealed class ProcessListingLog
{
    /// <summary>Shortest gap between two lines about the same run of failures.</summary>
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly RotatingFileLogger _logger;
    private readonly string _component;
    private readonly Func<TimeSpan> _elapsed;
    private int _consecutiveFailures;
    private TimeSpan _lastLoggedAt;

    /// <summary>Creates the log.</summary>
    /// <param name="logger">Local diagnostic log.</param>
    /// <param name="component">Component the lines are filed under.</param>
    /// <param name="elapsed">Monotonic time; a stopwatch started here when null.</param>
    public ProcessListingLog(RotatingFileLogger logger, string component, Func<TimeSpan>? elapsed = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(component);
        _logger = logger;
        _component = component;
        if (elapsed is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _elapsed = () => stopwatch.Elapsed;
        }
        else
        {
            _elapsed = elapsed;
        }
    }

    /// <summary>Takes the outcome of one process listing.</summary>
    /// <param name="succeeded">False when the process table could not be read.</param>
    public void Observe(bool succeeded)
    {
        lock (_gate)
        {
            if (succeeded)
            {
                if (_consecutiveFailures > 0)
                {
                    _logger.Write(LogLevel.Info, _component, "process_listing_recovered", new Dictionary<string, object?>
                    {
                        ["failed_readings"] = _consecutiveFailures,
                    });
                }

                _consecutiveFailures = 0;
                return;
            }

            _consecutiveFailures++;
            var now = _elapsed();
            if (_consecutiveFailures == 1 || now - _lastLoggedAt >= RepeatInterval)
            {
                _lastLoggedAt = now;
                _logger.Write(LogLevel.Warn, _component, "process_listing_failed", new Dictionary<string, object?>
                {
                    ["consecutive_failures"] = _consecutiveFailures,
                });
            }
        }
    }
}
