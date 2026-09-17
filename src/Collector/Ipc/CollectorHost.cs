using System.Diagnostics;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Recovery;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Everything one running Collector owns: the database, the repositories built on it, the
/// mutation and export services, and the live event bus.
///
/// It is constructed once at startup and shared by every pipe connection. The database class
/// serialises its own access, so sharing is safe and there is still exactly one writer
/// (docs/architecture.md section 2.1).
/// </summary>
public sealed class CollectorHost : IDisposable
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private RotatingFileLogger _logger = RotatingFileLogger.Disabled;
    private bool _disposed;

    private CollectorHost(SqliteDatabase database, IClock clock, string captureSessionId)
    {
        Database = database;
        Clock = clock;
        CaptureSessionId = captureSessionId;
        Runs = new RunRepository(database);
        Revisions = new RunRevisionRepository(database);
        Events = new RunEventRepository(database);
        Sessions = new CaptureSessionRepository(database);
        Settings = new SettingsRepository(database, clock);
        Statistics = new StatisticsRepository(database, Settings, JobCatalog.Default, DutyCatalog.Default);
        Mutations = new RunMutationService(database, Settings, clock);
        Reflections = new RunReflectionRepository(database);
        ReflectionWrites = new RunReflectionService(database, clock);
        Candidates = new CandidateObservationRepository(database, clock);
        CandidateExporter = new CandidateEvidenceExporter(Candidates, clock);
        Exporter = new RunExporter(Runs, clock, database);
        Backups = new BackupService(database, clock);
        DiagnosticsReports = new DiagnosticsReportExport(database.Path, clock);
        LiveEvents = new LiveEventBus(clock);
    }

    /// <summary>Open database; the single writer.</summary>
    public SqliteDatabase Database { get; }

    /// <summary>
    /// The local diagnostic log this process writes to, so <c>GetStatus</c> can report on the
    /// log's own health; a log that is quietly losing lines must be surfaced.
    /// </summary>
    public Diagnostics.RotatingFileLogger Logger => _logger;

    /// <summary>Clock used by every service.</summary>
    public IClock Clock { get; }

    /// <summary>Identifier of the session this process opened at startup.</summary>
    public string CaptureSessionId { get; }

    /// <summary>Run repository.</summary>
    public RunRepository Runs { get; }

    /// <summary>Append-only revision repository.</summary>
    public RunRevisionRepository Revisions { get; }

    /// <summary>Per-run event trail.</summary>
    public RunEventRepository Events { get; }

    /// <summary>Capture session repository.</summary>
    public CaptureSessionRepository Sessions { get; }

    /// <summary>Settings and achievement baseline.</summary>
    public SettingsRepository Settings { get; }

    /// <summary>Statistics over the local database.</summary>
    public StatisticsRepository Statistics { get; }

    /// <summary>Every write a human can cause.</summary>
    public RunMutationService Mutations { get; }

    /// <summary>导随心得 reads: one per run, plus the dashboard summary.</summary>
    public RunReflectionRepository Reflections { get; }

    /// <summary>The single, idempotent writer of 导随心得.</summary>
    public RunReflectionService ReflectionWrites { get; }

    /// <summary>独立候选账本；不参与正式记录和统计。</summary>
    public CandidateObservationRepository Candidates { get; }
    public CandidateEvidenceExporter CandidateExporter { get; }

    /// <summary>CSV and JSON export.</summary>
    public RunExporter Exporter { get; }

    /// <summary>Database backup and retention.</summary>
    public BackupService Backups { get; }

    /// <summary>Sanitized diagnostics report export.</summary>
    public DiagnosticsReportExport DiagnosticsReports { get; }

    /// <summary>In-process live event fan-out.</summary>
    public LiveEventBus LiveEvents { get; }

    /// <summary>
    /// Online speech: settings, the key, the audio cache beside the database, and the one request
    /// slot (docs/privacy-boundary.md §8.3). Off until the user configures it.
    /// </summary>
    public OnlineSpeechService Speech { get; private set; } = null!;

    /// <summary>
    /// The capture pipeline: Npcap detection, adapter selection, the Machina monitor and the
    /// bounded parser queue. Always present, even where capture cannot run -- there it
    /// reports <c>UNAVAILABLE</c> honestly rather than being absent.
    /// </summary>
    public CaptureController Capture { get; private set; } = null!;

    public CaptureValidationController Validation { get; private set; } = null!;

    /// <summary>
    /// Live protocol bridge used by the shipping capture path. It is null only when a test
    /// supplied its own capture sink and profile provider.
    /// </summary>
    public LiveProtocolPipeline? LiveProtocol { get; private set; }

    /// <summary>Milliseconds since this host was created.</summary>
    public long UptimeMs => (long)_uptime.Elapsed.TotalMilliseconds;

    /// <summary>Outcome of the crash recovery pass that ran at startup.</summary>
    public CrashRecoveryReport Recovery { get; private set; } = CrashRecoveryReport.Empty;

    /// <summary>
    /// Opens the database, migrates it, seeds defaults, opens this process's capture session
    /// and runs crash recovery. Everything that can refuse to start happens here, before any
    /// pipe is created.
    /// </summary>
    /// <param name="databasePath">Database file; the default location when null.</param>
    /// <param name="clock">Clock; the system clock when null.</param>
    /// <param name="capture">
    /// Capture dependencies. Tests substitute a <see cref="FakeCaptureSource"/> here so the
    /// whole pipeline can be exercised without Npcap and without the game; production passes
    /// null and gets the Machina monitor.
    /// </param>
    /// <param name="logger">Local diagnostic log used by the capture layer.</param>
    /// <param name="profileSelector">
    /// Optional selector used by end-to-end tests. Production loads the installed catalogue;
    /// providing this value also opts a custom capture service set into the real protocol
    /// bridge.
    /// </param>
    /// <param name="validation">Capture validation dependencies; the shipping ones when null.</param>
    /// <param name="speechClient">
    /// Online speech client. Production passes null and gets the real one, which still sends nothing
    /// until the user configures a service; tests pass one over a fake transport.
    /// </param>
    /// <param name="speechProtector">Key encryption; DPAPI when null.</param>
    public static CollectorHost Open(
        string? databasePath = null,
        IClock? clock = null,
        CaptureServices? capture = null,
        RotatingFileLogger? logger = null,
        Func<GameProcessDetection, ProfileSelection>? profileSelector = null,
        CaptureValidationServices? validation = null,
        OnlineSpeechClient? speechClient = null,
        ISecretProtector? speechProtector = null)
    {
        var effectiveClock = clock ?? SystemClock.Instance;
        var path = string.IsNullOrWhiteSpace(databasePath)
            ? DatabasePaths.DefaultDatabasePath
            : databasePath;

        var database = SqliteDatabase.Open(path, effectiveClock);
        try
        {
            ReclaimOodleTempCopies(path, logger);
            var host = new CollectorHost(database, effectiveClock, Guid.NewGuid().ToString("D"));
            host.Settings.EnsureDefaults();
            host.Recovery = CrashRecoveryService.Run(host);
            var services = host.BuildCaptureServices(capture, logger, profileSelector);
            host._logger = services.Logger;
            var dataDirectory = Path.GetDirectoryName(database.Path) ?? DatabasePaths.RootDirectory;
            host.Speech = new OnlineSpeechService(
                host.Settings,
                new SpeechKeyStore(dataDirectory, speechProtector),
                new SpeechCache(dataDirectory, effectiveClock),
                speechClient ?? OnlineSpeechClient.CreateDefault(),
                () => host._logger,
                effectiveClock);

            // The settings the user chose apply from the first line this process writes, not
            // from the first time they open the settings page.
            host.ApplyCaptureSettings(CaptureSettingsStore.Read(host.Settings));
            host.Capture = new CaptureController(services);
            host.Validation = new CaptureValidationController(database.Path, services.Ownership, validation ?? new CaptureValidationServices
            {
                Trace = new CaptureTraceServices
                {
                    Npcap = services.Npcap, Game = services.Game, Adapters = services.Adapters,
                    Clock = services.Clock, Logger = services.Logger, CollectorVersion = services.CollectorVersion,
                },
            });
            return host;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes the temporary copies of the game executable a previous Collector registered and
    /// never got to remove.
    ///
    /// The ordinary Desktop exit kills the Collector outright, so the deletion in
    /// <c>MachinaCaptureSource.StopCore</c>'s finally block does not run and each abandoned
    /// copy costs about 49.5 MiB. Ownership is definite rather than a TEMP scan: only paths
    /// this software wrote into the manifest are considered, and each must still sit inside
    /// Machina's own temp subdirectory (review finding H-2).
    /// </summary>
    /// <param name="databasePath">Database this Collector opened; the manifest sits beside it.</param>
    /// <param name="logger">Local diagnostic log.</param>
    private static void ReclaimOodleTempCopies(string databasePath, RotatingFileLogger? logger)
    {
        var manifest = DatabasePaths.ResolveOodleTempManifest(databasePath);
        var log = logger ?? RotatingFileLogger.Disabled;
        try
        {
            var removed = OodleTempCopyCleaner.SweepManifest(manifest);
            if (removed > 0)
            {
                log.Write(LogLevel.Info, "startup", "oodle_temp_copy_reclaimed",
                    new Dictionary<string, object?> { ["count"] = removed });
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            log.WriteError("startup", "oodle_temp_reclaim_failed", ex);
        }
    }

    /// <summary>
    /// Fills the storage-, clock- and event-shaped holes in the caller's capture services.
    /// Anything the caller supplied is kept, so a test can replace the source, the sink or the
    /// profile provider and still get the real settings and the real event bus.
    /// </summary>
    private CaptureServices BuildCaptureServices(
        CaptureServices? provided,
        RotatingFileLogger? logger,
        Func<GameProcessDetection, ProfileSelection>? profileSelector)
    {
        var services = provided ?? new CaptureServices();
        if (provided is null || profileSelector is not null)
        {
            var pipeline = profileSelector is null
                ? LiveProtocolPipeline.CreateDefault(Database, Clock, LiveEvents)
                : new LiveProtocolPipeline(Database, Clock, LiveEvents, profileSelector);
            LiveProtocol = pipeline;
            pipeline.CandidateObserved += observation =>
            {
                if (Candidates.Add(observation)) LiveEvents.PublishCandidateObserved(observation);
            };
            pipeline.CalibrationChanged += state =>
            {
                LiveEvents.PublishCalibrationChanged(state);
                var snapshot = pipeline.CalibrationStatus();
                _logger.Write(LogLevel.Info, "calibration", "calibration_state", new Dictionary<string, object?>
                {
                    ["state"] = MentorRecorder.Collector.Capture.CalibrationWire.State(state),
                    ["game_build"] = snapshot.GameBuild,
                    ["template"] = snapshot.TemplateProfileId,
                    ["blockers"] = snapshot.Blockers.Count,
                    ["sessions"] = snapshot.Evidence?.Sessions,
                    ["pairs"] = snapshot.Evidence is { } e ? string.Join(",", e.Pairs) : null,
                    ["pops"] = snapshot.Evidence is { } p ? string.Join(",", p.Pops) : null,
                    ["clusters"] = snapshot.Evidence?.Clusters,
                    ["duty_zones"] = snapshot.Evidence?.DutyZones,
                    ["zone_candidates"] = snapshot.Evidence?.ZoneCandidates,
                    ["territory_candidates"] = snapshot.Evidence?.TerritoryCandidates,
                    ["zone_once_only"] = snapshot.Evidence is { } z ? string.Join(",", z.ZoneOnceOnly) : null,
                    ["pop_shapes"] = snapshot.Evidence?.PopShapes,
                    ["pop_refusals"] = snapshot.Evidence is { } r ? string.Join(",", r.PopRefusals) : null,
                    ["finder_lengths"] = snapshot.Evidence is { } l ? string.Join(",", l.FinderLengths) : null,
                    ["roulette_echoes"] = snapshot.Evidence is { RouletteEchoes: { } echoes }
                        ? string.Join(",", echoes)
                        : null,
                    ["finder_states"] = snapshot.Evidence is { } s ? string.Join(",", s.FinderStates) : null,
                    ["overflow"] = snapshot.Evidence?.Overflow,
                });
            };
            services = services with
            {
                Sink = pipeline,
                ParserStats = pipeline,
                Profile = pipeline,
                Lifecycle = pipeline,
                Health = pipeline,
                RunState = () => pipeline.RunState,
                CandidateStatus = () => (pipeline.CandidateValidationEnabled, pipeline.CandidateProfileId, Candidates.Count()),
                CandidateHypotheses = pipeline.DescribeCandidateHypotheses,
                CalibrationStatus = pipeline.CalibrationStatus,
            };
        }

        return services with
        {
            Clock = provided?.Clock ?? Clock,
            Logger = logger ?? services.Logger,
            Game = services.Game.WithRegionOverride(
                () => CaptureSettingsStore.ReadRegionOverride(Settings)),
            Settings = services.Settings ?? Settings,
            Sessions = services.Sessions ?? Sessions,
            Database = services.Database ?? Database,
            CollectorVersion = provided is null ? Program.Version : services.CollectorVersion,
            StatusListener = services.StatusListener ?? new LiveEventStatusListener(LiveEvents),
            OodleTempManifestPath = services.OodleTempManifestPath
                ?? DatabasePaths.ResolveOodleTempManifest(Database.Path),
        };
    }

    /// <summary>
    /// Pushes the settings that live outside the settings table at their owners, so that
    /// "saved" and "in force" are the same moment.
    ///
    /// Only log retention needs this. <c>follow_game</c>, <c>allow_without_profile</c>,
    /// <c>adapter_id</c> and <c>region_override</c> are read from the settings table on every
    /// use, so writing them is already enough.
    /// </summary>
    /// <param name="settings">Settings as they now stand.</param>
    public void ApplyCaptureSettings(CaptureSettingsSnapshot settings, bool applyCandidateMode = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger.ApplyRetention(settings.LogRetentionDays);
        LiveProtocol?.ApplyCalibrationSetting(settings.AutoCalibrationEnabled);
        LiveProtocol?.ApplySharedCalibrationSetting(settings.SharedCalibrationEnabled);
        if (applyCandidateMode) LiveProtocol?.ApplyCandidateSettings(settings.CandidateValidationEnabled,
            researchPayloadOpcodes: settings.ResearchPayloadOpcodes);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Capture is stopped before the database closes: the session row is closed on the way
        // out, so a clean shutdown never leaves a session that looks like a crash.
        //
        // Every step is isolated so that one subsystem refusing to shut down cannot take the
        // next one's cleanup with it -- notably the database close, the capture lease and the
        // capture source (review findings L-3, R-15). The first failure is the one reported,
        // after everything else has been released.
        Exception? failure;
        try
        {
            // A download in flight is cancelled before anything it could claim into is torn down.
            LiveProtocol?.StopSharedCalibration();
            Speech?.Dispose();
            failure = DisposeSubsystems(
                Validation, Capture, (record, error) => _logger.WriteError("shutdown", record, error));
        }
        finally
        {
            Database.Dispose();
            _disposed = true;
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Disposes the two capture subsystems, each in its own try, and returns the first failure
    /// for the caller to rethrow after the database has been closed.
    /// </summary>
    /// <param name="validation">Validation controller, or null when the host never built one.</param>
    /// <param name="capture">Capture controller, or null when the host never built one.</param>
    /// <param name="log">Diagnostic sink; receives a record name and the exception.</param>
    internal static Exception? DisposeSubsystems(
        IDisposable? validation, IDisposable? capture, Action<string, Exception> log)
    {
        Exception? failure = null;
        try
        {
            validation?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            failure = ex;
            log("validation_dispose_failed", ex);
        }

        try
        {
            capture?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            failure ??= ex;
            log(ex is TimeoutException ? "capture_dispose_timed_out" : "capture_dispose_failed", ex);
        }

        return failure;
    }

    /// <summary>Publishes capture status changes as <c>CaptureStatusChanged</c> live events.</summary>
    private sealed class LiveEventStatusListener : ICaptureStatusListener
    {
        private readonly LiveEventBus _bus;

        public LiveEventStatusListener(LiveEventBus bus) => _bus = bus;

        public void OnCaptureStatusChanged(CaptureDiagnosticsSnapshot snapshot, string message) =>
            _bus.PublishCollectorStatus(CaptureWire.CaptureStatus(snapshot), message);
    }
}
