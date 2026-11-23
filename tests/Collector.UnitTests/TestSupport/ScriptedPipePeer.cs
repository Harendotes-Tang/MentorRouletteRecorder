using System.Text.Json.Nodes;
using System.Threading.Channels;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A pipe peer without a pipe: it hands the connection the framed requests, then blocks reads until cancelled,
/// and parses every frame the connection writes back into <see cref="NextWrittenAsync"/>.
/// </summary>
/// <param name="requests">Requests to send, in order.</param>
internal sealed class ScriptedPipePeer(params IpcRequest[] requests) : AsyncOnlyStream
{
    private readonly byte[] _input = requests
        .SelectMany(request => IpcTestFrames.Request(request.RequestId, request.MessageType, request.Payload))
        .ToArray();

    private readonly Channel<JsonObject> _writes = Channel.CreateUnbounded<JsonObject>();
    private int _offset;
    private int _writtenResponses;

    /// <summary>Runs before each write is taken in; a test cancels the connection with it.</summary>
    public Action? BeforeWrite { get; init; }

    public int WrittenResponses => Volatile.Read(ref _writtenResponses);

    public override bool CanWrite => true;

    /// <summary>The next envelope the connection wrote.</summary>
    public ValueTask<JsonObject> NextWrittenAsync(CancellationToken cancellationToken = default) =>
        _writes.Reader.ReadAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _input.Length)
        {
            var count = Math.Min(buffer.Length, _input.Length - _offset);
            _input.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeforeWrite?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        _writes.Writer.TryWrite(IpcTestFrames.Reply(buffer.Span));
        Interlocked.Increment(ref _writtenResponses);
        return ValueTask.CompletedTask;
    }

    // Stream.FlushAsync's default runs Flush on the thread pool. Under the full gate that
    // pool is shared with the Soak suites, so a reply could stay "unwritten" for seconds;
    // nine such replies in a burst make PipeConnection close the connection at its
    // deferred-response limit and the remaining replies never arrive. Complete inline.
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
