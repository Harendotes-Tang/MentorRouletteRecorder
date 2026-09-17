using System.Buffers.Binary;
using System.Diagnostics;
using Machina.Infrastructure;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Read-only Npcap ingress with bounded first-packet handoff into Machina IP/TCP/bundle decoding.
/// Prepare opens the selected device before Oodle work. Only OS-confirmed full process tuples
/// with observed stream starts reach independent direction decoders; there is no injected hook,
/// raw socket fallback, packet output or raw capture file. This source preserves the existing
/// framing and observer contract and releases Oodle only after all native/decoder workers stop.
/// </summary>
public sealed class MachinaCaptureSource : ICaptureSource
{
    /// <summary>Trace fragments from Machina that mean this capture will never produce data.</summary>
    private static readonly string[] FatalTraceMarkers =
    {
        "Cannot load",
        "Unable to retrieve network data",
        "PcapException",
        "Error opening",
        "Cannot find one or more signatures",
    };

    /// <summary>
    /// Trace fragments from Machina's bundle decoder that mean a bundle could not be
    /// decompressed.
    ///
    /// These are Machina's own internal debug strings, matched as text because Machina 2.4.7.7
    /// reports a decompression failure by writing a trace line and returning null -- it never
    /// raises the message, so <c>OnMessage</c> can never count it. Depending on another
    /// project's debug wording is guarded rather than hoped for:
    /// <c>OodleSignatureProfileTests.MachinaDecodeFailureTraceContractIsPinnedToTheReferencedPackage</c>
    /// fails the build when the referenced package is no longer the version these literals were
    /// read from. Without that gate a reworded upstream string would leave
    /// <c>decode_error_count</c> stuck at 0 and a session that was in fact all failures would
    /// read as clean evidence (review finding H-3).
    /// </summary>
    public static readonly IReadOnlyList<string> DecodeFailureTraceMarkers = new[]
    {
        "FFXIVBundleDecoder: Oodle Decompression failure",
        "FFXIVBundleDecoder: Oodle Decompression error",
        "FFXIVBundleDecoder: Decompression error",
    };

    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly object _lifecycleGate = new();
    private readonly OodleTempCopyCleaner _cleaner;
    private readonly RotatingFileLogger _logger;
    private readonly Domain.Time.IClock _clock;
    private OodleSignatureRuntime? _oodleSignatureRuntime;
    private IMachinaMonitor? _monitor;
    private readonly Func<CaptureStartOptions, IMachinaMonitor>? _monitorFactory;
    private MachinaTraceListener? _traceListener;
    private ICaptureSourceObserver? _observer;
    private CaptureStartOptions? _options;
    /// <summary>Rejected-frame header samples written per source lifetime.</summary>
    private const int FramingRejectionSamples = 8;

    /// <summary>Machina trace lines written verbatim per kind, per source lifetime.</summary>
    internal const int MonitorTraceSamples = 8;

    /// <summary>How often the suppressed trace counts are flushed as one aggregate record.</summary>
    internal const int MonitorTraceSummaryIntervalMs = 30_000;

    private readonly object _traceGate = new();
    private readonly long[] _traceSeen = new long[3];
    private readonly long[] _traceSuppressed = new long[3];
    private long _traceSummaryAtMs = Environment.TickCount64;

    private int _framingRejectionSampleLogged;
    private int _preexistingTcpConnections = -1;
    private bool _disposed;
    private bool _monitorStopped, _monitorDisposed;

    /// <summary>Creates a source.</summary>
    /// <param name="logger">Local diagnostic log; never receives payload bytes.</param>
    /// <param name="cleaner">Temp-copy cleaner; a default one over the temp folder when null.</param>
    /// <param name="clock">
    /// Clock every observation is stamped with. One clock, deliberately: the lifecycle events
    /// and crash recovery already read this machine's clock, and mixing them with the server's
    /// bundle epoch makes an ordinary finish look like a run that ended before it started
    /// (review finding H-8).
    /// </param>
    /// <param name="oodleTempManifestPath">
    /// Manifest of temp copies this capture owns; null keeps the copies in memory only.
    /// </param>
    public MachinaCaptureSource(
        RotatingFileLogger? logger = null,
        OodleTempCopyCleaner? cleaner = null,
        Domain.Time.IClock? clock = null,
        string? oodleTempManifestPath = null)
    {
        _logger = logger ?? RotatingFileLogger.Disabled;
        _clock = clock ?? Domain.Time.SystemClock.Instance;
        _cleaner = cleaner ?? new OodleTempCopyCleaner(
            null,
            (eventName, count) => _logger.Write(
                LogLevel.Info, "capture", eventName, new Dictionary<string, object?> { ["count"] = count }),
            oodleTempManifestPath);
    }

