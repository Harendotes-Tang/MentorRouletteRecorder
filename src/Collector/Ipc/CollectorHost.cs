using System.Diagnostics;
using MentorRecorder.Collector.Community;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Recovery;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Update;

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
    private readonly object _captureSettingsGate = new();
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
        LiveEvents = new LiveEventBus(clock);
        Mutations = new RunMutationService(database, Settings, clock, onPurged: LiveEvents.ForgetRuns);
        Profiles = new CharacterProfileService(database, Settings, clock);
        Identities = new CharacterIdentityService(database, Settings, clock);
        HistoryRetention = new HistoryRetentionService(database, clock, LiveEvents.ForgetRuns);
        HistorySelections = new HistorySelectionStore(database);
        var communityRepository = new CommunityRepository(database);
        CommunityLibrary = new CommunityLibrary(communityRepository, Settings);
        CommunityWorkspace = new CommunityWorkspace(communityRepository, CommunityLibrary);
        CommunityGithub = new CommunityGithub(Path.GetDirectoryName(database.Path)!);
        Reflections = new RunReflectionRepository(database);
        ReflectionWrites = new RunReflectionService(database, clock);
        Candidates = new CandidateObservationRepository(database, clock);
        CandidateRound = new CandidateRoundReporter(Candidates, Settings, () => LiveProtocol?.CandidateDisplayProfile(),
            ex => _logger.Write(LogLevel.Warn, "candidate_ledger", "round_report_failed",
                new Dictionary<string, object?> { ["error"] = ex.GetType().Name }));
        CandidateExporter = new CandidateEvidenceExporter(Candidates, clock, database.Path, CandidateRound.Read);
        Exporter = new RunExporter(Runs, clock, database);
        Backups = new BackupService(database, clock);
        Imports = new RunImportService(database, clock);
        DiagnosticsReports = new DiagnosticsReportExport(database.Path, clock, WindowsDesktopHangLog.Read);
    }

    /// <summary>Open database; the single writer.</summary>
    public SqliteDatabase Database { get; }

    internal CommunityLibrary CommunityLibrary { get; }
    internal CommunityWorkspace CommunityWorkspace { get; }
    internal CommunityGithub CommunityGithub { get; }

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

    /// <summary>The player's changes to the character profiles.</summary>
    public CharacterProfileService Profiles { get; }

    /// <summary>Which profile a character seen in the game traffic is.</summary>
    public CharacterIdentityService Identities { get; }

    /// <summary>Recycle-bin expiry and persistent attachment cleanup tasks.</summary>
    public HistoryRetentionService HistoryRetention { get; }

    /// <summary>Transient history snapshots and sealed batch targets for this host.</summary>
    internal HistorySelectionStore HistorySelections { get; }

    /// <summary>导随心得 reads: one per run, plus the dashboard summary.</summary>
    public RunReflectionRepository Reflections { get; }

    /// <summary>The single, idempotent writer of 导随心得.</summary>
    public RunReflectionService ReflectionWrites { get; }

    /// <summary>独立候选账本；不参与正式记录和统计。</summary>
    public CandidateObservationRepository Candidates { get; }
    public CandidateEvidenceExporter CandidateExporter { get; }

    private long _candidateRowsRefused;

    /// <summary>Candidate rows the ledger could not store since this host started.</summary>
    public long CandidateRowsRefused => Interlocked.Read(ref _candidateRowsRefused);

    /// <summary>
    /// Stores one candidate observation. A row the ledger refuses is counted, logged and dropped:
    /// this runs on the capture's message path, and an exception leaving it stops the capture,
    /// which is how one refused row cost a whole content round in 1.9.1-beta.3. Candidate rows
    /// are research evidence, never records, so losing one is always better than losing the rest.
    /// </summary>
    /// <param name="observation">Row emitted by the candidate observer.</param>
    public void StoreCandidate(Protocol.Parsing.CandidateObservation observation)
    {
        try
        {
            if (Candidates.Add(observation)) LiveEvents.PublishCandidateObserved(observation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The first few and then every thousandth: a failure that repeats must not flood the log.
            var refused = Interlocked.Increment(ref _candidateRowsRefused);
            if (refused <= 5 || refused % 1000 == 0)
                _logger.Write(LogLevel.Warn, "candidate_ledger", "observation_not_stored", new Dictionary<string, object?>
                {
                    ["hypothesis"] = observation.HypothesisName,
                    ["refused"] = refused,
                    ["error"] = ex.GetType().Name,
                });
        }
    }

    /// <summary>What the software takes the current content round to have been.</summary>
    public CandidateRoundReporter CandidateRound { get; }

    /// <summary>CSV and JSON export.</summary>
    public RunExporter Exporter { get; }

    /// <summary>Database backup and retention.</summary>
    public BackupService Backups { get; }

    /// <summary>Independent personal-history preview and merge workflow.</summary>
    public RunImportService Imports { get; }

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
    /// The update check: the switch, the cached answer and the once-a-day background request
    /// (docs/privacy-boundary.md §8.4). Notification only; the download the user may ask for is
    /// <see cref="UpdateDownloads"/>, and nothing is ever run.
    /// </summary>
    public UpdateCheckService Updates { get; private set; } = null!;

    /// <summary>
    /// 下载并安装: the installer of the newer version, downloaded only when the user asks, checked against its
    /// published SHA-256 and kept in <c>updates\</c> beside the database for the Desktop to start. Never run here.
    /// </summary>
    public UpdateDownloadService UpdateDownloads { get; private set; } = null!;

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
    /// <param name="updateCheckClient">
    /// Update-check client. Production passes null and gets the real one, which still sends nothing
    /// until a check falls due; tests pass one over a fake transport.
    /// </param>
    public static CollectorHost Open(
        string? databasePath = null,
        IClock? clock = null,
        CaptureServices? capture = null,
        RotatingFileLogger? logger = null,
        Func<GameProcessDetection, ProfileSelection>? profileSelector = null,
        CaptureValidationServices? validation = null,
        OnlineSpeechClient? speechClient = null,
        ISecretProtector? speechProtector = null,
        UpdateCheckClient? updateCheckClient = null)
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
            ImportedHistoryMaintenance.Run(host);
            host.Recovery = CrashRecoveryService.Run(host);
            try
            {
                // Rows flagged under the pre-1.3.1 rule (every CorrectRun, confirmations included).
                CorrectedFlagMaintenance.Run(database);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
            {
                // A label is not worth refusing to start over; the next launch tries again.
                logger?.WriteError("storage", "corrected_flag_maintenance_failed", ex);
            }
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
            var updateClient = updateCheckClient ?? UpdateCheckClient.CreateDefault();
            host.Updates = new UpdateCheckService(
                host.Settings,
                updateClient,
                Program.Version,
                effectiveClock);
            host.UpdateDownloads = new UpdateDownloadService(
                host.Updates, updateClient, new UpdateDownloadFiles(dataDirectory), effectiveClock, () => host._logger);

            // What an earlier run left in updates\: a .part a crash or a kill abandoned, installers no longer offered.
            host.UpdateDownloads.RemoveLeftovers();

            // The settings the user chose apply from the first line this process writes, not
            // from the first time they open the settings page.
            host.ApplyCaptureSettings(CaptureSettingsStore.Read(host.Settings));
            host.Capture = new CaptureController(services);
            host.Validation = new CaptureValidationController(database.Path, services.Ownership, validation ?? new CaptureValidationServices
            {
                LocateGame = host.Capture.RescanGame,
                LocateRestartedGame = host.Capture.RescanGameAfterValidationRestart,
                Trace = new CaptureTraceServices
                {
                    Npcap = services.Npcap, Game = services.Game, Adapters = services.Adapters,
                    Clock = services.Clock, Logger = services.Logger, CollectorVersion = services.CollectorVersion,
                },
            });
            host.HistoryRetention.CheckExpired(force: true);
            host.HistoryRetention.StartDailyChecks(
                ex => host._logger.WriteError("storage", "history_retention_failed", ex),
                count => host.LiveEvents.PublishStatsInvalidated($"已清理 {count} 条到期历史记录，请重新查询并处理附件清理任务。"));
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
            pipeline.CandidateObserved += StoreCandidate;
            // A name of what happened, a count and an exception type: never an identifier.
            pipeline.CharacterIdentityNoted += note => _logger.Write(
                note.Error is null && note.Event is not ("key_replace_failed" or "key_unusable" or "key_unavailable"
                    or "login_not_recognised" or "character_unknown_at_match" or "connection_limit_reached")
                    ? LogLevel.Info
                    : LogLevel.Warn,
                "character_identity",
                note.Event,
                new Dictionary<string, object?> { ["count"] = note.Count, ["error"] = note.Error });
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
            // The CN clear: that it was seen, for which run, and how long before the exit - nothing
            // about the message itself (duty-result brief, decision 7).
            pipeline.DutyClearObserved += note =>
                _logger.Write(LogLevel.Info, "protocol", note.LogEvent, note.LogFields());
            // The CN duty-start signals: which one decided how the player got into the duty, and
            // the time limit or time left it carried - nothing about the message itself.
            pipeline.DutyStartObserved += note =>
                _logger.Write(LogLevel.Info, "protocol", note.LogEvent, note.LogFields());
            // A confirmed calibration that could not be written or selected: the system's own words go to the local
            // log only, sanitized like every line, and never to the player (audit 2026-11-03, C-7).
            pipeline.CalibrationFault += (eventName, reason, error) =>
                _logger.WriteError("calibration", eventName, error, reason);
            services = services with
            {
                Sink = pipeline,
                ParserStats = pipeline,
                Profile = pipeline,
                Lifecycle = pipeline,
                Health = pipeline,
                RunState = () => pipeline.RunState,
                RunUnderWay = () => pipeline.RunUnderWayNow,
                CandidateStatus = () => (pipeline.CandidateValidationEnabled, pipeline.CandidateProfileId,
                    CandidatePlayerRound.Read(Settings) is { Active: true } round ? Candidates.CountPlayerRound(round) : Candidates.Count()),
                CandidateHypotheses = pipeline.DescribeCandidateHypotheses,
                CandidateRoundReport = CandidateRound.Read,
                CalibrationStatus = pipeline.CalibrationStatus,
            };
        }

        return services with
        {
            Clock = provided?.Clock ?? Clock,
            Logger = logger ?? services.Logger,
            Game = WithHostSeams(services.Game, logger ?? services.Logger),
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
    /// Gives the locator the two things only this host can supply: the user's region override,
    /// and a place to remember where the client is installed, so the client version is known at
    /// startup rather than only once the game runs. The note sits beside this host's database,
    /// like the Oodle manifest. A caller that supplied its own memory keeps it. Every process
    /// listing is also reported to a rate-limited log line, the only place a process table that
    /// keeps failing to read becomes visible (audit 2026-10-03, CS1-X1).
    /// </summary>
    /// <param name="locator">Locator from the capture services.</param>
    /// <param name="logger">The capture layer's log.</param>
    private GameProcessLocator WithHostSeams(GameProcessLocator locator, RotatingFileLogger logger)
    {
        var listingLog = new ProcessListingLog(logger, "capture");
        var located = locator
            .WithRegionOverride(() => CaptureSettingsStore.ReadRegionOverride(Settings))
            .WithListingObserver(listingLog.Observe);
        return located.RemembersInstall
            ? located
            : located.WithInstallMemory(
                FileGameInstallMemory.At(DatabasePaths.ResolveGameInstallMemory(Database.Path)));
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
    private void ApplyCaptureSettings(CaptureSettingsSnapshot settings, bool applyCandidateMode = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger.ApplyRetention(settings.LogRetentionDays);
        LiveProtocol?.ApplyCalibrationSetting(settings.AutoCalibrationEnabled);
        LiveProtocol?.ApplySharedCalibrationSetting(settings.SharedCalibrationEnabled);
        LiveProtocol?.ApplyCharacterIdentificationSetting(settings.CharacterIdentificationEnabled);
        Updates?.ApplySetting(settings.UpdateCheckEnabled);
        if (!settings.UpdateCheckEnabled)
        {
            // Switched off, nothing is offered (§8.4): a download in flight stops and a ready installer goes.
            UpdateDownloads?.Withdraw();
        }

        if (applyCandidateMode) LiveProtocol?.ApplyCandidateSettings(settings.CandidateValidationEnabled,
            researchPayloadOpcodes: settings.ResearchPayloadOpcodes);
    }

    /// <summary>
    /// Serializes persistence and runtime application across every connection using this host.
    /// The settings gate is acquired before pipeline and database locks; their callbacks must
    /// not start another settings update. Startup applies its initial snapshot before publication.
    /// </summary>
    /// <param name="update">Validated partial update; omitted fields retain their stored values.</param>
    /// <returns>The committed settings after all runtime owners have received the same snapshot.</returns>
    internal CaptureSettingsSnapshot UpdateCaptureSettings(CaptureSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_captureSettingsGate)
        {
            CaptureSettingsSnapshot? applied = null;
            if (update.CandidateValidationEnabled.HasValue || update.ResearchPayloadOpcodes is not null)
            {
                var current = CaptureSettingsStore.Read(Settings);
                var (round, profile, replay) = PlanPlayerRound(current, update);
                if (replay) return current;

                if (round is { Active: false })
                    update = update with { ResearchPayloadOpcodes = [] };
                if (LiveProtocol is { } pipeline)
                {
                    pipeline.ApplyCandidateSettings(update.CandidateValidationEnabled,
                        () => applied = CaptureSettingsStore.Apply(Settings, update, round, profile),
                        update.ResearchPayloadOpcodes);
                }
                else
                {
                    applied = CaptureSettingsStore.Apply(Settings, update, round, profile);
                }
            }
            else
            {
                applied = CaptureSettingsStore.Apply(Settings, update);
            }

            ApplyCaptureSettings(applied!, applyCandidateMode: false);
            return applied!;
        }
    }

    private (CandidatePlayerRound? Round, ProtocolProfile? Profile, bool Replay) PlanPlayerRound(
        CaptureSettingsSnapshot current, CaptureSettingsUpdate update)
    {
        var active = current.PlayerRound is { Active: true } round ? round : null;
        if (update.CandidateRoundId is not { } id)
        {
            if (active is null) return default;
            if (update.CandidateValidationEnabled == false && update.ResearchPayloadOpcodes is not { Count: > 0 })
                return (active with { Active = false }, null, false);
            // A generic whitelist write must not replace the consented scope of a player round.
            throw CollectorException.BadRequest(CollectorTexts.CandidateRoundFinishFirst(), "payload.candidate_round_id");
        }

        if (update.CandidateValidationEnabled == false)
        {
            if (update.ResearchPayloadOpcodes is { Count: > 0 })
                throw CollectorException.BadRequest(CollectorTexts.CaptureSettingsResearchNeedsValidation(),
                    "payload.research_payload_opcodes");
            if (current.PlayerRound is not { } previous || previous.Id != id)
                throw CollectorException.BadRequest(CollectorTexts.CandidateRoundUnavailable(), "payload.candidate_round_id");
            // The acknowledgement might have been lost, or a maintainer session started meanwhile.
            return previous.Active ? (previous with { Active = false }, null, false) : (null, null, true);
        }

        if (update.ResearchPayloadOpcodes is not { } requested)
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing("research_payload_opcodes"),
                "payload.research_payload_opcodes");
        if (active is not null && active.Id == id && active.ProfileId == update.CandidateRoundProfileId &&
            current.CandidateValidationEnabled && (current.ResearchPayloadOpcodes ?? []).SequenceEqual(requested))
            return (null, null, true);
        if (current.CandidateValidationEnabled)
            throw CollectorException.BadRequest(CollectorTexts.CandidateRoundFinishFirst(), "payload.candidate_round_id");
        if (current.PlayerRound?.Id == id || Candidates.HasPlayerRound(id))
            throw CollectorException.BadRequest(CollectorTexts.CandidateRoundUnavailable(), "payload.candidate_round_id");

        var offered = LiveProtocol?.CandidateDisplayProfile();
        if (offered is not { Status: ProfileCompatibilityStatus.Candidate } ||
            offered.ProfileId != update.CandidateRoundProfileId ||
            LiveProtocol?.Current is not { Status: ProfileStatus.UnsupportedBuild, GameBuild: { Length: > 0 } build } ||
            !string.Equals(build, offered.GameBuild, StringComparison.OrdinalIgnoreCase))
            throw CollectorException.BadRequest(CollectorTexts.CandidateRoundProfileChanged(), "payload.candidate_round_profile_id");

        return (new CandidatePlayerRound(id, offered.ProfileId, UtcTimestamp.Truncate(Clock.UtcNow), true), offered, false);
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
            failure = RunShutdownSteps(
                ShutdownSteps(), (record, error) => _logger.WriteError("shutdown", record, error));
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

    /// <summary>The shutdown steps in the order <see cref="Dispose"/> runs them; the database closes after all of them.</summary>
    internal IEnumerable<(string Record, Action Step)> ShutdownSteps()
    {
        // An evidence round starts stopping its capture source first: a source that stops cleanly
        // removes the crash marker of a self-found Oodle signature set, and the Desktop and the
        // parent watchdog give the whole shutdown ten seconds (audit 2026-11-20, A2-1).
        // Non-blocking; the validation dispose below still waits for the round to finish.
        yield return ("validation_stop", () => Validation?.Stop());
        yield return ("history_retention_dispose", HistoryRetention.Dispose);
        yield return ("history_selection_dispose", HistorySelections.Dispose);

        // A download in flight is cancelled before anything it could claim into is torn down.
        yield return ("shared_calibration_stop", () => LiveProtocol?.StopSharedCalibration());
        yield return ("speech_dispose", () => Speech?.Dispose());

        // Capture goes right after the round, before the update download and check below can use
        // up their waits: once the round has released the capture lease, the automatic-recording
        // timer would otherwise start a live capture while the host is shutting down (re-review R1-1).
        yield return ("validation_dispose", () => Validation?.Dispose());
        yield return ("capture_dispose", () => Capture?.Dispose());

        // A download in flight is cancelled and its partial file removed; it writes nothing to the database,
        // but the process should not exit with a .part half-written.
        yield return ("update_download_dispose", () => UpdateDownloads?.Dispose());

        // The update check writes settings from a background task; it is stopped and waited for
        // before the database can start closing under it.
        yield return ("update_check_dispose", () => Updates?.Dispose());
    }

    /// <summary>
    /// Runs every shutdown step, each in its own try, so one subsystem refusing to stop cannot
    /// take the next one's cleanup with it, and returns the first failure for the caller to
    /// rethrow after the database has been closed.
    /// </summary>
    /// <param name="steps">Record-name prefix and action of each step, in order.</param>
    /// <param name="log">Diagnostic sink; receives a record name and the exception.</param>
    internal static Exception? RunShutdownSteps(
        IEnumerable<(string Record, Action Step)> steps, Action<string, Exception> log)
    {
        Exception? failure = null;
        foreach (var (record, step) in steps)
        {
            try
            {
                step();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                failure ??= ex;
                log(record + (ex is TimeoutException ? "_timed_out" : "_failed"), ex);
            }
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
