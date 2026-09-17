using System.Buffers.Binary;
using Machina.FFXIV.Oodle;
using Machina.Infrastructure;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Prepare starts native reads before Oodle work; Start enables ownership polling/decoding.
/// Stop joins both owned workers and retains resources on timeout, permitting safe retry.
/// </summary>
internal sealed class FirstPacketMonitor : IMachinaMonitor
{
    private readonly INpcapPacketReader _reader;
    private readonly FirstPacketBuffer _buffer;
    private readonly Func<IReadOnlyCollection<TCPConnection>> _connections;
    private readonly Action _initialize;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ManualResetEventSlim _readerReady = new();
    private Action<string>? _fault;
    private Action<TCPConnection, long, byte[], bool>? _message;
    private Action? _connectionClosed;

    /// <summary>
    /// Connections that produced at least one decoded message.
    ///
    /// A connection ending is evidence about a run only if it was carrying the run: before
    /// login the client opens and closes lobby connections constantly, and reporting those
    /// would turn every ordinary login into a lost connection (review finding H-6). Bounded by
    /// <see cref="MaxTrackedConnections"/> so churn cannot grow the set without limit.
    /// </summary>
    private readonly HashSet<(uint LocalIP, ushort LocalPort, uint RemoteIP, ushort RemotePort)> _delivered = new();

    /// <summary>Upper bound on connections remembered as having delivered messages.</summary>
    internal const int MaxTrackedConnections = 256;
    private Thread? _readThread, _decodeThread;
    private int _faulted;
    private bool _disposed;
    private readonly TimeSpan _joinTimeout;
    private readonly OodleTempCopyCleaner _cleaner;
    private IOodleNative? _ownedNative;

    internal FirstPacketMonitor(CaptureStartOptions options,
        Action<TCPConnection, long, byte[], bool> message, Action<string> fault,
        INpcapPacketReader? reader = null, Func<IReadOnlyCollection<TCPConnection>>? connections = null,
        Action? initialize = null, TimeSpan? joinTimeout = null, OodleTempCopyCleaner? cleaner = null,
        Action? connectionClosed = null)
    {
        _cleaner = cleaner ?? new OodleTempCopyCleaner();
        if (cleaner is null) _cleaner.Arm(options.GameExecutablePath);
        _reader = reader ?? new NpcapPacketReader(options);
        _message = message; _fault = fault; _connectionClosed = connectionClosed;
        _joinTimeout = joinTimeout ?? TimeSpan.FromSeconds(5);
        var localIP = options.BindAddress is { } ip && ip.GetAddressBytes().Length == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(ip.GetAddressBytes())
            : throw new InvalidOperationException("未确定所选网卡的 IPv4 地址。");
        _buffer = new(localIP, checked((uint)options.ProcessId), c =>
            new FirstPacketDecoder(c, (connection, epoch, bytes, inbound) =>
            {
                if (_cancel.IsCancellationRequested) return;
                // Remembered before the message is handed on, so a connection that ends in the
                // very next pump is still known to have produced something.
                if (_delivered.Count < MaxTrackedConnections) _delivered.Add(Identify(connection));
                Volatile.Read(ref _message)?.Invoke(connection, epoch, bytes, inbound);
            }), ownedStreamEnded: OnOwnedStreamEnded);
        _connections = connections ?? (() =>
        {
            // Fresh list avoids Machina's per-tuple socket lifecycle and stale removed rows.
            var rows = new List<TCPConnection>();
            new ProcessTCPInfo { ProcessID = checked((uint)options.ProcessId), LocalIP = options.BindAddress }
                .UpdateTCPIPConnections(rows);
            return rows;
        });
        _initialize = initialize ?? (() => _cleaner.TrackFactoryInitialization(() => OodleFactory.SetImplementation(
            options.Oodle == OodleMode.LibraryTcp ? OodleImplementation.LibraryTcp : OodleImplementation.FfxivTcp,
            options.Oodle == OodleMode.LibraryTcp ? options.OodleLibraryPath : options.GameExecutablePath),
            native => _ownedNative = native));
    }

