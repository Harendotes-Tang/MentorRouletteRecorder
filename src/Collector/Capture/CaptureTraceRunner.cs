using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Machina.Infrastructure;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>What one trace run was asked to do.</summary>
/// <param name="OutputPath">JSON Lines file to write.</param>
/// <param name="DurationSeconds">Seconds to run; 0 means until Ctrl+C or the game exits.</param>
/// <param name="AdapterId">Adapter to use; null selects the one carrying the game's traffic.</param>
/// <param name="MaxLines">Cap on message lines written.</param>
public sealed record CaptureTraceOptions(
    string OutputPath,
    int DurationSeconds = 0,
    string? AdapterId = null,
    int MaxLines = CaptureTraceSink.DefaultMaxLines,
    bool AllowMidstream = false);

/// <summary>Everything <see cref="CaptureTraceRunner"/> depends on, so all of it can be substituted.</summary>
public sealed record CaptureTraceServices
{
    /// <summary>Npcap detection.</summary>
    public NpcapDetector Npcap { get; init; } = new();

    /// <summary>Game process discovery.</summary>
    public GameProcessLocator Game { get; init; } = new();

    /// <summary>Adapter enumeration.</summary>
    public AdapterEnumerator Adapters { get; init; } = new();

    /// <summary>Creates the capture source; the Machina/Npcap one when null.</summary>
    public Func<ICaptureSource>? SourceFactory { get; init; }

    /// <summary>Clock used for timestamps.</summary>
    public IClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>Local diagnostic log.</summary>
    public RotatingFileLogger Logger { get; init; } = RotatingFileLogger.Disabled;

    /// <summary>Where markers are read from; standard input when null.</summary>
    public TextReader? Markers { get; init; }

    /// <summary>Where the human-facing report is printed; standard output when null.</summary>
    public TextWriter? Output { get; init; }

    /// <summary>Where the live status line is printed; standard error when null.</summary>
    public TextWriter? Status { get; init; }

    /// <summary>
    /// Opens the trace destination; the exclusive, create-new file writer when null. Tests
    /// replace this to exercise failures that happen after a file has already been opened.
    /// </summary>
    public Func<string, TextWriter>? TraceWriterFactory { get; init; }

    /// <summary>Version stamped on the header line.</summary>
    public string CollectorVersion { get; init; } = "0.0.0";

    /// <summary>How often the status line is printed and the game is re-checked.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Counts the game's current TCP connections on the selected adapter address; null means
    /// the operating-system query failed and capture should proceed rather than refuse on a
    /// guess.
    /// </summary>
    public Func<int, IPAddress?, int?> TcpConnectionCounter { get; init; } =
        GameTcpConnectionProbe.TryCount;

    /// <summary>False leaves Ctrl+C alone, for tests that must not touch the console.</summary>
    public bool InstallCancelHandler { get; init; } = true;
}

/// <summary>
/// The <c>--capture-trace</c> mode: record one live session as opcode-level evidence.
///
/// docs/protocol-profile-format.md §5 refuses a constant that was not observed, so before any
/// protocol profile can exist somebody has to observe, on their own machine, which opcodes
/// appear when a duty pops and when it ends. This is the first step of
/// docs/live-validation-guide.md and produces that observation and nothing more: a sanitized
/// JSON Lines file of opcode, direction, length and payload digest, plus the markers the user
/// typed while playing.
///
/// It stands alone on purpose. No database is opened, no Named Pipe is served, no protocol
/// profile is consulted and nothing is parsed: there is no state to corrupt and no record to
/// write, so a trace run can never produce a run record out of guessed opcodes. The pipeline
/// is otherwise the shipping one -- the same detectors, the same Machina/Npcap source, the
/// same bounded queue -- so what the trace saw is what capture would have seen.
///
/// Everything the mode can produce is bounded: the queue is bounded and drops oldest, the
/// file is bounded by a line cap, and the run is bounded by <c>--duration-seconds</c>, by the
/// game exiting, or by Ctrl+C.
/// </summary>
public static class CaptureTraceRunner
{
    /// <summary>Kind reported by the live source, as opposed to any test double.</summary>
    public const string LiveSourceKind = "machina-npcap";

