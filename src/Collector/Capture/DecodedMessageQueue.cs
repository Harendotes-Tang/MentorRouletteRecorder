using System.Threading.Channels;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The bounded hand-off between the capture callback and the parser, plus the single thread
/// that drains it.
///
/// Two rules from docs/architecture.md section 4 are enforced here and nowhere else:
///
/// * the queue is bounded and drops the <em>oldest</em> element when full, so a slow parser
///   can never grow memory and can never block the capture callback -- a blocked callback
///   makes the driver drop packets, which is strictly worse because we would not even know;
/// * exactly one thread calls <see cref="IDecodedMessageSink.Accept"/>, in observation order,
///   so the state machine downstream needs no locking and stays deterministic under replay.
///
/// Drops are counted rather than swallowed: <see cref="DroppedCount"/> feeds
/// <c>CaptureStatus.packets_dropped</c>, and a run that lost messages is downgraded rather
/// than guessed at. The owner is told the moment a message is lost, not at teardown, and the
/// backlog a timed-out shutdown gives up on is counted separately as
/// <see cref="AbandonedCount"/> because it is not a gap in a live session
/// (review finding H-7).
/// </summary>
public sealed class DecodedMessageQueue : IDisposable
{
    /// <summary>Default queue capacity, matching <c>capture.queue_capacity</c>.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>Smallest capacity the setting accepts.</summary>
    public const int MinCapacity = 512;

    /// <summary>Largest capacity the setting accepts.</summary>
    public const int MaxCapacity = 65536;

    private readonly Channel<DecodedMessage> _channel;
    // Eviction and removal of the next surviving item must agree about which gap precedes it.
    // No lifecycle or parser work runs under this short channel-access lock.
    private readonly object _handoff = new();
    private long _pendingOverflow;
    private readonly Action<long>? _onSequenceGap;
    private readonly Task<bool>? _sourceStarted;
    private readonly IDecodedMessageSink _sink;
    private readonly Action<Exception>? _onSinkError;
    private readonly Action<long>? _onDropped;
    private readonly Action? _onConnectionLost;
    private readonly Action<string, MessageDirection>? _onDirectionDamaged;
    private readonly Action? _beforeWait;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _worker;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _dropped;
    private long _abandoned;
    private long _delivered;
    private long _sinkErrors;
    private bool _disposed;
    private volatile string _stage = StageStarting;
    private long _stageSinceTicks = Environment.TickCount64;
    private volatile bool _stoppedWhileParked;

    private const string StageStarting = "starting";
    private const string StageWaiting = "waiting";
    private const string StageConnectionLost = "connection-lost";
    private const string StageDirectionDamaged = "direction-damaged";
    private const string StageExited = "exited";

    /// <summary>Creates a queue and starts its parser thread.</summary>
    /// <param name="sink">Consumer called from the parser thread, in order.</param>
    /// <param name="capacity">Queue capacity; clamped into the documented range.</param>
    /// <param name="onSinkError">
    /// Called when the sink throws. The queue worker remains alive; its owner decides whether
    /// to continue or tear the capture session down.
    /// </param>
    /// <param name="onDropped">
    /// Immediate diagnostic count from the capture callback; the callback must return immediately.
    /// The parser receives the business gap separately, at its position in the observation stream.
    /// </param>
    /// <param name="onConnectionLost">
    /// Called from the parser thread when a connection-lost marker reaches the front of the
    /// queue. Routing the event through the queue keeps it behind the messages that same
    /// connection already delivered; otherwise a <c>DUTY_RESULT</c> still in the backlog
    /// arrives after the run was closed as DISCONNECTED, turning a cleared duty into a lost one
    /// (review finding R-4).
    /// </param>
    /// <param name="onDirectionDamaged">
    /// Called from the parser thread, with the connection key and the direction, when a
    /// direction-damaged marker reaches the front of the queue: behind every message that
    /// connection delivered before the direction was given up, for the same reason as
    /// <paramref name="onConnectionLost"/> (audit 2026-10-03, CS3a-X1).
    /// </param>
    public DecodedMessageQueue(
        IDecodedMessageSink sink,
        int capacity = DefaultCapacity,
        Action<Exception>? onSinkError = null,
        Action<long>? onDropped = null,
        Action? onConnectionLost = null,
        Action<string, MessageDirection>? onDirectionDamaged = null)
        : this(sink, capacity, onSinkError, onDropped, onConnectionLost, beforeWait: null, onDirectionDamaged)
    {
    }

