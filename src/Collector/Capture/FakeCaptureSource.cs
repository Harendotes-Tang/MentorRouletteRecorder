using System.Diagnostics;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// An in-memory capture source: it observes nothing and produces exactly the messages a test
/// hands it.
///
/// It covers everything above <see cref="ICaptureSource"/> -- the bounded queue, the
/// controller's state transitions, fault handling, diagnostics and the IPC surface -- on a
/// machine with no Npcap and no game. It touches no driver, no process and no network, so a
/// test using it exercises the shipping control flow rather than a mock of it.
/// </summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    private readonly Stopwatch _mono = Stopwatch.StartNew();
    private readonly object _gate = new();
    private ICaptureSourceObserver? _observer;
    private CaptureStartOptions? _options;
    private bool _disposed;

    /// <summary>When set, <see cref="Start"/> throws this instead of starting.</summary>
    public CollectorException? StartFailure { get; set; }

    /// <summary>
    /// When set, reports a monitor fault after installing the observer but before
    /// <see cref="Start"/> returns. This exercises the real callback timing boundary.
    /// </summary>
    public string? FaultDuringStart { get; set; }

    /// <summary>Set true to claim this source reads the game executable, as Machina's default does.</summary>
    public bool ReadsGameExecutable { get; set; }

    /// <summary>
    /// Connections the game is claimed to have held when this source started. A positive value
    /// reproduces a capture attached after the client had already connected, which the real
    /// source cannot be asked to do.
    /// </summary>
    public int? PreexistingTcpConnections { get; set; }

    /// <summary>
    /// Ingress counters this source claims. The fake observes no adapter, so a test needing
    /// "packets arrived and every one was dropped" states it here.
    /// </summary>
    public CaptureIngressCounters IngressCounters { get; set; } = CaptureIngressCounters.Empty;

    /// <summary>Which signature set this source claims to be using.</summary>
    public OodleSignatureUse SignatureUse { get; set; } = OodleSignatureUse.None;

    /// <summary>Number of times <see cref="Start"/> succeeded.</summary>
    public int StartCount { get; private set; }

    /// <summary>Number of times <see cref="Stop"/> actually stopped a running source.</summary>
    public int StopCount { get; private set; }

    /// <summary>Options the last successful start was given.</summary>
    public CaptureStartOptions? LastOptions
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }
    }

    /// <inheritdoc />
    public string Kind => "fake";

    /// <inheritdoc />
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _observer is not null;
            }
        }
    }

    /// <inheritdoc />
    public void Start(CaptureStartOptions options, ICaptureSourceObserver observer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observer);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (StartFailure is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            _observer = observer;
            _options = options;
            StartCount++;
        }

        if (FaultDuringStart is { } reason)
        {
            observer.OnFault(reason, new InvalidOperationException("simulated start callback fault"));
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (_observer is null)
            {
                return;
            }

            _observer = null;
            _options = null;
            StopCount++;
        }
    }

    /// <summary>Feeds one raw message through the same framing reader the real source uses.</summary>
    /// <param name="message">Complete message including both headers.</param>
    /// <param name="direction">Direction relative to the local client.</param>
    /// <param name="epochMs">Bundle epoch in milliseconds.</param>
    /// <param name="connectionSuffix">Distinguishes simulated connections.</param>
    public void PushRaw(
        ReadOnlySpan<byte> message,
        MessageDirection direction = MessageDirection.Inbound,
        long epochMs = 0,
        ushort connectionSuffix = 1)
    {
        ICaptureSourceObserver observer;
        string sessionId;
        lock (_gate)
        {
            if (_observer is null || _options is null)
            {
                return;
            }

            observer = _observer;
            sessionId = _options.CaptureSessionId;
        }

        if (!FfxivFraming.TryRead(message, out var frame))
        {
            observer.OnDecodeError();
            return;
        }

        var payload = message.Slice(frame.PayloadOffset, frame.PayloadLength).ToArray();
        observer.OnMessage(new DecodedMessage(
            sessionId,
            direction,
            epochMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs) : DateTimeOffset.UtcNow,
            _mono.Elapsed,
            epochMs,
            frame.SegmentType,
            frame.Opcode,
            payload,
            ConnectionKey.From(sessionId, 0, connectionSuffix, 0, 0)));
    }

    /// <summary>Feeds a minimal well-formed IPC message carrying one opcode and no payload.</summary>
    /// <param name="opcode">Opcode to place in the IPC header.</param>
    /// <param name="payloadLength">Number of zero payload bytes to append.</param>
    /// <param name="direction">Direction relative to the local client.</param>
    public void PushOpcode(
        ushort opcode, int payloadLength = 0, MessageDirection direction = MessageDirection.Inbound) =>
        PushRaw(BuildIpcMessage(opcode, payloadLength), direction);

    /// <summary>
    /// Reports one undecodable frame, exactly as the real source does when Machina hands it
    /// bytes the framing reader refuses.
    /// </summary>
    public void PushDecodeError()
    {
        ICaptureSourceObserver? observer;
        lock (_gate)
        {
            observer = _observer;
        }

        observer?.OnDecodeError();
    }

    /// <summary>
    /// Reports that a game connection which had been delivering messages ended, exactly as the
    /// real source does when an owned stream sees FIN/RST or leaves the OS connection table.
    /// </summary>
    public void PushConnectionClosed()
    {
        ICaptureSourceObserver? observer;
        lock (_gate)
        {
            observer = _observer;
        }

        observer?.OnConnectionClosed();
    }

    /// <summary>Reports a monitor fault, exactly as the real source would.</summary>
    /// <param name="reason">Short, user-facing reason.</param>
    /// <param name="error">Underlying exception, for the local log only.</param>
    public void Fault(string reason, Exception? error = null)
    {
        ICaptureSourceObserver? observer;
        lock (_gate)
        {
            observer = _observer;
        }

        observer?.OnFault(reason, error);
    }

    /// <summary>
    /// Builds a well-formed message with the framing this project reads: a 16-byte segment
    /// header of type 3, a 16-byte IPC header carrying <paramref name="opcode"/>, then zeroed
    /// payload bytes.
    /// </summary>
    /// <param name="opcode">Opcode to place in the IPC header.</param>
    /// <param name="payloadLength">Number of payload bytes to append.</param>
    /// <param name="segmentType">Segment type to declare.</param>
    public static byte[] BuildIpcMessage(
        ushort opcode, int payloadLength = 0, ushort segmentType = FfxivFraming.SegmentTypeIpc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);

        var headerBytes = segmentType == FfxivFraming.SegmentTypeIpc
            ? FfxivFraming.HeaderBytes
            : FfxivFraming.SegmentHeaderBytes;
        var message = new byte[headerBytes + payloadLength];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(12), segmentType);
        if (segmentType == FfxivFraming.SegmentTypeIpc)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(18), opcode);
        }

        return message;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