    /// <summary>Printed when Npcap cannot be used, under the doctor's own explanation.</summary>
    public const string NpcapRefusal =
        "未满足抓包前提，取证未开始。请先按上面的指引安装 Npcap" +
        "（安装时勾选 \"WinPcap API-compatible Mode\"）后重试。";

    /// <summary>Printed when no game client is running.</summary>
    public const string GameRefusal =
        "未找到正在运行的 FINAL FANTASY XIV 客户端，取证未开始。请先启动并登录游戏后重试。";

    /// <summary>Printed before opening a live trace if the client's identity is not yet readable.</summary>
    public const string GameIdentityRefusal =
        "客户端路径、区服或版本尚未确认，取证未开始。" +
        "请等待客户端初始化完成并确认安装路径可读后重试；未知版本不能用于选择 Oodle 签名或关联取证。";

    /// <summary>Printed when the adapter carrying the game's traffic cannot be determined.</summary>
    public const string AdapterRefusal =
        "无法确定游戏流量所在的网卡（不做猜测），取证未开始。" +
        "请从上面的网卡列表中挑一张，用 --adapter <id> 指定后重试。";

    /// <summary>
    /// Prefix of the refusal shown when trace would attach to an already-live TCP session.
    /// </summary>
    public const string MidstreamRefusalPrefix =
        "检测到游戏已经存在活动 TCP 连接，取证未开始。中途接入会让 Oodle 流状态不完整，" +
        "当前 trace 不能把这种结果当作有效证据。";

    /// <summary>Prefix of the warning printed when a midstream trace was explicitly allowed.</summary>
    public const string MidstreamWarningPrefix =
        "注意：游戏已有活动 TCP 连接，本次取证是中途接入。这些既有连接上的报文无法解压，" +
        "只有取证开始之后新建的连接（例如传送换区后）才是完整证据；汇总会按连接分别统计。";

    private static string MidstreamWarning(int preexistingConnections) =>
        MidstreamWarningPrefix + " 既有连接数: " +
        preexistingConnections.ToString(System.Globalization.CultureInfo.InvariantCulture) + "。";

    /// <summary>The prompt explaining what to type while the trace runs.</summary>
    public static readonly string MarkerPrompt = string.Join(
        Environment.NewLine,
        "抓包取证已开始。请在游戏里发生下面这些事的**当下**，在本窗口输入一个词并回车：",
        "  queued=开始排队  pop=弹出确认框  entered=进入副本  victory=通关结算  left=离开副本",
        "只接受上面 5 个固定词（大小写不敏感）；其他输入会被忽略，避免写入自由文本。",
        "文件里不含任何报文内容、地址、角色名——只有 opcode、方向、长度与负载摘要前 12 位。",
        "按 Ctrl+C 结束取证。");

