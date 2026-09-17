using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>验证依赖；测试替换探测和源，不接触真实游戏或网卡。</summary>
public sealed record CaptureValidationServices
{
    public CaptureTraceServices Trace { get; init; } = new();
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public Func<GameProcessDetection>? LocateGame { get; init; }
    public string? RememberedAdapterId { get; init; }
    public Func<TimeSpan>? Elapsed { get; init; }

    /// <summary>
    /// Wall-clock cap on one GUI validation session. Zero or less means no cap.
    ///
    /// docs/privacy-boundary.md requires a hard duration limit: without one, a user who pressed
    /// 开始验证 and then forgot would keep an opcode-level forensic file growing until the line
    /// cap or the machine stopped them (review finding H-1a).
    /// </summary>
    public TimeSpan MaxSessionDuration { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// How long the queue drain, the marker drain and Dispose may each block during teardown.
    ///
    /// Never infinite: a full disk or a trace file locked by an antivirus scanner would keep
    /// <c>CollectorHost.Dispose</c> from ever returning, so the Collector would not exit and
    /// the named pipe would not be released (review finding M-14).
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Newest trace sessions kept on disk.</summary>
    public int RetainedTraces { get; init; } = DefaultRetainedTraces;

    /// <summary>Days a trace session is kept regardless of count.</summary>
    public int TraceRetentionDays { get; init; } = DefaultTraceRetentionDays;

    /// <summary>Newest trace sessions kept when nothing else is configured.</summary>
    public const int DefaultRetainedTraces = 10;

    /// <summary>Days a trace session is kept when nothing else is configured.</summary>
    public const int DefaultTraceRetentionDays = 7;
}

/// <summary>
/// 主机持有的被动验证会话。单个后台工作流拥有源、队列和文件，IPC 只读取状态或发出停止请求。
/// 等待阶段也持有互斥租约；停止、完全排空、关闭文件和写入摘要哈希后才归还租约。
/// 验证消息只交给脱敏 trace sink，绝不进入正式解析器或数据库。
/// </summary>
public sealed partial class CaptureValidationController : IDisposable
{
    public const int MarkerQueueCapacity = 256;
    private readonly object _gate = new();
    private readonly CaptureValidationServices _services;
    private readonly CaptureOwnership _ownership;
    private readonly string _directory;
    private Session? _session;
    private bool _disposed;
    private string _state = "IDLE", _reason = "IDLE", _message = "尚未开始验证。";

    public CaptureValidationController(string databasePath, CaptureOwnership ownership, CaptureValidationServices? services = null)
    {
        _directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "traces");
        _ownership = ownership;
        _services = services ?? new();
        if (_services.PollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(services));
        SweepTraces();
    }

    /// <summary>Directory the forensic traces of this database live in.</summary>
    public string TraceDirectory => _directory;