    internal MachinaCaptureSource(Func<CaptureStartOptions, IMachinaMonitor> monitorFactory)
        : this() => _monitorFactory = monitorFactory;

    /// <inheritdoc />
    public string Kind => _monitorFactory is null ? "machina-npcap" : "synthetic-machina-adapter";

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _monitor is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool ReadsGameExecutable { get; private set; }

    /// <inheritdoc />
    public int? PreexistingTcpConnections => ConnectionCountForLog();

    /// <inheritdoc />
    /// <remarks>
    /// The monitor reference is taken under <c>_gate</c> and released again before its counters
    /// are read, and that ordering is load-bearing. The decode worker calls Machina while
    /// holding the reassembly buffer's own lock, and Machina reports a decompression failure
    /// through <see cref="Trace"/>, which re-enters this class and takes <c>_gate</c>. Reading
    /// the buffer's counters from inside <c>_gate</c> would let one thread hold the buffer lock
    /// and want this one while another holds this one and wants the buffer lock: a hang of the
    /// whole collector, on the exact path a silent capture is being diagnosed from.
    /// </remarks>
    public CaptureIngressCounters IngressCounters
    {
        get
        {
            IMachinaMonitor? monitor;
            lock (_gate)
            {
                monitor = _monitor;
            }

            return monitor?.Ingress ?? CaptureIngressCounters.Empty;
        }
    }

    /// <inheritdoc />
    public OodleSignatureUse SignatureUse
    {
        get
        {
            lock (_gate)
            {
                if (_monitor is null)
                {
                    return OodleSignatureUse.None;
                }

                return _oodleSignatureRuntime is { } runtime
                    ? new OodleSignatureUse(runtime.SourceToken, runtime.Profile.Id, runtime.Profile.Status)
                    : OodleSignatureUse.Builtin;
            }
        }
    }

    /// <summary>Number of Machina trace lines seen since the last start.</summary>
    public long MonitorMessageCount => _traceListener?.Count ?? 0;

    /// <inheritdoc />
    public void Start(CaptureStartOptions options, ICaptureSourceObserver observer)
    {
        lock (_lifecycleGate) StartCore(options, observer);
    }

    private void StartCore(CaptureStartOptions options, ICaptureSourceObserver observer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_monitor is not null)
            {
                throw new CollectorException(
                    ErrorCodes.CaptureAlreadyRunning, "抓包已在运行，无需重复启动。");
            }

            _observer = observer;
            _options = options;
            _framingRejectionSampleLogged = 0;
            lock (_traceGate)
            {
                Array.Clear(_traceSeen);
                Array.Clear(_traceSuppressed);
                _traceSummaryAtMs = Environment.TickCount64;
            }