    /// <summary>
    /// Runs one trace. Returns 0 when a trace was written, 1 when the machine could not
    /// produce one, so a script can gate on it exactly as it gates on <c>--capture-doctor</c>.
    /// </summary>
    /// <param name="options">What to record and where.</param>
    /// <param name="services">Dependencies; the real machine when null.</param>
    public static int Run(CaptureTraceOptions options, CaptureTraceServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var svc = services ?? new CaptureTraceServices();
        var output = svc.Output ?? Console.Out;
        var status = svc.Status ?? Console.Error;

        var npcap = svc.Npcap.Detect();
        if (!npcap.Usable)
        {
            return Refuse(svc, output, NpcapRefusal);
        }

        var game = svc.Game.Locate();
        if (!game.Running || game.ProcessId is not int processId)
        {
            return Refuse(svc, output, GameRefusal);
        }

        var adapters = svc.Adapters.List(processId, options.AdapterId);
        var adapter = options.AdapterId is null
            ? adapters.FirstOrDefault(candidate => candidate.Recommended)
            : AdapterEnumerator.Find(adapters, options.AdapterId);
        if (adapter is null)
        {
            return Refuse(svc, output, AdapterRefusal);
        }

        using var source = svc.SourceFactory?.Invoke() ?? new MachinaCaptureSource(svc.Logger);
        var isLiveSource = string.Equals(source.Kind, LiveSourceKind, StringComparison.Ordinal);
        // A new process can be visible before MainModule exposes its path. Starting from
        // that partial snapshot would skip the exact-build Oodle profile and permanently
        // stamp UNKNOWN/null onto this evidence file, even when the next query is ready.
        if (isLiveSource &&
            (string.IsNullOrWhiteSpace(game.ExecutablePath) ||
             string.IsNullOrWhiteSpace(game.GameBuild) || game.Region == Domain.Region.Unknown))
        {
            return Refuse(svc, output, GameIdentityRefusal);
        }

        if (isLiveSource && !GameTcpConnectionProbe.IsUsableLocalAddress(adapter.BindAddress))
        {
            return Refuse(svc, output, AdapterRefusal);
        }

        // Null means the table could not be read; treated as "none known", so an unreadable
        // table never blocks a trace on its own.
        var preexistingConnections = isLiveSource
            ? svc.TcpConnectionCounter(processId, adapter.BindAddress) ?? 0
            : 0;
        if (preexistingConnections > 0)
        {
            if (!options.AllowMidstream)
            {
                return Refuse(svc, output, MidstreamRefusal(preexistingConnections));
            }

            // Oodle state of a connection that predates the trace is unknowable, so the
            // messages on it are recorded but flagged: every line carries a connection
            // key and the summary is broken down per connection. A connection that starts
            // after this point (the game opens a fresh one on every zone change) is
            // complete evidence; the pre-existing ones are not, and the header says so.
            output.WriteLine(MidstreamWarning(preexistingConnections));
        }

        return Record(
            options, svc, output, status, npcap, game, adapter, processId, source,
            preexistingConnections);
    }

    /// <summary>
    /// Prints the same report <c>--capture-doctor</c> prints, then says why no trace was
    /// taken. The wording is not copied: the doctor's own writer produces it, so the two
    /// commands can never drift apart.
    /// </summary>
    /// <param name="services">Dependencies, reused so both commands look at one machine.</param>
    /// <param name="output">Where to print.</param>
    /// <param name="reason">The refusal line.</param>
    private static int Refuse(CaptureTraceServices services, TextWriter output, string reason)
    {
        CaptureCli.Run(
            new[] { CaptureCli.Flag },
            new CaptureServices
            {
                Npcap = services.Npcap,
                Game = services.Game,
                Adapters = services.Adapters,
                Clock = services.Clock,
                Logger = services.Logger,
                CollectorVersion = services.CollectorVersion,
                EnableFollowTimer = false,
            },
            output);

        output.WriteLine();
        output.WriteLine(reason);
        output.Flush();
        return 1;
    }