    /// <summary>
    /// Deletes trace sessions beyond the retention policy: keeps the newest
    /// <see cref="CaptureValidationServices.RetainedTraces"/> and nothing older than
    /// <see cref="CaptureValidationServices.TraceRetentionDays"/> days.
    ///
    /// Called at startup and after every session; three documents describe traces as 用完即弃
    /// (review finding H-1b, spec gap P1-21). Only whole session directories written by this
    /// service are touched: they are named &lt;timestamp&gt;-&lt;guid&gt; and contain nothing
    /// the user put there.
    /// </summary>
    public void SweepTraces()
    {
        var keep = Math.Max(0, _services.RetainedTraces);
        var cutoff = _services.Trace.Clock.UtcNow.AddDays(-Math.Max(0, _services.TraceRetentionDays));

        try
        {
            if (!System.IO.Directory.Exists(_directory)) return;
            var sessions = System.IO.Directory.GetDirectories(_directory)
                .Where(path => SessionDirectoryPattern().IsMatch(Path.GetFileName(path)))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToArray();

            for (var index = 0; index < sessions.Length; index++)
            {
                var name = Path.GetFileName(sessions[index]);
                var aged = DateTimeOffset.TryParseExact(
                    name[..15], "yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal, out var written) &&
                    written < cutoff;
                if (index < keep && !aged) continue;
                try { System.IO.Directory.Delete(sessions[index], recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file still open, or a permission we do not have. Retention is
                    // best-effort; the next sweep tries again.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Trace.Logger.WriteError("capture", "trace_retention_failed", ex);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^[0-9]{8}T[0-9]{6}[0-9]{3}-[0-9a-fA-F-]{36}$")]
    private static partial System.Text.RegularExpressions.Regex SessionDirectoryPattern();

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var s = _session;
            var measured = s is not null && (s.RecordingStarted || s.ErrorCode is not null);
            return new JsonObject
            {
                ["state"] = _state, ["active"] = Active, ["reason"] = _reason, ["message"] = _message,
                ["session_id"] = s?.Id,
                ["started_at_utc"] = measured && s?.Started is { } start ? UtcTimestamp.ToText(start) : null,
                ["ended_at_utc"] = s?.Ended is { } end ? UtcTimestamp.ToText(end) : null,
                ["message_count"] = measured ? s?.Sink?.MessageCount : null, ["marker_count"] = measured ? s?.Sink?.MarkerCount : null,
                ["decode_error_count"] = measured ? s?.Sink?.DecodeErrorCount : null, ["queue_dropped"] = measured ? s?.Dropped ?? s?.Queue?.LostCount : null,
                ["truncated"] = s?.Sink?.Truncated ?? false, ["trace_path"] = measured ? s?.Path : null,
                ["sha256_path"] = s?.Digest is not null ? s.Path + CaptureTraceDigest.SidecarExtension : null,
                ["sha256"] = s?.Digest, ["error_code"] = s?.ErrorCode,
            };
        }
    }

    private bool Active => _state is "WAITING" or "RECORDING" or "STOPPING";

    public JsonObject Start(string? adapterId = null)
    {
        if (adapterId is not null && (string.IsNullOrWhiteSpace(adapterId) || adapterId.Length > 400))
            throw CollectorException.BadRequest("网卡标识必须为非空字符串，且不超过 400 字符。", "payload.adapter_id");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Active) throw CollectorException.BadRequest("验证会话仍在进行，请先停止。 ");
            var lease = _ownership.Acquire();
            try
            {
                var npcap = _services.Trace.Npcap.Detect();
                if (!npcap.Usable) throw new CollectorException(ErrorCodes.NpcapMissing, "Npcap 不可用，请安装官方 Npcap 并启用兼容模式后重试。");
                var session = new Session(adapterId ?? _services.RememberedAdapterId, lease, npcap);
                _session = session;
                SetState("WAITING", "WAITING_GAME", "仅验证，不自动记录。正在等待游戏启动。");
                var snapshot = Snapshot();
                session.Work = Task.Run(() => RunAsync(session));
                return snapshot;
            }
            catch { lease.Dispose(); throw; }
        }
    }

    public JsonObject Stop()
    {
        lock (_gate)
        {
            if (Active)
            {
                SetState("STOPPING", "STOPPING", "正在停止验证并保存本地文件……");
                _session!.Stop.Cancel();
            }
            return Snapshot();
        }
    }

    public JsonObject AddMarker(string marker)
    {
        // The allowlist is the sink's, not a fourth copy of it (review finding M-18).
        if (CaptureTraceMarkerText.Sanitize(marker) != marker)
            throw CollectorException.BadRequest(
                "标记只能为 " + string.Join(" / ", CaptureTraceMarkerText.Allowed) + "。",
                "payload.marker");
        lock (_gate)
        {
            if (_state != "RECORDING") throw CollectorException.BadRequest("仅在验证录制期间可以添加标记。");
            var s = _session!;
            if (s.AcceptedMarkers >= CaptureTraceSink.MaxMarkers ||
                !s.Markers.Writer.TryWrite(new Marker(marker, _services.Trace.Clock.UtcNow, s.Elapsed!())))
                throw CollectorException.BadRequest("标记队列或会话标记总数已满，请等待已接收标记写入。");
            s.AcceptedMarkers++;
            return Snapshot();
        }
    }

    private GameProcessDetection Locate() => _services.LocateGame?.Invoke() ?? _services.Trace.Game.Locate();

    private async Task RunAsync(Session s)
    {
        ICaptureSource? source = null;
        Observer? observer = null;
        TextWriter? writer = null;
        var trace = _services.Trace;
        try
        {
            while (!s.Stop.IsCancellationRequested)
            {
                var game = Locate();
                var adapter = Candidate(s, game);
                if (adapter is null)
                {
                    await Task.Delay(_services.PollInterval, s.Stop.Token).ConfigureAwait(false);
                    continue;
                }
                // Recheck the complete identity and TCP table immediately before source creation/start.
                var checkedGame = Locate();
                var checkedAdapter = Candidate(s, checkedGame);
                if (checkedAdapter is null || checkedGame.ProcessId != game.ProcessId ||
                    checkedGame.StartedAtUtc != game.StartedAtUtc || checkedGame.ExecutablePath != game.ExecutablePath ||
                    checkedGame.GameBuild != game.GameBuild || checkedGame.Region != game.Region ||
                    checkedAdapter.Id != adapter.Id || !Equals(checkedAdapter.BindAddress, adapter.BindAddress))
                {
                    await Task.Delay(_services.PollInterval, s.Stop.Token).ConfigureAwait(false);
                    continue;
                }
                s.Stop.Token.ThrowIfCancellationRequested();
                source = trace.SourceFactory?.Invoke() ?? new MachinaCaptureSource(trace.Logger);
                s.Stop.Token.ThrowIfCancellationRequested();
                var path = Path.Combine(_directory, trace.Clock.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + s.Id, "trace.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                lock (_gate) s.Path = path;
                writer = trace.TraceWriterFactory?.Invoke(path) ?? new StreamWriter(
                    new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                var elapsed = Stopwatch.StartNew();
                s.Elapsed = _services.Elapsed ?? (() => elapsed.Elapsed);
                var sink = new CaptureTraceSink(writer, trace.Clock, s.Elapsed);
                lock (_gate) { s.Sink = sink; s.Started = UtcTimestamp.Truncate(trace.Clock.UtcNow); }
                sink.WriteHeader(new CaptureTraceHeader(s.Started.Value, s.Npcap.Version, game.GameBuild,
                    game.Region, SanitizedDiagnosticsReport.Fingerprint(adapter.Id), trace.CollectorVersion,
                    OodleMode.FfxivTcp, source.Kind != CaptureTraceRunner.LiveSourceKind));
                s.MarkerWork = Task.Run(() => WriteMarkersAsync(s));
                var queue = new DecodedMessageQueue(sink, onSinkError: ex => Fault(s, ex, writeFailed: true));
                lock (_gate) s.Queue = queue;
                s.Stop.Token.ThrowIfCancellationRequested();
                observer = new Observer(queue, sink, ex => Fault(s, ex));
                try { source.Start(new CaptureStartOptions(s.Id, game.ProcessId!.Value, adapter.BindAddress, adapter.Id,
                    OodleMode.FfxivTcp, null, game.ExecutablePath, game.Region, game.GameBuild,
                    AllowCandidateOodleSignature: true), observer); }
                catch (Exception ex) { Fault(s, ex); }
                lock (_gate)
                {
                    if (!s.Stop.IsCancellationRequested)
                    {
                        s.RecordingStarted = true;
                        SetState("RECORDING", "RECORDING", "仅验证，不自动记录。正在保存脱敏取证，可添加事件标记。");
                    }
                }
                var deadline = _services.MaxSessionDuration > TimeSpan.Zero
                    ? s.Elapsed!() + _services.MaxSessionDuration
                    : (TimeSpan?)null;
                while (!s.Stop.IsCancellationRequested)
                {
                    await Task.Delay(_services.PollInterval, s.Stop.Token).ConfigureAwait(false);
                    if (deadline is { } limit && s.Elapsed!() >= limit)
                    {
                        // The documented hard duration cap. Reaching it is a normal end, not a
                        // failure: the file written so far stays and stays citable.
                        lock (_gate) if (Active) SetState(
                            "STOPPING", "STOPPING", "已达到单次验证的时长上限，正在停止并保存本地文件……");
                        break;
                    }

                    var current = Locate();
                    if (!current.Running || current.ProcessId != game.ProcessId || current.StartedAtUtc != game.StartedAtUtc) break;
                }
                break;
            }
        }
        catch (OperationCanceledException) when (s.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Fault(s, ex, writeFailed: true); }
        finally
        {
            lock (_gate) SetState("STOPPING", "STOPPING", "正在停止验证并保存本地文件……");
            // Each cleanup is attempted even if the previous operation failed. Hashing can only
            // run after source disposal, full queue drain and successful writer close.
            Cleanup(() => source?.Stop(), s);
            var released = Cleanup(() => source?.Dispose(), s);
            observer?.Close();
            // Bounded, not infinite: a blocked write must cost a truncated trace, never a
            // Collector that cannot exit (review finding M-14).
            if (s.Queue is { } draining && !draining.Complete(_services.ShutdownTimeout))
            {
                Fault(s, new TimeoutException("取证写入尚未退出，保留会话与文件等待收尾。"), writeFailed: true);
                // This continuation owns the writer and lease while a disk call is blocked.
                // Stop/Dispose remain bounded; neither may expose or reuse these resources.
                await draining.Completion.ConfigureAwait(false);
            }
            s.Markers.Writer.TryComplete();
            if (s.MarkerWork is not null)
            {
                if (await Task.WhenAny(s.MarkerWork, Task.Delay(_services.ShutdownTimeout)) != s.MarkerWork)
                    Fault(s, new TimeoutException("取证标记写入尚未退出，保留文件等待收尾。"), writeFailed: true);
                await s.MarkerWork.ConfigureAwait(false);
            }
            lock (_gate) s.Dropped = s.Queue?.LostCount;
            Cleanup(() => s.Sink?.WriteSummary(s.Dropped ?? 0), s);
            Cleanup(() => s.Queue?.Dispose(), s);
            Cleanup(() => writer?.Dispose(), s);
            if (!s.RecordingStarted && s.ErrorCode is null && s.Path is { } cancelledPath)
            {
                // The service exclusively created this unique directory. Cancellation before
                // startup succeeds removes only its own uncommitted trace, never user input.
                if (Cleanup(() => { File.Delete(cancelledPath); Directory.Delete(Path.GetDirectoryName(cancelledPath)!); }, s))
                    lock (_gate) { s.Path = null; s.Sink = null; s.Queue = null; s.Dropped = null; s.Started = null; }
            }
            if (writer is not null && s.Path is not null && !s.WriteFailed)
                Cleanup(() => { var digest = CaptureTraceDigest.WriteSidecar(s.Path); lock (_gate) s.Digest = digest; }, s);
            lock (_gate)
            {
                s.Ended = UtcTimestamp.Truncate(trace.Clock.UtcNow);
                s.Finalized = true;
                if (released) s.Lease.Dispose();
                else s.RetainedSource = source;
                s.Stop.Dispose();
                if (s.ErrorCode is not null) SetState("FAILED", "FAILED", "验证失败，文件可能不完整。详情见本机诊断日志。");
                else if (s.Path is null) SetState("COMPLETED", "CANCELLED", "已取消等待，未创建取证文件。");
                else SetState("COMPLETED", "COMPLETED", "取证文件已保存到本机。真实 FF14 / Oodle 仍未验证。");
            }

            // Retention runs after the session is finalized, so this session's own directory
            // is already complete and counted.
            SweepTraces();
        }
    }

    private CaptureAdapterView? Candidate(Session s, GameProcessDetection game)
    {
        if (!game.Running || game.ProcessId is not > 0) return Wait("WAITING_GAME", "正在等待游戏启动。");
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || string.IsNullOrWhiteSpace(game.GameBuild))
            return Wait("WAITING_IDENTITY", "正在等待可读取的游戏路径与客户端版本。");
        if (game.Region == Region.Unknown)
            // Not the same thing as "still initialising": the path was read and simply does
            // not say which service region it is. Waiting will never fix that, so the message
            // must not suggest it will (review finding H-9).
            return Wait("WAITING_REGION",
                "无法从安装路径判断区服。请在设置里手动指定区服（国服 / 国际服）后重试。");
        var adapters = _services.Trace.Adapters.List(game.ProcessId, s.AdapterId);
        if (s.AdapterId is null && adapters.Count(a => a.CarriesGameTraffic && a.IsUp && !a.IsLoopback &&
                GameTcpConnectionProbe.IsUsableLocalAddress(a.BindAddress)) > 1)
            return Wait("WAITING_ADAPTER", "多张网卡承载游戏连接，请取消并明确选择网卡。");
        var selected = s.AdapterId is not null ? AdapterEnumerator.Find(adapters, s.AdapterId) :
            adapters.Where(a => a.Recommended).Take(2).ToArray() is { Length: 1 } recommended ? recommended[0] : null;
        if (selected is null || !selected.IsUp || selected.IsLoopback || !GameTcpConnectionProbe.IsUsableLocalAddress(selected.BindAddress))
            return Wait("WAITING_ADAPTER", "等待可用 IPv4 网卡；无法自动确定时，请取消并明确选择网卡。");
        s.AdapterId ??= selected.Id;
        if (s.BlockedPids.Contains(game.ProcessId.Value)) return Wait("WAITING_RESTART", "游戏已有连接，请退出并重新启动游戏，验证将自动继续。");
        var count = _services.Trace.TcpConnectionCounter(game.ProcessId.Value, selected.BindAddress);
        if (count is null or < 0) return Wait("WAITING_CONNECTION_CHECK", "无法确认游戏连接状态，正在等待系统查询恢复。");
        if (count > 0)
        {
            s.BlockedPids.Add(game.ProcessId.Value);
            return Wait("WAITING_RESTART", "游戏已有连接，请退出并重新启动游戏，验证将自动继续。");
        }
        return selected;
    }

    private async Task WriteMarkersAsync(Session s)
    {
        // No controller gate is held while waiting for the sink lock or disk. Closing the
        // channel drains every accepted item even when the session Stop token was cancelled.
        await foreach (var marker in s.Markers.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (!s.Sink!.WriteMarker(marker.Text, marker.ReceivedAtUtc, marker.Elapsed))
                    throw new IOException("已接收标记未能写入取证文件。");
            }
            catch (Exception ex) { Fault(s, ex, writeFailed: true); }
        }
    }

    private CaptureAdapterView? Wait(string reason, string message)
    {
        lock (_gate) if (_state == "WAITING") SetState("WAITING", reason, "仅验证，不自动记录。" + message);
        return null;
    }

    private void Fault(Session s, Exception? error, bool writeFailed = false)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, s) || s.Finalized) return;
            s.ErrorCode = ErrorCodes.Internal;
            s.WriteFailed |= writeFailed;
            SetState("STOPPING", "STOPPING", "验证遇到错误，正在停止并保存可用证据……");
            s.Stop.Cancel();
        }
        _services.Trace.Logger.WriteError("capture", "validation_failed", error);
    }

    private bool Cleanup(Action action, Session s)
    {
        try { action(); return true; }
        catch (Exception ex) { Fault(s, ex, writeFailed: true); return false; }
    }

    private void SetState(string state, string reason, string message) { _state = state; _reason = reason; _message = message; }

    public void Dispose()
    {
        Task? work;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            work = _session?.Work;
        }
        // Bounded: a session stuck on a write must not keep the process alive for ever.
        work?.Wait(_services.ShutdownTimeout);
        if (_session?.RetainedSource is { } retained)
        {
            try
            {
                try { retained.Stop(); } finally { retained.Dispose(); }
                _session.RetainedSource = null;
                _session.Lease.Dispose();
            }
            catch (Exception ex) { _services.Trace.Logger.WriteError("capture", "validation_release_failed", ex); }
        }
    }

    private sealed class Session(string? adapterId, IDisposable lease, NpcapDetection npcap)
    {
        public readonly string Id = Guid.NewGuid().ToString("D");
        public readonly IDisposable Lease = lease;
        public readonly NpcapDetection Npcap = npcap;
        public readonly CancellationTokenSource Stop = new();
        public readonly HashSet<int> BlockedPids = new();
        public readonly Channel<Marker> Markers = Channel.CreateBounded<Marker>(new BoundedChannelOptions(MarkerQueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
        public int AcceptedMarkers;
        public Func<TimeSpan>? Elapsed;
        public string? AdapterId = adapterId, Path, Digest, ErrorCode;
        public DateTimeOffset? Started, Ended;
        public CaptureTraceSink? Sink;
        public DecodedMessageQueue? Queue;
        public long? Dropped;
        public bool WriteFailed;
        public bool Finalized, RecordingStarted;
        public ICaptureSource? RetainedSource;
        public Task? Work, MarkerWork;
    }

    private sealed record Marker(string Text, DateTimeOffset ReceivedAtUtc, TimeSpan Elapsed);

    private sealed class Observer(DecodedMessageQueue queue, CaptureTraceSink sink, Action<Exception?> fault) : ICaptureSourceObserver
    {
        private readonly object _gate = new();
        private bool _closed;
        public void Close() { lock (_gate) _closed = true; }
        public void OnMessage(DecodedMessage message) { lock (_gate) if (!_closed) queue.Offer(message); }
        public void OnDecodeError() { lock (_gate) if (!_closed) sink.CountDecodeError(); }
        public void OnFault(string reason, Exception? error)
        {
            lock (_gate) if (_closed) return;
            // Fault takes the controller gate; never hold the observer gate across it.
            fault(error);
        }
    }
}
