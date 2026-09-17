using System.Buffers.Binary;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.Ipc;

/// <summary>Result of trying to pull one frame out of a receive buffer.</summary>
public enum FrameStatus
{
    /// <summary>A complete frame was extracted.</summary>
    Ok,

    /// <summary>Not enough bytes yet; keep reading.</summary>
    Incomplete,

    /// <summary>The declared length exceeds the cap. The connection must be closed.</summary>
    Oversized,
}

/// <summary>
/// The wire framing: a 4-byte little-endian unsigned length followed by that many bytes of
/// UTF-8 JSON.
///
/// The length is checked <em>before</em> anything is allocated, which is the whole point: a
/// peer that announces a 3 GiB frame gets its connection closed, not a 3 GiB buffer. The cap
/// matches <c>kMaxFrameBytes</c> in <c>src/Desktop/cpp/IpcFraming.h</c>.
/// </summary>
public static class FrameCodec
{
    /// <summary>Largest frame this build will encode or decode.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>Size of the length prefix.</summary>
    public const int PrefixBytes = 4;

    /// <summary>Wraps a JSON body in its length prefix.</summary>
    /// <param name="body">UTF-8 JSON body.</param>
    public static byte[] Encode(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxFrameBytes)
        {
            throw new CollectorException(
                ErrorCodes.Internal,
                "待发送的帧超过 4 MiB 上限，已拒绝发送。",
                new Dictionary<string, object?> { ["byte_count"] = body.Length });
        }

        var frame = new byte[PrefixBytes + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(PrefixBytes));
        return frame;
    }
}

/// <summary>
/// A growable receive buffer that hands out whole frames.
///
/// Kept free of any transport so that partial reads, split prefixes and oversized frames can
/// be unit tested without a pipe.
/// </summary>
public sealed class FrameBuffer
{
    /// <summary>Size the buffer starts at, and returns to whenever it runs dry.</summary>
    public const int InitialCapacity = 8192;

    private byte[] _buffer = new byte[InitialCapacity];
    private int _length;

    /// <summary>Number of buffered bytes not yet consumed.</summary>
    public int Buffered => _length;

    /// <summary>Bytes currently allocated. Exposed so the shrink can be asserted on.</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Appends received bytes.</summary>
    /// <param name="bytes">Bytes just read from the transport.</param>
    public void Append(ReadOnlySpan<byte> bytes)
    {
        EnsureCapacity(_length + bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>
    /// Takes the first complete frame. On <see cref="FrameStatus.Ok"/> the consumed bytes are
    /// removed; on <see cref="FrameStatus.Incomplete"/> the buffer is untouched; on
    /// <see cref="FrameStatus.Oversized"/> the stream can no longer be resynchronised and the
    /// caller must close the connection.
    /// </summary>
    /// <param name="body">Extracted JSON body.</param>
    public FrameStatus TryTake(out byte[] body)
    {
        body = Array.Empty<byte>();
        if (_length < FrameCodec.PrefixBytes)
        {
            return FrameStatus.Incomplete;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
        if (declared > FrameCodec.MaxFrameBytes)
        {
            return FrameStatus.Oversized;
        }

        var total = FrameCodec.PrefixBytes + (int)declared;
        if (_length < total)
        {
            return FrameStatus.Incomplete;
        }

        body = _buffer.AsSpan(FrameCodec.PrefixBytes, (int)declared).ToArray();
        Buffer.BlockCopy(_buffer, total, _buffer, 0, _length - total);
        _length -= total;
        ShrinkWhenEmpty();
        return FrameStatus.Ok;
    }

    /// <summary>Drops everything buffered.</summary>
    public void Clear()
    {
        _length = 0;
        ShrinkWhenEmpty();
    }

    /// <summary>
    /// Returns the buffer to its initial size once nothing is left in it.
    ///
    /// A single four-megabyte frame -- which a client is entitled to send -- would otherwise
    /// keep that capacity for the life of the connection. Shrinking only while the buffer is
    /// empty means no live bytes are ever copied for it (review finding L3).
    /// </summary>
    private void ShrinkWhenEmpty()
    {
        if (_length == 0 && _buffer.Length > InitialCapacity)
        {
            _buffer = new byte[InitialCapacity];
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var capacity = _buffer.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        var grown = new byte[capacity];
        Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
        _buffer = grown;
    }
}
