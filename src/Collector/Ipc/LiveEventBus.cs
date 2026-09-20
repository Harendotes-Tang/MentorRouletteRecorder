using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Calibration;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Semantic kinds of live event this build emits, and the schema event type each maps to.
/// </summary>
public enum LiveEventKind
{
    /// <summary>The state machine moved; carries <c>state</c>.</summary>
    RunStateChanged,

    /// <summary>A run row was created; carries <c>run</c>.</summary>
    RunCreated,

    /// <summary>A run row changed; carries <c>run</c>.</summary>
    RunUpdated,

    /// <summary>
    /// A run reached a final result; carries <c>run</c> and the terminal <c>state</c>.
    ///
    /// Separate from <see cref="RunUpdated"/> so a client that only needs "this attempt is
    /// over, and how it ended" -- the TTS announcement here -- need not diff two revisions.
    /// Emitted even when the next duty-finder pop displaces the run in the same step, which
    /// the observable state machine cannot show (spec gaps P1-18, P1-19).
    /// </summary>
    RunFinished,

    /// <summary>Cached statistics are stale and should be re-queried.</summary>
    StatsInvalidated,

    /// <summary>Collector or capture status changed.</summary>
    CollectorStatus,

    /// <summary>A candidate ledger row was observed; carries only a refresh hint.</summary>
    CandidateObserved,

    /// <summary>Calibration moved between observing, ready, blocked and done.</summary>
    CalibrationChanged,

    /// <summary>
    /// Proof that a subscription is still alive. Carries nothing else: a client that has seen
    /// no event for longer than its heartbeat interval knows the stream is dead rather than
    /// merely quiet (spec gap P1-20).
    /// </summary>
    Heartbeat,
}

/// <summary>
/// In-process publish and subscribe for live events.
///
/// Each subscriber owns a bounded channel. A subscriber that falls behind loses its oldest
/// events and its sequence numbers gap; the contract defines a gap as "re-query"
/// (<c>$defs/LiveEvent.sequence</c>). Dropping is deliberate: blocking the publisher would let
/// a slow pipe reader stall the writer thread.
///
/// The bus also keeps the last <see cref="ReplayCapacity"/> events it published and hands them
/// to every new subscriber before any live one, in order and with their original sequence
/// numbers. <c>CollectorHost.Open</c> runs crash recovery -- the pass that turns an unfinished
/// run into <c>INTERRUPTED</c> and flags it for review -- before <c>PipeServer</c> is
/// constructed, so those events are published before any client can be listening.
///
/// Replayed events are not marked as such on the wire: <c>$defs/LiveEvent</c> in
/// contracts/ipc-v1.schema.json is <c>additionalProperties: false</c> and the live event *is*
/// the event envelope's payload, so v1 has nowhere to put a <c>replayed</c> flag. It is not
/// needed either: <c>event_id</c> is stable across the replay and <c>sequence</c> is monotonic
/// across the join. See docs/architecture.md section 3.2.
/// </summary>
public sealed class LiveEventBus
{
    /// <summary>Events buffered per subscriber before the oldest are dropped.</summary>
    public const int SubscriberCapacity = 256;