            Volatile.Write(
                ref _preexistingTcpConnections,
                GameTcpConnectionProbe.TryCount(options.ProcessId, options.BindAddress) ?? -1);
            ReadsGameExecutable = options.Oodle == OodleMode.FfxivTcp;
        }

        if (ReadsGameExecutable)
        {
            // Enable registration of the exact native-owned copy path during initialization.
            // Directory contents, file lengths and hashes cannot establish ownership.
            _cleaner.Arm(options.GameExecutablePath);
        }

        var monitor = _monitorFactory?.Invoke(options) ?? Configure(options);
        lock (_gate) _monitor = monitor;
        _monitorStopped = _monitorDisposed = false;
        try
        {
            monitor.Prepare();
            // Own both resources before startup can fail. A rollback failure must leave
            // references on the source so its owner can retry instead of releasing Oodle.
            var signatureRuntime = OodleSignatureRuntime.TryCreate(options, logger: _logger, cleaner: _cleaner);
            _oodleSignatureRuntime = signatureRuntime;
            try
            {
                signatureRuntime?.Install();
            }
            catch (CollectorException ex) when (signatureRuntime is { IsPatternFallback: true })
            {
                // A donor that fit the file but not the mapped image is not a reason to
                // refuse capture: the built-in table is the same answer we had before.
                _logger.WriteError("capture", "oodle_pattern_fallback_failed", ex);
                signatureRuntime.Dispose();
                _oodleSignatureRuntime = null;
            }

            // Attach after profile initialization so bootstrap fallback traces are not
            // mistaken for a live-monitor fault.
            var listener = new MachinaTraceListener(OnMonitorTrace);
            Trace.Listeners.Add(listener);
            _traceListener = listener;
            monitor.Start();
        }
        catch (Exception ex)
        {
            try { StopCore(); }
            catch (Exception cleanupError)
            {
                throw new CollectorException(ErrorCodes.Internal,
                    "启动失败且原生采集资源未能释放，已保留资源供停止操作重试。", inner: new AggregateException(ex, cleanupError));
            }
            throw Translate(ex, options.AdapterId);
        }

        _logger.Write(LogLevel.Info, "capture", "monitor_started", new Dictionary<string, object?>
        {
            ["monitor_type"] = nameof(NetworkMonitorType.WinPCap),
            ["injected_hook_enabled"] = false,
            ["oodle_mode"] = options.Oodle.ToString(),
            ["reads_game_executable"] = ReadsGameExecutable,
            ["process_id"] = options.ProcessId,
            ["preexisting_tcp_connections"] = ConnectionCountForLog(),
            ["remote_ip_filter"] = false,
        });
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lifecycleGate) StopCore();
        // Machina copies the game executable into its own temp folder on every start; the
        // copy of a start that faulted is not registered anywhere and would stay forever.
        var swept = _cleaner.SweepOrphansHere((name, count) =>
            _logger.Write(LogLevel.Info, "capture", name, new Dictionary<string, object?> { ["removed"] = count }));
        if (swept.Locked > 0)
        {
            _logger.Write(LogLevel.Info, "capture", "oodle_temp_orphans_locked",
                new Dictionary<string, object?> { ["locked"] = swept.Locked });
        }
    }

    private void StopCore()
    {
        IMachinaMonitor? monitor;
        OodleSignatureRuntime? signatureRuntime;
        lock (_gate)
        {
            monitor = _monitor;
            signatureRuntime = _oodleSignatureRuntime;
            _observer = null;
            _options = null;
        }

        if (monitor is null)
        {
            signatureRuntime?.Dispose();
            _oodleSignatureRuntime = null;
            return;
        }

        int removed;
        try
        {
            Detach(monitor);
            signatureRuntime?.Dispose();

            // Only a fully released monitor forgets its resources. A failure deliberately
            // leaves both references on the source so its owner can retry rather than
            // proceeding to release Oodle while a live monitor may still be using it.
            lock (_gate)
            {
                _monitor = null;
                _oodleSignatureRuntime = null;
            }
        }
        finally
        {
            // The sweep and the trace listener are independent of whether the native monitor
            // could be released, and both are promises this project has made: the temp copy of
            // the game executable must be gone (docs/privacy-boundary.md section 4.2), and a
            // listener left on the process-wide Trace.Listeners keeps a dead capture source
            // reachable and still receiving. A monitor.Stop() that keeps throwing must not be
            // able to skip either of them forever (review finding H-8). The cleaner stays armed
            // until the monitor really is gone, so a copy still locked by the loader is swept
            // again on the next attempt instead of being forgotten.
            RemoveTraceListener();
            FlushMonitorTraceSummary();
            removed = SafeSweep(disarm: _monitorStopped && _monitorDisposed);
        }

        _logger.Write(LogLevel.Info, "capture", "monitor_stopped", new Dictionary<string, object?>
        {
            ["oodle_temp_copies_removed"] = removed,
        });
    }

    private IMachinaMonitor Configure(CaptureStartOptions options) => new FirstPacketMonitor(options,
        (connection, epoch, bytes, inbound) => OnMessage(connection, epoch, bytes,
            inbound ? MessageDirection.Inbound : MessageDirection.Outbound),
        reason => { ICaptureSourceObserver? sink; lock (_gate) sink = _observer; sink?.OnFault(reason, null); },
        cleaner: _cleaner,
        connectionClosed: OnConnectionClosed);

    /// <summary>
    /// One connection that had been delivering decoded game messages is over: it was closed
    /// (FIN/RST) or the operating system no longer lists it for the game process. Reported so
    /// the state machine can reach DISCONNECTED, which the live path otherwise has no producer
    /// for (review finding H-6).
    /// </summary>
    private void OnConnectionClosed()
    {
        ICaptureSourceObserver? observer;
        lock (_gate)
        {
            observer = _observer;
        }

        _logger.Write(LogLevel.Warn, "capture", "game_connection_closed", new Dictionary<string, object?>());
        observer?.OnConnectionClosed();
    }

    private void OnMessage(TCPConnection? connection, long epoch, byte[]? message, MessageDirection direction)
    {
        ICaptureSourceObserver? observer;
        CaptureStartOptions? options;
        lock (_gate)
        {
            observer = _observer;
            options = _options;
        }

        if (observer is null || options is null)
        {
            return;
        }

        try
        {
            if (message is null || !FfxivFraming.TryRead(message, out var frame))
            {
                observer.OnDecodeError();
                LogFramingRejection(message, direction);
                return;
            }

            // The payload is copied out of Machina's buffer immediately: the buffer is reused
            // by the capture loop, and nothing downstream may hold a reference to it.
            var payload = new byte[frame.PayloadLength];
            if (frame.PayloadLength > 0)
            {
                Array.Copy(message, frame.PayloadOffset, payload, 0, frame.PayloadLength);
            }

            observer.OnMessage(new DecodedMessage(
                options.CaptureSessionId,
                direction,
                // This machine's clock, never the bundle epoch. The epoch travels on as a
                // diagnostic field (DecodedMessage.Epoch) so a clock comparison is still
                // possible, but it must not be the wall clock a run's own timestamps are
                // built from: those are compared against lifecycle events and recovery
                // timestamps, which come from here (review finding H-8).
                _clock.UtcNow,
                _mono.Elapsed,
                epoch,
                frame.SegmentType,
                frame.Opcode,
                payload,
                Key(options.CaptureSessionId, connection)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A malformed message is data, not a bug in the caller. Count it and carry on;
            // throwing back into Machina's capture loop would end the capture.
            observer.OnDecodeError();
            _logger.WriteError("capture", "decode_failed", ex);
        }
    }

    /// <summary>
    /// Logs one content-free structural sample when Machina delivered a message that our
    /// framing gate rejected. The callback bytes and all decoded fields are deliberately
    /// excluded; the event only records whether capture may have attached mid-connection.
    /// </summary>
    private void LogFramingRejection(byte[]? message, MessageDirection direction)
    {
        // A handful of samples per source, not one: the first rejection alone cannot tell a
        // midstream garbage stream (random bytes) from a systematic framing mismatch (a
        // consistent, plausible-looking header the reader nevertheless refuses).
        if (Interlocked.Increment(ref _framingRejectionSampleLogged) > FramingRejectionSamples)
        {
            return;
        }

        var length = message?.Length ?? 0;
        var preexistingConnections = Volatile.Read(ref _preexistingTcpConnections);
        var fields = new Dictionary<string, object?>
        {
            ["direction"] = direction.ToString(),
            ["buffer_length"] = length,
            ["preexisting_tcp_connections"] =
                preexistingConnections >= 0 ? preexistingConnections : null,
            ["midstream_start_possible"] = preexistingConnections > 0,
        };

        // Structural header fields only (declared length, segment type, the first four
        // bytes): enough to see whether a segment header is there at all. Never the body.
        if (message is { Length: >= FfxivFraming.SegmentHeaderBytes })
        {
            fields["declared_length"] = BinaryPrimitives.ReadUInt32LittleEndian(message.AsSpan(0, 4));
            fields["segment_type"] = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(12, 2));
            fields["head4_hex"] = Convert.ToHexString(message.AsSpan(0, 4)).ToLowerInvariant();
        }

        _logger.Write(LogLevel.Warn, "capture", "framing_rejected", fields);
    }

    private int? ConnectionCountForLog()
    {
        var count = Volatile.Read(ref _preexistingTcpConnections);
        return count >= 0 ? count : null;
    }

    private static string Key(string captureSessionId, TCPConnection? connection) =>
        connection is null
            ? ConnectionKey.From(captureSessionId, 0, 0, 0, 0)
            : ConnectionKey.From(
                captureSessionId,
                connection.LocalIP,
                connection.LocalPort,
                connection.RemoteIP,
                connection.RemotePort);

    /// <summary>
    /// Turns one Machina trace line into at most one log record.
    ///
    /// Sampled like <see cref="LogFramingRejection"/>: Oodle's TCP decompressor is stateful, so
    /// one lost segment makes every later compressed bundle on that direction fail, and logging
    /// each failure is a synchronous disk write on the decode thread while it holds the
    /// process-wide trace gate. Forty minutes of that rotates away the very log the failure
    /// would have to be diagnosed from (review finding M-6). The first
    /// <see cref="MonitorTraceSamples"/> lines of each kind are written verbatim; after that
    /// only a periodic count is.
    /// </summary>
    /// <param name="text">Raw trace line from Machina.</param>
    private void OnMonitorTrace(string text)
    {
        var sanitized = RotatingFileLogger.Sanitize(text);
        var fatal = Array.Exists(
            FatalTraceMarkers, marker => sanitized.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
            (sanitized.Contains("OodleNative_Ffxiv: ffxiv_dx11 executable at path", StringComparison.OrdinalIgnoreCase) &&
             sanitized.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        // Bundle decompression fails upstream of MessageReceived/MessageSent. Machina 2.4.7.7
        // only emits a trace and returns null, so OnMessage cannot count those failures.
        // Do not count the following "Resetting stream" line a second time.
        var decodeError = false;
        foreach (var marker in DecodeFailureTraceMarkers)
        {
            if (sanitized.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                decodeError = true;
                break;
            }
        }

        var kind = fatal ? "fatal" : decodeError ? "decode_error" : "diagnostic";
        if (SampleMonitorTrace(kind))
        {
            _logger.Write(
                fatal ? LogLevel.Error : decodeError ? LogLevel.Warn : LogLevel.Info,
                "capture",
                "monitor_trace",
                // Upstream trace strings may include packet hex, addresses and paths. Only
                // categorical severity crosses the local logger boundary.
                new Dictionary<string, object?> { ["kind"] = kind });
        }

        if (!fatal && !decodeError)
        {
            return;
        }

        ICaptureSourceObserver? observer;
        lock (_gate)
        {
            observer = _observer;
        }

        if (decodeError)
        {
            observer?.OnDecodeError();
        }

        if (!fatal)
        {
            return;
        }

        // Machina reports its own thread's failures through Trace rather than by throwing
        // into ours. Without this, a monitor that died on startup would sit at zero packets
        // and look merely idle.
        observer?.OnFault("抓包监视器报告了致命错误，抓包已停止。详情见本机诊断日志。", null);
    }

    /// <summary>
    /// Decides whether this trace line is written verbatim, and folds the rest into the
    /// periodic summary. True for at most <see cref="MonitorTraceSamples"/> lines per kind per
    /// source lifetime; suppressed lines are counted and flushed as one aggregate record every
    /// <see cref="MonitorTraceSummaryIntervalMs"/>.
    /// </summary>
    /// <param name="kind">Categorical severity of the line.</param>
    private bool SampleMonitorTrace(string kind)
    {
        long[] pending;
        int index = kind switch { "fatal" => 0, "decode_error" => 1, _ => 2 };
        lock (_traceGate)
        {
            if (++_traceSeen[index] <= MonitorTraceSamples)
            {
                return true;
            }

            _traceSuppressed[index]++;
            var now = Environment.TickCount64;
            if (now - _traceSummaryAtMs < MonitorTraceSummaryIntervalMs)
            {
                return false;
            }

            _traceSummaryAtMs = now;
            pending = (long[])_traceSuppressed.Clone();
            Array.Clear(_traceSuppressed);
        }

        _logger.Write(LogLevel.Info, "capture", "monitor_trace_summary", new Dictionary<string, object?>
        {
            ["fatal"] = pending[0],
            ["decode_error"] = pending[1],
            ["diagnostic"] = pending[2],
            ["interval_ms"] = MonitorTraceSummaryIntervalMs,
        });
        return false;
    }

    /// <summary>Flushes any suppressed trace counts that never reached a summary interval.</summary>
    private void FlushMonitorTraceSummary()
    {
        long[] pending;
        lock (_traceGate)
        {
            if (_traceSuppressed[0] + _traceSuppressed[1] + _traceSuppressed[2] == 0)
            {
                return;
            }

            pending = (long[])_traceSuppressed.Clone();
            Array.Clear(_traceSuppressed);
            Array.Clear(_traceSeen);
            _traceSummaryAtMs = Environment.TickCount64;
        }

        _logger.Write(LogLevel.Info, "capture", "monitor_trace_summary", new Dictionary<string, object?>
        {
            ["fatal"] = pending[0],
            ["decode_error"] = pending[1],
            ["diagnostic"] = pending[2],
            ["interval_ms"] = MonitorTraceSummaryIntervalMs,
        });
    }

    private void Detach(IMachinaMonitor monitor)
    {
        monitor.DetachCallbacks();
        // Successful stages are remembered; a failure never discards the native monitor or
        // proceeds to release Oodle while its monitor may still be using that global state.
        if (!_monitorStopped) { monitor.Stop(); _monitorStopped = true; }
        if (!_monitorDisposed) { monitor.Dispose(); _monitorDisposed = true; }
    }

    private void RemoveTraceListener()
    {
        if (_traceListener is not { } listener)
        {
            return;
        }

        _traceListener = null;
        try
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.WriteError("capture", "trace_listener_remove_failed", ex);
        }
    }

    private int SafeSweep(bool disarm)
    {
        try
        {
            var removed = _cleaner.Sweep();
            if (disarm)
            {
                _cleaner.Disarm();
            }

            return removed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.WriteError("capture", "oodle_temp_sweep_failed", ex);
            return 0;
        }
    }

    internal static CollectorException Translate(Exception error, string? adapterId = null)
    {
        // "The card is not in the list" is not "Npcap is missing": translating it that way
        // tells the user to reinstall a working driver and makes the follow poll back off for
        // thirty seconds when all that is needed is another look at the device list
        // (review finding H-1).
        if (error is CollectorException refusal)
        {
            return refusal;
        }

        if (error.Message.Contains(NpcapPacketReader.AdapterListChangedMessage, StringComparison.Ordinal))
        {
            return NpcapPacketReader.AdapterListChanged(adapterId);
        }

        // Machina raises its pcap failures as ApplicationException subclasses. The user-facing
        // answer is the same for all of them: Npcap is not usable from here.
        var message = error.GetType().Name.Contains("Pcap", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("pcap", StringComparison.OrdinalIgnoreCase)
            ? "无法通过 Npcap 打开网卡。请确认 Npcap 已安装并勾选了 WinPcap 兼容模式，" +
              "必要时以管理员身份运行本软件。"
            : "启动抓包监视器失败，抓包未开始。详情见本机诊断日志。";

        return new CollectorException(
            ErrorCodes.NpcapMissing,
            message,
            new Dictionary<string, object?> { ["monitor"] = "START_FAILED" },
            inner: error);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            StopCore();
            _disposed = true;
        }
    }

    /// <summary>
    /// Forwards Machina's <see cref="Trace"/> output to a callback. Machina reports failures
    /// on its own threads this way rather than by throwing, so this is the only way to notice
    /// a monitor that died after a successful start.
    /// </summary>
    private sealed class MachinaTraceListener : TraceListener
    {
        private readonly Action<string> _onLine;
        private long _count;

        public MachinaTraceListener(Action<string> onLine) => _onLine = onLine;

        public long Count => Interlocked.Read(ref _count);

        public override void Write(string? message) => Forward(message);

        public override void WriteLine(string? message) => Forward(message);

        private void Forward(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // Only Machina's own components are of interest; anything else on the process's
            // trace listeners belongs to somebody else.
            if (!message.Contains("Machina", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("FFXIV", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("Oodle", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("PCap", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Interlocked.Increment(ref _count);
            try
            {
                _onLine(message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // A diagnostic path must never be able to break the thread it observes.
            }
        }
    }
}