    private static int Record(
        CaptureTraceOptions options,
        CaptureTraceServices services,
        TextWriter output,
        TextWriter status,
        NpcapDetection npcap,
        GameProcessDetection game,
        CaptureAdapterView adapter,
        int processId,
        ICaptureSource source,
        int preexistingConnections = 0)
    {
        var path = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stopwatch = Stopwatch.StartNew();
        CaptureTraceSink sink;
        long dropped;
        string? fault;

        var file = services.TraceWriterFactory?.Invoke(path) ?? OpenTraceWriter(path);
        var closeFile = true;
        try
        {
            sink = new CaptureTraceSink(file, services.Clock, () => stopwatch.Elapsed, options.MaxLines);
            sink.WriteHeader(new CaptureTraceHeader(
                UtcTimestamp.Truncate(services.Clock.UtcNow),
                npcap.Version,
                game.GameBuild,
                game.Region,
                SanitizedDiagnosticsReport.Fingerprint(adapter.Id),
                services.CollectorVersion,
                OodleMode.FfxivTcp,
                Synthetic: !string.Equals(source.Kind, LiveSourceKind, StringComparison.Ordinal),
                PreexistingConnections: preexistingConnections));

            (dropped, fault) = Observe(
                options, services, output, status, sink, source, game, adapter, processId);
        }
        catch (TraceDrainTimeoutException ex)
        {
            closeFile = false;
            // The synchronous command can return failure promptly. The outstanding callbacks
            // retain their writer until they actually finish; no summary or digest is claimed.
            _ = Task.Run(async () =>
            {
                await ex.Completion.ConfigureAwait(false);
                try { ex.Queue.Dispose(); file.Dispose(); }
                catch (Exception error) { services.Logger.WriteError("capture", "trace_deferred_close_failed", error); }
            });
            services.Logger.WriteError("capture", "trace_drain_timed_out", ex);
            output.WriteLine("取证写入未能及时停止，文件可能不完整，未生成摘要校验文件。");
            return 1;
        }
        finally { if (closeFile) file.Dispose(); }

        var digest = CaptureTraceDigest.WriteSidecar(path);
        Report(output, path, digest, sink, dropped, fault);

        // A trace that ended in a monitor fault is still written and still hashed -- what was
        // observed before the fault is evidence -- but it is not a successful run, and a
        // script that gates on the exit code must not be told otherwise.
        return fault is null ? 0 : 1;
    }

