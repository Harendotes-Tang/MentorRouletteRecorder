using System.Diagnostics;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Serves one connected client: read a frame, answer it, repeat.
///
/// Errors fall into two kinds. A framing error is fatal to the connection, because the byte
/// stream can no longer be resynchronised. Everything else -- bad JSON, an unknown message
/// type, a business refusal, an unexpected exception -- becomes one error envelope and the
/// connection carries on.
///
/// <c>SubscribeLiveEvents</c> is the one message that changes the shape of the connection:
/// after the acknowledgement, a background pump pushes event frames while the read loop keeps
/// answering ordinary requests. Exactly one pump is allowed per connection, because each owns
/// a bounded channel and receives a deep clone of every event published, under the bus's lock;
/// unbounded pumps would let one client slow every publisher in the process, the capture
/// thread included (review finding M2). A client that wants a different filter opens a new
/// connection.
/// </summary>
public sealed class PipeConnection
{
    /// <summary>Heartbeat cadence used when the client does not ask for one.</summary>
    public const int DefaultHeartbeatMs = 5000;

    /// <summary>Live subscriptions one connection may hold at a time.</summary>
    public const int MaxLiveSubscriptions = 1;

    /// <summary>Longest event type name a subscription filter accepts.</summary>
    private const int MaxEventTypeLength = 64;

    private static readonly string ZeroUuid = Guid.Empty.ToString("D");

    private readonly Stream _stream;
    private readonly MessageDispatcher _dispatcher;
    private readonly Action<string, Exception?>? _log;

    /// <summary>Creates a connection handler.</summary>
    /// <param name="stream">Connected duplex stream.</param>
    /// <param name="dispatcher">Dispatcher answering requests.</param>
    /// <param name="log">Optional diagnostic sink; never receives payloads.</param>
    public PipeConnection(Stream stream, MessageDispatcher dispatcher, Action<string, Exception?>? log = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _stream = stream;
        _dispatcher = dispatcher;
        _log = log;
    }

