using MentorRecorder.Collector.Protocol.Calibration;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Capture;

/// <summary>Told whenever the capture status changes, so the change can be pushed to clients.</summary>
public interface ICaptureStatusListener
{
    /// <summary>The capture status changed.</summary>
    /// <param name="snapshot">Reading taken at the moment of the change.</param>
    /// <param name="message">Non-sensitive explanation for the user.</param>
    void OnCaptureStatusChanged(CaptureDiagnosticsSnapshot snapshot, string message);
}

/// <summary>Everything <see cref="CaptureController"/> depends on, so all of it can be substituted.</summary>
public sealed record CaptureServices
{
    public CaptureOwnership Ownership { get; init; } = new();
    /// <summary>Npcap detection.</summary>
    public NpcapDetector Npcap { get; init; } = new();

    /// <summary>Game process discovery.</summary>
    public GameProcessLocator Game { get; init; } = new();

    /// <summary>Adapter enumeration.</summary>
    public AdapterEnumerator Adapters { get; init; } = new();

    /// <summary>Creates the capture source for one session.</summary>
    public Func<ICaptureSource>? SourceFactory { get; init; }

    /// <summary>Consumer of decoded messages; production supplies the live protocol pipeline.</summary>
    public IDecodedMessageSink? Sink { get; init; }

    /// <summary>Parser counters shown in diagnostics.</summary>
    public IParserStats? ParserStats { get; init; }

    /// <summary>Protocol profile status.</summary>
    public IProfileStatusProvider Profile { get; init; } = NoProfileStatusProvider.Instance;

    /// <summary>Calibration status for the running build; idle when the pipeline is absent.</summary>
    public Func<CalibrationStatusSnapshot> CalibrationStatus { get; init; } = () => CalibrationStatusSnapshot.Idle;

    /// <summary>Told when a capture session starts and stops.</summary>
    public ICaptureLifecycleListener Lifecycle { get; init; } = NullCaptureLifecycleListener.Instance;

    /// <summary>Told how trustworthy each session's capture is; see <see cref="ICaptureHealthListener"/>.</summary>
    public ICaptureHealthListener Health { get; init; } = NullCaptureHealthListener.Instance;

    /// <summary>Told when the capture status changes.</summary>
    public ICaptureStatusListener? StatusListener { get; init; }

    /// <summary>State of the run in flight.</summary>
    public Func<RunState> RunState { get; init; } = static () => Domain.RunState.Idle;

    /// <summary>
    /// Candidate observer status, read before the controller lock. The callback must not
    /// call back into the capture controller while holding a pipeline or database lock.
    /// </summary>
    public Func<(bool Enabled, string? ProfileId, int Count)> CandidateStatus { get; init; } =
        static () => (false, null, 0);

    /// <summary>
    /// Hypotheses of the candidate profile matching the current client, for the Desktop's
    /// whitelist labels. Same threading rule as <see cref="CandidateStatus"/>.
    /// </summary>
    public Func<IReadOnlyList<CandidateHypothesisView>> CandidateHypotheses { get; init; } =
        static () => Array.Empty<CandidateHypothesisView>();

    /// <summary>
    /// How long the follow-the-game poll waits before retrying an auto-start that was
    /// refused (no Npcap, no profile, no adapter). Without this the poll would re-refuse
    /// once a second for as long as the game runs, churning the last-error fields.
    /// </summary>
    public TimeSpan FollowRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// First wait before the follow poll retries a capture that faulted while the game is
    /// still running. It doubles on every further fault, up to
    /// <see cref="CaptureController.MaxFaultBackoffMs"/>.
    /// </summary>
    public TimeSpan FaultRetryInterval { get; init; } =
        TimeSpan.FromMilliseconds(CaptureController.InitialFaultBackoffMs);

    /// <summary>Clock used for timestamps.</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>
    /// Monotonic reading a capture's uptime is measured with. Production leaves this null and
    /// gets a stopwatch; a test that has to reach the one-minute liveness grace substitutes a
    /// value it controls, because waiting a real minute in a unit test is not a test.
    /// </summary>
    public Func<TimeSpan>? Uptime { get; init; }

    /// <summary>
    /// Monotonic process time for retry deadlines and detection caches. Production uses a
    /// stopwatch; tests can advance this clock without relying on thread scheduling.
    /// </summary>
    public Func<TimeSpan>? ProcessUptime { get; init; }

    /// <summary>Local diagnostic log.</summary>
    public RotatingFileLogger Logger { get; init; } = RotatingFileLogger.Disabled;

    /// <summary>Settings store, for the remembered adapter and the queue capacity.</summary>
    public SettingsRepository? Settings { get; init; }

    /// <summary>Capture session rows.</summary>
    public CaptureSessionRepository? Sessions { get; init; }

    /// <summary>Database, needed to write a capture session row inside a transaction.</summary>
    public SqliteDatabase? Database { get; init; }

    /// <summary>Collector version stamped on capture session rows.</summary>
    public string CollectorVersion { get; init; } = "0.0.0";

    /// <summary>
    /// Manifest of the temporary game-executable copies this Collector owns. Null keeps the
    /// ownership record in memory only, which is what a test without a database wants.
    /// </summary>
    public string? OodleTempManifestPath { get; init; }

    /// <summary>
    /// How often the follow-the-game poll runs.
    ///
    /// One second, not five: the poll's job is to be capturing <em>before</em> the client
    /// opens its connection, because Oodle's TCP decompressor is stateful and a capture that
    /// starts after the connection can never decode anything on it. A process enumeration by
    /// name costs far less than the session it saves.
    /// </summary>
    public TimeSpan FollowInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a detection result is reused before it is taken again.</summary>
    public TimeSpan DetectionTtl { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>False disables the background poll; tests drive <see cref="CaptureController.Poll"/> directly.</summary>
    public bool EnableFollowTimer { get; init; } = true;
}

/// <summary>
/// Owns the capture lifecycle: the four pre-flight checks, the source, the bounded queue, the
/// counters, and the decision to stop.
///
/// The pre-flight order is the one in docs/capture-diagnostics.md section 1, and every failure
/// is an explicit contract error rather than a capture that starts and quietly observes
/// nothing. In particular there is no fallback anywhere in this class: no raw-socket monitor
/// when Npcap is missing, no guessed adapter when the game's connections cannot be located,
/// and no parsing when the protocol profile is not verified.
/// </summary>
public sealed class CaptureController : IDisposable
{
    /// <summary>Setting holding the adapter the user last chose.</summary>
    public const string AdapterSetting = "capture.adapter_id";

    /// <summary>Setting enabling the follow-the-game poll.</summary>
    public const string FollowGameSetting = "capture.follow_game";

    /// <summary>Legacy name of the follow-the-game setting, still honoured.</summary>
    public const string AutostartSetting = "capture.autostart";

    /// <summary>Setting holding the parser queue capacity.</summary>
    public const string QueueCapacitySetting = "capture.queue_capacity";

    /// <summary>Setting holding the path of a user-supplied Oodle library.</summary>
    public const string OodleLibrarySetting = "capture.oodle_library_path";

    /// <summary>
    /// Setting that allows capture to run while the protocol profile is not verified.
    ///
    /// Default false, which is what contracts/error-codes.md requires: starting capture with
    /// no usable profile is refused with <c>ERR_PROFILE_UNSUPPORTED</c>. Turning it on runs
    /// the pipeline in diagnostics-only mode -- packets are counted, nothing is parsed and
    /// nothing is ever written to the database -- which is how the capture link is validated
    /// on a real machine before any profile exists (docs/live-validation-guide.md).
    /// </summary>
    public const string AllowWithoutProfileSetting = "capture.allow_without_profile";

    /// <summary>Distinct connection keys remembered per session.</summary>
    public const int MaxTrackedConnections = 64;

    /// <summary>
    /// Rejected frames, with no IPC decoded, before a capture is called midstream.
    ///
    /// Twenty rather than one: a single rejected frame proves nothing, and the point of the
    /// verdict is that it is worth telling the user to log out and back in over.
    /// </summary>
    public const int MidstreamRejectionThreshold = 20;

    /// <summary>
    /// How long a capture must have been running before silence is evidence of anything.
    ///
    /// A minute, because a client sitting at the character-select screen legitimately sends
    /// almost nothing, and telling that user to relog would be wrong as well as annoying.
    /// </summary>
    public static readonly TimeSpan LivenessGrace = TimeSpan.FromSeconds(60);

    /// <summary>First wait before a faulted capture is retried, doubling up to the cap.</summary>
    public const int InitialFaultBackoffMs = 30_000;

    /// <summary>Longest wait between retries of a faulted capture.</summary>
    public const int MaxFaultBackoffMs = 5 * 60_000;