    private static TextWriter OpenTraceWriter(string path) =>
        new StreamWriter(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Starts the source, pumps markers, prints the status line, and waits for whichever of
    /// the five stop conditions arrives first: the duration, Ctrl+C, the game exiting, a
    /// monitor fault, or a trace-writer failure.
    /// </summary>
    private static (long Dropped, string? Fault) Observe(
        CaptureTraceOptions options,
        CaptureTraceServices services,
        TextWriter output,
        TextWriter status,
        CaptureTraceSink sink,
        ICaptureSource source,
        GameProcessDetection game,
        CaptureAdapterView adapter,
        int processId)
    {
        using var stopping = new ManualResetEventSlim(false);

        // The fault is written on the capture callback thread and read here, so it lives in a
        // holder with a volatile field rather than in a captured local: a local captured by a
        // lambda cannot be declared volatile, and "the event that follows it will publish it"
        // is a guarantee about one of the two ways this loop can end, not both.
        var fault = new FaultBox();

        var observer = new TraceObserver(
            sink,
            reason =>
            {
                fault.Reason = reason;
                try { stopping.Set(); } catch (ObjectDisposedException) { }
            });

        ConsoleCancelEventHandler? cancel = null;
        if (services.InstallCancelHandler)
        {
            cancel = (_, eventArgs) =>
            {
                // A requested stop must still write the summary and the sidecar, so the
                // default kill is refused and the wait loop is released instead.
                eventArgs.Cancel = true;
                stopping.Set();
            };
            Console.CancelKeyPress += cancel;
        }

        var queue = new DecodedMessageQueue(
            sink,
            DecodedMessageQueue.DefaultCapacity,
            StopForWriterFailure);
        observer.Queue = queue;

        void StopForWriterFailure(Exception error)
        {
            fault.Reason = "写入取证文件失败，取证已停止。详情见本机诊断日志。";
            services.Logger.WriteError("capture", "trace_write_failed", error);
            try { stopping.Set(); } catch (ObjectDisposedException) { }
        }

        CaptureTraceMarkerPump? markers = null;
        try
        {
            source.Start(
                new CaptureStartOptions(
                    Guid.NewGuid().ToString("D"),
                    processId,
                    adapter.BindAddress,
                    adapter.Id,
                    OodleMode.FfxivTcp,
                    OodleLibraryPath: null,
                    GameExecutablePath: game.ExecutablePath,
                    Region: game.Region,
                    GameBuild: game.GameBuild,
                    AllowCandidateOodleSignature: true),
                observer);

            // Do not tell the user capture has started, or accept markers, until the source
            // has actually started. In particular, an Oodle/Npcap startup refusal must not
            // leave buffered marker input looking as though it belonged to a live trace.
            output.WriteLine(MarkerPrompt);
            output.WriteLine();
            output.Flush();

            markers = new CaptureTraceMarkerPump(
                services.Markers ?? Console.In,
                sink,
                error => services.Logger.WriteError("capture", "trace_marker_input_failed", error),
                StopForWriterFailure);
            markers.Start();
            Wait(options, services, status, sink, queue, stopping);
        }
        catch (Contracts.Errors.CollectorException ex)
        {
            // A source that refuses to start is reported the way a source that failed midway
            // is reported: the file still gets its summary line and its digest, because a
            // trace that recorded nothing is itself a result worth being able to cite.
            fault.Reason = ex.Message;
            services.Logger.WriteError("capture", "trace_start_failed", ex);
        }
        finally
        {
            if (cancel is not null)
            {
                Console.CancelKeyPress -= cancel;
            }

            try
            {
                source.Stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                services.Logger.WriteError("capture", "trace_source_stop_failed", ex);
            }
            markers?.Dispose();
            var queueStopped = queue.Complete(TimeSpan.FromSeconds(2));
            var writes = markers?.WritesCompletion ?? Task.CompletedTask;
            if (!queueStopped || !writes.Wait(TimeSpan.FromSeconds(2)))
                throw new TraceDrainTimeoutException(queue, Task.WhenAll(queue.Completion, writes));
            queue.Dispose();
        }

        sink.WriteSummary(queue.LostCount);
        return (queue.LostCount, fault.Reason);
    }

    private sealed class TraceDrainTimeoutException(DecodedMessageQueue queue, Task completion)
        : TimeoutException("Trace callbacks have not stopped.")
    {
        internal DecodedMessageQueue Queue { get; } = queue;
        internal Task Completion { get; } = completion;
    }

    private static void Wait(
        CaptureTraceOptions options,
        CaptureTraceServices services,
        TextWriter status,
        CaptureTraceSink sink,
        DecodedMessageQueue queue,
        ManualResetEventSlim stopping)
    {
        var deadline = options.DurationSeconds > 0
            ? TimeSpan.FromSeconds(options.DurationSeconds)
            : (TimeSpan?)null;
        var elapsed = Stopwatch.StartNew();
        var lastStatus = TimeSpan.Zero;

        while (!stopping.IsSet)
        {
            if (deadline is { } limit && elapsed.Elapsed >= limit)
            {
                return;
            }

            var slice = TimeSpan.FromMilliseconds(200);
            if (deadline is { } remaining && remaining - elapsed.Elapsed < slice)
            {
                slice = remaining - elapsed.Elapsed;
            }

            if (slice > TimeSpan.Zero && stopping.Wait(slice))
            {
                return;
            }

            if (elapsed.Elapsed - lastStatus < services.StatusInterval)
            {
                continue;
            }

            lastStatus = elapsed.Elapsed;
            WriteStatus(status, sink, queue, elapsed.Elapsed);
            if (!services.Game.Locate().Running)
            {
                status.WriteLine("游戏进程已退出，取证结束。");
                status.Flush();
                return;
            }
        }
    }

    private static void WriteStatus(
        TextWriter status, CaptureTraceSink sink, DecodedMessageQueue queue, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        status.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"[{elapsed.TotalSeconds:F0}s] 消息 {sink.MessageCount}（{sink.MessageCount / seconds:F1}/s） " +
            $"已写入 {sink.WrittenCount} 解码错误 {sink.DecodeErrorCount} 丢弃 {queue.LostCount} " +
            $"标记 {sink.MarkerCount}"));
        status.Flush();
    }

    private static void Report(
        TextWriter output,
        string path,
        string digest,
        CaptureTraceSink sink,
        long dropped,
        string? fault)
    {
        output.WriteLine();
        output.WriteLine("取证已结束。");
        output.WriteLine("  文件:       " + path);
        output.WriteLine("  sha256:     " + digest);
        output.WriteLine("  sha256 文件: " + path + CaptureTraceDigest.SidecarExtension);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  消息 {sink.MessageCount}（已写入 {sink.WrittenCount}） 标记 {sink.MarkerCount} " +
            $"解码错误 {sink.DecodeErrorCount} 丢弃 {dropped}"));

        if (sink.Truncated)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  ⚠ 已达到行数上限 {sink.MaxLines}，后续消息只计数不写入。" +
                $"需要更长的取证请提高 --max-lines，或分多次记录。"));
        }

        if (fault is not null)
        {
            output.WriteLine("  ⚠ 抓包中断：" + fault);
        }

        output.WriteLine();
        output.WriteLine("下一步：MentorRecorder.Collector --trace-report \"" + path + "\"");
        output.WriteLine(
            "提醒：本文件里的 opcode 只是**候选**。按 docs/protocol-profile-format.md §5，" +
            "在另一次独立会话复现之前，任何数字都不得写进协议档案。");
        output.Flush();
    }

    private static string MidstreamRefusal(int connectionCount) =>
        $"{MidstreamRefusalPrefix} 当前连接数: {connectionCount.ToString(CultureInfo.InvariantCulture)}。" +
        "请先记下 --capture-doctor 里那张网卡的 id，然后在重新登录前启动 " +
        "--capture-trace --adapter <id>，或断线重连后立刻重试。";

    /// <summary>Carries a fault reason across threads.</summary>
    private sealed class FaultBox
    {
        public volatile string? Reason;
    }

    /// <summary>
    /// Feeds decoded messages into the bounded queue and counts what could not be framed.
    ///
    /// Every method runs on the capture callback thread, so none of them may block: the queue
    /// offer is non-blocking by construction and a fault only sets an event.
    /// </summary>
    private sealed class TraceObserver(CaptureTraceSink sink, Action<string> onFault) : ICaptureSourceObserver
    {
        public DecodedMessageQueue? Queue { get; set; }

        public void OnMessage(DecodedMessage message) => Queue?.Offer(message);

        public void OnDecodeError() => sink.CountDecodeError();

        public void OnFault(string reason, Exception? error) => onFault(reason);
    }
}