    /// <summary>Most recent events kept for replay to subscribers that arrive later.</summary>
    public const int ReplayCapacity = 64;

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);

    /// <summary>
    /// Guards the replay buffer together with the fan-out and the subscriber set.
    ///
    /// Publishing and subscribing must be atomic with respect to each other or the ordering
    /// guarantee fails: snapshot-then-register loses an event published in between, and
    /// register-then-replay lets a live event overtake the replay it should follow. One lock
    /// across both is cheap because delivery is a non-blocking <c>TryWrite</c> per subscriber.
    /// </summary>
    private readonly object _gate = new();
    private readonly Queue<JsonObject> _replay = new(ReplayCapacity);
    private readonly IClock _clock;
    private long _sequence;

    /// <summary>Creates a bus stamping events with <paramref name="clock"/>.</summary>
    /// <param name="clock">Clock used for <c>emitted_at_utc</c>.</param>
    public LiveEventBus(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>Number of live subscriptions.</summary>
    public int SubscriberCount => _subscriptions.Count;

    /// <summary>Events currently held for replay; at most <see cref="ReplayCapacity"/>.</summary>
    public int ReplayCount
    {
        get
        {
            lock (_gate)
            {
                return _replay.Count;
            }
        }
    }

    /// <summary>
    /// Opens a subscription and immediately queues the retained events into it, oldest first.
    /// Dispose it to unsubscribe.
    /// </summary>
    /// <param name="subscriptionId">Identifier returned to the client.</param>
    public LiveEventSubscription Subscribe(string subscriptionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(subscriptionId);

        var subscription = new Subscription(subscriptionId, this);
        lock (_gate)
        {
            foreach (var retained in _replay)
            {
                subscription.Offer(retained);
            }

            _subscriptions[subscriptionId] = subscription;
        }

        return subscription;
    }

    /// <summary>Publishes a run-shaped event.</summary>
    /// <param name="kind">Semantic kind.</param>
    /// <param name="run">Run to attach.</param>
    public void PublishRun(LiveEventKind kind, MentorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Publish(kind, payload => payload["run"] = Wire.Run(run));
    }

    /// <summary>Publishes the terminal state of a run that has just finished.</summary>
    /// <param name="run">Run as it now stands, with its final result.</param>
    /// <param name="terminalState">Terminal state matching the run's result.</param>
    public void PublishRunFinished(MentorRun run, RunState terminalState)
    {
        ArgumentNullException.ThrowIfNull(run);
        Publish(LiveEventKind.RunFinished, payload =>
        {
            payload["run"] = Wire.Run(run);
            payload["state"] = EnumWire<RunState>.Format(terminalState);
        });
    }

    /// <summary>Publishes a heartbeat.</summary>
    public void PublishHeartbeat() => Publish(LiveEventKind.Heartbeat, static _ => { });

    /// <summary>
    /// Publishes a state change, carrying the run the state belongs to.
    ///
    /// The run is load-bearing: a client announcing "进入 {duty}" reads the duty name from this
    /// event and de-duplicates repeated announcements by <c>run_id</c>. Without them it falls
    /// back to the snapshot taken when the pop arrived, which on the CN client does not name
    /// the duty yet (review finding H-3).
    /// </summary>
    /// <param name="state">New state of the run in flight.</param>
    /// <param name="run">Run the state belongs to, or null when no run is in flight.</param>
    /// <param name="matchFromQueue">Match source of the bound machine; null when unknown.</param>
    /// <param name="matchOffer">
    /// MENTOR_MATCHED only: which offer of this run the event is about, from 1. A second event for
    /// the same run and state with a higher number is the match popping again.
    /// </param>
    public void PublishState(
        RunState state, MentorRun? run = null, bool? matchFromQueue = null, int? matchOffer = null) =>
        Publish(LiveEventKind.RunStateChanged, payload =>
        {
            payload["state"] = EnumWire<RunState>.Format(state);
            if (matchFromQueue is { } queued)
            {
                payload["match_from_queue"] = queued;
            }
            if (matchOffer is { } offer)
            {
                payload["match_offer"] = offer;
            }
            if (run is not null)
            {
                payload["run"] = Wire.Run(run);
            }
        });

    /// <summary>Publishes a "statistics are stale" hint.</summary>
    /// <param name="message">Non-sensitive explanation.</param>
    public void PublishStatsInvalidated(string message) =>
        Publish(LiveEventKind.StatsInvalidated, payload =>
        {
            payload["severity"] = "INFO";
            payload["message"] = message;
        });

    /// <summary>Publishes a collector or capture status change.</summary>
    /// <param name="capture">Rendered capture status.</param>
    /// <param name="message">Non-sensitive explanation.</param>
    public void PublishCollectorStatus(JsonObject capture, string message)
    {
        ArgumentNullException.ThrowIfNull(capture);
        Publish(LiveEventKind.CollectorStatus, payload =>
        {
            payload["capture"] = capture;
            payload["severity"] = "INFO";
            payload["message"] = message;
        });
    }

    /// <summary>Tells subscribers calibration changed state; the payload carries no timeline.</summary>
    /// <param name="state">New state.</param>
    public void PublishCalibrationChanged(CalibrationState state) =>
        Publish(LiveEventKind.CalibrationChanged, payload =>
        {
            payload["calibration_state"] = CalibrationWire.State(state);
            payload["ready"] = state == CalibrationState.Ready;
        });

    /// <summary>Publishes minimal candidate metadata without a run or statistics event.</summary>
    /// <param name="observation">Persisted candidate observation; packet fields never enter this event.</param>
    public void PublishCandidateObserved(CandidateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        Publish(LiveEventKind.CandidateObserved, payload =>
        {
            payload["name"] = observation.HypothesisName;
            payload["group"] = observation.Group;
            payload["t_ms"] = observation.TMs;
            payload["observation_id"] = observation.ObservationId;
            payload["capture_session_id"] = observation.CaptureSessionId;
        });
    }

    /// <summary>Builds and fans out one event.</summary>
    /// <param name="kind">Semantic kind.</param>
    /// <param name="fill">Adds the kind-specific fields.</param>
    public void Publish(LiveEventKind kind, Action<JsonObject> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);

        // Deliberately no early return when nobody is subscribed: the replay buffer gives an
        // event with no audience now an audience later.
        var payload = new JsonObject
        {
            ["event_id"] = Guid.NewGuid().ToString("D"),
            ["event_type"] = SchemaEventType(kind),

            // Proposed contract addition: the schema enum has no bucket for
            // "statistics are stale". See docs/architecture.md section 3.2.
            ["kind"] = KindToken(kind),
            ["emitted_at_utc"] = UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)),
        };
        fill(payload);

        lock (_gate)
        {
            // Taken inside the lock that fans the event out: outside it, two publishers could
            // enqueue out of order and a subscriber would see sequence numbers move backwards.
            // The contract defines a gap but not a backwards number (review finding M1).
            payload["sequence"] = ++_sequence;

            // Heartbeats are deliberately not retained. They say "the stream is alive now",
            // which is worthless a minute later, and retaining them would let an idle
            // Collector evict the crash-recovery events the replay buffer exists to keep.
            if (kind != LiveEventKind.Heartbeat)
            {
                _replay.Enqueue(payload);
                while (_replay.Count > ReplayCapacity)
                {
                    _replay.Dequeue();
                }
            }

            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Offer(payload);
            }
        }
    }

    /// <summary>Schema <c>$defs/LiveEvent.event_type</c> token for a kind.</summary>
    /// <param name="kind">Semantic kind.</param>
    public static string SchemaEventType(LiveEventKind kind) => kind switch
    {
        LiveEventKind.RunStateChanged => "StateChanged",
        LiveEventKind.RunCreated => "RunStarted",
        LiveEventKind.RunUpdated => "RunUpdated",
        LiveEventKind.RunFinished => "RunFinished",
        LiveEventKind.CollectorStatus => "CaptureStatusChanged",
        LiveEventKind.CandidateObserved => "CandidateObserved",
        LiveEventKind.CalibrationChanged => "CalibrationChanged",
        LiveEventKind.Heartbeat => "Heartbeat",
        _ => "DiagnosticsMessage",
    };

    /// <summary>Snake-case identifier of a kind.</summary>
    /// <param name="kind">Semantic kind.</param>
    public static string KindToken(LiveEventKind kind) => kind switch
    {
        LiveEventKind.RunStateChanged => "run_state_changed",
        LiveEventKind.RunCreated => "run_created",
        LiveEventKind.RunUpdated => "run_updated",
        LiveEventKind.RunFinished => "run_finished",
        LiveEventKind.CollectorStatus => "collector_status",
        LiveEventKind.CandidateObserved => "candidate_observed",
        LiveEventKind.CalibrationChanged => "calibration_changed",
        LiveEventKind.Heartbeat => "heartbeat",
        _ => "stats_invalidated",
    };

    private void Remove(string subscriptionId) => _subscriptions.TryRemove(subscriptionId, out _);

    private sealed class Subscription : LiveEventSubscription
    {
        private readonly LiveEventBus _bus;

        public Subscription(string subscriptionId, LiveEventBus bus)
            : base(subscriptionId)
        {
            _bus = bus;
        }

        protected override void OnDispose() => _bus.Remove(SubscriptionId);
    }
}

