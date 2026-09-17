namespace MentorRecorder.Collector;

/// <summary>What the Collector was asked to do.</summary>
public enum CollectorMode
{
    /// <summary>Open the database and serve the Named Pipe. The default.</summary>
    Serve,

    /// <summary>Replay an offline semantic-event fixture.</summary>
    Replay,

    /// <summary>Replay an offline decoded-message fixture through the profile parser.</summary>
    ReplayDecoded,

    /// <summary>Validate one protocol profile file and print the report.</summary>
    ValidateProfile,

    /// <summary>List the installed protocol profiles and their statuses.</summary>
    ListProfiles,

    /// <summary>Inspect capture prerequisites without opening an adapter or writing data.</summary>
    CaptureDoctor,

    /// <summary>Record one live session as a sanitized, opcode-level trace file.</summary>
    CaptureTrace,

    /// <summary>Read a trace file back and print what it suggests. Offline.</summary>
    TraceReport,

    /// <summary>Print the version banner.</summary>
    Version,

    /// <summary>Print the Named Pipe name and exit.</summary>
    PipeNameOnly,
}

/// <summary>
/// The parsed command line.
///
/// Parsing is strict on purpose: an unrecognised switch is an error rather than something
/// silently ignored, so a typo in a shortcut or a script can never quietly run the wrong
/// mode against the wrong database.
/// </summary>
/// <param name="Mode">Selected mode.</param>
/// <param name="DatabasePath">Database file, or null for the default location.</param>
/// <param name="FixturePath">Fixture to replay, in either replay mode.</param>
/// <param name="ProfilePath">
/// Protocol profile file, for <see cref="CollectorMode.ValidateProfile"/> and, optionally,
/// for <see cref="CollectorMode.ReplayDecoded"/>.
/// </param>
/// <param name="ProfilesDirectory">Directory to list profiles from, or null for the default.</param>
/// <param name="Json">True when machine-readable output was requested.</param>
/// <param name="ParentProcessId">
/// Process id to shadow, or null when this Collector answers to nobody. When it is given, the
/// Collector stops itself as soon as that process ends, so a Desktop that is killed outright
/// cannot leave an invisible Collector behind holding the per-user serve lease. See
/// <c>Diagnostics.ParentProcessWatchdog</c>.
/// </param>
/// <param name="TracePath">
/// Trace file, written by <see cref="CollectorMode.CaptureTrace"/> and read by
/// <see cref="CollectorMode.TraceReport"/>.
/// </param>
/// <param name="DurationSeconds">Seconds a live trace runs; 0 or null means until Ctrl+C.</param>
/// <param name="AdapterId">Adapter a live trace observes, or null to use the recommended one.</param>
/// <param name="MaxLines">Cap on the message lines a live trace writes.</param>
/// <param name="AllowMidstream">Let a live trace start while the game already has connections open.</param>
/// <param name="AroundMarker">Marker name a report restricts itself to.</param>
/// <param name="WindowMs">Half-width of a report's marker window, in milliseconds.</param>
/// <param name="PipeName">
/// Bare Named Pipe name to serve instead of the per-user one, or null for the default. Only a
/// test harness has a reason to set it: a Collector on a throw-away database must never share
/// the pipe - or the single-instance lease - with the Collector the user's Desktop is talking
/// to, or the harness quietly writes its fixtures into the user's real records.
/// </param>
/// <param name="LogDirectory">
/// Folder the rotated diagnostic log is written to, or null to derive it. Deriving means:
/// beside an explicit <c>--db</c>, otherwise the managed location. A harness that names a
/// throw-away database therefore gets throw-away logs, instead of rotating and pruning the
/// diagnostics of the user sitting at the machine (see
/// <c>Storage.DatabasePaths.ResolveLogDirectory</c>).
/// </param>
/// <param name="ParentStartTime">
/// Start time of the process named by <paramref name="ParentProcessId"/>, or null when the
/// caller did not supply one. Windows reuses process ids as soon as they are free, so the id
/// alone can name a stranger; the pair (id, start time) is unique in practice. When it is
/// absent the watchdog matches on the id alone.
/// </param>
public sealed record CommandLineOptions(
    CollectorMode Mode,
    string? DatabasePath,
    string? FixturePath,
    string? ProfilePath,
    string? ProfilesDirectory,
    bool Json,
    int? ParentProcessId = null,
    string? TracePath = null,
    int? DurationSeconds = null,
    string? AdapterId = null,
    int? MaxLines = null,
    string? AroundMarker = null,
    int? WindowMs = null,
    bool AllowMidstream = false,
    string? PipeName = null,
    string? LogDirectory = null,
    DateTimeOffset? ParentStartTime = null)
{
    /// <summary>
    /// Longest live trace the command line accepts, in seconds (24 hours). A trace is a
    /// diagnostics artefact with an expiry, not a background service.
    /// </summary>
    public const int MaxDurationSeconds = 86_400;

    /// <summary>Longest bare pipe name <c>--pipe</c> accepts.</summary>
    public const int MaxPipeNameLength = 200;

    /// <summary>Parses arguments, throwing <see cref="FormatException"/> on anything unknown.</summary>
    /// <param name="args">Raw command line arguments.</param>
    public static CommandLineOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var mode = CollectorMode.Serve;
        string? databasePath = null;
        string? fixturePath = null;
        string? profilePath = null;
        string? profilesDirectory = null;
        var json = false;
        int? parentProcessId = null;
        string? tracePath = null;
        int? durationSeconds = null;
        string? adapterId = null;
        int? maxLines = null;
        string? aroundMarker = null;
        int? windowMs = null;
        var allowMidstream = false;
        string? pipeName = null;
        string? logDirectory = null;
        DateTimeOffset? parentStartTime = null;
        var modeChosen = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--serve":
                    mode = Choose(mode, CollectorMode.Serve, ref modeChosen);
                    break;

                case "--version":
                case "-v":
                case "/version":
                    mode = Choose(mode, CollectorMode.Version, ref modeChosen);
                    break;

                case "--pipe-name-only":
                    mode = Choose(mode, CollectorMode.PipeNameOnly, ref modeChosen);
                    break;

                case "--replay":
                    mode = Choose(mode, CollectorMode.Replay, ref modeChosen);
                    fixturePath = Value(args, ref i, "--replay");
                    break;

                case "--replay-decoded":
                    mode = Choose(mode, CollectorMode.ReplayDecoded, ref modeChosen);
                    fixturePath = Value(args, ref i, "--replay-decoded");
                    break;

                case "--validate-profile":
                    mode = Choose(mode, CollectorMode.ValidateProfile, ref modeChosen);
                    profilePath = Value(args, ref i, "--validate-profile");
                    break;

                case "--list-profiles":
                    mode = Choose(mode, CollectorMode.ListProfiles, ref modeChosen);
                    break;

                case "--capture-doctor":
                    mode = Choose(mode, CollectorMode.CaptureDoctor, ref modeChosen);
                    break;

                case Capture.CaptureTraceCli.CaptureFlag:
                    mode = Choose(mode, CollectorMode.CaptureTrace, ref modeChosen);
                    tracePath = Value(args, ref i, Capture.CaptureTraceCli.CaptureFlag);
                    break;

                case Capture.CaptureTraceCli.ReportFlag:
                    mode = Choose(mode, CollectorMode.TraceReport, ref modeChosen);
                    tracePath = Value(args, ref i, Capture.CaptureTraceCli.ReportFlag);
                    break;

                case Capture.CaptureTraceCli.DurationFlag:
                    durationSeconds = ParseBounded(
                        Value(args, ref i, Capture.CaptureTraceCli.DurationFlag),
                        Capture.CaptureTraceCli.DurationFlag,
                        0,
                        MaxDurationSeconds);
                    break;

                case Capture.CaptureTraceCli.AdapterFlag:
                    adapterId = Value(args, ref i, Capture.CaptureTraceCli.AdapterFlag);
                    break;

                case Capture.CaptureTraceCli.MaxLinesFlag:
                    maxLines = ParseBounded(
                        Value(args, ref i, Capture.CaptureTraceCli.MaxLinesFlag),
                        Capture.CaptureTraceCli.MaxLinesFlag,
                        Capture.CaptureTraceSink.MinMaxLines,
                        Capture.CaptureTraceSink.MaxMaxLines);
                    break;

                case Capture.CaptureTraceCli.AroundFlag:
                    aroundMarker = Value(args, ref i, Capture.CaptureTraceCli.AroundFlag);
                    break;

                case Capture.CaptureTraceCli.AllowMidstreamFlag:
                    allowMidstream = true;
                    break;

                case Capture.CaptureTraceCli.WindowFlag:
                    windowMs = ParseBounded(
                        Value(args, ref i, Capture.CaptureTraceCli.WindowFlag),
                        Capture.CaptureTraceCli.WindowFlag,
                        Capture.CaptureTraceReport.MinWindowMs,
                        Capture.CaptureTraceReport.MaxWindowMs);
                    break;

                case "--profile":
                    profilePath = Value(args, ref i, "--profile");
                    break;

                case "--profiles-dir":
                    profilesDirectory = Value(args, ref i, "--profiles-dir");
                    break;

                // --database is the spelling the Phase-1 replay tooling shipped with; both
                // are accepted so existing scripts keep working.
                case "--db":
                case "--database":
                    databasePath = Value(args, ref i, arg);
                    break;

                case "--parent-pid":
                    parentProcessId = ParsePid(Value(args, ref i, "--parent-pid"));
                    break;

                case "--parent-start-time":
                    parentStartTime = ParseStartTime(Value(args, ref i, "--parent-start-time"));
                    break;

                case "--log-dir":
                    logDirectory = Value(args, ref i, "--log-dir");
                    break;

                case "--pipe":
                    pipeName = ParsePipeName(Value(args, ref i, "--pipe"));
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    throw new FormatException("unknown argument: " + arg);
            }
        }

        if (mode is CollectorMode.Replay or CollectorMode.ReplayDecoded &&
            string.IsNullOrWhiteSpace(fixturePath))
        {
            throw new FormatException("a replay mode requires a fixture path");
        }

        if (mode == CollectorMode.ValidateProfile && string.IsNullOrWhiteSpace(profilePath))
        {
            throw new FormatException("--validate-profile requires a profile path");
        }

        if (mode == CollectorMode.CaptureDoctor &&
            (databasePath is not null || fixturePath is not null || profilePath is not null ||
             profilesDirectory is not null || parentProcessId is not null || pipeName is not null ||
             logDirectory is not null || parentStartTime is not null))
        {
            throw new FormatException("--capture-doctor accepts only --json");
        }

        // The two trace modes are strictly separated: a live capture never reads a file and a
        // report never opens an adapter, so a flag belonging to one is refused outright rather
        // than ignored by the other. A typo must not silently produce an unbounded capture, or
        // a report with the wrong window, discovered only after the session is over.
        var traceOnly = durationSeconds is not null || adapterId is not null || maxLines is not null ||
                        allowMidstream;
        var reportOnly = aroundMarker is not null || windowMs is not null;
        var storageArguments =
            databasePath is not null || fixturePath is not null || profilePath is not null ||
            profilesDirectory is not null || parentProcessId is not null || pipeName is not null ||
            logDirectory is not null || parentStartTime is not null;

        if (mode == CollectorMode.CaptureTrace && (reportOnly || json || storageArguments))
        {
            throw new FormatException(
                "--capture-trace accepts only --duration-seconds, --adapter, --max-lines and --allow-midstream");
        }

        if (mode == CollectorMode.TraceReport && (traceOnly || json || storageArguments))
        {
            throw new FormatException("--trace-report accepts only --around and --window-ms");
        }

        if (mode is not (CollectorMode.CaptureTrace or CollectorMode.TraceReport) &&
            (traceOnly || reportOnly))
        {
            throw new FormatException(
                "--duration-seconds, --adapter, --max-lines, --around and --window-ms apply to " +
                "--capture-trace and --trace-report only");
        }

        // The switch only means anything for the mode that keeps running. Accepting it
        // elsewhere would let a caller believe a one-shot command is being supervised.
        if (mode != CollectorMode.Serve && parentProcessId is not null)
        {
            throw new FormatException("--parent-pid applies to --serve only");
        }

        // Same reasoning: only the serving Collector owns a pipe.
        if (mode != CollectorMode.Serve && pipeName is not null)
        {
            throw new FormatException("--pipe applies to --serve only");
        }

        // The serving mode is the only one that opens the rotating diagnostic log, so it is
        // the only one for which naming a folder means anything. Accepting the switch elsewhere
        // would let a caller believe a one-shot command had been redirected when it had not.
        if (mode != CollectorMode.Serve && logDirectory is not null)
        {
            throw new FormatException("--log-dir applies to --serve only");
        }

        if (mode != CollectorMode.Serve && parentStartTime is not null)
        {
            throw new FormatException("--parent-start-time applies to --serve only");
        }

        // A start time with no id names nothing. Refusing it here stops a caller from
        // believing the identity check is on when there is no process to check it against.
        if (parentStartTime is not null && parentProcessId is null)
        {
            throw new FormatException("--parent-start-time requires --parent-pid");
        }

        if (mode != CollectorMode.ReplayDecoded && mode != CollectorMode.ValidateProfile &&
            profilePath is not null)
        {
            throw new FormatException("--profile applies to --replay-decoded only");
        }

        return new CommandLineOptions(
            mode, databasePath, fixturePath, profilePath, profilesDirectory, json, parentProcessId,
            tracePath, durationSeconds, adapterId, maxLines, aroundMarker, windowMs, allowMidstream,
            pipeName, logDirectory, parentStartTime);
    }

    /// <summary>Folder this run writes its rotated diagnostic log to.</summary>
    public string ResolvedLogDirectory =>
        Storage.DatabasePaths.ResolveLogDirectory(LogDirectory, DatabasePath);

    /// <summary>
    /// Parses a parent start time given either as UTC ticks -- what
    /// <c>Process.StartTime.Ticks</c> produces, and the form the Desktop passes -- or as an
    /// ISO-8601 timestamp, which is what a person types when reproducing a launch by hand.
    ///
    /// Both are accepted because both are unambiguous. Anything else is refused rather than
    /// guessed at: a start time parsed to the wrong instant would turn the identity check into
    /// a permanent mismatch and silently disable the watchdog.
    /// </summary>
    /// <param name="text">Raw argument value.</param>
    private static DateTimeOffset ParseStartTime(string text)
    {
        var value = text.Trim();
        if (long.TryParse(
                value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var ticks) &&
            ticks >= 0 && ticks <= DateTimeOffset.MaxValue.UtcTicks)
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return parsed;
        }

        throw new FormatException(
            "--parent-start-time requires UTC ticks or an ISO-8601 timestamp");
    }

    /// <summary>
    /// Parses a bare pipe name: the part after <c>\.\pipe\</c>, nothing else.
    ///
    /// A path separator is refused rather than stripped so a caller who pastes the full
    /// server name learns about it here, instead of serving a pipe nobody connects to.
    /// </summary>
    /// <param name="text">Raw argument value.</param>
    private static string ParsePipeName(string text)
    {
        var name = text.Trim();
        if (name.Length == 0 || name.Length > MaxPipeNameLength)
        {
            throw new FormatException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "--pipe requires a bare pipe name of 1 to {0} characters",
                MaxPipeNameLength));
        }

        foreach (var c in name)
        {
            if (c is '\\' or '/' || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                throw new FormatException(
                    "--pipe requires a bare pipe name without separators or whitespace");
            }
        }

        return name;
    }

    /// <summary>
    /// Parses a bounded, non-negative integer argument.
    ///
    /// Bounds are checked here rather than clamped later, so a mistyped value is refused while
    /// the user is still looking at the command line instead of becoming a capture that runs
    /// for a week or a report with a window the size of the trace.
    /// </summary>
    /// <param name="text">Raw argument value.</param>
    /// <param name="flag">Flag the value belongs to, for the message.</param>
    /// <param name="minimum">Smallest accepted value.</param>
    /// <param name="maximum">Largest accepted value.</param>
    private static int ParseBounded(string text, string flag, int minimum, int maximum)
    {
        if (!int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) ||
            value < minimum || value > maximum)
        {
            throw new FormatException(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} requires an integer between {1} and {2}",
                flag,
                minimum,
                maximum));
        }

        return value;
    }

    /// <summary>Parses a process id, refusing anything that could not be one.</summary>
    /// <param name="text">Raw argument value.</param>
    private static int ParsePid(string text)
    {
        if (!int.TryParse(
                text,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var pid) ||
            pid <= 0)
        {
            throw new FormatException("--parent-pid requires a positive process id");
        }

        return pid;
    }

    private static CollectorMode Choose(CollectorMode current, CollectorMode requested, ref bool chosen)
    {
        if (chosen && current != requested)
        {
            throw new FormatException("only one mode may be given");
        }

        chosen = true;
        return requested;
    }

    private static string Value(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new FormatException(flag + " requires a value");
        }

        return args[++index];
    }

}
