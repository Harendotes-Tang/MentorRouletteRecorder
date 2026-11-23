namespace MentorRecorder.Collector.Tests;

/// <summary>
/// Serves a prefix in one read, then fails the way a connection reset mid-answer does. Without a prefix the
/// first read already fails.
/// </summary>
/// <param name="prefix">Bytes served before the failure; none by default.</param>
internal sealed class FailingStream(byte[]? prefix = null) : AsyncOnlyStream
{
    private readonly byte[] _prefix = prefix ?? Array.Empty<byte>();
    private bool _served;

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_served || _prefix.Length == 0)
        {
            return ValueTask.FromException<int>(new IOException("the connection was reset"));
        }

        _served = true;
        var count = Math.Min(buffer.Length, _prefix.Length);
        _prefix.AsMemory(0, count).CopyTo(buffer);
        return ValueTask.FromResult(count);
    }
}