/// <summary>One client's view of the live event stream.</summary>
public abstract class LiveEventSubscription : IDisposable
{
    private readonly System.Threading.Channels.Channel<JsonObject> _channel;
    private long _dropped;
    private bool _disposed;

    /// <summary>Creates a subscription with a bounded, drop-oldest buffer.</summary>
    /// <param name="subscriptionId">Identifier returned to the client.</param>
    protected LiveEventSubscription(string subscriptionId)
    {
        SubscriptionId = subscriptionId;
        _channel = System.Threading.Channels.Channel.CreateBounded<JsonObject>(
            new System.Threading.Channels.BoundedChannelOptions(LiveEventBus.SubscriberCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>Identifier returned to the client.</summary>
    public string SubscriptionId { get; }

    /// <summary>Number of events dropped because this subscriber fell behind.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>True once the subscription has been disposed and can produce nothing more.</summary>
    public bool IsClosed => _disposed;

    /// <summary>Waits for the next event, or returns null when the subscription ends.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the next event for at most <paramref name="timeout"/>, then returns null.
    ///
    /// Null therefore means one of two things, and the caller has to tell them apart with
    /// <see cref="IsClosed"/>: the subscription ended, or nothing happened for a whole
    /// interval and it is time to send a heartbeat.
    /// </summary>
    /// <param name="timeout">Longest wait before giving up.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<JsonObject?> ReadAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            return await _channel.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
            when (ex is OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Offers one event, dropping the oldest when the buffer is full.</summary>
    /// <param name="payload">Rendered event payload.</param>
    internal void Offer(JsonObject payload)
    {
        if (!_channel.Writer.TryWrite(payload.DeepClone().AsObject()))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Called once when the subscription is disposed.</summary>
    protected abstract void OnDispose();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        OnDispose();
        GC.SuppressFinalize(this);
    }
}