    /// <summary>
    /// How long a session must have run before a later fault is treated as bad luck rather
    /// than a broken environment. Below this, the backoff keeps growing.
    ///
    /// Resetting the backoff on a successful Start alone is not enough: a monitor that accepts
    /// Start and faults half a second later would then retry at a flat thirty seconds forever,
    /// and every attempt copies the game executable - fifty megabytes - into the temp folder
    /// (docs/privacy-boundary.md section 4), eventually filling the system drive.
    /// </summary>
    public const int HealthySessionMs = 60_000;

    /// <summary>How often the ingress counters are written to the local log while running.</summary>
    public const int IngressLogIntervalMs = 30_000;

    /// <summary>
    /// Explains the observed decoding gap without claiming the user's startup order is known.
    /// A fresh login may supply a new connection prefix, but recovery is not guaranteed.
    /// </summary>
    public const string MidstreamHint =
        "已发现游戏连接，但持续未解码出有效 IPC，可能缺少连接起始数据。" +
        "可在方便时登出到标题画面再重新登录（不用关闭游戏），让软件尝试捕获新连接。" +
        "若仍无记录，请导出诊断报告。";

    /// <summary>
    /// The same silence, after the player has already done the one thing that was asked. The
    /// operating system listed a connection the client opened while this capture was running,
    /// so the login is not what is missing and repeating it will not help.
    /// </summary>
    public const string MidstreamReconnectedHint =
        "游戏在本软件运行期间新建过连接，但连接的起始报文没有被捕获到，" +
        "再登录一次也解决不了。请先关闭加速器、VPN 和正在占用网络的下载，" +
        "重启本软件后再试；若仍如此，请导出诊断报告。";

    /// <summary>
    /// The game's traffic is not on the card we are listening to. Named without jargon: the
    /// user has to recognise their own situation (a 加速器 or a VPN) from this sentence.
    /// </summary>
    public const string NoPacketsHint =
        "所选网卡上没有看到游戏流量，可能使用了加速器或 VPN。" +
        "请在捕获诊断页重新选择网卡，或关闭加速器后重新登录游戏。";

    /// <summary>
    /// Packets are arriving but the system never confirms those connections are the game's:
    /// another process's traffic, or a privilege level that cannot read the connection table.
    /// </summary>
    public const string NoOwnershipHint =
        "看到了网络流量，但无法确认这些连接属于游戏。" +
        "请确认没有使用加速器或代理；若仍然如此，请尝试以管理员身份运行本软件后重新开始。";

    /// <summary>The user-facing sentence for one silent-capture verdict, or null when healthy.</summary>
    /// <param name="reason">Verdict to explain.</param>
    public static string? HintFor(CaptureSilentReason reason) => HintFor(reason, CaptureIngressCounters.Empty, null);

    /// <summary>The user-facing sentence for one silent-capture verdict, or null when healthy.</summary>
    /// <param name="reason">Verdict to explain.</param>
    /// <param name="ingress">What ingress has seen, which decides between the two midstream sentences.</param>
    /// <param name="preexisting">Connections the game already held when capture started.</param>
    public static string? HintFor(
        CaptureSilentReason reason, CaptureIngressCounters ingress, int? preexisting) => reason switch
    {
        CaptureSilentReason.Midstream => ingress.ReconnectedUnreadable(preexisting)
            ? MidstreamReconnectedHint
            : MidstreamHint,
        CaptureSilentReason.NoPacketsOnAdapter => NoPacketsHint,
        CaptureSilentReason.NoStreamOwnership => NoOwnershipHint,
        _ => null,
    };

    private readonly CaptureServices _services;
    private readonly IDecodedMessageSink _sink;
    private readonly IParserStats _parserStats;
    private readonly ExponentialRateEstimator _rate = new();
    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly Timer? _followTimer;

    private CaptureControllerState _state = CaptureControllerState.Idle;
    private CaptureRun? _run;
    private ICaptureSource? _source;
    private DecodedMessageQueue? _queue;
    private NpcapDetection? _npcap;
    private GameProcessDetection? _game;
    private long _npcapTakenAtMs;
    private long _gameTakenAtMs;
    private string? _lastErrorCode;
    private string? _lastErrorMessage;
    private string? _pendingStartFaultReason;
    private Exception? _pendingStartFaultError;
    private bool _disposed;
    private volatile CaptureSilentReason _silentReason;
    private int? _preexistingConnections;
    private long _followRetryAtMs = -1;
    private long _faultRetryAtMs = -1;
    private long _faultBackoffMs;
    private long _ingressLoggedAtMs = -1;
    private bool _adapterReevaluated;
    private IDisposable? _ownershipLease;
    private bool _releaseFailed;
    private long _generation;
    private long _pendingDropped;
    private int _dropFlushScheduled;
    private int _polling;

    private readonly Stopwatch _processUptime = Stopwatch.StartNew();
    private long ProcessUptimeMs => _services.ProcessUptime is { } uptime
        ? (long)uptime().TotalMilliseconds
        : _processUptime.ElapsedMilliseconds;

    /// <summary>Creates a controller.</summary>
    /// <param name="services">Dependencies; defaults describe the real machine.</param>
    public CaptureController(CaptureServices? services = null)
    {
        _services = services ?? new CaptureServices();
        _faultBackoffMs = (long)_services.FaultRetryInterval.TotalMilliseconds;
        var counting = new CountingSink();
        _sink = _services.Sink ?? counting;
        _parserStats = _services.ParserStats ?? (_sink as IParserStats) ?? NullParserStats.Instance;

        if (_services.EnableFollowTimer)
        {
            _followTimer = new Timer(
                _ => Poll(), null, _services.FollowInterval, _services.FollowInterval);
        }
    }

    /// <summary>Current lifecycle state.</summary>
    public CaptureControllerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Takes one consistent diagnostics reading.</summary>
    public CaptureDiagnosticsSnapshot Snapshot()
    {
        var npcap = DetectNpcap(force: false);
        var game = DetectGame(force: false);
        var profile = ResolveProfile(game);
        var candidate = _services.CandidateStatus();
        var hypotheses = _services.CandidateHypotheses();
        var calibration = _services.CalibrationStatus();
        // The one outbound request's switches, read as they stand now (docs/privacy-boundary.md §8.2). A setting
        // that is missing or unreadable is its default, on.
        var sharedCalibrationEnabled = ReadSetting(CaptureSettingsStore.SharedCalibrationSetting) is not JsonValue sharedSetting ||
                                       !sharedSetting.TryGetValue<bool>(out var sharedOn) || sharedOn;
        var sharedFetchKillSwitch = Protocol.Sharing.SharedCalibrationClient.IsDisabled(
            Environment.GetEnvironmentVariable(Protocol.Sharing.SharedCalibrationClient.DisableVariable));

        // Read outside the lock, deliberately. The decode worker holds the reassembly buffer's
        // own lock while it feeds a decoded message all the way through to this controller, so
        // taking that buffer's lock from inside _gate would invert the order and can deadlock.
        var counters = IngressCounters();

        lock (_gate)
        {
            var run = _run;
            var queue = _queue;
            var state = _state == CaptureControllerState.Idle && !npcap.Usable
                ? CaptureControllerState.Unavailable
                : _state;

            var observed = run?.PacketsObserved ?? 0;
            var ingress = run is null ? CaptureIngressCounters.Empty : counters;
            var uptime = run?.Elapsed ?? TimeSpan.Zero;
            var rate = run is null ? 0 : _rate.Observe(observed, uptime);

            var warnings = new List<string>(game.Warnings);
            if (npcap.Status != NpcapStatus.Ready)
            {
                warnings.Add(npcap.Guidance);
            }

            if (state == CaptureControllerState.Running
                && profile.Status != ProfileStatus.Verified)
            {
                warnings.Add(profile.CalibrationActive
                    ? "游戏更新到了新版本，正在重新校准：正常打一把随机任务（进本、打完出本）即可，" +
                      "期间不解析任何字段，也不会自动写入任何记录。"
                    : "抓包正在运行，但协议档案未验证：本次只统计报文数量，" +
                      "不解析任何字段，也不会自动写入任何记录（fail-closed）。");
            }

            // The game was already logged in when capture started, so the connections it
            // already held cannot be read at all. Play on them counts for nothing, and
            // calibration looks broken rather than deaf: it can only report what it received.
            if (state == CaptureControllerState.Running && _preexistingConnections > 0)
            {
                // Which of the two sentences depends on whether the login already happened:
                // telling a player to go back to the title screen when they just did reads as
                // the software not listening to them, and is the wrong instruction besides.
                warnings.Add(ingress.ReconnectedUnreadable(_preexistingConnections)
                    ? "游戏在本软件运行期间已经重新连过线，但连接的起始报文没有捕获到，" +
                      "再登录一次也没用。请先关闭加速器、VPN 和占用网络的下载，重启本软件后再试。"
                    : "本软件是在游戏已经登录之后才开始抓包的，游戏那时已经建立的连接读不到，" +
                      "这部分游戏过程不会被记录，校准也会显得一直没进展。" +
                      "让本软件开着，回到游戏标题界面重新登录一次即可。");
            }

            return new CaptureDiagnosticsSnapshot
            {
                State = state,
                CaptureSessionId = run?.CaptureSessionId,
                Npcap = npcap,
                Game = game,
                Profile = profile,
                CandidateValidationEnabled = candidate.Enabled,
                CandidateProfileId = candidate.ProfileId,
                CandidateObservationCount = candidate.Count,
                CandidateHypotheses = hypotheses,
                Calibration = calibration,
                ProfileOrigin = profile.Origin,
                SharedCalibrationEnabled = sharedCalibrationEnabled,
                SharedFetchKillSwitch = sharedFetchKillSwitch,
                AdapterId = run?.Adapter?.Id,
                AdapterName = run?.Adapter?.FriendlyName,
                AdapterMaskedIPv4 = run?.Adapter?.MaskedIPv4 ?? Array.Empty<string>(),
                ConnectionCount = run?.ConnectionCount ?? 0,
                PacketsObserved = observed,
                RawPacketsObserved = ingress.RawPackets,
                Ingress = ingress,
                PreexistingConnections = run is null ? null : _preexistingConnections,
                SilentReason = run is null ? CaptureSilentReason.None : _silentReason,
                MessagesDecoded = run?.MessagesDecoded ?? 0,
                DecodeErrors = run?.DecodeErrors ?? 0,
                ParseOkCount = _parserStats.ParseOkCount,
                ParseFailCount = _parserStats.ParseFailCount,
                DuplicateCount = _parserStats.DuplicateCount,
                IgnoredCount = _parserStats.IgnoredCount,
                DroppedCount = queue?.LostCount ?? 0,
                QueueDepth = queue?.Depth ?? 0,
                QueueCapacity = queue?.Capacity ?? ReadQueueCapacity(),
                LastValidEventAtUtc = _parserStats.LastValidEventAtUtc,
                LastValidEventKind = _parserStats.LastValidEventKind,
                RecentParserErrors = _parserStats.RecentErrors,
                RunState = SafeRunState(),
                MessageRatePerSecond = rate,
                StartedAtUtc = run?.StartedAtUtc,
                UptimeMs = run is null ? ProcessUptimeMs : (long)uptime.TotalMilliseconds,
                Oodle = run?.Oodle ?? ResolveOodleMode(),
                ReadsGameExecutable = run?.ReadsGameExecutable ?? (ResolveOodleMode() == OodleMode.FfxivTcp),
                GameExecutableKnown = game.ExecutablePath is not null,
                // MIDSTREAM predates silent_reason and keeps its original meaning, so existing
                // clients read this field unchanged.
                MidstreamSuspected = run is null ? null : _silentReason == CaptureSilentReason.Midstream,
                Hint = run is null ? null : HintFor(_silentReason, ingress, _preexistingConnections),
                OodleSignature = _source?.SignatureUse ?? OodleSignatureUse.None,
                LastErrorCode = _lastErrorCode,
                LastErrorMessage = _lastErrorMessage,
                Warnings = warnings,
            };
        }
    }

