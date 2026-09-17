using MentorRecorder.Collector.Protocol.Calibration;
using System.Diagnostics;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;
using CaptureLifecycleListener = MentorRecorder.Collector.Capture.ICaptureLifecycleListener;
using CaptureParserErrorView = MentorRecorder.Collector.Capture.ParserErrorView;
using CaptureParserStats = MentorRecorder.Collector.Capture.IParserStats;
using CaptureProfileProvider = MentorRecorder.Collector.Capture.IGameAwareProfileStatusProvider;
using CaptureProfileSnapshot = MentorRecorder.Collector.Capture.ProfileStatusSnapshot;

namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>A consistent answer for the <c>GetCurrentRun</c> IPC message.</summary>
/// <param name="State">Current state-machine state.</param>
/// <param name="Run">Run in flight, or null when no run is active.</param>
/// <param name="ElapsedMs">Monotonic time since the active match was observed.</param>
public sealed record LiveRunSnapshot(RunState State, MentorRun? Run, long? ElapsedMs);

/// <summary>
/// Production bridge from the Phase-2 capture queue to the Phase-3 profile parser, state
/// machine and persistence path.
///
/// A profile is selected from the exact <see cref="GameProcessDetection"/> used by capture
/// pre-flight. The selection is frozen for the lifetime of a capture session: a client update
/// or a changed profile directory can never silently reinterpret packets halfway through a
/// run. The one exception is a one-way upgrade from "no profile" to a VERIFIED local profile
/// the user just confirmed through calibration (<see cref="ConfirmCalibration"/>): it binds a
/// parser only when the session has been a pure counting sink so far, and only to the file as
/// re-loaded from disk, never to an in-memory draft. When no verified profile matches, this
/// bridge behaves as a bounded counting sink and writes no run data, preserving the
/// diagnostics-only fail-closed path.
/// </summary>
public sealed partial class LiveProtocolPipeline :
    IDecodedMessageSink,
    CaptureParserStats,
    CaptureProfileProvider,
    CaptureLifecycleListener,
    MentorRecorder.Collector.Capture.ICaptureHealthListener,
    ISharedCalibrationHost
{
    private readonly object _gate = new();
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly LiveEventBus _liveEvents;
    private readonly RunRepository _runs;
    private readonly ParserErrorRepository _parserErrors;
    private Func<GameProcessDetection, ProfileSelection> _select;
    private readonly Func<GameProcessDetection, ProtocolProfile?>? _selectCandidate;
    private readonly CalibrationServices _calibrationServices;
    private readonly CalibrationCoordinator _calibration = new();
    private bool _autoCalibrationEnabled;
    private DateTimeOffset? _calibrationBoundAt;
    private TimeSpan _calibrationLastDerive;
    private TimeSpan _calibrationLastSave;
    private string _calibrationSignature = string.Empty;
    private readonly SharedCalibrationSession _shared;
    private string? _boundProfileId;
    private StateMachineMemory? _sessionCarried;

    /// <summary>Raised under the pipeline lock whenever calibration changes state.</summary>
    public event Action<CalibrationState>? CalibrationChanged;

    /// <summary>
    /// How much captured time passes between two re-derivations of the calibration draft.
    /// Measured on the capture source's own clock, the one every message carries, so the
    /// throttle is the same whether the traffic is live or replayed.
    /// </summary>
    public static readonly TimeSpan CalibrationDeriveInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the evidence is written to disk while observing. Evidence is also written
    /// when capture stops, which covers the ordinary "quit the software" case; this covers the
    /// ones that do not ask - a crash, a power cut, a machine that went to sleep - and is slow
    /// enough that a two-hour session costs a couple of dozen small writes.
    /// </summary>
    public static readonly TimeSpan CalibrationSaveInterval = TimeSpan.FromMinutes(2);
    private GameProcessDetection _game = GameProcessDetection.NotRunning;
    private bool _candidateEnabled;
    private ProtocolProfile? _candidateProfile;
    private CandidateObserver? _candidateObserver;

    /// <summary>只向候选账本发布，不能接入语义事件/正式统计路径。</summary>
    public event Action<CandidateObservation>? CandidateObserved;
    private IReadOnlySet<ushort> _researchOpcodes = new HashSet<ushort>();

    public string? CandidateProfileId { get { lock (_gate) return _candidateProfile?.ProfileId; } }
    public bool CandidateValidationEnabled { get { lock (_gate) return _candidateEnabled; } }

    private (Region Region, string? GameBuild) _hypothesisKey;
    private IReadOnlyList<CandidateHypothesisView>? _hypothesisCache;

    /// <summary>
    /// Every CANDIDATE profile the catalogue holds, used only to label the whitelist while
    /// the game is not running (so the client build is unknown) and exactly one candidate
    /// exists. Injectable so tests never depend on the shipped catalogue.
    /// </summary>
    public Func<IReadOnlyList<ProtocolProfile>> ListCandidateProfiles { get; init; } = static () =>
        ProfileCatalog.LoadDefault(allowCandidate: true).Entries
            .Select(entry => entry.Profile)
            .Where(profile => profile is { Status: ProfileCompatibilityStatus.Candidate })
            .Select(profile => profile!)
            .ToArray();

    /// <summary>
    /// Hypotheses of the candidate profile for the current client, for display. Resolved
    /// whether or not candidate validation is on, so the whitelist can be labelled before
    /// the switch is flipped; cached per (region, build) because the selector reads the
    /// catalogue from disk. Never touches the formal selection.
    /// </summary>
    public IReadOnlyList<CandidateHypothesisView> DescribeCandidateHypotheses()
    {
        lock (_gate)
        {
            var key = (_game.Region, _game.GameBuild);
            if (_hypothesisCache is not null && key == _hypothesisKey)
            {
                return _hypothesisCache;
            }

            var profile = _candidateProfile;
            if (profile is null)
            {
                try
                {
                    profile = string.IsNullOrWhiteSpace(_game.GameBuild)
                        ? SingleCandidateOrNull(ListCandidateProfiles())
                        : _selectCandidate?.Invoke(_game);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    profile = null;
                }
            }

            _hypothesisKey = key;
            _hypothesisCache = profile is null
                ? Array.Empty<CandidateHypothesisView>()
                : profile.Hypotheses.Select(h => CandidateHypothesisView.From(profile, h)).ToArray();
            return _hypothesisCache;
        }
    }

    private ProfileSelection _selection;
    private CountingSink _counting = new();
    private ProfileMessageParser? _parser;
    private SemanticEventProcessor? _processor;
    private Stopwatch? _sessionTimer;
    private Stopwatch? _runTimer;
    private TimeSpan? _lastMessageMono;
    private string? _sessionId;
    private bool _active;

    /// <summary>Creates a bridge with an injectable, deterministic profile selector.</summary>
    /// <param name="database">Collector database; the sole writer remains this process.</param>
    /// <param name="clock">Clock for persisted diagnostics and lifecycle events.</param>
    /// <param name="liveEvents">Event bus used to invalidate the Desktop's cached views.</param>
    /// <param name="select">
    /// Selection function. Production supplies the on-disk profile catalogue; tests may
    /// supply an in-memory verified profile without putting synthetic data on the live path.
    /// </param>
    public LiveProtocolPipeline(
        SqliteDatabase database,
        IClock clock,
        LiveEventBus liveEvents,
        Func<GameProcessDetection, ProfileSelection> select,
        Func<GameProcessDetection, ProtocolProfile?>? selectCandidate = null,
        CalibrationServices? calibration = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(liveEvents);
        ArgumentNullException.ThrowIfNull(select);

        _database = database;
        _clock = clock;
        _liveEvents = liveEvents;
        _runs = new RunRepository(database);
        _parserErrors = new ParserErrorRepository(database, clock);
        _select = select;
        _selectCandidate = selectCandidate ?? (game => new ProfileSelector(ProfileCatalog.LoadDefault(allowCandidate: true))
            .SelectCandidate(game.Region, game.GameBuild, enabled: true));
        var settingsRepository = new SettingsRepository(database, clock);
        var settings = CaptureSettingsStore.Read(settingsRepository);
        _candidateEnabled = settings.CandidateValidationEnabled;
        _researchOpcodes = ParseResearchOpcodes(settings.ResearchPayloadOpcodes);
        _calibrationServices = calibration ?? CalibrationServices.Default;
        _autoCalibrationEnabled = settings.AutoCalibrationEnabled;
        _shared = new SharedCalibrationSession(
            _gate, this, _calibrationServices, clock, settingsRepository, settings.SharedCalibrationEnabled);
        _selection = SafeSelect(GameProcessDetection.NotRunning);
        if (_candidateEnabled) SelectProfiles();
    }

    /// <summary>Creates the shipping bridge over the installed profile directory.</summary>
    public static LiveProtocolPipeline CreateDefault(
        SqliteDatabase database, IClock clock, LiveEventBus liveEvents)
    {
        // The formal selector sees the shipped, local (calibrated) and shared directories; only an
        // entry whose binding is usable shadows another for the same build (ProfileCatalog).
        var selector = new ProfileSelector(ProfileCatalog.LoadMerged(
            ProfileCatalog.FindDefaultRoot(), ProfileCatalog.FindLocalRoot(), ProfileCatalog.FindSharedRoot()));
        // The shipping pipeline, and only it, downloads shared calibrations and writes shared profiles.
        var services = CalibrationServices.Default
            .WithSharedCalibrationIn(SharedCalibrationStore.RootPath)
            .WithSharedProfilesIn(ProfileCatalog.SharedRootPath);
        return new LiveProtocolPipeline(
            database,
            clock,
            liveEvents,
            game => selector.Select(game.Region, game.GameBuild),
            game => new ProfileSelector(ProfileCatalog.LoadDefault(allowCandidate: true))
                .SelectCandidate(game.Region, game.GameBuild, enabled: true),
            services);
    }

    /// <summary>普通捕获中的档案被冻结，必须先停止该会话才能切入候选模式。</summary>
    public void ValidateCandidateModeChange(bool enabled)
    {
        lock (_gate)
            if (enabled && !_candidateEnabled && _active)
                throw new CollectorException(ErrorCodes.CaptureAlreadyRunning, "请先停止捕获，再开启候选档案验证。");
    }

    /// <summary>关闭立即丢弃观测器。不会为仍在运行的候选会话创建正式解析器。</summary>
    public void ApplyCandidateSettings(bool? enabled, Action? persist = null, IReadOnlyList<string>? researchPayloadOpcodes = null)
    {
        lock (_gate)
        {
            var effectiveEnabled = enabled ?? _candidateEnabled;
            ValidateCandidateModeChange(effectiveEnabled);
            persist?.Invoke();
            _candidateEnabled = effectiveEnabled;
            if (researchPayloadOpcodes is not null) _researchOpcodes = ParseResearchOpcodes(researchPayloadOpcodes);
            _candidateObserver?.SetResearchOpcodes(_researchOpcodes);
            if (!effectiveEnabled)
            {
                _candidateObserver?.Flush();
                _candidateObserver = null;
                _candidateProfile = null;
            }
            if (!_active) SelectProfiles();
        }
    }

    private static ProtocolProfile? SingleCandidateOrNull(IReadOnlyList<ProtocolProfile> candidates) =>
        candidates.Count == 1 ? candidates[0] : null;

    private static IReadOnlySet<ushort> ParseResearchOpcodes(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>()).Where(value => ResearchPayloadPolicy.TryParse(value, out _))
            .Select(value => { ResearchPayloadPolicy.TryParse(value, out var opcode); return opcode; }).ToHashSet();

    private void SelectProfiles()
    {
        try
        {
            _candidateProfile = _candidateEnabled ? _selectCandidate?.Invoke(_game) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _candidateProfile = null;
        }
        // Candidate validation observes alongside the formal profile, never instead of it:
        // a VERIFIED profile keeps writing formal records while the candidate ledger samples.
        _selection = SafeSelect(_game);
        ArmCalibration();
    }

    /// <inheritdoc />
    public CaptureProfileSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return ToCaptureStatus(_selection);
            }
        }
    }

    /// <inheritdoc />
    public CaptureProfileSnapshot Refresh(GameProcessDetection game)
    {
        ArgumentNullException.ThrowIfNull(game);
        lock (_gate)
        {
            if (!_active)
            {
                _game = game;
                SelectProfiles();
            }

            return ToCaptureStatus(_selection);
        }
    }

    /// <summary>Current state-machine state, for capture diagnostics.</summary>
    public RunState RunState
    {
        get
        {
            lock (_gate)
            {
                return _processor?.Machine.State ?? Domain.RunState.Idle;
            }
        }
    }

    /// <summary>Returns the current run without exposing mutable state-machine objects.</summary>
    public LiveRunSnapshot GetCurrentRun()
    {
        lock (_gate)
        {
            var machine = _processor?.Machine;

            // A finished run reads IDLE immediately instead of waiting for the next declared
            // opcode to normalise the terminal state (review finding L-9). The collapse goes
            // through the same publication path as every other state change: done silently on
            // a read, a client that polls once between the terminal state and the next
            // declared message never saw run_state_changed(IDLE) at all, because by the time
            // the message arrived the before-state was already IDLE (review finding R-9).
            // Idempotent: the second call finds IDLE and publishes nothing.
            if (machine is not null && IsTerminal(machine.State))
            {
                try
                {
                    ApplyAndPublish(machine.NormalizeIfTerminal);
                }
                catch (InvalidOperationException)
                {
                    // A latched storage failure must not turn reading the current run into an
                    // error; the latch is reported through capture status instead.
                    machine.NormalizeIfTerminal();
                }
            }

            var state = machine?.State ?? Domain.RunState.Idle;
            var runId = machine?.CurrentRunId;
            var run = runId is null ? null : _runs.Get(runId);
            var elapsed = runId is null || _runTimer is null
                ? (long?)null
                : Math.Max(0, _runTimer.ElapsedMilliseconds);
            return new LiveRunSnapshot(state, run, elapsed);
        }
    }

    /// <inheritdoc />
    public long ParseOkCount => ParserStats().ParseOk;

    /// <inheritdoc />
    public long ParseFailCount => ParserStats().ParseFailed;

    /// <inheritdoc />
    public long DuplicateCount => ParserStats().Duplicates;

    /// <inheritdoc />
    public long IgnoredCount => ParserStats().Ignored;

    /// <inheritdoc />
    public DateTimeOffset? LastValidEventAtUtc => ParserStats().LastValidEventAtUtc;

    /// <inheritdoc />
    public string? LastValidEventKind => ParserStats().LastValidEventKind;

    /// <inheritdoc />
    public IReadOnlyList<CaptureParserErrorView> RecentErrors
    {
        get
        {
            var recent = ParserStats().RecentErrors;
            var views = new List<CaptureParserErrorView>(recent.Count);
            foreach (var error in recent)
            {
                views.Add(new CaptureParserErrorView(
                    error.AtUtc, error.Kind, error.Opcode, DirectionToken(error.Direction), error.Message));
            }

            return views;
        }
    }

    /// <inheritdoc />
    public void OnCaptureStarted(string captureSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(captureSessionId);

        lock (_gate)
        {
            _active = true;
            _sessionId = captureSessionId;
            _sessionTimer = Stopwatch.StartNew();
            _runTimer = null;
            _lastMessageMono = null;
            _counting = new CountingSink();

            // What the previous machine knew about the player -- the job, and only the job --
            // survives a retried capture session, so the first run after an automatic restart
            // is not recorded job-less (review finding L-8).
            var carried = _processor?.Machine.Memory;
            _parser = null;
            _processor = null;
            _boundProfileId = null;
            _sessionCarried = carried;
            _candidateObserver = _candidateEnabled && _candidateProfile is { } candidate
                ? new CandidateObserver(candidate, captureSessionId, observation => CandidateObserved?.Invoke(observation), _researchOpcodes)
                : null;

            _calibrationBoundAt = null;
            // Observation starts whenever calibration is armed, which now includes a session
            // running under a provisional profile: that profile records mentor roulettes today
            // and the search for the server's own match message goes on underneath it.
            if (_calibration.Armed)
            {
                _calibration.Begin(captureSessionId);
                _calibrationLastDerive = TimeSpan.Zero;
                _calibrationLastSave = TimeSpan.Zero;
                NotifyCalibrationChanged();
            }

            // Shared candidates stage this session whenever no parser is about to be bound.
            _shared.OnCaptureStarted(captureSessionId, staging: !_selection.IsUsable || _selection.Profile is null);
            if (!_selection.IsUsable || _selection.Profile is not { } profile)
            {
                return;
            }

            BindParser(profile, captureSessionId, carried);
        }
    }

    /// <summary>
    /// What the calibration observer last read from the job message the profile just declared,
    /// as the memory a fresh state machine is seeded with; null when the profile has no job
    /// message or the observer never read a value from it.
    /// </summary>
    /// <param name="profile">The profile about to be bound: a confirmed local one, or a shared one whose staging held no job.</param>
    private StateMachineMemory? JobRemembered(ProtocolProfile profile)
    {
        if (profile.Message("PLAYER_JOB") is not { } job || _calibration.Evidence() is not { } evidence ||
            !evidence.LatestJobValues.TryGetValue(job.Opcode, out var value) || value < 0 || value > int.MaxValue)
        {
            return null;
        }

        return new StateMachineMemory((int)value);
    }

    private void BindParser(ProtocolProfile profile, string captureSessionId, StateMachineMemory? carried)
    {
        var machine = new MentorRunStateMachine(
            _selection.Binding,
            new StateMachineOptions
            {
                MatchWindow = profile.MatchWindow,
                // Only a profile that infers the match from the queue asks this question, and
                // it must be answerable before such a profile can call a zone change an entry.
                IsKnownDuty = territory =>
                    DutyCatalog.Default.FindByTerritory(territory, _selection.Region) is not null,
            });
        machine.Seed(carried);
        _processor = new SemanticEventProcessor(
            _database,
            machine,
            new SemanticEventProcessorOptions(
                captureSessionId,
                _selection.Region,
                profile.ProfileId,
                captureSessionId,
                _selection.GameBuild),
            _clock);
        _parser = new ProfileMessageParser(profile, _processor);
        _boundProfileId = profile.ProfileId;
    }

    /// <inheritdoc />
    public void Accept(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (!_active || !string.Equals(message.CaptureSessionId, _sessionId, StringComparison.Ordinal))
            {
                return;
            }

            // Durations are measured against readings taken by the capture source's own
            // stopwatch, so the last one observed is what a lifecycle event must be stamped
            // with (see LifecycleMono).
            _lastMessageMono = message.Mono;
            _candidateObserver?.Accept(message);
            _calibration.Accept(message);
            if (_parser is null || _processor is null)
            {
                // Staged before verification runs, so the message that completes a pass is in the stage.
                _shared.Stage(message);
            }

            MaybeRefreshCalibration(message.Mono);
            if (_parser is null || _processor is null)
            {
                _counting.Accept(message);
                return;
            }

            // A durable write failure is latched until teardown. Do not let queued messages
            // advance the state machine after the first missing observation while the
            // controller's asynchronous fault path is stopping the source.
            ThrowIfStorageFailed();
            var beforeFailed = _parser.GetParserStats().ParseFailed;
            ApplyAndPublish(() => _parser.Accept(message));
            var afterStats = _parser.GetParserStats();
            if (afterStats.ParseFailed > beforeFailed && afterStats.RecentErrors.Count > 0)
            {
                PersistParserError(afterStats.RecentErrors[^1]);
            }
        }
    }

    /// <inheritdoc />
    public void OnEventsDropped(string captureSessionId, long droppedCount)
    {
        lock (_gate)
        {
            if (!SessionMatches(captureSessionId) || droppedCount <= 0)
            {
                return;
            }

            if (_processor is null)
            {
                // Nothing bound yet: the hole goes into every candidate's staging, in sequence.
                _shared.EventsDropped(droppedCount, _clock.UtcNow, LifecycleMono());
                return;
            }

            ApplyAndPublish(() => _processor.OnEventsDropped(
                droppedCount, _clock.UtcNow, LifecycleMono()));
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The live producer of DISCONNECTED: no other path reaches that terminal state outside
    /// tests that call the processor directly (review finding H-6).
    /// </remarks>
    public void OnConnectionLost(string captureSessionId)
    {
        lock (_gate)
        {
            if (!SessionMatches(captureSessionId))
            {
                return;
            }

            if (_processor is null)
            {
                _shared.ConnectionLost(_clock.UtcNow, LifecycleMono());
                return;
            }

            ApplyAndPublish(() => _processor.OnConnectionLost(_clock.UtcNow, LifecycleMono()));
        }
    }

    /// <inheritdoc />
    public void OnCaptureStopped(string captureSessionId, CaptureEndReason reason)
    {
        lock (_gate)
        {
            try
            {
                if (_processor is not null && SessionMatches(captureSessionId))
                {
                    ApplyAndPublish(() => _processor.OnCaptureStopped(
                        reason == CaptureEndReason.ProcessExit,
                        _clock.UtcNow,
                        LifecycleMono()));
                }
            }
            finally
            {
                _active = false;
                _sessionId = null;
                _candidateObserver?.Flush();
                _candidateObserver = null;
                _calibration.Stop();
                SaveCalibrationEvidence();
                // Staging belongs to the session that just ended; the flushed bursts may complete a verdict.
                _shared.OnCaptureStopped();
                _shared.Evaluate();
                _sessionTimer?.Stop();
                _runTimer?.Stop();
            }
        }
    }

    /// <summary>
    /// Monotonic reading to stamp a lifecycle event with.
    ///
    /// A run's duration is the distance between two monotonic readings, and the entry reading
    /// came off the capture source's stopwatch, which starts when the source is created, not
    /// when this pipeline's session timer starts. Stamping a lifecycle event with the session
    /// timer would subtract two different clocks and make an interrupted duty come out short or
    /// clamped to zero. The last reading observed on the message clock is used instead; the
    /// session timer is only a fallback for a session that never saw a message, where no run
    /// can be in flight to have a duration at all.
    /// </summary>
    private TimeSpan LifecycleMono() => _lastMessageMono ?? _sessionTimer?.Elapsed ?? TimeSpan.Zero;

    private void ApplyAndPublish(Action action)
    {
        var machine = _processor!.Machine;
        var beforeState = machine.State;
        var beforeId = machine.CurrentRunId;
        var beforeRun = beforeId is null ? null : _runs.Get(beforeId);
        var beforeFinished = beforeRun?.EndedAtUtc is not null;

        action();
        ThrowIfStorageFailed();

        var afterState = machine.State;
        var afterId = machine.CurrentRunId;
        UpdateRunTimer(beforeId, afterId);

        var changed = false;
        if (beforeState != afterState)
        {
            // The run travels with the state. Read after the action, so the entry event
            // carries the row as it now stands rather than the snapshot taken at the pop --
            // which on the CN client has no duty name in it at all (review finding H-3).
            // Use the bound machine, not the latest catalogue or calibration state: a newly
            // confirmed profile can be selected before the current parser is replaced.
            _liveEvents.PublishState(afterState, afterId is null ? null : _runs.Get(afterId),
                machine.MatchFromQueue);
        }

        if (beforeId is not null)
        {
            var updated = _runs.Get(beforeId);

            // Automatic writes retain their audit revision, and separate observations may
            // have the same millisecond timestamp. Compare the immutable row values so
            // entry, job and territory changes still invalidate the client's cached views.
            if (updated is not null && updated != beforeRun)
            {
                _liveEvents.PublishRun(LiveEventKind.RunUpdated, updated);
                changed = true;
            }

            // The terminal state has to be published from the row, not from the machine.
            // MentorRunStateMachine.RestartOn writes the terminal state and then immediately
            // clears it to start the next run, so a before/after sample of machine.State never
            // sees it when one pop follows another (spec gaps P1-18, P1-19).
            if (updated is not null && !beforeFinished && updated.EndedAtUtc is not null)
            {
                _liveEvents.PublishRunFinished(updated, TerminalState(updated.Result));
                if (_shared.OnRunFinished(updated))
                {
                    FinishSharedRetention(updated.ProtocolProfileId!, machine.MatchFromQueue);
                }
            }
        }

        if (afterId is not null && !string.Equals(afterId, beforeId, StringComparison.Ordinal))
        {
            var created = _runs.Get(afterId);
            if (created is not null)
            {
                _liveEvents.PublishRun(LiveEventKind.RunCreated, created);
                changed = true;
            }
        }

        if (changed)
        {
            _liveEvents.PublishStatsInvalidated("自动记录已更新，统计需要重新查询。");
        }
    }

    /// <summary>True for the states a run rests in once it is over.</summary>
    /// <param name="state">State to classify.</param>
    private static bool IsTerminal(RunState state) =>
        state is not (Domain.RunState.Idle or Domain.RunState.MentorMatched or Domain.RunState.EnteredDuty);

    /// <summary>The terminal state matching a final result (docs/state-machine.md section 3).</summary>
    /// <param name="result">Final result.</param>
    private static RunState TerminalState(RunResult result) => result switch
    {
        RunResult.Completed => RunState.Completed,
        RunResult.LeftOrAbandoned => RunState.LeftOrAbandoned,
        RunResult.CancelledBeforeEntry => RunState.CancelledBeforeEntry,
        RunResult.Disconnected => RunState.Disconnected,
        RunResult.Interrupted => RunState.Interrupted,
        _ => RunState.UnknownFinalState,
    };

    private void ThrowIfStorageFailed()
    {
        if (!string.IsNullOrWhiteSpace(_processor?.LastStorageError))
        {
            throw new InvalidOperationException(
                "live protocol storage failed: " + _processor.LastStorageError);
        }
    }

    private void UpdateRunTimer(string? beforeId, string? afterId)
    {
        if (afterId is null)
        {
            _runTimer?.Stop();
            _runTimer = null;
            return;
        }

        if (!string.Equals(beforeId, afterId, StringComparison.Ordinal))
        {
            _runTimer = Stopwatch.StartNew();
        }
    }

    private void PersistParserError(ParserError error)
    {
        try
        {
            _database.RunInTransaction(transaction =>
                _parserErrors.Record(error.Kind, error.Message, _sessionId, transaction));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Parser diagnostics must never take down the parser loop. The primary capture
            // counters still expose the refusal even when the optional diagnostic row fails.
            _ = ex;
        }
    }

    /// <summary>Wire token of a packet direction, as the diagnostics page spells it.</summary>
    /// <param name="direction">Direction to render.</param>
    private static string DirectionToken(PacketDirection direction) => direction switch
    {
        PacketDirection.ServerToClient => "S2C",
        PacketDirection.ClientToServer => "C2S",
        _ => "NONE",
    };

    private ParserStatsSnapshot ParserStats()
    {
        lock (_gate)
        {
            return _parser?.GetParserStats()
                ?? new ParserStatsSnapshot(0, 0, 0, 0, null, Array.Empty<ParserError>());
        }
    }

    private bool SessionMatches(string captureSessionId) =>
        string.Equals(captureSessionId, _sessionId, StringComparison.Ordinal);

    private ProfileSelection SafeSelect(GameProcessDetection game)
    {
        try
        {
            return _select(game);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new ProfileSelection(
                ProfileCompatibilityStatus.Unsupported,
                ProfileBinding.FailClosed,
                null,
                game.Region,
                game.GameBuild,
                "profile selection failed: " + ex.GetType().Name);
        }
    }

    private CaptureProfileSnapshot ToCaptureStatus(ProfileSelection selection)
    {
        var status = selection.Status switch
        {
            ProfileCompatibilityStatus.Verified when selection.IsUsable => ProfileStatus.Verified,
            ProfileCompatibilityStatus.Candidate => ProfileStatus.Unverified,
            _ when string.IsNullOrWhiteSpace(selection.GameBuild) && selection.Profile is null => ProfileStatus.None,
            _ => ProfileStatus.UnsupportedBuild,
        };

        var profile = selection.Profile;
        return new CaptureProfileSnapshot(
            status,
            profile?.ProfileId,
            selection.Region,
            selection.GameBuild,
            null,
            status == ProfileStatus.Verified ? profile?.ProvenanceSummary : null,
            status == ProfileStatus.Verified
                ? null
                : "协议档案不可用（fail-closed）：" + selection.Reason,
            selection.Origin,
            _calibration.Armed);
    }

    // ------------------------------------------------------------------ calibration

    /// <summary>Calibration status for the capture page and the diagnostics report.</summary>
    public CalibrationStatusSnapshot CalibrationStatus()
    {
        lock (_gate)
        {
            return _calibration.Snapshot() with { Shared = _shared.Snapshot() };
        }
    }

    /// <summary>True when the running build has no profile but a template to calibrate from.</summary>
    public bool CalibrationArmed
    {
        get
        {
            lock (_gate)
            {
                return _calibration.Armed;
            }
        }
    }

    /// <summary>Turns automatic calibration on or off; off forgets any evidence at once.</summary>
    /// <param name="enabled">Setting value.</param>
    public void ApplyCalibrationSetting(bool enabled)
    {
        lock (_gate)
        {
            if (_autoCalibrationEnabled == enabled)
            {
                return;
            }

            _autoCalibrationEnabled = enabled;
            if (!enabled)
            {
                _calibration.Disarm();
                _shared.Sync();
                NotifyCalibrationChanged();
            }
            else if (!_active)
            {
                SelectProfiles();
            }
        }
    }

    /// <summary>
    /// The user confirmed the calibration timeline. Writes the local profile, re-selects from
    /// disk, and binds a parser inside the running session when it has been a pure counting
    /// sink and its session row can be updated. Any "wrong" verdict voids the draft instead.
    /// </summary>
    /// <param name="verdicts">CORRECT, WRONG or RELABEL per event id.</param>
    public CalibrationConfirmation ConfirmCalibration(IReadOnlyDictionary<string, CalibrationVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        CalibrationDraft draft;
        CalibrationTemplate template;
        string build;
        int generation;
        lock (_gate)
        {
            var judged = _calibration.Judge(verdicts, out var renamings);
            // A name the player corrected is kept whatever becomes of the draft: they told the
            // software something true about their own client, and it is true again tomorrow.
            RecordRenamings(renamings);
            if (judged is null)
            {
                NotifyCalibrationChanged();
                throw new CollectorException(
                    ErrorCodes.CalibrationRejected,
                    "有事件被标为不对，这次校准作废；再打一把随机任务后会重新核对。");
            }

            draft = judged;
            template = _calibration.Template!;
            build = _game.GameBuild ?? throw new CollectorException(
                ErrorCodes.CalibrationNotReady, "客户端版本未知，无法写出档案。");
            generation = _calibration.Generation;
        }

        // Disk work stays outside the lock: writing the file and re-reading both directories
        // is slow, and nothing here depends on state that a message could change meanwhile.
        LocalProfileWriteResult written;
        try
        {
            written = _calibrationServices.Write(draft, template, build, _clock.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new CollectorException(ErrorCodes.ExportFailed, "无法写出本机校准档案：" + ex.Message, inner: ex);
        }

        var select = _calibrationServices.ReloadSelect();
        lock (_gate)
        {
            // Anything that changed what is being calibrated while the lock was released --
            // a discard, the setting turned off, the game coming back on another build --
            // wins over this confirmation. The file already on disk is harmless: the next
            // selection for that build picks it up like any other profile.
            if (_calibration.Generation != generation || _calibration.State != CalibrationState.Ready)
            {
                throw new CollectorException(
                    ErrorCodes.CalibrationNotReady,
                    "写出档案期间校准状态发生了变化，这次确认作废；请重新核对。",
                    new Dictionary<string, object?> { ["profile_id"] = written.ProfileId });
            }

            _select = select;
            var selection = SafeSelect(_game);
            if (!selection.IsUsable || selection.Profile is not { } profile ||
                profile.Status != ProfileCompatibilityStatus.Verified ||
                !string.Equals(profile.ProfileId, written.ProfileId, StringComparison.Ordinal))
            {
                throw new CollectorException(
                    ErrorCodes.Internal,
                    "本机校准档案已写出，但档案目录没有选中它：" + selection.Reason,
                    new Dictionary<string, object?> { ["profile_id"] = written.ProfileId, ["reason"] = selection.Reason });
            }

            _selection = selection;
            var bound = false;
            if (_active && _parser is null && _processor is null && _sessionId is { } sessionId)
            {
                var updated = new CaptureSessionRepository(_database)
                    .UpdateProfile(sessionId, profile.ProfileId, ProfileStatus.Verified);
                if (updated)
                {
                    // The job was announced at login and on every zone change while the
                    // observer watched, so the machine need not wait for the next one; a pop
                    // straight after binding would otherwise make a job-less record.
                    BindParser(profile, sessionId, JobRemembered(profile));
                    _calibrationBoundAt = _clock.UtcNow;
                    bound = true;
                }
            }

            var provisional = draft.MatchSource == CalibrationMatchSource.QueueRequest;
            if (provisional)
            {
                // Still looking for the server's own announcement, so the evidence still matters.
                SaveCalibrationEvidence();
            }
            else
            {
                ForgetCalibrationEvidence();
            }

            _calibration.MarkDone(written.ProfileId, bound ? _calibrationBoundAt : null, provisional);
            _shared.Sync();
            NotifyCalibrationChanged();
            return new CalibrationConfirmation(written.ProfileId, written.Path, bound);
        }
    }

    /// <summary>Throws away the evidence of the running session and observes again.</summary>
    public CalibrationStatusSnapshot DiscardCalibration()
    {
        lock (_gate)
        {
            // 重新观察 has to survive a restart, or the next launch would hand the player back
            // exactly what they asked the software to forget.
            ForgetCalibrationEvidence();
            _calibration.Discard(_active ? _sessionId : null);
            // 重新观察 also forgets which shared calibrations this build's traffic contradicted.
            _shared.OnDiscard();
            NotifyCalibrationChanged();
            return _calibration.Snapshot() with { Shared = _shared.Snapshot() };
        }
    }

    /// <summary>
    /// Arms or disarms calibration for the selection in force, then brings shared calibration in line with that
    /// same selection. The only way calibration is armed: no path can arm beside a shared profile the shared
    /// session has not adopted and registered for verification (B2a review finding 2).
    /// </summary>
    private void ArmCalibration()
    {
        ArmCalibrationForSelection();
        _shared.Sync();
    }

    private void ArmCalibrationForSelection()
    {
        if (!_autoCalibrationEnabled)
        {
            _calibration.Disarm();
            return;
        }

        // The game is not running: keep whatever was armed, so a client that comes back on
        // the same build does not lose its template.
        if (string.IsNullOrWhiteSpace(_game.GameBuild))
        {
            return;
        }

        var (eligible, upgrading, retaining) = CalibrationRoles();
        if (!eligible)
        {
            _calibration.Disarm();
            return;
        }

        if (_calibration.Armed && string.Equals(_calibration.GameBuild, _game.GameBuild, StringComparison.Ordinal))
        {
            UseCalibrationRole(upgrading, retaining);
            return;
        }

        CalibrationTemplate? template;
        try
        {
            template = _calibrationServices.SelectTemplate(_game.Region);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            template = null;
        }

        if (template is null)
        {
            _calibration.Disarm();
            return;
        }

        _calibration.Arm(template, _game.Region, _game.GameBuild);
        UseCalibrationRole(upgrading, retaining);
        CarryCalibrationMemory(template);
    }

    /// <summary>Whether calibration runs beside the selection in force, and in which role. Changes nothing.</summary>
    private (bool Eligible, bool Upgrading, bool Retaining) CalibrationRoles()
    {
        var calibratable = _game.Region is Region.Cn or Region.Global;
        // A provisional profile is in force and working, and is still the wrong answer: it
        // infers the match instead of reading it. Calibration stays armed underneath it so the
        // announcement can still be found and the profile replaced. Read off the profile itself
        // - one this machine calibrated or verified - not off an in-memory flag, which a restart
        // loses (review finding 6).
        var upgrading = calibratable && _selection.IsUsable && _selection.Profile is { MatchFromQueue: true } &&
            _selection.Origin is ProfileOrigin.Local or ProfileOrigin.Shared;
        // A shared profile is watched until it records one complete entry and exit, so a contradiction
        // can still withdraw it.
        var retaining = calibratable && _shared.Retains(_selection);
        // A local profile this machine wrote that no longer binds - since 0.7.3 one that infers
        // the match and cannot recognise a duty is refused - still matched the build, so the
        // selector's reason is not "no profile matches". Calibration must not stay idle beside
        // it, or the software does nothing at all: no recording and no calibration either.
        var localProfileRefused = calibratable && !_selection.IsUsable &&
            _selection.Profile is { Status: ProfileCompatibilityStatus.Verified } refused &&
            refused.MatchFromQueue && refused.Message("ZONE_TERRITORY") is null;
        var unknownBuild = calibratable && !_selection.IsUsable &&
            _selection.Status == ProfileCompatibilityStatus.Unsupported &&
            string.Equals(_selection.Reason, ProfileSelector.NoProfileMatchesReason, StringComparison.Ordinal);
        return (upgrading || retaining || localProfileRefused || unknownBuild, upgrading, retaining);
    }

    /// <summary>What earlier runs left for a newly armed build: corrected roulette names and carried evidence.</summary>
    private void CarryCalibrationMemory(CalibrationTemplate template)
    {
        // Names the player corrected on an earlier run, so the timeline is written with them
        // from the first draft rather than only after the next correction.
        try
        {
            _calibration.UseRoulettes(_calibrationServices.LoadRoulettes());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }

        // What an earlier run of the Collector learned about this same build under this same
        // template. Without it every new version the player installs costs another evening of
        // play to re-collect what the software already knew.
        try
        {
            _calibration.Carry(
                _calibrationServices.LoadEvidence(
                    _game.Region, _game.GameBuild!, template.Source.ProfileSha256),
                _calibrationServices.ExplainEvidence(
                    _game.Region, _game.GameBuild!, template.Source.ProfileSha256));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Carried evidence is an optimisation, never a requirement.
        }
    }

    /// <summary>
    /// Remembers the roulette names the player corrected and re-renders the timeline under
    /// them. A failure here costs a label and nothing else, so it is swallowed like the rest
    /// of the reference-data writes.
    /// </summary>
    /// <param name="renamings">What the player named, possibly empty.</param>
    private void RecordRenamings(IReadOnlyList<RouletteRenaming> renamings)
    {
        if (renamings.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var renaming in renamings)
            {
                _calibrationServices.RecordRouletteName(
                    _selection.Region, renaming.RouletteId, renaming.Name, _clock.UtcNow);
            }

            _calibration.UseRoulettes(_calibrationServices.LoadRoulettes());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    /// <summary>Removes the evidence file. Failure is never allowed to matter.</summary>
    private void ForgetCalibrationEvidence()
    {
        if (_calibration.GameBuild is not { } build)
        {
            return;
        }

        try
        {
            _calibrationServices.DeleteEvidence(_game.Region, build);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    /// <summary>Writes the evidence for the next run. Failure is never allowed to matter.</summary>
    private void SaveCalibrationEvidence()
    {
        if (_calibration.Template is not { } template || _calibration.GameBuild is not { } build ||
            _calibration.Evidence() is not { } evidence)
        {
            return;
        }

        try
        {
            _calibrationServices.SaveEvidence(_game.Region, build, template.Source.ProfileSha256, evidence);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
        }
    }

    private void MaybeRefreshCalibration(TimeSpan mono)
    {
        if (!_calibration.Active)
        {
            return;
        }

        if (mono - _calibrationLastDerive < CalibrationDeriveInterval)
        {
            return;
        }

        _calibrationLastDerive = mono;
        if (mono - _calibrationLastSave >= CalibrationSaveInterval)
        {
            _calibrationLastSave = mono;
            SaveCalibrationEvidence();
        }

        // On the same captured-time throttle as the draft: verification reads a full snapshot.
        _shared.Evaluate();

        // Progress ticks, blockers and timeline entries all change without the state
        // changing; the card must still learn about them, so the comparison is against what
        // was last announced, not against a value computed from the same freshly derived
        // draft a line earlier.
        var signature = CalibrationSignature();
        if (!string.Equals(signature, _calibrationSignature, StringComparison.Ordinal))
        {
            NotifyCalibrationChanged(signature);
        }
    }

    /// <summary>Announces calibration and remembers what was announced.</summary>
    /// <param name="signature">Signature just computed, when the caller already has one.</param>
    private void NotifyCalibrationChanged(string? signature = null)
    {
        _calibrationSignature = signature ?? CalibrationSignature();
        CalibrationChanged?.Invoke(_calibration.State);
    }

    private string CalibrationSignature()
    {
        var snapshot = _calibration.Snapshot();
        var progress = snapshot.Progress;
        return string.Join("|",
            snapshot.State,
            progress?.FinderRequestSeen, progress?.PopSeen, progress?.ZoneClusters,
            progress?.DutyEntrySeen, progress?.DutyExitSeen, progress?.PopShapeSeen,
            progress?.DutyZoneSeen, progress?.JobSeen,
            snapshot.Events.Count,
            snapshot.Events.Count > 0 ? snapshot.Events[^1].EventId : string.Empty,
            string.Join(";", snapshot.Blockers),
            _shared.Signature());
    }
}