internal static class GameTcpConnectionProbe
{
    public static int? TryCount(int processId, IPAddress? localAddress)
    {
        if (processId <= 0 || !IsUsableLocalAddress(localAddress))
        {
            return null;
        }

        try
        {
            var info = new ProcessTCPInfo { ProcessID = (uint)processId };
            var connections = new List<TCPConnection>();
            info.UpdateTCPIPConnections(connections);
            return CountOnLocalAddress(connections, localAddress!);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return null;
        }
    }

    internal static int CountOnLocalAddress(
        IEnumerable<TCPConnection> connections,
        IPAddress localAddress)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(localAddress);

        return connections.Count(connection =>
            new IPAddress(connection.LocalIP).Equals(localAddress));
    }

    internal static bool IsUsableLocalAddress(IPAddress? localAddress) =>
        localAddress is { AddressFamily: AddressFamily.InterNetwork } &&
        !localAddress.Equals(IPAddress.Any) &&
        !localAddress.Equals(IPAddress.None);
}

/// <summary>
/// Reads user markers from a reader and hands them to the sink.
///
/// Standard input is read on its own background thread because a console read blocks until
/// the user presses Enter, and the capture must not pause while it waits. The thread is a
/// background thread so a blocked read can never keep the process alive after the trace has
/// finished -- there is no portable way to interrupt a pending console read, and abandoning
/// one is harmless when the writer it feeds has already written its summary.
/// </summary>
public sealed class CaptureTraceMarkerPump : IDisposable
{
    private readonly TextReader _reader;
    private readonly CaptureTraceSink _sink;
    private readonly Action<Exception>? _onInputError;
    private readonly Action<Exception>? _onSinkError;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private readonly object _writeGate = new();
    private readonly TaskCompletionSource _writesComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeWrites;