    /// <summary>Re-reads the game process, bypassing the cache.</summary>
    public GameProcessDetection RescanGame() => DetectGame(force: true);

    /// <summary>Re-reads Npcap, bypassing the cache.</summary>
    public NpcapDetection RescanNpcap() => DetectNpcap(force: true);

    /// <summary>Lists adapters, flagging the one that carries the game's traffic.</summary>
    public IReadOnlyList<CaptureAdapterView> RescanAdapters()
    {
        var game = DetectGame(force: false);
        return _services.Adapters.List(game.ProcessId, ReadSettingString(AdapterSetting));
    }

    /// <summary>
    /// Runs the pre-flight checks and starts observing. Throws a contract error rather than
    /// starting a capture that cannot work.
    /// </summary>
    /// <param name="adapterId">Adapter to use; null selects the recommended one.</param>
    /// <param name="processId">Game process to observe; null selects the discovered one.</param>
    public CaptureDiagnosticsSnapshot Start(string? adapterId = null, int? processId = null)
    {
        lock (_lifecycleGate) return StartOwned(adapterId, processId);
    }

    private CaptureDiagnosticsSnapshot StartOwned(string? adapterId, int? processId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownershipLease is not null)
                throw new CollectorException(ErrorCodes.CaptureAlreadyRunning, "抓包已在运行，无需重复启动。");
            _ownershipLease = _services.Ownership.Acquire();
            _generation++;
        }
        try { return StartCore(adapterId, processId); }
        catch
        {
            lock (_gate)
            {
                if (!_releaseFailed) { _ownershipLease?.Dispose(); _ownershipLease = null; }
                if (_state == CaptureControllerState.Starting) _state = CaptureControllerState.Idle;
            }
            throw;
        }
    }

    private CaptureDiagnosticsSnapshot StartCore(string? adapterId, int? processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_state is CaptureControllerState.Starting
                or CaptureControllerState.Running
                or CaptureControllerState.Stopping)
            {
                throw new CollectorException(
                    ErrorCodes.CaptureAlreadyRunning,
                    "抓包已在运行，无需重复启动。",
                    new Dictionary<string, object?> { ["capture"] = _state.ToString().ToUpperInvariant() });
            }

            // A session starts owing nothing. Drops counted after the previous session was
            // torn down are never reported -- the flush task returns without clearing once the
            // generation no longer matches -- and left standing they would reach the *next*
            // session's first flush, where an EVENT_SEQUENCE_GAP downgrades a perfectly good
            // run in flight to INTERRUPTED (review finding R-5).
            Interlocked.Exchange(ref _pendingDropped, 0);
            Interlocked.Exchange(ref _dropFlushScheduled, 0);
        }

        var npcap = DetectNpcap(force: true);
        if (!npcap.Usable)
        {
            throw Refuse(ErrorCodes.NpcapMissing, npcap.Guidance, new Dictionary<string, object?>
            {
                ["capture"] = "UNAVAILABLE",
                ["npcap"] = npcap.StatusToken,
            });
        }

        var game = DetectGame(force: true);
        if (!game.Running || game.ProcessId is null)
        {
            throw Refuse(
                ErrorCodes.FfxivNotRunning,
                "未找到正在运行的 FINAL FANTASY XIV 客户端，抓包未启动。请先启动并登录游戏后重试。",
                new Dictionary<string, object?> { ["capture"] = "IDLE" },
                retryable: true);
        }

        var targetPid = processId is > 0 ? processId.Value : game.ProcessId.Value;
        var adapters = _services.Adapters.List(targetPid, ReadSettingString(AdapterSetting));
        if (adapters.Count == 0)
        {
            throw Refuse(
                ErrorCodes.NpcapMissing,
                "没有枚举到任何可用网卡。请确认 Npcap 服务已启动，必要时以管理员身份运行一次本软件。",
                new Dictionary<string, object?> { ["capture"] = "UNAVAILABLE", ["adapters"] = 0 });
        }

        // The remembered card no longer carries the game and another one does: the
        // 加速器/VPN case. Recorded because the selection silently moving is exactly the
        // kind of thing a user cannot reconstruct afterwards. No GUID and no address: the
        // adapter GUID is stable across reboots and therefore identifies the machine
        // (docs/privacy-boundary.md section 5).
        if (adapters.Any(candidate => candidate.PreferenceStale))
        {
            _services.Logger.Write(LogLevel.Warn, "capture", "adapter_preference_stale", new Dictionary<string, object?>
            {
                ["adapters"] = adapters.Count,
                ["honoured"] = adapterId is not null,
            });
        }

        var adapter = ChooseAdapter(adapters, adapterId);
        if (!GameTcpConnectionProbe.IsUsableLocalAddress(adapter.BindAddress))
        {
            throw Refuse(
                ErrorCodes.BadRequest,
                "所选网卡没有可绑定的 IPv4 地址，抓包未启动。请刷新网卡列表并选择实际承载游戏流量的 IPv4 网卡。",
                new Dictionary<string, object?> { ["adapter_id"] = adapter.Id },
                field: "adapter_id");
        }

        var profile = ResolveProfile(game);
        // Calibration is the third way past the gate, and the narrowest: the pipeline only
        // arms it for "no profile matches this build" with a shipped template at hand, never
        // for an ambiguous directory or a load failure.
        var allowWithoutProfile = (ReadSettingBool(AllowWithoutProfileSetting) ?? false) ||
            (ReadSettingBool(CaptureSettingsStore.CandidateValidationSetting) ?? false) ||
            profile.CalibrationActive;
        if (profile.Status != ProfileStatus.Verified && !allowWithoutProfile)
        {
            // Fail-closed, as required by contracts/error-codes.md. The diagnostics-only
            // opt-in exists for the live-validation pass that has to happen before any profile
            // can be written in the first place.
            throw Refuse(
                ErrorCodes.ProfileUnsupported,
                "协议档案不可用（fail-closed），未启动抓包：本版本不会解析任何报文，也不会自动写入任何记录。" +
                "若只是想验证抓包链路本身，请先把设置 capture.allow_without_profile 打开（仅统计，不记录）。",
                new Dictionary<string, object?>
                {
                    ["capture"] = "IDLE",
                    ["profile_status"] = EnumWire<ProfileStatus>.Format(profile.Status),
                });
        }

        return Launch(adapter, targetPid, game, profile, adapterId is not null);
    }

    /// <summary>Stops observing. Refuses when nothing is running, rather than succeeding silently.</summary>
    public CaptureDiagnosticsSnapshot Stop()
    {
        lock (_lifecycleGate) return StopCore();
    }

    private CaptureDiagnosticsSnapshot StopCore()
    {
        lock (_gate)
        {
            if (_state is not (CaptureControllerState.Running or CaptureControllerState.Starting) && !_releaseFailed)
            {
                throw new CollectorException(
                    ErrorCodes.CaptureNotRunning,
                    "抓包未在运行，无需停止。",
                    new Dictionary<string, object?> { ["capture"] = _state.ToString().ToUpperInvariant() });
            }

            _state = CaptureControllerState.Stopping;
        }

        Teardown(CaptureEndReason.UserStop);
        if (_queue is not null)
            throw new CollectorException(ErrorCodes.Internal,
                "协议处理尚未退出，已保留会话资源。请稍后重试停止，或退出采集服务。");
        var snapshot = Snapshot();
        Publish(snapshot, "抓包已停止。");
        return snapshot;
    }

    /// <summary>
    /// Whether the poll starts capture on its own when the game appears. On unless the user
    /// turned it off: the Oodle stream can only be decoded from the start of a connection, so
    /// the recorder has to be listening before login, and the way to make that not the user's
    /// problem is to have it happen by itself (docs/live-validation-guide.md section 6).
    /// </summary>
    public bool FollowGameEnabled =>
        ReadSettingBool(FollowGameSetting)
            ?? (ReadSettingBool(AutostartSetting) is true || FollowGameDefault);

    /// <summary>Default of <see cref="FollowGameSetting"/> when nothing was ever written.</summary>
    public const bool FollowGameDefault = true;

    /// <summary>
    /// One tick of the follow-the-game poll: start when the game appears, stop when it goes
    /// away. Public so tests can drive it deterministically instead of waiting on a timer.
    /// </summary>
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        // The timer does not serialise its own ticks. Without this guard, a pre-flight that
        // takes longer than the interval -- a slow process enumeration, an Npcap probe on a
        // busy machine -- lets the next tick call Start on the session the first tick just
        // started and stamps CAPTURE_ALREADY_RUNNING onto last_error for the rest of the
        // session (review finding L-1). An overlapping tick has nothing to add anyway: the
        // next one is a second away.
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }

        try
        {
            PollCore();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private void PollCore()
    {
        try
        {
            var running = State == CaptureControllerState.Running;
            var game = DetectGame(force: true);

            if (running)
            {
                if (!game.Running)
                {
                    lock (_gate)
                    {
                        if (_state != CaptureControllerState.Running)
                        {
                            return;
                        }

                        _state = CaptureControllerState.Stopping;
                    }

                    if (Teardown(CaptureEndReason.ProcessExit))
                        Publish(Snapshot(), "游戏进程已退出，抓包已停止。");
                    return;
                }

                // A running capture is checked too: without these, it reports RUNNING and
                // healthy for a whole session while every packet is discarded uncounted.
                EvaluateLiveness();
                ReportHealth();
                LogIngressStats();
                ReevaluateAdapterIfBlind();
                return;
            }

            // A fault must not be terminal for the lifetime of the process: following only
            // from Idle would mean one transient driver failure costs capture until a restart.
            if (State == CaptureControllerState.Faulted)
            {
                RecoverFromFault(game.Running);
            }

            if (State != CaptureControllerState.Idle || !FollowGameEnabled)
            {
                return;
            }

            // The timer does not serialise ticks, so the back-off stamp lives under the same
            // gate as the rest of the lifecycle state.
            lock (_gate)
            {
                if (!game.Running)
                {
                    _followRetryAtMs = -1;
                    // A new client is a new chance for the adapter guess, and the one-shot
                    // re-evaluation is per game session rather than per capture attempt:
                    // re-choosing every minute would churn instead of informing.
                    _adapterReevaluated = false;
                    return;
                }

                if (ProcessUptimeMs < _followRetryAtMs)
                {
                    return;
                }
            }

            // A validation session holding the capture is not a refusal to back off from:
            // the moment it ends, the next tick should start. It is also not an error.
            if (_services.Ownership.IsHeld)
            {
                return;
            }

            Start();
        }
        catch (CollectorException ex)
        {
            // A client still starting up can expose its path/version or first TCP address
            // on the next tick. A 30-second driver back-off here would make an already-open
            // recorder miss the beginning of the game connection. Keep the longer delay
            // for driver/resource failures; readiness checks use the normal bounded poll.
            //
            // "Already running" is never recorded here. It is not a failure the user can act
            // on -- it means a start this software issued raced another start this software
            // issued -- and leaving it in last_error makes a perfectly healthy session look
            // broken for the rest of its life (review finding L-1).
            if (ex.Code != ErrorCodes.CaptureAlreadyRunning)
            {
                RecordError(ex);
            }

            var waitingForReadiness = ex.Code is ErrorCodes.FfxivNotRunning or ErrorCodes.ProfileUnsupported
                or ErrorCodes.CaptureAlreadyRunning ||
                (ex.Code == ErrorCodes.BadRequest && ex.Field == "adapter_id");
            lock (_gate)
            {
                // The periodic timer already limits readiness retries. Adding an interval
                // from this failure's completion would skip the next scheduled tick.
                _followRetryAtMs = waitingForReadiness ? -1 : ProcessUptimeMs
                    + (long)_services.FollowRetryInterval.TotalMilliseconds;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "follow_poll_failed", ex);
        }
    }

    private CaptureDiagnosticsSnapshot Launch(
        CaptureAdapterView adapter,
        int processId,
        GameProcessDetection game,
        ProfileStatusSnapshot profile,
        bool adapterWasExplicit)
    {
        var sessionId = Guid.NewGuid().ToString("D");
        var oodle = ResolveOodleMode();
        var startedAt = UtcTimestamp.Truncate(_services.Clock.UtcNow);

        lock (_gate)
        {
            _state = CaptureControllerState.Starting;
            _lastErrorCode = null;
            _lastErrorMessage = null;
            _pendingStartFaultReason = null;
            _pendingStartFaultError = null;
        }

        Publish(Snapshot(), "正在启动抓包……");

        var source = _services.SourceFactory?.Invoke()
            ?? new MachinaCaptureSource(
                _services.Logger,
                clock: _services.Clock,
                oodleTempManifestPath: _services.OodleTempManifestPath);
        var generation = _generation;
        var queue = new DecodedMessageQueue(
            _sink,
            ReadQueueCapacity(),
            error => OnFault("协议处理或写库失败，抓包已停止以避免漏记。", error, generation),
            dropped => NoteDropped(dropped, generation),
            onConnectionLost: () => DeliverConnectionLost(sessionId, processId, generation));
        var run = new CaptureRun(
            sessionId,
            startedAt,
            adapter,
            oodle,
            source.ReadsGameExecutable || oodle == OodleMode.FfxivTcp,
            adapterWasExplicit,
            _services.Uptime);

        var lifecycleStarted = false;
        try
        {
            // The session row and protocol pipeline must exist before the source is allowed to
            // invoke its first callback. Otherwise a fast source could produce a run whose
            // capture_session_id has not been inserted yet.
            var sessionRecorded = RecordSessionRow(run, game, profile, out var sessionError);
            // A calibration session intends to become a recording session, so its row is as
            // mandatory as a verified one's: the hot-bind later refuses without it.
            if (!sessionRecorded && (profile.Status == ProfileStatus.Verified || profile.CalibrationActive))
            {
                throw new CollectorException(
                    ErrorCodes.Internal,
                    "无法建立抓包会话日志，未启动抓包。详情见本机诊断日志。",
                    inner: sessionError);
            }

            _services.Lifecycle.OnCaptureStarted(sessionId);
            lifecycleStarted = true;

            source.Start(
                new CaptureStartOptions(
                    sessionId,
                    processId,
                    adapter.BindAddress,
                    adapter.Id,
                    oodle,
                    ReadSettingString(OodleLibrarySetting),
                    game.ExecutablePath,
                    game.Region,
                    game.GameBuild),
                new Observer(this, run, queue, generation));

            // A source is allowed to report asynchronously as soon as Start is entered. Make
            // the Starting -> Running transition atomic with the pending-fault check so an
            // immediate source/sink failure cannot be overwritten by a later Running state.
            lock (_gate)
            {
                if (_pendingStartFaultReason is { } pendingReason)
                {
                    var pendingError = _pendingStartFaultError;
                    _pendingStartFaultReason = null;
                    _pendingStartFaultError = null;
                    throw new CollectorException(
                        ErrorCodes.Internal, pendingReason, inner: pendingError);
                }

                _source = source;
                _queue = queue;
                _run = run;
                _state = CaptureControllerState.Running;
            }
        }
        catch (Exception ex)
        {
            var released = StopAndDisposeSource(source);
            var queueStopped = StopQueue(queue);

            if (lifecycleStarted && queueStopped)
            {
                try
                {
                    _services.Lifecycle.OnCaptureStopped(sessionId, CaptureEndReason.Error);
                }
                catch (Exception lifecycleError)
                    when (lifecycleError is not OutOfMemoryException and not StackOverflowException)
                {
                    _services.Logger.WriteError("capture", "lifecycle_start_rollback_failed", lifecycleError);
                }
            }
            if (queueStopped) CloseSessionRow(run, CaptureEndReason.Error);

            lock (_gate)
            {
                _releaseFailed = !released || !queueStopped;
                _source = released ? null : source;
                _queue = queueStopped ? null : queue;
                _run = queueStopped ? null : run;
                _state = released && queueStopped ? CaptureControllerState.Idle : CaptureControllerState.Faulted;
                _pendingStartFaultReason = null;
                _pendingStartFaultError = null;
            }

            var refusal = ex as CollectorException ?? new CollectorException(
                ErrorCodes.Internal, "启动抓包失败，详情见本机诊断日志。", inner: ex);
            RecordError(refusal);
            _services.Logger.WriteError("capture", "start_failed", ex);
            if (queueStopped) Publish(Snapshot(), "抓包启动失败。");
            throw refusal;
        }

        _rate.Reset();
        lock (_gate)
        {
            _silentReason = CaptureSilentReason.None;
            _preexistingConnections = source.PreexistingTcpConnections;
            // The backoff is reset by a session that lasted, not by one that started; see
            // HealthySessionMs.
            _faultRetryAtMs = -1;
            _ingressLoggedAtMs = ProcessUptimeMs + IngressLogIntervalMs;
        }

        ReportHealth();

        if (adapterWasExplicit)
        {
            WriteSetting(AdapterSetting, JsonValue.Create(adapter.Id));
        }

        var snapshot = Snapshot();
        Publish(snapshot, "抓包已启动。");
        return snapshot;
    }

    private CaptureAdapterView ChooseAdapter(IReadOnlyList<CaptureAdapterView> adapters, string? adapterId)
    {
        if (adapterId is not null)
        {
            return AdapterEnumerator.Find(adapters, adapterId)
                ?? throw Refuse(
                    ErrorCodes.BadRequest,
                    "指定的网卡不存在，请刷新网卡列表后重新选择。",
                    new Dictionary<string, object?> { ["adapter_id"] = adapterId },
                    field: "adapter_id");
        }

        return adapters.FirstOrDefault(candidate => candidate.Recommended)
            ?? throw Refuse(
                ErrorCodes.BadRequest,
                "无法确定游戏流量所在的网卡（不做猜测）。请在诊断页手动选择一张网卡后重试。",
                new Dictionary<string, object?> { ["adapters"] = adapters.Count },
                field: "adapter_id");
    }

    private bool Teardown(CaptureEndReason reason)
    {
        lock (_lifecycleGate) return TeardownCore(reason);
    }

    private bool TeardownCore(CaptureEndReason reason)
    {
        ICaptureSource? source;
        DecodedMessageQueue? queue;
        CaptureRun? run;
        CaptureSilentReason silentReason;
        int? preexisting;
        lock (_gate)
        {
            source = _source;
            queue = _queue;
            run = _run;
            silentReason = _silentReason;
            preexisting = _preexistingConnections;
            _source = null;
            _queue = null;
        }

        // The last health reading needs the adapter's drop counter, which dies with the source.
        var finalIngress = ReadIngress(source);
        var released = StopAndDisposeSource(source);

        if (!StopQueue(queue))
        {
            // The currently executing sink may still own the pipeline/database lock. Do
            // not close its session, reuse its pipeline, or release the capture lease.
            lock (_gate)
            {
                _source = released ? null : source;
                _queue = queue;
                _releaseFailed = true;
                _state = CaptureControllerState.Faulted;
                _lastErrorCode = ErrorCodes.Internal;
                _lastErrorMessage =
                    $"协议处理线程尚未退出（{queue?.Stage}，积压 {queue?.Depth}），已保留会话资源供停止操作重试。";
            }
            return false;
        }

        if (run is not null)
        {
            // Only overflow drops nobody has reported yet. The backlog a timed-out shutdown
            // gave up on (AbandonedCount) is deliberately not reported: a session that is
            // already over has no sequence left to have a gap in, and reporting it would turn
            // a correctly finished duty into EVENT_SEQUENCE_GAP (review finding H-7).
            FlushDropped(run.CaptureSessionId);
            ReportHealth(new Protocol.Calibration.CaptureSessionHealth(
                run.CaptureSessionId, silentReason, preexisting, finalIngress.AdapterDropped));

            try
            {
                _services.Lifecycle.OnCaptureStopped(run.CaptureSessionId, reason);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                _services.Logger.WriteError("capture", "lifecycle_stop_failed", ex);
            }
            finally
            {
                CloseSessionRow(run, reason);
            }
        }

        lock (_gate)
        {
            _run = null;
            _releaseFailed = !released;
            _source = released ? null : source;
            if (released) { _ownershipLease?.Dispose(); _ownershipLease = null; }
            else
            {
                _lastErrorCode = ErrorCodes.Internal;
                _lastErrorMessage = "采集源未能释放，已阻止再次采集。请退出采集服务后重试。";
            }
            if (reason != CaptureEndReason.Error ||
                (run?.Elapsed ?? TimeSpan.Zero) >= TimeSpan.FromMilliseconds(HealthySessionMs))
            {
                _faultBackoffMs = (long)_services.FaultRetryInterval.TotalMilliseconds;
            }

            _state = !released || reason == CaptureEndReason.Error
                ? CaptureControllerState.Faulted
                : CaptureControllerState.Idle;
            _faultRetryAtMs = _state == CaptureControllerState.Faulted
                ? ProcessUptimeMs + _faultBackoffMs
                : -1;
            _silentReason = CaptureSilentReason.None;
        }

        _services.Logger.Write(LogLevel.Info, "capture", "session_closed", new Dictionary<string, object?>
        {
            ["reason"] = EnumWire<CaptureEndReason>.Format(reason),
            ["packets_observed"] = run?.PacketsObserved ?? 0,
            ["messages_decoded"] = run?.MessagesDecoded ?? 0,
            ["decode_errors"] = run?.DecodeErrors ?? 0,
            ["dropped"] = queue?.DroppedCount ?? 0,
            ["abandoned"] = queue?.AbandonedCount ?? 0,
        });
        return released;
    }

    /// <summary>
    /// Decides why a running capture is producing nothing, from the counters alone.
    ///
    /// Called on every follow tick and whenever a frame is rejected. The verdicts are mutually
    /// exclusive and each one has a different user action: without them "running, zero events"
    /// reads as healthy, and the three causes -- attached too late, listening to the wrong
    /// card, watching somebody else's connections -- are indistinguishable
    /// (docs/capture-diagnostics.md section 5.5).
    /// </summary>
    private void EvaluateLiveness()
    {
        CaptureSilentReason reason;
        long generation;
        var counters = IngressCounters(); // Outside _gate: see the note in Snapshot.
        lock (_gate)
        {
            if (_state != CaptureControllerState.Running || _run is not { } run)
            {
                return;
            }

            generation = _generation;
            reason = Classify(run, counters, _preexistingConnections);
            if (reason == _silentReason)
            {
                return;
            }

            _silentReason = reason;
        }

        if (HintFor(reason) is { } hint)
        {
            _services.Logger.Write(LogLevel.Warn, "capture", "capture_silent", new Dictionary<string, object?>
            {
                ["silent_reason"] = EnumWire<CaptureSilentReason>.Format(reason),
            });
            PublishOffThread(hint, generation);
        }
    }

    /// <summary>
    /// Reads one verdict out of the counters. Pure, so the table of cases is testable without
    /// a controller, a clock or a source.
    /// </summary>
    /// <param name="run">Counters of the capture in flight.</param>
    /// <param name="ingress">What the adapter delivered and what was discarded.</param>
    /// <param name="preexisting">Connections the game already held at start, when known.</param>
    private static CaptureSilentReason Classify(
        CaptureRun run, CaptureIngressCounters ingress, int? preexisting)
    {
        // One decoded IPC segment proves the compressed stream is readable. Control segments
        // do not: they stay readable exactly when Oodle state is missing.
        if (run.IpcMessagesDecoded > 0)
        {
            return CaptureSilentReason.None;
        }

        // Two forms of hard evidence, neither of which needs to wait out the grace period.
        // A sustained rejection run on a capture that started with connections is the
        // original midstream signature; every frame being dropped for want of a handshake is
        // the same fact observed one layer lower, where nothing reaches the observer at all.
        if (preexisting > 0 && run.DecodeErrors >= MidstreamRejectionThreshold)
        {
            return CaptureSilentReason.Midstream;
        }

        // Every frame the card delivered was discarded before reassembly could use it. At
        // the wire a mid-connection attach shows up as DroppedNoStream, not DroppedNoSyn:
        // a continuation whose handshake happened before we were listening has no tracked
        // stream at all, and only a connection that showed one SYN and not the other lands
        // in DroppedNoSyn. Both mean the same thing -- no handshake was observed -- so both
        // count, and the sample has to be big enough that ordinary background chatter on the
        // same local address cannot produce this verdict on its own.
        if (ingress.AllDroppedBeforeDecode && ingress.RawPackets >= MidstreamRejectionThreshold)
        {
            return CaptureSilentReason.Midstream;
        }

        if (run.Elapsed < LivenessGrace)
        {
            return CaptureSilentReason.None;
        }

        if (preexisting > 0)
        {
            return CaptureSilentReason.Midstream;
        }

        // Nothing at all on this card for a whole minute while the game is running: the
        // traffic is somewhere else, which is what a 加速器 or a VPN does.
        if (ingress.RawPackets == 0)
        {
            return CaptureSilentReason.NoPacketsOnAdapter;
        }

        // Traffic arrives, but no tuple was ever confirmed as belonging to the game.
        return ingress.UnconfirmedTuples > 0
            ? CaptureSilentReason.NoStreamOwnership
            : CaptureSilentReason.None;
    }

    /// <summary>
    /// Writes the ingress counters to the local log at a fixed interval, so a session that
    /// recorded nothing can be diagnosed afterwards from the log alone. No address, path or
    /// packet byte is written (docs/privacy-boundary.md section 5).
    /// </summary>
    private void LogIngressStats()
    {
        CaptureRun run;
        CaptureSilentReason reason;
        int? preexisting;
        var ingress = IngressCounters(); // Outside _gate: see the note in Snapshot.
        lock (_gate)
        {
            if (_state != CaptureControllerState.Running || _run is not { } current)
            {
                return;
            }

            var now = ProcessUptimeMs;
            if (now < _ingressLoggedAtMs)
            {
                return;
            }

            _ingressLoggedAtMs = now + IngressLogIntervalMs;
            run = current;
            reason = _silentReason;
            preexisting = _preexistingConnections;
        }

        _services.Logger.Write(LogLevel.Info, "capture", "ingress_stats", new Dictionary<string, object?>
        {
            ["raw_packets"] = ingress.RawPackets,
            ["dropped_no_stream"] = ingress.DroppedNoStream,
            ["dropped_no_syn"] = ingress.DroppedNoSyn,
            ["expired_streams"] = ingress.ExpiredStreams,
            ["unconfirmed_tuples"] = ingress.UnconfirmedTuples,
            ["stream_resets"] = ingress.StreamResets,
            ["adapter_dropped"] = ingress.AdapterDropped,
            ["packets_observed"] = run.PacketsObserved,
            ["messages_decoded"] = run.MessagesDecoded,
            ["ipc_decoded"] = run.IpcMessagesDecoded,
            ["decode_errors"] = run.DecodeErrors,
            ["preexisting_connections"] = preexisting,
            ["silent_reason"] = EnumWire<CaptureSilentReason>.Format(reason),
            ["uptime_ms"] = (long)run.Elapsed.TotalMilliseconds,
        });
    }

    /// <summary>
    /// Stops a capture that has seen no packet at all on the adapter it guessed, so the next
    /// follow tick can choose again. Once per capture, and never against an adapter the user
    /// chose themselves: their choice is authoritative, and repeatedly restarting a capture
    /// the user configured would be worse than the silence.
    /// </summary>
    private void ReevaluateAdapterIfBlind()
    {
        if (!FollowGameEnabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_state != CaptureControllerState.Running ||
                _run is not { AdapterWasExplicit: false } ||
                _adapterReevaluated ||
                _silentReason != CaptureSilentReason.NoPacketsOnAdapter)
            {
                return;
            }

            _adapterReevaluated = true;
            _state = CaptureControllerState.Stopping;
        }

        _services.Logger.Write(LogLevel.Warn, "capture", "adapter_reevaluated", new Dictionary<string, object?>
        {
            ["silent_reason"] = EnumWire<CaptureSilentReason>.Format(CaptureSilentReason.NoPacketsOnAdapter),
        });
        if (Teardown(CaptureEndReason.Unknown))
            Publish(Snapshot(), "所选网卡上没有看到游戏流量，正在重新选择网卡后重试。");
    }

    /// <summary>
    /// Leaves the terminal fault state so the follow poll can start again: at once when the
    /// game has gone (the next launch deserves a fresh attempt), otherwise after an
    /// exponential back-off. The last error code is deliberately left in place until a start
    /// succeeds, so the diagnostics page still explains what went wrong.
    /// </summary>
    /// <param name="gameRunning">Whether the game process is still there.</param>
    private void RecoverFromFault(bool gameRunning)
    {
        lock (_gate)
        {
            if (_state != CaptureControllerState.Faulted)
            {
                return;
            }

            // A source that could not be released still owns the driver and Oodle. Retrying
            // over the top of it is the one thing that would be worse than staying faulted.
            if (_releaseFailed || _source is not null || _ownershipLease is not null)
            {
                return;
            }

            if (gameRunning)
            {
                if (ProcessUptimeMs < _faultRetryAtMs)
                {
                    return;
                }

                _faultBackoffMs = Math.Min(_faultBackoffMs * 2, MaxFaultBackoffMs);
            }
            else
            {
                _faultBackoffMs = (long)_services.FaultRetryInterval.TotalMilliseconds;
            }

            _faultRetryAtMs = -1;
            _state = CaptureControllerState.Idle;
        }

        _services.Logger.Write(LogLevel.Info, "capture", "fault_recovered", new Dictionary<string, object?>
        {
            ["game_running"] = gameRunning,
        });
    }

    /// <summary>
    /// One rejected frame. Re-runs the verdict rather than counting towards a threshold of
    /// its own: the interesting rejection runs are the ones no observer ever sees, so the
    /// decision has to be made from the ingress counters too.
    ///
    /// This runs on the decode worker, inside the reassembly buffer's lock. It is bounded by
    /// the early-out above rather than by a timer: once a verdict stands, no further rejection
    /// does any work, and until one does, three uncontended locks per rejected frame is the
    /// price of answering in the first seconds rather than at the next poll tick. None of the
    /// three may be taken in the other order -- see the note on <see cref="IngressCounters"/>.
    /// </summary>
    /// <param name="generation">Session generation the reporting observer belongs to.</param>
    private void NoteDecodeError(long generation)
    {
        if (_silentReason != CaptureSilentReason.None)
        {
            return;
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

        }

        EvaluateLiveness();
    }

    /// <summary>
    /// Withdraws the midstream verdict when an IPC segment decodes.
    ///
    /// Control segments can be readable even while compressed IPC is unavailable. They
    /// must not cancel the warning. An IPC segment ends the all-IPC-unreadable condition;
    /// it does not establish that every connection or every business event is readable.
    /// </summary>
    /// <param name="generation">Session generation the reporting observer belongs to.</param>
    private void NoteDecoded(long generation)
    {
        if (_silentReason == CaptureSilentReason.None)
        {
            return;
        }

        lock (_gate)
        {
            if (generation != _generation || _silentReason == CaptureSilentReason.None)
            {
                return;
            }

            _silentReason = CaptureSilentReason.None;
        }

        PublishOffThread("已经读到可解压的报文，抓包状态恢复正常。", generation);
    }

    /// <summary>
    /// One or more observations were lost to queue overflow. Reported to the lifecycle
    /// listener at once rather than at teardown, because the run this happens to is still in
    /// flight and its classification depends on knowing about the hole (review finding H-7).
    ///
    /// The forwarding itself is deferred off the capture callback thread: the listener writes
    /// to the database, which may retry for seconds against a busy file, and blocking the
    /// callback there would make the driver drop packets instead of us. Counts accumulate, so
    /// a burst of drops becomes one report carrying all of them rather than one report each.
    /// </summary>
    /// <param name="count">How many observations were lost by this report.</param>
    /// <param name="generation">Session generation the reporting queue belongs to.</param>
    private void NoteDropped(long count, long generation)
    {
        if (count <= 0)
        {
            return;
        }

        Interlocked.Add(ref _pendingDropped, count);
        if (Interlocked.Exchange(ref _dropFlushScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Interlocked.Exchange(ref _dropFlushScheduled, 0);
            string? session;
            lock (_gate)
            {
                if (_disposed || generation != _generation || _run is not { } run)
                {
                    return;
                }

                session = run.CaptureSessionId;
            }

            FlushDropped(session);
        });
    }

    /// <summary>
    /// Overflow drops counted but not yet handed to the lifecycle listener.
    ///
    /// Exposed to tests because the leak this guards against has no other observable form:
    /// the reporting task returns without clearing precisely when the session it belongs to
    /// has already gone, leaving a count for the next session's first flush to pick up as an
    /// EVENT_SEQUENCE_GAP of its own (review finding R-5).
    /// </summary>
    internal long PendingDroppedCount => Interlocked.Read(ref _pendingDropped);

    /// <summary>
    /// Counts a drop reported by a queue whose session generation no longer matches -- the
    /// in-flight callback that arrives after teardown. Test seam for
    /// <see cref="PendingDroppedCount"/>; production always reports the live generation.
    /// </summary>
    /// <param name="count">How many observations were lost.</param>
    /// <param name="generation">Generation the reporting queue belongs to.</param>
    internal void NoteDroppedFromRetiredSession(long count, long generation) =>
        NoteDropped(count, generation);

    /// <summary>
    /// Hands every drop counted since the last report to the lifecycle listener. Safe to call
    /// with nothing pending; safe to call from teardown, where it is the final report.
    /// </summary>
    /// <param name="captureSessionId">Session the drops belong to.</param>
    private void FlushDropped(string captureSessionId)
    {
        var dropped = Interlocked.Exchange(ref _pendingDropped, 0);
        if (dropped <= 0)
        {
            return;
        }

        try
        {
            _services.Lifecycle.OnEventsDropped(captureSessionId, dropped);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "lifecycle_dropped_failed", ex);
        }
    }

    /// <summary>
    /// The last game connection that had been delivering decoded messages ended. Queued so a
    /// run inside a duty can reach DISCONNECTED, which the live path otherwise has no producer
    /// for (review finding H-6).
    ///
    /// It goes into the bounded queue rather than straight to the listener because the very
    /// messages that would have ended the run properly -- a DUTY_RESULT that arrived moments
    /// before the FIN -- may still be waiting in that queue. Delivered out of band, the
    /// connection loss closes the run first and the result is then discarded as belonging to a
    /// run that has already finished (review finding R-4).
    /// </summary>
    /// <param name="queue">Queue of the reporting session; the marker keeps its place in it.</param>
    /// <param name="generation">Session generation the reporting observer belongs to.</param>
    private void OnConnectionLost(DecodedMessageQueue queue, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }
        }

        queue.OfferConnectionLost();
    }

    /// <summary>
    /// The queued connection-lost marker reached the front of the queue. This runs on the
    /// parser thread, which is already off the decode thread, so the listener may take a
    /// database transaction here exactly as it does for an ordinary message.
    ///
    /// The game process is confirmed alive first. A client that is closing sends its FIN and
    /// leaves the OS connection table well before the one-second follow poll notices the
    /// process is gone, so without this check an ordinary "quit while inside a duty" is
    /// recorded as DISCONNECTED instead of INTERRUPTED, contradicting
    /// docs/state-machine.md section 3.6 (review finding R-2).
    /// </summary>
    /// <param name="captureSessionId">Session the marker was queued for.</param>
    /// <param name="gameProcessId">Game process this session observes.</param>
    /// <param name="generation">Session generation the marker belongs to.</param>
    private void DeliverConnectionLost(string captureSessionId, int gameProcessId, long generation)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation ||
                _state != CaptureControllerState.Running || _run is not { } run ||
                !string.Equals(run.CaptureSessionId, captureSessionId, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (!_services.Game.IsRunning(gameProcessId))
        {
            _services.Logger.Write(
                LogLevel.Info, "capture", "connection_lost_game_exited", new Dictionary<string, object?>
                {
                    ["capture_session_id"] = captureSessionId,
                });
            return;
        }

        try
        {
            _services.Lifecycle.OnConnectionLost(captureSessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "lifecycle_connection_lost_failed", ex);
        }
    }

    private void PublishOffThread(string message, long generation) => _ = Task.Run(() =>
    {
        // Snapshot() may re-run Npcap and process detection, which must never happen on the
        // capture callback thread: blocking there drops packets at the driver.
        if (_disposed || generation != Interlocked.Read(ref _generation))
        {
            return;
        }

        Publish(Snapshot(), message);
    });

    private void OnFault(string reason, Exception? error, long generation)
    {
        var deferredUntilStartReturns = false;
        lock (_gate)
        {
            if (generation != _generation) return;
            if (_state == CaptureControllerState.Starting)
            {
                _pendingStartFaultReason ??= reason;
                _pendingStartFaultError ??= error;
                _lastErrorCode = ErrorCodes.Internal;
                _lastErrorMessage = reason;
                deferredUntilStartReturns = true;
            }
            else if (_state == CaptureControllerState.Running)
            {
                _state = CaptureControllerState.Stopping;
                _lastErrorCode = ErrorCodes.Internal;
                _lastErrorMessage = reason;
            }
            else
            {
                return;
            }
        }

        _services.Logger.WriteError("capture", "monitor_faulted", error, reason);

        if (deferredUntilStartReturns)
        {
            return;
        }

        // Tear down off the reporting thread: the fault can be raised from inside the
        // monitor's own callback, and stopping it from there would deadlock.
        _ = Task.Run(() =>
        {
            lock (_lifecycleGate)
            {
                // Dispose or a user stop may already have completed this session while the
                // callback was waiting for Start to release the lifecycle gate.
                if (State != CaptureControllerState.Stopping || generation != _generation) return;
                if (Teardown(CaptureEndReason.Error))
                    Publish(Snapshot(), reason);
            }
        });
    }

    private bool StopAndDisposeSource(ICaptureSource? source)
    {
        try { source?.Stop(); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { _services.Logger.WriteError("capture", "source_stop_failed", ex); }
        try { source?.Dispose(); return true; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { _services.Logger.WriteError("capture", "source_dispose_failed", ex); return false; }
    }

    private bool StopQueue(DecodedMessageQueue? queue)
    {
        try { queue?.Dispose(); return true; }
        catch (TimeoutException ex)
        {
            _services.Logger.WriteError("capture", "queue_stop_timed_out", ex);
            return false;
        }
    }

    private bool RecordSessionRow(
        CaptureRun run, GameProcessDetection game, ProfileStatusSnapshot profile, out Exception? failure)
    {
        failure = null;
        if (_services.Database is not { } database || _services.Sessions is not { } sessions)
        {
            return true;
        }

        try
        {
            var inserted = database.RunInTransaction(tx => sessions.Insert(
                new CaptureSession
                {
                    CaptureSessionId = run.CaptureSessionId,
                    StartedAtUtc = run.StartedAtUtc,
                    CollectorVersion = _services.CollectorVersion,
                    Region = game.Region,
                    GameBuild = game.GameBuild,
                    ProtocolProfileId = profile.ProfileId,
                    ProfileStatus = profile.Status,
                    AdapterId = run.Adapter?.Id,
                },
                tx));
            if (!inserted)
            {
                failure = new InvalidDataException("capture session already exists");
                _services.Logger.WriteError("capture", "session_row_duplicate", failure);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Diagnostics-only capture may continue without the audit row, but a verified live
            // session cannot: later run writes would fail their capture_session foreign key.
            _services.Logger.WriteError("capture", "session_row_failed", ex);
            failure = ex;
            return false;
        }
    }

    private void CloseSessionRow(CaptureRun run, CaptureEndReason reason)
    {
        if (_services.Database is not { } database || _services.Sessions is not { } sessions)
        {
            return;
        }

        try
        {
            database.RunInTransaction(tx => sessions.Close(
                run.CaptureSessionId, UtcTimestamp.Truncate(_services.Clock.UtcNow), reason, tx));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "session_close_failed", ex);
        }
    }

    /// <summary>
    /// Resolves the profile against the exact game detection used by this controller pass.
    /// Static providers used by diagnostics-only tests keep their existing behavior.
    /// </summary>
    private ProfileStatusSnapshot ResolveProfile(GameProcessDetection game) =>
        _services.Profile is IGameAwareProfileStatusProvider aware
            ? aware.Refresh(game)
            : _services.Profile.Current;

    private NpcapDetection DetectNpcap(bool force)
    {
        lock (_gate)
        {
            if (!force && _npcap is { } cached && Fresh(_npcapTakenAtMs))
            {
                return cached;
            }
        }

        var detection = _services.Npcap.Detect();
        lock (_gate)
        {
            _npcap = detection;
            _npcapTakenAtMs = ProcessUptimeMs;
        }

        return detection;
    }

    private GameProcessDetection DetectGame(bool force)
    {
        lock (_gate)
        {
            if (!force && _game is { } cached && Fresh(_gameTakenAtMs))
            {
                return cached;
            }
        }

        var detection = _services.Game.Locate();
        lock (_gate)
        {
            _game = detection;
            _gameTakenAtMs = ProcessUptimeMs;
        }

        return detection;
    }

    /// <summary>
    /// Reads the source's ingress counters. Never call this while holding <c>_gate</c>: the
    /// decode worker holds the reassembly buffer's lock across the whole message callback,
    /// which ends inside <c>_gate</c>, so the reverse order is a deadlock.
    /// </summary>
    private CaptureIngressCounters IngressCounters() => ReadIngress(Volatile.Read(ref _source));

    /// <summary>
    /// Hands the running session's capture health to the health listener: the silent reason as it
    /// stands, the connections it attached to midway and the adapter's drops so far. Outside
    /// <c>_gate</c> like every listener call, and never allowed to fail capture. Deliberately not
    /// sent from the decode worker's rejection path, which holds the reassembly lock: the verdict it
    /// reaches there is picked up by the next poll.
    /// </summary>
    private void ReportHealth()
    {
        var ingress = IngressCounters(); // Outside _gate: see the note on IngressCounters.
        Protocol.Calibration.CaptureSessionHealth health;
        lock (_gate)
        {
            if (_state != CaptureControllerState.Running || _run is not { } run)
            {
                return;
            }

            health = new(run.CaptureSessionId, _silentReason, _preexistingConnections, ingress.AdapterDropped);
        }

        ReportHealth(health);
    }

    private void ReportHealth(Protocol.Calibration.CaptureSessionHealth health)
    {
        try
        {
            _services.Health.OnCaptureHealth(health);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "health_report_failed", ex);
        }
    }

    /// <summary>Reads a source's ingress counters; same threading rule as <see cref="IngressCounters"/>.</summary>
    /// <param name="source">Source to read, possibly null.</param>
    private CaptureIngressCounters ReadIngress(ICaptureSource? source)
    {
        try
        {
            return source?.IngressCounters ?? CaptureIngressCounters.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A source being torn down concurrently must not be able to fail a status read.
            _services.Logger.WriteError("capture", "ingress_counters_failed", ex);
            return CaptureIngressCounters.Empty;
        }
    }

    private bool Fresh(long takenAtMs) =>
        ProcessUptimeMs - takenAtMs < (long)_services.DetectionTtl.TotalMilliseconds;

    private OodleMode ResolveOodleMode()
    {
        // DEC-OODLE-01: the library implementation is preferred whenever the user has
        // supplied their own Oodle DLL, because it does not touch the game binary at all.
        // Otherwise the default reads the game executable from disk, which is disclosed
        // through GetStatus.reads_game_executable.
        var path = ReadSettingString(OodleLibrarySetting);
        return string.IsNullOrWhiteSpace(path) ? OodleMode.FfxivTcp : OodleMode.LibraryTcp;
    }

    private int ReadQueueCapacity()
    {
        var configured = ReadSettingInt(QueueCapacitySetting) ?? DecodedMessageQueue.DefaultCapacity;
        return Math.Clamp(configured, DecodedMessageQueue.MinCapacity, DecodedMessageQueue.MaxCapacity);
    }

    private RunState SafeRunState()
    {
        try
        {
            return _services.RunState();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return Domain.RunState.Idle;
        }
    }

    private JsonNode? ReadSetting(string key)
    {
        if (_services.Settings is not { } settings)
        {
            return null;
        }

        try
        {
            var raw = settings.GetSetting(key);
            return raw is null ? null : JsonNode.Parse(raw);
        }
        catch (Exception ex) when (ex is JsonException or CollectorException)
        {
            return null;
        }
    }

    private string? ReadSettingString(string key) =>
        ReadSetting(key) is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private bool? ReadSettingBool(string key) =>
        ReadSetting(key) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private int? ReadSettingInt(string key) =>
        ReadSetting(key) is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private void WriteSetting(string key, JsonNode? value)
    {
        try
        {
            _services.Settings?.SetSetting(key, value?.ToJsonString() ?? "null");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "setting_write_failed", ex);
        }
    }

    private CollectorException Refuse(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        string? field = null,
        bool retryable = false)
    {
        var error = new CollectorException(code, message, details, field, retryable);
        RecordError(error);
        return error;
    }

    private void RecordError(CollectorException error)
    {
        lock (_gate)
        {
            _lastErrorCode = error.Code;
            _lastErrorMessage = error.Message;
        }
    }

    private void Publish(CaptureDiagnosticsSnapshot snapshot, string message)
    {
        try
        {
            _services.StatusListener?.OnCaptureStatusChanged(snapshot, message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _services.Logger.WriteError("capture", "status_publish_failed", ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleGate) DisposeCore();
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _followTimer?.Dispose();

        if (_ownershipLease is not null)
        {
            Teardown(CaptureEndReason.UserStop);
        }
        if (_releaseFailed)
        {
            // The last error names the stage the parser thread was stuck in; without it a
            // timed-out release cannot be told apart from a worker that never woke up.
            throw new TimeoutException(
                "采集资源尚未退出，不能释放仍被使用的会话与数据库。" +
                (string.IsNullOrEmpty(_lastErrorMessage) ? string.Empty : " " + _lastErrorMessage));
        }
        _disposed = true;
    }

    /// <summary>Mutable counters of one capture session.</summary>
    private sealed class CaptureRun
    {
        private readonly Stopwatch _mono = Stopwatch.StartNew();
        private readonly Func<TimeSpan>? _uptime;
        private readonly TimeSpan _startedAt;
        private readonly HashSet<string> _connections = new(StringComparer.Ordinal);
        private long _packets;
        private long _decoded;
        private long _ipcDecoded;
        private long _decodeErrors;

        public CaptureRun(
            string captureSessionId,
            DateTimeOffset startedAtUtc,
            CaptureAdapterView? adapter,
            OodleMode oodle,
            bool readsGameExecutable,
            bool adapterWasExplicit = false,
            Func<TimeSpan>? uptime = null)
        {
            CaptureSessionId = captureSessionId;
            StartedAtUtc = startedAtUtc;
            Adapter = adapter;
            Oodle = oodle;
            ReadsGameExecutable = readsGameExecutable;
            AdapterWasExplicit = adapterWasExplicit;
            _uptime = uptime;
            _startedAt = uptime?.Invoke() ?? TimeSpan.Zero;
        }

        /// <summary>True when the user named this adapter rather than the software choosing it.</summary>
        public bool AdapterWasExplicit { get; }

        public string CaptureSessionId { get; }

        public DateTimeOffset StartedAtUtc { get; }

        public CaptureAdapterView? Adapter { get; }

        public OodleMode Oodle { get; }

        public bool ReadsGameExecutable { get; }

        public TimeSpan Elapsed => _uptime is null ? _mono.Elapsed : _uptime() - _startedAt;

        public long PacketsObserved => Interlocked.Read(ref _packets);

        public long MessagesDecoded => Interlocked.Read(ref _decoded);

        public long IpcMessagesDecoded => Interlocked.Read(ref _ipcDecoded);

        public long DecodeErrors => Interlocked.Read(ref _decodeErrors);

        public int ConnectionCount
        {
            get
            {
                lock (_connections)
                {
                    return _connections.Count;
                }
            }
        }

        public void CountMessage(DecodedMessage message)
        {
            Interlocked.Increment(ref _packets);
            Interlocked.Increment(ref _decoded);
            if (message.SegmentType == FfxivFraming.SegmentTypeIpc)
                Interlocked.Increment(ref _ipcDecoded);

            lock (_connections)
            {
                if (_connections.Count < MaxTrackedConnections)
                {
                    _connections.Add(message.ConnectionKey);
                }
            }
        }

        public void CountDecodeError()
        {
            Interlocked.Increment(ref _packets);
            Interlocked.Increment(ref _decodeErrors);
        }
    }

    /// <summary>Bridges one capture source to this session's counters and bounded queue.</summary>
    private sealed class Observer : ICaptureSourceObserver
    {
        private readonly CaptureController _controller;
        private readonly CaptureRun _run;
        private readonly DecodedMessageQueue _queue;
        private readonly long _generation;

        public Observer(CaptureController controller, CaptureRun run, DecodedMessageQueue queue, long generation)
        {
            _controller = controller;
            _run = run;
            _queue = queue;
            _generation = generation;
        }

        public void OnMessage(DecodedMessage message)
        {
            _run.CountMessage(message);
            _queue.Offer(message);
            if (message.SegmentType == FfxivFraming.SegmentTypeIpc)
                _controller.NoteDecoded(_generation);
        }

        public void OnDecodeError()
        {
            _run.CountDecodeError();
            _controller.NoteDecodeError(_generation);
        }

        public void OnFault(string reason, Exception? error) => _controller.OnFault(reason, error, _generation);

        public void OnConnectionClosed() => _controller.OnConnectionLost(_queue, _generation);
    }
}
