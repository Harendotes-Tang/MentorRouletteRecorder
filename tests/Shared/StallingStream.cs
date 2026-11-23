namespace MentorRecorder.Collector.Tests;

/// <summary>Serves a prefix, if any, then waits for bytes that never come until the read is cancelled.</summary>
/// <param name="prefix">Bytes served before the stall; none by default.</param>
internal sealed class StallingStream(byte[]? prefix = null) : AsyncOnlyStream
{
    private readonly byte[] _prefix = prefix ?? Array.Empty<byte>();
    private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    /// <summary>Completes once the stalled read has been cancelled.</summary>
    public Task Stalled => _stalled.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stalled.TrySetResult();
        }

        return 0;
    }
}
