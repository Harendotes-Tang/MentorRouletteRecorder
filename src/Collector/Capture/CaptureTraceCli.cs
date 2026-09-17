namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Wires the two trace command-line modes to their implementations, so that the mapping from
/// parsed options to run options lives in one place.
/// </summary>
public static class CaptureTraceCli
{
    /// <summary>Flag that starts a live trace.</summary>
    public const string CaptureFlag = "--capture-trace";

    /// <summary>Flag that reads a trace back.</summary>
    public const string ReportFlag = "--trace-report";

    /// <summary>Flag bounding a live trace in time.</summary>
    public const string DurationFlag = "--duration-seconds";

    /// <summary>Flag choosing the adapter.</summary>
    public const string AdapterFlag = "--adapter";

    /// <summary>Flag bounding a live trace in lines.</summary>
    public const string MaxLinesFlag = "--max-lines";

    /// <summary>Record even though the game already has connections open (flagged, per-connection summary).</summary>
    public const string AllowMidstreamFlag = "--allow-midstream";

    /// <summary>Flag restricting the report to one marker name.</summary>
    public const string AroundFlag = "--around";

    /// <summary>Flag widening or narrowing the marker window.</summary>
    public const string WindowFlag = "--window-ms";

    /// <summary>Runs a live trace. Returns 0 when one was written, 1 when the machine cannot.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="services">Dependencies; the real machine when null.</param>
    public static int RunCapture(CommandLineOptions options, CaptureTraceServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var traceOptions = new CaptureTraceOptions(
            options.TracePath!,
            options.DurationSeconds ?? 0,
            options.AdapterId,
            options.MaxLines ?? CaptureTraceSink.DefaultMaxLines,
            options.AllowMidstream);

        if (services is not null)
        {
            return CaptureTraceRunner.Run(traceOptions, services);
        }

        // This is the one mode that runs without the host that normally opens the local log,
        // and a failed trace directs the user to that log. The logger sanitizes every line it
        // writes (docs/privacy-boundary.md §5), so opening it here adds no exposure.
        using var logger = new Diagnostics.RotatingFileLogger(clock: Domain.Time.SystemClock.Instance);
        return CaptureTraceRunner.Run(
            traceOptions,
            new CaptureTraceServices { CollectorVersion = Program.Version, Logger = logger });
    }

    /// <summary>Reads a trace back and prints the report.</summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="output">Where to print; standard output when null.</param>
    public static int RunReport(CommandLineOptions options, TextWriter? output = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return CaptureTraceReport.Run(
            options.TracePath!,
            options.AroundMarker,
            options.WindowMs ?? CaptureTraceReport.DefaultWindowMs,
            output);
    }
}
