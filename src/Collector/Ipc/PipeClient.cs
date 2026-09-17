using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Ipc;

/// <summary>One answered request.</summary>
/// <param name="Ok">True when the request succeeded.</param>
/// <param name="MessageType">Message type echoed by the server.</param>
/// <param name="Payload">Response payload.</param>
/// <param name="ErrorCode">Contract error code when <paramref name="Ok"/> is false.</param>
/// <param name="ErrorMessage">User-facing error message when <paramref name="Ok"/> is false.</param>
public sealed record IpcResponse(
    bool Ok,
    string MessageType,
    JsonObject Payload,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Throws when the response is an error; otherwise returns the payload.</summary>
    public JsonObject Require() => Ok
        ? Payload
        : throw new CollectorException(
            ErrorCode ?? ErrorCodes.Internal, ErrorMessage ?? "请求失败。");
}

/// <summary>
/// A Named Pipe client for tests and tooling, living in the Collector assembly so both sides
/// of the wire are exercised by the same code that defines it.
///
/// It correlates responses by <c>request_id</c> and hands live events to a callback. It is a
/// pure client: it never creates a server and never touches the network stack.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pending =
        new(StringComparer.Ordinal);

    private readonly CancellationTokenSource _closing = new();
    private NamedPipeClientStream? _stream;
    private FrameChannel? _channel;
    private Task? _reader;
    private bool _disposed;

    /// <summary>Creates a client for a pipe name.</summary>
    /// <param name="pipeName">Bare pipe name; the current user's name when null.</param>
    public PipeClient(string? pipeName = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeNaming.CurrentUserPipeName() : pipeName;
    }

    /// <summary>Raised for every live event frame.</summary>
    public event Action<JsonObject>? EventReceived;

    /// <summary>
    /// Raised for every decoded frame, before it is correlated, with the whole envelope
    /// rather than just the payload.
    ///
    /// It lets the contract tests validate what actually went over the wire, <c>ok</c>,
    /// <c>error</c> and unawaited frames included, rather than a reconstruction of it.
    /// </summary>
    public event Action<JsonObject>? EnvelopeReceived;

    /// <summary>Bare pipe name this client connects to.</summary>
    public string PipeName => _pipeName;

    /// <summary>True while the underlying pipe is connected.</summary>
    public bool IsConnected => _stream?.IsConnected ?? false;

    /// <summary>Connects to the server.</summary>
    /// <param name="timeout">How long to wait for the server to accept.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var stream = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(timeout);
        try
        {
            await stream.ConnectAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new CollectorException(
                ErrorCodes.Internal,
                "无法连接 Collector 命名管道，请确认 Collector 正在运行。",
                new Dictionary<string, object?> { ["timeout_ms"] = (long)timeout.TotalMilliseconds });
        }

        _stream = stream;
        _channel = new FrameChannel(stream);
        _reader = Task.Run(() => ReadLoopAsync(_closing.Token), CancellationToken.None);
    }

    /// <summary>Sends one request and waits for its response.</summary>
    /// <param name="messageType">Message type from the contract.</param>
    /// <param name="payload">Request payload; an empty object when null.</param>
    /// <param name="requestId">Idempotency key; a fresh UUID when null.</param>
    /// <param name="timeout">How long to wait for the response.</param>
    /// <param name="protocolVersion">
    /// Version to announce. Only tests override it, to prove that a mismatch is refused.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<IpcResponse> SendAsync(
        string messageType,
        JsonObject? payload = null,
        string? requestId = null,
        TimeSpan? timeout = null,
        int protocolVersion = IpcEnvelope.ProtocolVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageType);

        var channel = _channel
            ?? throw new CollectorException(ErrorCodes.Internal, "尚未连接 Collector。");

        var id = requestId ?? Guid.NewGuid().ToString("D");
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var envelope = new JsonObject
        {
            ["protocol_version"] = protocolVersion,
            ["request_id"] = id,
            ["message_type"] = messageType,
            ["payload"] = payload ?? new JsonObject(),
        };

        try
        {
            await channel.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            return await completion.Task
                .WaitAsync(timeout ?? TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a raw frame body, for tests that need malformed input.</summary>
    /// <param name="body">Raw UTF-8 body to frame and send.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task SendRawAsync(byte[] body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        var stream = _stream ?? throw new CollectorException(ErrorCodes.Internal, "尚未连接 Collector。");
        var frame = FrameCodec.Encode(body);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends an arbitrary length prefix, for tests of the oversize guard.</summary>
    /// <param name="declaredLength">Length to announce.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task SendRawPrefixAsync(uint declaredLength, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new CollectorException(ErrorCodes.Internal, "尚未连接 Collector。");
        var prefix = new byte[FrameCodec.PrefixBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(prefix, declaredLength);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var channel = _channel;
        if (channel is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var body = await channel.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    break;
                }

                Deliver(body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailAll(ErrorCodes.Internal, ex.Message);
            return;
        }

        FailAll(ErrorCodes.Internal, "Collector 连接已关闭。");
    }

    private void Deliver(byte[] body)
    {
        if (JsonNode.Parse(body) is not JsonObject envelope)
        {
            return;
        }

        EnvelopeReceived?.Invoke(envelope);

        var messageType = envelope["message_type"]?.GetValue<string>() ?? IpcEnvelope.ErrorMessageType;
        var payload = envelope["payload"] as JsonObject ?? new JsonObject();

        if (string.Equals(messageType, IpcEnvelope.EventMessageType, StringComparison.Ordinal))
        {
            EventReceived?.Invoke(payload);
            return;
        }

        var requestId = envelope["request_id"]?.GetValue<string>();
        if (requestId is null || !_pending.TryRemove(requestId, out var completion))
        {
            return;
        }

        var ok = envelope["ok"]?.GetValue<bool>() ?? true;
        if (ok)
        {
            completion.TrySetResult(new IpcResponse(true, messageType, payload, null, null));
            return;
        }

        var error = envelope["error"] as JsonObject ?? payload;
        completion.TrySetResult(new IpcResponse(
            false,
            messageType,
            payload,
            error["code"]?.GetValue<string>() ?? ErrorCodes.Internal,
            error["message"]?.GetValue<string>() ?? string.Empty));
    }

    private void FailAll(string code, string message)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
            {
                completion.TrySetResult(new IpcResponse(false, IpcEnvelope.ErrorMessageType, new JsonObject(), code, message));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _closing.CancelAsync().ConfigureAwait(false);

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The read loop ends with the stream; nothing to report.
            }
        }

        _closing.Dispose();
    }
}
