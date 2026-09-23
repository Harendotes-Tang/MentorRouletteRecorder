using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Replay;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector;

/// <summary>
/// Entry point of the Collector process.
///
/// Local operating modes:
/// <list type="bullet">
///   <item><description><c>--serve</c> opens the database, migrates it, checks its integrity,
///   recovers what a previous process left unfinished, and serves the Named Pipe until
///   Ctrl+C;</description></item>
///   <item><description><c>--replay</c> drives the state machine from an offline fixture;</description></item>
///   <item><description><c>--replay-decoded</c> drives the parser and state machine from decoded fixtures;</description></item>
///   <item><description><c>--validate-profile</c> and <c>--list-profiles</c> inspect protocol profiles;</description></item>
///   <item><description><c>--capture-doctor</c> checks capture prerequisites without opening an adapter;</description></item>
///   <item><description><c>--capture-trace</c> records one live session as a sanitized,
///   opcode-level trace file, and <c>--trace-report</c> reads that file back;</description></item>
///   <item><description><c>--pipe-name-only</c> prints the pipe name the Desktop must connect to;</description></item>
///   <item><description><c>--version</c> prints the version banner.</description></item>
/// </list>
///
/// <c>--serve</c> also takes <c>--parent-pid &lt;pid&gt;</c>, optionally paired with
/// <c>--parent-start-time &lt;ticks|ISO-8601&gt;</c>. Together they make this process shadow the
/// one that launched it and stop when that one ends, which is what keeps a force-killed
/// Desktop from leaving an orphaned Collector behind
/// (<see cref="Diagnostics.ParentProcessWatchdog"/>).
///
/// Where files go: <c>--db</c> names the database, <c>--log-dir</c> names the diagnostic log
/// folder, and the environment variable <c>MR_DATA_DIR</c> moves the whole root at once. A run
/// that names a database but no log folder logs beside that database, so a test harness
/// cannot rotate or prune the diagnostics of the user sitting at the machine
/// (<see cref="Storage.DatabasePaths"/>).
///
/// Exit codes:
/// <list type="bullet">
///   <item><description>0 -- finished, or stopped on request;</description></item>
///   <item><description>1 -- <c>--validate-profile</c> refused the profile;</description></item>
///   <item><description>2 -- the command line could not be parsed, or a profile check could
///   not run;</description></item>
///   <item><description>3 -- the process could not do its job: database integrity, an
///   unwritable path, an I/O failure;</description></item>
///   <item><description>4 -- another Collector is already serving this pipe
///   (<c>ERR_ALREADY_RUNNING</c>). Distinct from 3 because the response is to talk to the
///   running instance, not to treat the machine as faulty;</description></item>
///   <item><description>6 -- another Collector holds the pipe but does not answer
///   <c>GetVersion</c>. Distinct from 4 because there is nothing to reuse: the hung instance
///   has to be ended first.</description></item>
/// </list>
/// </summary>
public static class Program
{
    private const string ServeLeaseSuffix = ".serve";

    /// <summary>
    /// Suffix of the named event a running Collector watches for a graceful stop request.
    ///
    /// The Desktop starts this process with <c>CREATE_NO_WINDOW</c> and stops it with
    /// <c>QProcess::terminate()</c>, which posts <c>WM_CLOSE</c> to top-level windows that a
    /// console-less child does not have, then kills it. Setting this event raises the same stop
    /// Ctrl+C raises, so the graceful-shutdown work still runs (review finding H-2).
    /// </summary>
    public const string ServeStopSuffix = ".stop";

    /// <summary>File written beside the log folder naming the process that holds the pipe.</summary>
    public const string ServePidFileName = "serve.pid";

    /// <summary>
    /// Name of the pid file for one pipe name.
    ///
    /// The default per-user pipe keeps the bare <c>serve.pid</c>, because that is the name the
    /// Desktop looks for. Any other pipe -- a harness or a development Collector started with
    /// <c>--pipe</c> -- gets its own file, so a second instance cannot overwrite and then delete
    /// the pid of the Collector the user is running (review finding R-10).
    /// </summary>
    /// <param name="pipeName">Bare pipe name this Collector serves.</param>
    public static string ServePidFileNameFor(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);

        if (string.Equals(pipeName, PipeNaming.CurrentUserPipeName(), StringComparison.Ordinal))
        {
            return ServePidFileName;
        }

        var sanitized = new StringBuilder(pipeName.Length);
        foreach (var c in pipeName)
        {
            sanitized.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        }