    /// <summary>After disposal, completes when the last permitted marker write has returned.</summary>
    public Task WritesCompletion => _writesComplete.Task;

    /// <summary>Creates a pump; nothing is read until <see cref="Start"/> is called.</summary>
    /// <param name="reader">Source of marker lines.</param>
    /// <param name="sink">Sink the markers are written to.</param>
    /// <param name="onError">Optional diagnostic callback when the input reader fails.</param>
    /// <param name="onSinkError">Callback when writing a marker fails; capture should stop.</param>
    public CaptureTraceMarkerPump(
        TextReader reader,
        CaptureTraceSink sink,
        Action<Exception>? onError = null,
        Action<Exception>? onSinkError = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sink);

        _reader = reader;
        _sink = sink;
        _onInputError = onError;
        _onSinkError = onSinkError;
        _thread = new Thread(() =>
            PumpAll(_reader, _sink, () => _stopped, _onInputError, _onSinkError, WriteIfActive))
        {
            IsBackground = true,
            Name = "mentor-recorder-trace-markers",
        };
    }

    /// <summary>Starts reading.</summary>
    public void Start() => _thread.Start();

    /// <summary>
    /// Reads every line and writes each allowed marker. Synchronous, so a test can drive it
    /// without a thread.
    /// </summary>
    /// <param name="reader">Source of marker lines.</param>
    /// <param name="sink">Sink the markers are written to.</param>
    /// <param name="stopped">Consulted between lines; true ends the pump.</param>
    /// <param name="onError">Optional callback for input failures; they do not stop capture.</param>
    /// <param name="onSinkError">Callback for output failures; the owner should stop capture.</param>
    public static void PumpAll(
        TextReader reader,
        CaptureTraceSink sink,
        Func<bool>? stopped = null,
        Action<Exception>? onError = null,
        Action<Exception>? onSinkError = null,
        Func<string, bool>? writeMarker = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sink);

        while (true)
        {
            string? line;
            try
            {
                if (stopped?.Invoke() == true)
                {
                    return;
                }

                line = reader.ReadLine();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Notify(onError, ex);
                return;
            }

            if (line is null)
            {
                return;
            }

            try
            {
                if (stopped?.Invoke() == true) return;
                if (writeMarker is null) sink.WriteMarker(line);
                else if (!writeMarker(line)) return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                Notify(onSinkError, ex);
                return;
            }
        }
    }

    private static void Notify(Action<Exception>? callback, Exception error)
    {
        try
        {
            callback?.Invoke(error);
        }
        catch (Exception callbackError)
            when (callbackError is not OutOfMemoryException and not StackOverflowException)
        {
            // Diagnostics and stop callbacks are best-effort; this background thread must end.
        }
    }

    /// <summary>
    /// Writes one marker unless the pump has been disposed, and completes
    /// <see cref="WritesCompletion"/> when the last permitted write returns.
    /// </summary>
    private bool WriteIfActive(string marker)
    {
        lock (_writeGate)
        {
            if (_stopped) return false;
            _activeWrites++;
        }
        try { _sink.WriteMarker(marker); return true; }
        finally
        {
            lock (_writeGate)
                if (--_activeWrites == 0 && _stopped) _writesComplete.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            _stopped = true;
            if (_activeWrites == 0) _writesComplete.TrySetResult();
        }
    }
}