    /// <summary>
    /// A controller can defer draining until its source has started. A failed start discards staged items.
    /// On a successful start the parser receives each overflow gap before its next surviving item.
    /// The optional beforeWait hook permits deterministic tests of shutdown during a wait.
    /// </summary>
    internal DecodedMessageQueue(
        IDecodedMessageSink sink,
        int capacity,
        Action<Exception>? onSinkError,
        Action<long>? onDropped,
        Action? onConnectionLost,
        Action? beforeWait,
        Action<string, MessageDirection>? onDirectionDamaged = null,
        Action<long>? onSequenceGap = null,
        Task<bool>? sourceStarted = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _onSinkError = onSinkError;
        _onDropped = onDropped;
        _onConnectionLost = onConnectionLost;
        _onDirectionDamaged = onDirectionDamaged;
        _beforeWait = beforeWait;
        _onSequenceGap = onSequenceGap;
        _sourceStarted = sourceStarted;
        Capacity = Math.Clamp(capacity, MinCapacity, MaxCapacity);
        _channel = Channel.CreateBounded<DecodedMessage>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                // A timed-out shutdown drains abandoned items alongside the worker.
                SingleReader = false,
                SingleWriter = false,
                // The parser thread blocks in WaitToReadAsync(...).GetResult(). By default a
                // channel hands that wake-up to the thread pool, so on a two-core runner whose
                // pool threads are busy the worker can miss the 2 s drain budget, Dispose
                // reports the session as unreleasable and the session row is never closed.
                // Waking the worker inline needs no free pool thread.
                AllowSynchronousContinuations = true,
            }, _ =>
            {
                _pendingOverflow++;
                Drop();
            });

        _worker = new Thread(Drain)
        {
            IsBackground = true,
            Name = "mentor-recorder-parser",
        };
        _worker.Start();
    }

    /// <summary>Capacity actually in force.</summary>
    public int Capacity { get; }

    /// <summary>
    /// Where the parser thread is right now and for how long: <c>waiting</c>,
    /// <c>delivering 0x1234</c>, <c>connection-lost</c>, <c>direction-damaged</c> or <c>exited</c>, each with the
    /// milliseconds spent there. Diagnostic only; the timeout message names the stage, which is
    /// what tells a stuck sink apart from a worker that never woke.
    /// </summary>
    public string Stage => $"{_stage} for {Environment.TickCount64 - Interlocked.Read(ref _stageSinceTicks)} ms";

    /// <summary>
    /// True when <see cref="Complete"/> returned before the parser thread had actually left:
    /// it was parked in <c>WaitToReadAsync</c> with nothing in hand, so nothing it can do on
    /// waking reaches the sink. Diagnostic only.
    /// </summary>
    public bool StoppedWhileParked => _stoppedWhileParked;

    private void EnterStage(string stage)
    {
        _stage = stage;
        Interlocked.Exchange(ref _stageSinceTicks, Environment.TickCount64);
    }

    /// <summary>Messages waiting to be parsed.</summary>
    public int Depth => _channel.Reader.Count;

    /// <summary>
    /// Messages the parser will never see because the queue was full, or because it had
    /// already been completed.
    ///
    /// A hole in the middle of a live session: the state machine can no longer classify the
    /// run and has to be told so. Deliberately separate from <see cref="AbandonedCount"/>.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Messages still waiting when a timed-out shutdown gave up on the backlog.
    ///
    /// Counted apart from <see cref="DroppedCount"/> because it means something else. An
    /// overflow drop is a gap in the observations of a session still running; an abandoned
    /// backlog is the tail of a session already over, and reporting it as EVENT_SEQUENCE_GAP
    /// would turn a correctly finished duty into an interrupted one (review finding H-7).
    /// </summary>
    public long AbandonedCount => Interlocked.Read(ref _abandoned);

    /// <summary>Everything the parser never saw, however it was lost. Diagnostics only.</summary>
    public long LostCount => DroppedCount + AbandonedCount;

    /// <summary>Messages handed to the sink.</summary>
    public long DeliveredCount => Interlocked.Read(ref _delivered);

    /// <summary>Times the sink threw. The worker catches every failure and notifies its owner.</summary>
    public long SinkErrorCount => Interlocked.Read(ref _sinkErrors);

    /// <summary>Completes after the last sink callback returns and the worker exits.</summary>
    public Task Completion => _completion.Task;

    /// <summary>True when the queue is more than 80% full, the documented DEGRADED threshold.</summary>
    public bool IsUnderPressure => Depth * 5 > Capacity * 4;

    /// <summary>
    /// Offers one message. Always returns immediately and never throws; when the queue is
    /// full the oldest waiting message is discarded and counted.
    /// </summary>
    /// <param name="message">Message to enqueue.</param>
    public void Offer(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!TryOffer(message))
        {
            Drop();
        }
    }

    private bool TryOffer(DecodedMessage message)
    {
        lock (_handoff)
        {
            return !_disposed && _channel.Writer.TryWrite(message);
        }
    }

    /// <summary>Unreported evictions when a timed-out drain abandons every remaining survivor.</summary>
    internal long TakePendingOverflow()
    {
        lock (_handoff)
        {
            var count = _pendingOverflow;
            _pendingOverflow = 0;
            return count;
        }
    }

    /// <summary>
    /// Marker enqueued for "the game connection ended".
    ///
    /// It travels in the same channel as the decoded messages precisely so it cannot overtake
    /// them; nothing ever reads its fields, only its identity.
    /// </summary>
    private static readonly DecodedMessage ConnectionLostMarker = new(
        string.Empty,
        MessageDirection.Inbound,
        DateTimeOffset.UnixEpoch,
        TimeSpan.Zero,
        0,
        0,
        0,
        ReadOnlyMemory<byte>.Empty,
        string.Empty);

    /// <summary>
    /// Offers the connection-lost marker, so the listener is told only after everything that
    /// connection already delivered has been parsed (review finding R-4).
    ///
    /// A marker that cannot be queued -- the session is already closing -- is dropped silently
    /// rather than counted: it is not an observation, and counting it would report a hole in a
    /// sequence that has none and downgrade the run to INTERRUPTED.
    /// </summary>
    /// <returns>True when the marker was queued.</returns>
    public bool OfferConnectionLost() => TryOffer(ConnectionLostMarker);

    /// <summary>
    /// Session id of a direction-damaged marker, compared by reference. Unlike
    /// <see cref="ConnectionLostMarker"/> that marker carries something -- the connection key and
    /// the direction, in the fields a message carries them in -- so each one is a message of its
    /// own, recognised by this very string instance, which no decoded message can hold.
    /// </summary>
    private static readonly string DirectionDamagedTag = new string('#', 1);

    /// <summary>
    /// Offers a direction-damaged marker, so the listener is told only after everything that
    /// connection delivered before the loss has been parsed (audit 2026-10-03, CS3a-X1). A marker
    /// that cannot be queued is dropped silently, like the connection-lost marker.
    /// </summary>
    /// <param name="connectionKey">Opaque key of the connection, as on its decoded messages.</param>
    /// <param name="direction">The direction given up.</param>
    /// <returns>True when the marker was queued.</returns>
    public bool OfferDirectionDamaged(string connectionKey, MessageDirection direction)
    {
        ArgumentNullException.ThrowIfNull(connectionKey);
        return TryOffer(new DecodedMessage(
            DirectionDamagedTag,
            direction,
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            0,
            0,
            0,
            ReadOnlyMemory<byte>.Empty,
            connectionKey));
    }

    /// <summary>
    /// Counts an evicted or refused message for diagnostics on the producer thread.
    /// </summary>
    private void Drop()
    {
        Interlocked.Increment(ref _dropped);
        _onDropped?.Invoke(1);
    }

    /// <summary>Stops accepting messages and waits for the parser thread to finish the backlog.</summary>
    /// <param name="timeout">How long to wait for the drain.</param>
    /// <returns>True only once no sink callback can still run.</returns>
    public bool Complete(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();
        if (_worker.Join(timeout)) return true;
        lock (_handoff)
        {
            _stopping.Cancel();
        }
        // These accepted messages will never reach the sink. Counted as abandoned rather than
        // dropped: the session is already ending, so this is not a gap the state machine has
        // to downgrade a run over (review finding H-7).
        while (_channel.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _abandoned);
        }
        if (_worker.Join(TimeSpan.Zero)) return true;

        // The worker is still alive but parked in WaitToReadAsync with nothing in hand: the
        // channel is complete and empty and cancellation is raised, so the only thing it can
        // do on waking is leave. That is not a stuck sink and must not be reported as one:
        // CPU starvation alone would otherwise refuse every stop on a healthy queue.
        // Dequeuing and marking the delivering stage share the lock used to cancel the worker.
        // Once cancelled, a worker that still reports waiting can no longer take an item.
        // "starting" (not yet in the loop) and "exited" (in its last instructions) hold nothing
        // either; only a delivering, connection-lost or direction-damaged stage means the sink may
        // be running.
        var stage = _stage;
        if (stage == StageWaiting || stage == StageStarting || stage == StageExited)
        {
            _stoppedWhileParked = true;
            return true;
        }

        return false;
    }

    private void Drain()
    {
        var reader = _channel.Reader;
        try
        {
            // Start can produce both packets and lifecycle markers. They all stay in the same bounded
            // queue until the controller commits startup; none belongs to a failed startup.
            if (_sourceStarted is not null && !_sourceStarted.GetAwaiter().GetResult())
            {
                while (reader.TryRead(out _)) Interlocked.Increment(ref _abandoned);
                return;
            }
            while (!_stopping.IsCancellationRequested)
            {
                EnterStage(StageWaiting);
                _beforeWait?.Invoke();
                if (!reader.WaitToReadAsync(_stopping.Token).AsTask().GetAwaiter().GetResult())
                {
                    return;
                }

                while (true)
                {
                    DecodedMessage message;
                    long gap;
                    lock (_handoff)
                    {
                        if (_stopping.IsCancellationRequested || !reader.TryRead(out var next)) break;
                        message = next;
                        gap = _pendingOverflow;
                        _pendingOverflow = 0;
                        // Complete must see a worker with an item in hand as delivering, even if a gap
                        // callback blocks before the item's own sink is called.
                        EnterStage(ReferenceEquals(message, ConnectionLostMarker)
                            ? StageConnectionLost
                            : ReferenceEquals(message.CaptureSessionId, DirectionDamagedTag)
                                ? StageDirectionDamaged
                                : $"delivering 0x{message.Opcode:x4}");
                    }
                    Deliver(message, gap);

                    if (_stopping.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // Shutdown.
        }
        finally
        {
            EnterStage(StageExited);
            _completion.TrySetResult();
        }
    }

    private void Deliver(DecodedMessage message, long gap)
    {
        try
        {
            if (gap > 0) _onSequenceGap?.Invoke(gap);
            if (ReferenceEquals(message, ConnectionLostMarker))
            {
                _onConnectionLost?.Invoke();
                return;
            }

            if (ReferenceEquals(message.CaptureSessionId, DirectionDamagedTag))
            {
                _onDirectionDamaged?.Invoke(message.ConnectionKey, message.Direction);
                return;
            }

            _sink.Accept(message);
            Interlocked.Increment(ref _delivered);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A broken sink never crashes this worker. The owner callback may keep the queue
            // alive for diagnostics, or stop capture when continuing would lose run data.
            Interlocked.Increment(ref _sinkErrors);
            _onSinkError?.Invoke(ex);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!Complete(TimeSpan.FromSeconds(2)))
            throw new TimeoutException(
                $"协议处理线程尚未退出（{Stage}，积压 {Depth}，已交付 {DeliveredCount}），保留会话资源供停止操作重试。");
        _disposed = true;
        _stopping.Cancel();
        // Complete may accept a parked worker before it actually exits. That worker can
        // still obtain Token on waking, so keep the source alive until its final callback.
        _ = _completion.Task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Dispose(),
            _stopping, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