    /// <inheritdoc />
    public CaptureIngressCounters Ingress =>
        _buffer.Counters with { AdapterDropped = _reader.DroppedPackets };

    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readThread is not null) throw new InvalidOperationException("监视器已准备。");
        _reader.Open();
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "Npcap packet reader" };
        _readThread.Start();
        if (!_readerReady.Wait(_joinTimeout) || _cancel.IsCancellationRequested)
            throw new IOException("准备阶段网卡读取未能安全启动。");
    }

    public void Start()
    {
        if (_readThread is null || _decodeThread is not null || _cancel.IsCancellationRequested)
            throw new InvalidOperationException("监视器未准备或已停止。");
        _initialize();
        if (_cancel.IsCancellationRequested) throw new InvalidOperationException("准备阶段采集已失败。");
        _decodeThread = new Thread(DecodeLoop) { IsBackground = true, Name = "Owned TCP decoder" };
        _decodeThread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                var received = _reader.Read(_buffer.Offer);
                // Ageing is deliberately not enforced yet: FirstPacketBuffer.Expire does
                // nothing until the decode loop has started, because everything captured while
                // Oodle initializes is connection prefix, which waiting cannot recover. Tick
                // keeps the ageing clock running for the moment Pump does start
                // (review finding L-13).
                _buffer.Tick();
                _readerReady.Set();
                if (_buffer.Failure is { } failure) { Fault(failure); return; }
                if (!received) _cancel.Token.WaitHandle.WaitOne(5);
            }
        }
        catch (Exception) { Fault("Npcap 读取失败，已停止采集。"); }
        finally { _readerReady.Set(); }
    }

    private void DecodeLoop()
    {
        try
        {
            while (!_cancel.IsCancellationRequested)
            {
                _buffer.Pump(_connections());
                if (_buffer.Failure is { } failure) { Fault(failure); return; }
                _cancel.Token.WaitHandle.WaitOne(50);
            }
        }
        catch (Exception) { Fault("连接归属确认或解码失败，已停止采集。"); }
    }

    private static (uint, ushort, uint, ushort) Identify(TCPConnection connection) =>
        (connection.LocalIP, connection.LocalPort, connection.RemoteIP, connection.RemotePort);

    /// <summary>
    /// One owned stream ended. Reported upwards only when it was the <em>last</em> connection
    /// still delivering decoded messages.
    ///
    /// The CN client keeps three decoded connections open at once -- lobby, zone and chat --
    /// and the chat server drops and reconnects on its own schedule, so "delivered something"
    /// alone would turn a cleared duty into a permanent DISCONNECTED record while the zone
    /// connection was never disturbed (review finding R-2). The game has stopped talking
    /// only when nothing is left, the reading docs/state-machine.md section 3.6 requires.
    /// </summary>
    /// <param name="connection">Connection the buffer was decoding.</param>
    private void OnOwnedStreamEnded(TCPConnection connection)
    {
        if (_cancel.IsCancellationRequested || !_delivered.Remove(Identify(connection))) return;
        if (_delivered.Count != 0) return;
        Volatile.Read(ref _connectionClosed)?.Invoke();
    }

    private void Fault(string reason)
    {
        _cancel.Cancel();
        _buffer.Fault(reason);
        if (Interlocked.Exchange(ref _faulted, 1) == 0) Volatile.Read(ref _fault)?.Invoke(reason);
    }

    public void Stop()
    {
        _cancel.Cancel();
        foreach (var thread in new[] { _readThread, _decodeThread })
            if (thread is not null && (thread == Thread.CurrentThread || !thread.Join(_joinTimeout)))
                throw new TimeoutException("采集线程尚未退出，保留原生与 Oodle 资源供重试停止。");
        _buffer.Stop();
    }

    public void DetachCallbacks()
    {
        Volatile.Write(ref _message, null);
        Volatile.Write(ref _fault, null);
        Volatile.Write(ref _connectionClosed, null);
    }
    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _reader.Dispose();
        if (_ownedNative is { } native)
        {
            _cleaner.ReleaseNative(native);
            _ownedNative = null;
        }
        _cleaner.Sweep();
        _cancel.Dispose();
        _readerReady.Dispose();
        _disposed = true;
    }
}