    /// <summary>Serves requests until the peer disconnects or the token fires.</summary>
    /// <param name="cancellationToken">Cancels the connection.</param>
    public async Task ServeAsync(CancellationToken cancellationToken)
    {
        var channel = new FrameChannel(_stream);
        var subscriptions = new List<Task>();
        var deferred = new List<Task>();
        using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            while (!connectionScope.IsCancellationRequested)
            {
                byte[]? body;
                try
                {
                    body = await channel.ReadFrameAsync(connectionScope.Token).ConfigureAwait(false);
                }
                catch (CollectorException framing)
                {
                    // Oversized frame: answer once, then close. There is no way to find the
                    // next frame boundary in a stream we no longer trust.
                    await TrySendAsync(
                        channel,
                        IpcEnvelope.Failure(ZeroUuid, IpcEnvelope.ErrorMessageType, framing),
                        connectionScope.Token).ConfigureAwait(false);
                    _log?.Invoke("closing connection after a framing error", null);
                    return;
                }

                if (body is null)
                {
                    return;
                }

                var response = Handle(channel, body, subscriptions, deferred, connectionScope);
                if (response is not null)
                {
                    await TrySendAsync(channel, response, connectionScope.Token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await connectionScope.CancelAsync().ConfigureAwait(false);
            if (subscriptions.Count > 0 || deferred.Count > 0)
            {
                await Task.WhenAll(subscriptions.Concat(deferred)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Answers one decoded frame. Deliberately synchronous: dispatching is pure CPU plus
    /// SQLite, and starting a subscription only hands a pump task to the caller, so there is
    /// nothing here to await.
    /// </summary>
    private JsonObject? Handle(
        FrameChannel channel,
        byte[] body,
        List<Task> subscriptions,
        List<Task> deferred,
        CancellationTokenSource connectionScope)
    {
        IpcRequest request;
        try
        {
            request = IpcEnvelope.ParseRequest(body);
        }
        catch (CollectorException ex)
        {
            // Answer against the id the client actually sent when it can still be read, so a
            // rejected request completes on the client instead of timing out.
            return IpcEnvelope.Failure(
                IpcEnvelope.TryPeekRequestId(body) ?? ZeroUuid, IpcEnvelope.ErrorMessageType, ex);
        }

        try
        {
            if (string.Equals(request.MessageType, "SubscribeLiveEvents", StringComparison.Ordinal))
            {
                var acknowledgement = StartSubscription(channel, request, subscriptions, connectionScope);
                return IpcEnvelope.Success(request.RequestId, request.MessageType, acknowledgement);
            }

            if (MessageDispatcher.AsynchronousMessageTypes.Contains(request.MessageType))
            {
                // Answered later, on its own, so a sentence waiting for the speech service does not
                // hold up the status polls behind it on this same connection. Finished answers are
                // dropped here; the rest are awaited when the connection closes.
                deferred.RemoveAll(task => task.IsCompleted);
                deferred.Add(RespondLaterAsync(channel, request, connectionScope.Token));
                return null;
            }

            var payload = _dispatcher.Dispatch(request);
            return IpcEnvelope.Success(request.RequestId, request.MessageType, payload);
        }
        catch (CollectorException ex)
        {
            var messageType = MessageDispatcher.KnownMessageTypes.Contains(request.MessageType)
                ? request.MessageType
                : IpcEnvelope.ErrorMessageType;
            return IpcEnvelope.Failure(request.RequestId, messageType, ex);
        }
        catch (Exception ex)
        {
            // Never let one bad message end the connection, and never leak a stack trace to
            // the client: the details go to the local log only.
            _log?.Invoke("unhandled error while dispatching a message", ex);
            return IpcEnvelope.Failure(
                request.RequestId,
                IpcEnvelope.ErrorMessageType,
                new CollectorException(ErrorCodes.Internal, "内部错误，详情见本机诊断日志。"));
        }
    }

    private async Task RespondLaterAsync(FrameChannel channel, IpcRequest request, CancellationToken cancellationToken)
    {
        JsonObject envelope;
        try
        {
            var payload = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            envelope = IpcEnvelope.Success(request.RequestId, request.MessageType, payload);
        }
        catch (CollectorException ex)
        {
            envelope = IpcEnvelope.Failure(request.RequestId, request.MessageType, ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke("unhandled error while answering a deferred message", ex);
            envelope = IpcEnvelope.Failure(
                request.RequestId,
                IpcEnvelope.ErrorMessageType,
                new CollectorException(ErrorCodes.Internal, "内部错误，详情见本机诊断日志。"));
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await TrySendAsync(channel, envelope, cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonObject StartSubscription(
        FrameChannel channel,
        IpcRequest request,
        List<Task> subscriptions,
        CancellationTokenSource connectionScope)
    {
        var reader = new PayloadReader(request.Payload);
        reader.RejectUnknown("event_types", "heartbeat_interval_ms");
        var eventTypes = reader.StringArray("event_types", 16, MaxEventTypeLength);
        var heartbeat = reader.Int("heartbeat_interval_ms", 1000, 60000) ?? DefaultHeartbeatMs;

        // Pumps that have already ended are not subscriptions any more; dropping them here is
        // what keeps a long-lived connection that reconnects its stream from accumulating
        // finished tasks.
        subscriptions.RemoveAll(task => task.IsCompleted);
        if (subscriptions.Count >= MaxLiveSubscriptions)
        {
            throw CollectorException.BadRequest(
                "该连接已订阅实时事件。请复用现有订阅，或断开后重新连接。", "message_type");
        }

        var subscriptionId = Guid.NewGuid().ToString("D");
        var subscription = _dispatcher.Host.LiveEvents.Subscribe(subscriptionId);
        subscriptions.Add(PumpAsync(
            channel,
            request.RequestId,
            subscription,
            eventTypes.Count == 0 ? null : new HashSet<string>(eventTypes, StringComparer.Ordinal),
            TimeSpan.FromMilliseconds(heartbeat),
            connectionScope.Token));

        return new JsonObject
        {
            ["subscription_id"] = subscriptionId,
            ["heartbeat_interval_ms"] = heartbeat,
        };
    }

    private async Task PumpAsync(
        FrameChannel channel,
        string subscriptionRequestId,
        LiveEventSubscription subscription,
        IReadOnlySet<string>? eventTypes,
        TimeSpan heartbeatInterval,
        CancellationToken cancellationToken)
    {
        try
        {
            var sinceDelivery = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested)
            {
                var remaining = heartbeatInterval - sinceDelivery.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    if (subscription.IsClosed)
                    {
                        return;
                    }

                    // Only delivered events reset the deadline. Continuous traffic excluded
                    // by event_types must not starve the client's heartbeat. Publishing keeps
                    // one sequence space for every subscriber.
                    _dispatcher.Host.LiveEvents.PublishHeartbeat();
                    sinceDelivery.Restart();
                    remaining = heartbeatInterval;
                }

                var liveEvent = await subscription
                    .ReadAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);

                if (liveEvent is null)
                {
                    if (subscription.IsClosed)
                    {
                        return;
                    }

                    continue;
                }

                // event_types filters what the client asked to see. Heartbeat is exempt: its
                // whole job is to prove the subscription is alive, and a filter that silenced
                // it would make an idle stream indistinguishable from a dead one.
                if (eventTypes is not null &&
                    liveEvent["event_type"]?.GetValue<string>() is { } eventType &&
                    !string.Equals(eventType, "Heartbeat", StringComparison.Ordinal) &&
                    !eventTypes.Contains(eventType))
                {
                    continue;
                }

                await channel
                    .WriteAsync(IpcEnvelope.Event(subscriptionRequestId, liveEvent), cancellationToken)
                    .ConfigureAwait(false);
                sinceDelivery.Restart();
            }
        }
        catch (OperationCanceledException)
        {
            // The connection is closing. A stop is not a failure.
        }
        catch (Exception ex)
        {
            _log?.Invoke("live event pump stopped", ex);
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private async Task TrySendAsync(FrameChannel channel, JsonObject envelope, CancellationToken cancellationToken)
    {
        try
        {
            await channel.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _log?.Invoke("peer disconnected before the response could be written", null);
        }
    }
}