        return "serve." + sanitized + ".pid";
    }

    /// <summary>
    /// Exit code used when another Collector already holds this pipe. Kept apart from the
    /// general failure code so a launcher can tell "already running, reuse it" from "this
    /// machine's data is in trouble" without parsing text.
    /// </summary>
    public const int ExitCodeAlreadyRunning = 4;

    /// <summary>
    /// Exit code used when another Collector holds the pipe but does not answer.
    ///
    /// Separate from <see cref="ExitCodeAlreadyRunning"/> because the responses are opposite:
    /// "already running" means connect to it, this means connecting will never succeed and the
    /// holder has to be ended -- which is why its id is written to
    /// <see cref="ServePidFileName"/> (review finding H-5).
    /// </summary>
    public const int ExitCodeAlreadyRunningUnresponsive = 6;

    /// <summary>How long one liveness probe attempt waits for the holder to answer <c>GetVersion</c>.</summary>
    public static readonly TimeSpan LivenessProbeTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How many times the liveness probe is retried before the holder is called hung.
    ///
    /// A Collector still opening its database -- integrity check, migration, crash recovery --
    /// has created the pipe but does not yet answer, and one shutting down has stopped
    /// answering but still holds the lease. Both are ordinary and both last well over a second
    /// on a large database, while exit code 6 makes the Desktop run <c>taskkill /F</c> (review
    /// finding R-3). Six half-second attempts give the holder three seconds to answer first.
    /// </summary>
    public const int LivenessProbeAttempts = 6;

    /// <summary>Full name of the graceful-stop event for one pipe name.</summary>
    /// <param name="pipeName">Bare pipe name the Collector serves.</param>
    public static string StopEventName(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        return "Local\\" + pipeName + ServeStopSuffix;
    }

    /// <summary>Product identifier used in IPC handshakes and log headers.</summary>
    public const string ProductName = "MentorRecorder.Collector";

    /// <summary>Semantic version stamped from Directory.Build.props, excluding the source commit suffix.</summary>
    public static readonly string Version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    /// <summary>IPC envelope protocol version implemented by this build.</summary>
    public const int IpcProtocolVersion = 1;

    /// <summary>
    /// Single-line version banner. Kept stable because tests assert on it and the Desktop
    /// process shows it in the diagnostics page.
    /// </summary>
    public static string VersionString =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1} (ipc v{2})", ProductName, Version, IpcProtocolVersion);

    /// <summary>Parses the command line and runs the selected mode.</summary>
    /// <param name="args">Command line arguments.</param>
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        UseUtf8Console();

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        try
        {
            return options.Mode switch
            {
                CollectorMode.Version => PrintVersion(options),
                CollectorMode.PipeNameOnly => PrintPipeName(options),
                CollectorMode.Replay => RunReplay(options),
                CollectorMode.ReplayDecoded => RunDecodedReplay(options),
                CollectorMode.ValidateProfile => ValidateProfile(options),
                CollectorMode.ListProfiles => ListProfiles(options),
                CollectorMode.CaptureDoctor => CaptureCli.Run(args),
                CollectorMode.CaptureTrace => CaptureTraceCli.RunCapture(options),
                CollectorMode.TraceReport => CaptureTraceCli.RunReport(options),
                _ => ServeAsync(options).GetAwaiter().GetResult(),
            };
        }
        catch (UnresponsiveCollectorException ex)
        {
            Console.Error.WriteLine($"{ErrorCodes.AlreadyRunning}: {ex.Message}");
            return ExitCodeAlreadyRunningUnresponsive;
        }
        catch (CollectorException ex)
        {
            Console.Error.WriteLine($"{ex.Code}: {ex.Message}");
            return ex.Code == ErrorCodes.AlreadyRunning ? ExitCodeAlreadyRunning : 3;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("failed: " + ex.Message);
            return 3;
        }
        catch (TimeoutException ex)
        {
            // Capture or the protocol pipeline would not let go within its bounded shutdown.
            // Caught here so the process reports exit code 3 instead of dying as an unhandled
            // exception (0xE0434352), which tells a launcher nothing (review finding L-3).
            Console.Error.WriteLine("failed: " + ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// Writes standard output and standard error as UTF-8, without a byte-order mark.
    ///
    /// Every user-facing message this process prints is Chinese. By default .NET on Windows
    /// encodes console output with the machine's ANSI code page, which leaves a caller reading
    /// a redirected stream to guess at the locale. One fixed encoding removes the question.
    ///
    /// <c>Console.OutputEncoding</c> covers standard error as well: setting it recreates both
    /// <c>Console.Out</c> and <c>Console.Error</c>. It throws when there is no console to
    /// reconfigure -- a service, a detached process, a closed handle -- and the streams are
    /// then rebuilt directly. If that fails too the process still starts.
    /// </summary>
    private static void UseUtf8Console()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            Console.OutputEncoding = utf8;
            return;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or NotSupportedException)
        {
            // No console attached. Fall through and rebuild the writers by hand.
        }

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Output is unusable either way; there is nothing left to configure.
        }
    }

    private static int PrintVersion(CommandLineOptions options)
    {
        if (options.Json)
        {
            Console.WriteLine(new JsonObject
            {
                ["product"] = ProductName,
                ["collector_version"] = Version,
                ["protocol_version"] = IpcProtocolVersion,
                ["schema_version"] = MigrationRunner.LatestVersion,
            }.ToJsonString());
            return 0;
        }

        Console.WriteLine(VersionString);
        return 0;
    }

    private static int PrintPipeName(CommandLineOptions options)
    {
        var name = PipeNaming.CurrentUserPipeName();
        if (options.Json)
        {
            Console.WriteLine(new JsonObject
            {
                ["pipe_name"] = name,
                ["server_name"] = PipeNaming.ToServerName(name),
            }.ToJsonString());
            return 0;
        }

        Console.WriteLine(name);
        return 0;
    }

    private static int RunReplay(CommandLineOptions options)
    {
        var result = FixtureReplayRunner.Run(options.FixturePath!, options.DatabasePath);
        Console.WriteLine(FixtureReplayRunner.Serialize(result));
        return 0;
    }

    private static int RunDecodedReplay(CommandLineOptions options)
    {
        var result = DecodedReplayRunner.Run(
            options.FixturePath!, options.ProfilePath, options.DatabasePath);
        Console.WriteLine(DecodedReplayRunner.Serialize(result));
        return 0;
    }

    /// <summary>
    /// Validates one protocol profile. Exit code 0 means the profile is loadable, 1 means it
    /// was refused, and 2 means the check itself could not run. CI relies on those three.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    private static int ValidateProfile(CommandLineOptions options)
    {
        ProfileValidationReport report;
        try
        {
            report = ProfileLoader.Validate(options.ProfilePath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine("cannot validate: " + ex.Message);
            return 2;
        }

        if (options.Json)
        {
            Console.WriteLine(report.ToJson());
            return report.Ok ? 0 : 1;
        }

        Console.WriteLine(report.Path);
        Console.WriteLine("  profile_id:  " + (report.ProfileId ?? "<unreadable>"));
        Console.WriteLine("  region:      " + (report.Region ?? "<unreadable>"));
        Console.WriteLine("  game_build:  " + (report.GameBuild ?? "<unreadable>"));
        Console.WriteLine("  status:      " + (report.Status ?? "<unreadable>"));
        Console.WriteLine("  messages:    " + report.MessageCount.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("  fixtures:    " + (report.FixtureVerified ? "verified" : "not verified"));
        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"  warning {warning.Code} at {warning.Path}: {warning.Message}");
        }

        foreach (var error in report.Errors)
        {
            Console.WriteLine($"  error {error.Code} at {error.Path}: {error.Message}");
        }

        Console.WriteLine(report.Ok ? "  result:      VALID" : "  result:      REFUSED");
        return report.Ok ? 0 : 1;
    }

    private static int ListProfiles(CommandLineOptions options)
    {
        var root = options.ProfilesDirectory ?? ProfileCatalog.FindDefaultRoot();
        var catalog = ProfileCatalog.Load(root);
        if (options.Json)
        {
            var rows = new JsonArray();
            foreach (var entry in catalog.Entries)
            {
                rows.Add(new JsonObject
                {
                    ["path"] = entry.Path,
                    ["profile_id"] = entry.Report.ProfileId,
                    ["region"] = entry.Report.Region,
                    ["game_build"] = entry.Report.GameBuild,
                    ["status"] = EnumWire<ProfileCompatibilityStatus>.Format(entry.Status),
                    ["message_count"] = entry.Report.MessageCount,
                    ["fixture_verified"] = entry.Report.FixtureVerified,
                    ["usable"] = entry.Profile?.ToBinding().IsUsable ?? false,
                    ["error_count"] = entry.Report.Errors.Count,
                });
            }

            Console.WriteLine(new JsonObject
            {
                ["root"] = catalog.Root,
                ["profiles"] = rows,
            }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine("protocol profiles root: " + (catalog.Root ?? "<none found>"));
        if (catalog.Entries.Count == 0)
        {
            Console.WriteLine("(no profiles installed)");
            return 0;
        }

        foreach (var entry in catalog.Entries)
        {
            var status = EnumWire<ProfileCompatibilityStatus>.Format(entry.Status);
            var usable = (entry.Profile?.ToBinding().IsUsable ?? false) ? "usable" : "fail-closed";
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "  {0,-20} {1,-8} {2,-22} {3,-11} {4,-11} messages={5}",
                entry.Report.ProfileId ?? "<unreadable>",
                entry.Report.Region ?? "-",
                entry.Report.GameBuild ?? "-",
                status,
                usable,
                entry.Report.MessageCount));
            foreach (var error in entry.Report.Errors)
            {
                Console.WriteLine("      error " + error.Code + ": " + error.Message);
            }
        }

        return 0;
    }

    private static async Task<int> ServeAsync(CommandLineOptions options)
    {
        var pipeName = options.PipeName ?? PipeNaming.CurrentUserPipeName();

        // The log folder is probed before anything is written to it: an unwritable one would
        // otherwise discard every line in silence, leaving only a counter in
        // GetStatus.warnings. An unwritable --db refuses to start; so does this
        // (review finding L-2).
        var logDirectory = EnsureWritableLogDirectory(options.ResolvedLogDirectory);

        // The logger comes first so that a refused single-instance lease leaves a trace. Its
        // folder comes from the command line, not from %LOCALAPPDATA%: a run pointed at a
        // throw-away database must not rotate -- let alone prune -- the diagnostics of the
        // user sitting at the machine.
        using var logger = new RotatingFileLogger(logDirectory, Domain.Time.SystemClock.Instance);
        using var serveLease = AcquireServeLease(pipeName, logger);
        using var stopping = new CancellationTokenSource();
        using var stopRequested = OpenStopRequest(pipeName, stopping, logger);
        // Declare the watchdog before every resource whose disposal it supervises. Its
        // hard-exit deadline must survive a blocked pipe, capture or database shutdown.
        using var watchdog = StartParentWatchdog(options, stopping, logger);
        using var host = CollectorHost.Open(options.DatabasePath, logger: logger);

        logger.Write(LogLevel.Info, "startup", "database_ready", new Dictionary<string, object?>
        {
            ["schema_version"] = host.Database.SchemaVersion,
            ["recovered_runs"] = host.Recovery.RecoveredCount,
            ["closed_sessions"] = host.Recovery.ClosedSessionCount,
        });
        PruneLocalProfiles(logger);
        var sweptOrphans = Capture.OodleTempCopyCleaner.SweepOrphans();
        if (sweptOrphans.Removed > 0 || sweptOrphans.Locked > 0)
        {
            logger.Write(LogLevel.Info, "startup", "oodle_temp_orphans", new Dictionary<string, object?>
            {
                ["removed"] = sweptOrphans.Removed,
                ["bytes"] = sweptOrphans.Bytes,
                ["locked"] = sweptOrphans.Locked,
            });
        }

        var dispatcher = new MessageDispatcher(host);
        await using var server = new PipeServer(
            dispatcher,
            pipeName,
            log: (message, error) => logger.WriteError("ipc", message, error));

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            // Graceful stop: refuse the default kill so open connections get to finish.
            eventArgs.Cancel = true;
            try
            {
                stopping.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ctrl+C after the serve loop already finished and released the source: the
                // same window the stop event and the watchdog guard against (R-11).
            }
        };

        // A Collector launched by the Desktop stops when the Desktop does. The watchdog raises
        // the same request Ctrl+C raises above -- one stop path, not two -- so capture stops,
        // the run in flight closes through the ordinary lifecycle, the pipe drains and the host
        // disposes. Without it a force-killed Desktop leaves an invisible Collector holding the
        // per-user serve lease, which the next Desktop launch silently "reuses".

        if (options.Json)
        {
            Console.WriteLine(new JsonObject
            {
                ["mode"] = "serve",
                ["pipe_name"] = server.PipeName,
                ["database_path"] = host.Database.Path,
                ["schema_version"] = host.Database.SchemaVersion,
                ["recovered_runs"] = host.Recovery.RecoveredCount,
                ["parent_pid"] = options.ParentProcessId,
            }.ToJsonString());
        }
        else
        {
            Console.WriteLine(VersionString);
            Console.WriteLine("pipe: " + server.PipeName);
            Console.WriteLine("database: " + host.Database.Path);
            Console.WriteLine($"schema: v{host.Database.SchemaVersion}");
            Console.WriteLine($"recovered unfinished runs: {host.Recovery.RecoveredCount}");
            if (options.ParentProcessId is int parentPid)
            {
                Console.WriteLine(
                    "parent pid: " + parentPid.ToString(CultureInfo.InvariantCulture) +
                    " (this process stops when that one does)");
            }

            Console.WriteLine("press Ctrl+C to stop");
        }

        Console.Out.Flush();
        // The pid file is the Desktop's licence to end a hung holder, so it is written only
        // after migrations, crash recovery and the pipe server are all in place: a Collector
        // still running a schema migration is not hung, and a pid file that appears before the
        // pipe lets a second Desktop kill a healthy instance (review 2 finding H-B). Declared
        // last, it is also the first thing removed on the way out, so a stopping instance is
        // never mistaken for a live one.
        using var servePid = ServePidFile.Write(logDirectory, pipeName, logger);
        try
        {
            await server.RunAsync(stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: a requested stop is a successful exit, not an error.
        }
        logger.Write(LogLevel.Info, "shutdown", "stopped", new Dictionary<string, object?>
        {
            ["accepted_connections"] = server.AcceptedCount,
        });
        return 0;
    }

    /// <summary>
    /// Creates the log folder and proves a file can be written into it.
    ///
    /// A probe rather than a permission check: the answer that matters is whether a line will
    /// land, and the ways it can fail (a file where a folder was named, a read-only folder, a
    /// disconnected network path, a name the filesystem refuses) are not all visible from an
    /// access-control list.
    /// </summary>
    /// <param name="logDirectory">Folder the diagnostics will be written to.</param>
    /// <returns>The same folder, once it is known to be usable.</returns>
    private static string EnsureWritableLogDirectory(string logDirectory)
    {
        try
        {
            Directory.CreateDirectory(logDirectory);
            var probe = Path.Combine(logDirectory, ".write-probe-" + Guid.NewGuid().ToString("N"));
            using (var stream = new FileStream(
                probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return logDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            throw new IOException(
                "无法写入日志目录，采集服务未启动。请检查 --log-dir 指向的位置是否存在、是否是文件、" +
                "以及当前用户是否有写入权限。", ex);
        }
    }

    /// <summary>
    /// Opens the graceful-stop event and routes it into the one cancellation source the serve
    /// loop watches -- the same one Ctrl+C and the parent watchdog use. One stop path, not
    /// three (review finding H-2).
    /// </summary>
    /// <param name="pipeName">Bare pipe name; the event is named after it.</param>
    /// <param name="stopping">The one cancellation source the serve loop watches.</param>
    /// <param name="logger">Local diagnostic log.</param>
    internal static IDisposable OpenStopRequest(
        string pipeName, CancellationTokenSource stopping, RotatingFileLogger logger)
    {
        EventWaitHandle handle;
        try
        {
            // The Desktop may have created it already; either way both sides end up on the
            // same kernel object, and a manual-reset event stays set until this process exits.
            handle = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName(pipeName));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            logger.WriteError("lifecycle", "stop_event_unavailable", ex);
            return NullDisposable.Instance;
        }

        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (_, _) =>
            {
                logger.Write(LogLevel.Info, "lifecycle", "stop_requested", new Dictionary<string, object?>());
                try
                {
                    if (!stopping.IsCancellationRequested)
                    {
                        stopping.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // The serve loop finished and released the source between the check and the
                    // call. Nothing is left to stop, and an unhandled exception on a pool
                    // thread would end the process with 0xE0434352 instead of 0
                    // (review finding R-11).
                }
            },
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);

        return new StopRequestSubscription(handle, registration);
    }

    /// <summary>
    /// Unregisters the stop-event wait and closes the handle, in that order.
    ///
    /// The unregister waits for a callback that is already running: without that wait, a
    /// callback could still be inside <c>stopping.Cancel()</c> while this method returns and
    /// the serve loop goes on to dispose the source it is cancelling (review finding R-11).
    /// </summary>
    private sealed class StopRequestSubscription(
        EventWaitHandle handle, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            using var unregistered = new ManualResetEvent(false);
            if (registration.Unregister(unregistered))
            {
                unregistered.WaitOne(UnregisterWaitTimeout);
            }

            handle.Dispose();
        }
    }

    /// <summary>
    /// How long disposal waits for an in-flight stop-event callback to return. The callback
    /// only cancels a token; a bounded wait exists so shutdown can never hang on it.
    /// </summary>
    private static readonly TimeSpan UnregisterWaitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Does nothing on disposal; used where an optional resource could not be taken.</summary>
    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The <c>serve.pid</c> file: which process holds the pipe right now.
    ///
    /// Written so a launcher that finds the holder unresponsive has something to act on. A
    /// stale file left by a crash is harmless: it is overwritten on the next successful lease,
    /// and the lease is what decides who serves.
    /// </summary>
    private sealed class ServePidFile : IDisposable
    {
        private readonly string _path;

        private ServePidFile(string path) => _path = path;

        public static IDisposable Write(string directory, string pipeName, RotatingFileLogger logger)
        {
            var path = Path.Combine(directory, ServePidFileNameFor(pipeName));
            try
            {
                File.WriteAllText(
                    path,
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    new UTF8Encoding(false));
                return new ServePidFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                logger.WriteError("startup", "serve_pid_write_failed", ex);
                return NullDisposable.Instance;
            }
        }

        public void Dispose()
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover file names a process that is gone; the lease still decides.
            }
        }
    }

    /// <summary>
    /// Keeps the newest three self-calibrated profiles per region under the data directory;
    /// the game is only ever playable on its latest build, so older ones are dead weight.
    /// Best effort: a failure is logged, never fatal.
    /// </summary>
    private static void PruneLocalProfiles(RotatingFileLogger logger)
    {
        try
        {
            // Each calibrated root keeps its own quota, so shared profiles never push out local ones.
            var deleted = Protocol.Profiles.ProfileCatalog.PruneLocalProfiles(Protocol.Profiles.ProfileCatalog.LocalRootPath).Count;
            var deletedShared = Protocol.Profiles.ProfileCatalog.PruneLocalProfiles(Protocol.Profiles.ProfileCatalog.SharedRootPath).Count;
            if (deleted + deletedShared > 0)
            {
                logger.Write(LogLevel.Info, "startup", "local_profiles_pruned", new Dictionary<string, object?>
                {
                    ["deleted"] = deleted,
                    ["deleted_shared"] = deletedShared,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.WriteError("startup", "local_profiles_prune_failed", ex);
        }
    }

    /// <summary>
    /// Starts the parent-process watchdog when <c>--parent-pid</c> was given, and returns null
    /// when it was not. A Collector started by hand answers to nobody and keeps running until
    /// it is told to stop.
    /// </summary>
    /// <param name="options">Parsed command line.</param>
    /// <param name="stopping">The one cancellation source the serve loop watches.</param>
    /// <param name="logger">Local diagnostic log.</param>
    private static ParentProcessWatchdog? StartParentWatchdog(
        CommandLineOptions options, CancellationTokenSource stopping, RotatingFileLogger logger)
    {
        if (options.ParentProcessId is not int parentProcessId)
        {
            return null;
        }

        var watchdog = new ParentProcessWatchdog(
            parentProcessId,
            parentStartTime: options.ParentStartTime,
            requestStop: () =>
            {
                if (!stopping.IsCancellationRequested)
                {
                    stopping.Cancel();
                }
            },
            log: (message, error) => logger.WriteError("lifecycle", message, error));

        watchdog.Start();
        logger.Write(LogLevel.Info, "lifecycle", "parent_watchdog_started", new Dictionary<string, object?>
        {
            ["parent_pid"] = parentProcessId,
        });

        return watchdog;
    }

    /// <summary>
    /// Takes the single-instance lease for <paramref name="pipeName"/>, or refuses to start.
    ///
    /// Two gates, because the lease alone has a blind spot: the named event lives in the
    /// <c>Local\</c> namespace, which is per logon session, so the same user logged on twice --
    /// a switched user, an RDP session alongside a console session -- would take two leases and
    /// two Collectors would fight over one database. The pipe is machine-wide and answers what
    /// the lease cannot.
    ///
    /// The refusal is neither <c>ERR_INTERNAL</c> nor exit code 3: "another Collector is
    /// already serving" is an ordinary outcome the Desktop acts on by connecting to the running
    /// one, and <see cref="ExitCodeAlreadyRunning"/> exists to keep it apart from a fault.
    /// </summary>
    /// <param name="pipeName">
    /// Bare pipe name this Collector serves. The lease is keyed by it so a harness Collector on
    /// its own pipe and the user's Collector on the per-user pipe can both be alive.
    /// </param>
    /// <param name="logger">Local diagnostic log; the refusal is recorded before it is thrown.</param>
    private static IDisposable AcquireServeLease(string pipeName, RotatingFileLogger logger)
    {
        var name = "Local\\" + pipeName + ServeLeaseSuffix;

        var presence = ProbePipe(pipeName);
        if (presence != PipePresence.Absent)
        {
            throw AlreadyRunningOrHung(logger, "pipe", pipeName, presence);
        }

        try
        {
            // A named event is used as a lifetime lease rather than a Mutex: a Mutex is
            // thread-affine, while ServeAsync can resume on a different pool thread after an
            // await. The kernel object disappears automatically when the owning process closes
            // its last handle, including after a crash.
            var lease = new EventWaitHandle(
                initialState: false,
                EventResetMode.ManualReset,
                name,
                out var createdNew);
            if (!createdNew)
            {
                lease.Dispose();

                // Re-read the pipe: between the two gates the holder may have finished
                // creating it, or finished tearing it down.
                throw AlreadyRunningOrHung(logger, "lease", pipeName, ProbePipe(pipeName));
            }

            return lease;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.WriteError("startup", "serve_lease_failed", ex);
            throw new CollectorException(
                ErrorCodes.Internal,
                "无法建立 Collector 单实例锁，详情见本机日志。",
                inner: ex);
        }
    }

    /// <summary>What the object manager says about a pipe name right now.</summary>
    internal enum PipePresence
    {
        /// <summary>Nobody is serving the name.</summary>
        Absent,

        /// <summary>An instance is available to connect to.</summary>
        Present,

        /// <summary>The name is served, but every instance is currently busy with a client.</summary>
        Busy,
    }

    /// <summary>
    /// Asks the object manager whether the pipe exists, without connecting to it.
    ///
    /// <c>File.Exists(@"\\.\pipe\...")</c> must not be used: it calls <c>CreateFile</c>, which
    /// really connects to the serving instance and produces a phantom accept on a healthy
    /// Collector. <c>WaitNamedPipe</c> answers the same question and connects to nothing
    /// (review finding H-5).
    ///
    /// The timeout argument must be <c>NMPWAIT_NOWAIT</c> (1), which returns immediately; 0 is
    /// <c>NMPWAIT_USE_DEFAULT_WAIT</c>, the default timeout baked into the server when it
    /// created the pipe (review finding R-3).
    ///
    /// "Busy" is kept apart from "present": an instance busy serving another client cannot
    /// accept the liveness probe either, so probing it would report a healthy Collector as
    /// hung.
    /// </summary>
    /// <param name="pipeName">Bare pipe name.</param>
    internal static PipePresence ProbePipe(string pipeName)
    {
        if (WaitNamedPipeW(PipeNaming.ToServerName(pipeName), NmpWaitNoWait))
        {
            return PipePresence.Present;
        }

        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() switch
        {
            // ERROR_FILE_NOT_FOUND: nobody is serving this name.
            2 => PipePresence.Absent,

            // ERROR_SEM_TIMEOUT / ERROR_PIPE_BUSY: served, every instance occupied.
            121 or 231 => PipePresence.Busy,

            _ => PipePresence.Present,
        };
    }

    /// <summary><c>NMPWAIT_NOWAIT</c>: return at once instead of using the server's default wait.</summary>
    private const uint NmpWaitNoWait = 1;

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeoutMilliseconds);

    /// <summary>
    /// Records and builds the refusal, after finding out whether the holder is alive.
    ///
    /// The two answers need opposite responses. A Collector that answers <c>GetVersion</c> is
    /// something to connect to; one that holds the pipe and says nothing is something to end.
    /// A launcher told "already running, reuse it" for both keeps restarting a child that exits
    /// with 4 every time (review finding H-5).
    /// </summary>
    /// <param name="logger">Local diagnostic log.</param>
    /// <param name="gate">Which of the two gates refused; never contains a path or a name.</param>
    /// <param name="pipeName">Pipe the holder is serving, for the liveness probe.</param>
    /// <param name="presence">
    /// What the object manager says about the pipe. Only a pipe that exists and is free to
    /// accept a connection may be called hung: a holder that has the lease but no pipe is
    /// either still starting or already stopping, and a holder whose every instance is busy is
    /// demonstrably serving somebody. Both mean "reuse it, keep reconnecting"; reporting them
    /// as hung has the Desktop <c>taskkill /F</c> a healthy or gracefully closing Collector
    /// (review finding R-3).
    /// </param>
    internal static Exception AlreadyRunningOrHung(
        RotatingFileLogger logger, string gate, string pipeName, PipePresence presence)
    {
        var probed = presence == PipePresence.Present;
        var responsive = !probed || ProbeHolder(pipeName);
        logger.Write(LogLevel.Warn, "startup", "already_running", new Dictionary<string, object?>
        {
            ["gate"] = gate,
            ["pipe"] = presence.ToString().ToUpperInvariant(),
            ["probed"] = probed,
            ["holder_responsive"] = responsive,
            ["exit_code"] = responsive ? ExitCodeAlreadyRunning : ExitCodeAlreadyRunningUnresponsive,
        });

        return responsive
            ? new CollectorException(
                ErrorCodes.AlreadyRunning,
                "Collector 已在本机运行。请复用现有实例，而不是再次启动。")
            : new UnresponsiveCollectorException(
                "本机已有一个 Collector 占用着通信管道，但它没有响应，无法复用。" +
                "请在任务管理器中结束 MentorRecorder.Collector 后重试；" +
                "它的进程号记录在日志目录下的 " + ServePidFileNameFor(pipeName) + "。");
    }

    /// <summary>
    /// Connects to the holder and asks it for its version, repeatedly.
    ///
    /// <see cref="LivenessProbeAttempts"/> attempts of <see cref="LivenessProbeTimeout"/>
    /// each, three seconds in total: long enough to cover a large database being opened or a
    /// capture being stopped, and unnoticeable in the common case, where the first attempt
    /// succeeds against a healthy Collector.
    /// </summary>
    /// <param name="pipeName">Pipe the holder is serving.</param>
    /// <returns>True when the holder answered.</returns>
    internal static bool ProbeHolder(string pipeName)
    {
        for (var attempt = 0; attempt < LivenessProbeAttempts; attempt++)
        {
            try
            {
                if (ProbeHolderAsync(pipeName).GetAwaiter().GetResult())
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // Try again; only the last answer decides.
            }
        }

        return false;
    }

    private static async Task<bool> ProbeHolderAsync(string pipeName)
    {
        await using var client = new PipeClient(pipeName);
        using var deadline = new CancellationTokenSource(LivenessProbeTimeout);
        try
        {
            await client.ConnectAsync(LivenessProbeTimeout, deadline.Token).ConfigureAwait(false);
            var response = await client.SendAsync(
                "GetVersion",
                new JsonObject(),
                timeout: LivenessProbeTimeout,
                cancellationToken: deadline.Token).ConfigureAwait(false);
            return response.Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// The pipe is held by a Collector that will not answer. Carried as its own exception type
    /// so <see cref="Main"/> can map it to <see cref="ExitCodeAlreadyRunningUnresponsive"/>
    /// without inspecting the message.
    /// </summary>
    private sealed class UnresponsiveCollectorException(string message) : Exception(message);

    private static void PrintUsage() =>
        Console.Error.WriteLine(
            "usage: MentorRecorder.Collector [--serve] [--db <path>] [--log-dir <path>]" +
            " [--parent-pid <pid>] [--parent-start-time <ticks|ISO-8601>] [--pipe <name>] [--json]" +
            Environment.NewLine +
            "       MentorRecorder.Collector --replay <fixture> [--db <path>]" + Environment.NewLine +
            "       MentorRecorder.Collector --replay-decoded <fixture> [--profile <file>] [--db <path>] [--json]" +
            Environment.NewLine +
            "       MentorRecorder.Collector --validate-profile <file> [--json]" + Environment.NewLine +
            "       MentorRecorder.Collector --list-profiles [--profiles-dir <path>] [--json]" +
            Environment.NewLine +
            "       MentorRecorder.Collector --capture-doctor [--json]" + Environment.NewLine +
            "       MentorRecorder.Collector --capture-trace <out.jsonl> [--duration-seconds <n>]" +
            " [--adapter <id>] [--max-lines <n>]" + Environment.NewLine +
            "       MentorRecorder.Collector --trace-report <in.jsonl> [--around <marker>]" +
            " [--window-ms <n>]" + Environment.NewLine +
            "       MentorRecorder.Collector --pipe-name-only [--json]" + Environment.NewLine +
            "       MentorRecorder.Collector --version [--json]" + Environment.NewLine +
            Environment.NewLine +
            "  --log-dir defaults to <dir of --db>\\logs, or %LOCALAPPDATA%\\MentorRecorder\\logs." +
            Environment.NewLine +
            "  MR_DATA_DIR moves the database, logs, backups and default export folders at once." +
            Environment.NewLine +
            "  --log-dir must be creatable and writable; an unusable one refuses to start (3)." +
            Environment.NewLine +
            "  exit codes: 0 ok, 1 profile refused, 2 bad command line, 3 failed," +
            " 4 already running, 6 already running but unresponsive.");
}
