using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The Named Pipe server. The only inbound surface this process has.
///
/// It is a pipe, not a socket: nothing binds a port, nothing listens on the network, and the
/// ACL grants the current user's SID alone.
///
/// The restriction is an explicit <see cref="PipeSecurity"/> with a single allow rule rather
/// than <see cref="PipeOptions.CurrentUserOnly"/>, because .NET refuses the two together
/// (<c>NamedPipeServerStreamAcl.Create</c> throws when both are given) and an explicit ACL is
/// auditable from outside the process. The client keeps
/// <see cref="PipeOptions.CurrentUserOnly"/>, which does what the ACL cannot: it verifies that
/// the server it just connected to is owned by this same user.
///
/// Isolation is per connection and per message. A connection that sends garbage is closed
/// without touching the others; a message that throws is answered with an error envelope and
/// the connection keeps serving, so one misbehaving client can never take the Collector down
/// (docs/architecture.md section 4).
///
/// With all <see cref="MaxConcurrentConnections"/> instances in use, creating the next one
/// fails with <c>ERROR_PIPE_BUSY</c>. That is the operating system saying "wait", so it does
/// not count towards <see cref="MaxConsecutiveAcceptFailures"/>: the loop keeps serving and
/// backs off a little between attempts. Counted as a failure, eight idle connections would end
/// the accept loop and take the process down mid-session (review finding H1).
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    /// <summary>Concurrent pipe instances accepted at once.</summary>
    public const int MaxConcurrentConnections = 8;

    /// <summary>Accept failures in a row before the server gives up and reports the fault.</summary>
    public const int MaxConsecutiveAcceptFailures = 10;

    /// <summary>Win32 <c>ERROR_PIPE_BUSY</c>: every instance of the pipe is in use.</summary>
    private const int ErrorPipeBusy = 231;

    /// <summary>Win32 <c>ERROR_NO_DATA</c>, seen when an instance is being torn down.</summary>
    private const int ErrorNoData = 232;

    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Shortest wait before retrying an accept that found the pipe full.</summary>
    private static readonly TimeSpan MinBackpressureDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Longest wait before retrying an accept that found the pipe full.</summary>
    private static readonly TimeSpan MaxBackpressureDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a give-up drain waits for connections to notice they were cancelled.
    ///
    /// Bounded because the whole point of the give-up path is to end: a connection blocked in
    /// a write to a peer that stopped reading must not be able to hold the shutdown open
    /// indefinitely.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly MessageDispatcher _dispatcher;
    private readonly string _pipeName;
    private readonly Action<string, Exception?>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _workers = new();
    private int _accepted;
    private int _active;
    private bool _disposed;

    /// <summary>Creates a server bound to a pipe name.</summary>
    /// <param name="dispatcher">Dispatcher answering requests.</param>
    /// <param name="pipeName">Bare pipe name; the current user's name when null.</param>
    /// <param name="log">Optional diagnostic sink; never receives payloads.</param>
    public PipeServer(MessageDispatcher dispatcher, string? pipeName = null, Action<string, Exception?>? log = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? PipeNaming.CurrentUserPipeName() : pipeName;
        _log = log;
    }

    /// <summary>Bare pipe name this server listens on.</summary>
    public string PipeName => _pipeName;

    /// <summary>Number of connections accepted since start.</summary>
    public int AcceptedCount => Volatile.Read(ref _accepted);

    /// <summary>Number of connections currently being served.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>
    /// Accepts connections until <paramref name="cancellationToken"/> or <see cref="StopAsync"/>
    /// fires. Each accepted connection is served on its own task.
    /// </summary>
    /// <param name="cancellationToken">Stops the accept loop.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _stopping.Token);
        var token = linked.Token;
        var consecutiveFailures = 0;
        var backpressureWaits = 0;

        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreateStream();
                await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                break;
            }
            catch (Exception ex) when (IsPipeFull(ex))
            {
                // Every instance is busy, which is not a fault: the next client waits for one
                // to leave. Serving continues, this does not count towards the give-up
                // threshold, and the wait grows to a small cap so a full pipe does not spin.
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                if (backpressureWaits == 0)
                {
                    _log?.Invoke("all pipe instances are busy; waiting for one to free up", null);
                }

                try
                {
                    await Task.Delay(BackpressureDelay(++backpressureWaits), token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }
            catch (Exception ex)
            {
                // A client that vanished between connect and accept is normal, and a
                // transient failure to create the next pipe instance must not end the server.
                // Retry, but give up after a run of failures rather than spinning forever on
                // something that is never going to work.
                _log?.Invoke("pipe accept failed", ex);
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                if (++consecutiveFailures > MaxConsecutiveAcceptFailures)
                {
                    // Cancel first, then drain. Draining alone would wait on connections that
                    // are parked in a read on a token nobody has cancelled, so the server that
                    // decided to give up would hang instead of reporting the fault.
                    await _stopping.CancelAsync().ConfigureAwait(false);
                    await DrainAsync(DrainTimeout).ConfigureAwait(false);
                    throw;
                }

                await Task.Delay(AcceptRetryDelay, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            consecutiveFailures = 0;
            backpressureWaits = 0;
            Interlocked.Increment(ref _accepted);
            var connection = stream;
            var worker = Task.Run(() => ServeAsync(connection, token), CancellationToken.None);
            lock (_workers)
            {
                _workers.RemoveAll(task => task.IsCompleted);
                _workers.Add(worker);
            }
        }

        await DrainAsync(DrainTimeout).ConfigureAwait(false);
    }

    /// <summary>Signals the accept loop to stop and waits for the connections to finish.</summary>
    public async Task StopAsync()
    {
        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        await DrainAsync(DrainTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// True when an accept failed only because every pipe instance is currently in use.
    ///
    /// Windows reports this as <c>ERROR_PIPE_BUSY</c>, which .NET surfaces as an
    /// <see cref="IOException"/> carrying the Win32 code in the low word of its HResult. It is
    /// matched narrowly on purpose: a genuinely broken pipe must still reach the failure path
    /// and still be able to end the loop.
    /// </summary>
    /// <param name="error">Exception thrown while creating or awaiting an instance.</param>
    private static bool IsPipeFull(Exception error) =>
        error is IOException io &&
        (io.HResult & 0xFFFF) is ErrorPipeBusy or ErrorNoData;

    /// <summary>Backoff for a full pipe: short at first, capped so it never spins.</summary>
    /// <param name="waits">How many times in a row the pipe has been found full.</param>
    private static TimeSpan BackpressureDelay(int waits)
    {
        var scaled = MinBackpressureDelay * Math.Min(waits, 16);
        return scaled > MaxBackpressureDelay ? MaxBackpressureDelay : scaled;
    }

    /// <summary>
    /// Waits for the connections in flight to finish, for at most <paramref name="timeout"/>.
    ///
    /// The bound matters on the give-up path: a connection can be parked in a write to a peer
    /// that has stopped reading, and an unbounded wait would leave the shutdown stuck.
    /// </summary>
    /// <param name="timeout">Longest wait before the remaining workers are abandoned.</param>
    private async Task DrainAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_workers)
        {
            pending = _workers.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke("a connection did not finish within the drain timeout", null);
        }
        catch (OperationCanceledException)
        {
            // Draining after a stop request: cancelled workers are the expected outcome,
            // not something to propagate out of StopAsync.
        }
        catch (Exception ex)
        {
            _log?.Invoke("a connection faulted while the server was stopping", ex);
        }
    }

    private NamedPipeServerStream CreateStream()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new CollectorException(
                ErrorCodes.Internal, "无法读取当前用户 SID，命名管道 ACL 无法设置。");

        // Exactly one entry. No Everyone, no Authenticated Users, no NETWORK.
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            MaxConcurrentConnections,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    private async Task ServeAsync(NamedPipeServerStream stream, CancellationToken serverToken)
    {
        Interlocked.Increment(ref _active);
        using var connectionScope = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var connection = new PipeConnection(stream, _dispatcher, _log);
        try
        {
            await connection.ServeAsync(connectionScope.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One connection's failure is that connection's problem only.
            _log?.Invoke("pipe connection ended with an error", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            try
            {
                if (stream.IsConnected)
                {
                    stream.Disconnect();
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The peer already went away; nothing to disconnect.
            }

            await stream.DisposeAsync().ConfigureAwait(false);
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
        await StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}

/// <summary>Reads and writes whole frames on one duplex stream.</summary>
public sealed class FrameChannel
{
    private readonly Stream _stream;
    private readonly FrameBuffer _buffer = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _readBuffer = new byte[16 * 1024];

    /// <summary>Wraps a connected stream.</summary>
    /// <param name="stream">Connected duplex stream.</param>
    public FrameChannel(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Reads one frame body, or null at end of stream. Throws
    /// <c>ERR_PROTOCOL_VERSION</c> when the peer announces an oversized frame; the caller
    /// must close the connection because the byte stream can no longer be resynchronised.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<byte[]?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            switch (_buffer.TryTake(out var body))
            {
                case FrameStatus.Ok:
                    return body;
                case FrameStatus.Oversized:
                    throw new CollectorException(
                        ErrorCodes.ProtocolVersion,
                        "收到超过 4 MiB 的帧，连接已断开。",
                        new Dictionary<string, object?> { ["max_frame_bytes"] = FrameCodec.MaxFrameBytes });
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return null;
            }

            if (read <= 0)
            {
                return null;
            }

            _buffer.Append(_readBuffer.AsSpan(0, read));
        }
    }

    /// <summary>Writes one envelope as a frame; writes are serialised per connection.</summary>
    /// <param name="envelope">Envelope to send.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task WriteAsync(JsonObject envelope, CancellationToken cancellationToken)
    {
        var frame = FrameCodec.Encode(IpcEnvelope.ToBytes(envelope));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
