using System.Buffers.Binary;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// One FFXIV message as it arrives from Machina, split into the two framing headers and the
/// payload that follows them.
/// </summary>
/// <param name="DeclaredLength">Segment length declared in the segment header.</param>
/// <param name="SegmentType">Segment type; <see cref="FfxivFraming.SegmentTypeIpc"/> is the one that carries an IPC header.</param>
/// <param name="Opcode">IPC opcode, or 0 when the segment carries no IPC header.</param>
/// <param name="Seconds">Timestamp field of the IPC header, in seconds; 0 when absent.</param>
/// <param name="PayloadOffset">Offset of the first payload byte inside the message.</param>
/// <param name="PayloadLength">Number of payload bytes.</param>
public readonly record struct FfxivFrame(
    uint DeclaredLength,
    ushort SegmentType,
    ushort Opcode,
    uint Seconds,
    int PayloadOffset,
    int PayloadLength);

/// <summary>
/// Reads the two fixed FFXIV framing headers that Machina hands us, and nothing else.
///
/// Machina has already reassembled TCP, split the bundle and decompressed it, so each
/// callback carries exactly one message: a 16-byte segment header, optionally a 16-byte IPC
/// header, then the payload. Only the three routing fields -- segment type, opcode and the
/// header timestamp -- are extracted; the rest is passed on as opaque bytes. The layout below
/// is Machina's own transport framing (<c>Machina.FFXIV.Headers.Server_MessageHeader</c>), not
/// knowledge about any particular message; see docs/protocol-profile-format.md for what does
/// require evidence.
///
/// Layout, matching <c>Server_MessageHeader</c> in Machina.FFXIV 2.4.7.7 field for field
/// (MessageLength, ActorID, LoginUserID, Unknown1, Unknown2, MessageType, Unknown3, Seconds,
/// Unknown4 -- 32 bytes total, little-endian):
///
/// <code>
///   segment header (16 bytes)
///     0  u32  size            (Server_MessageHeader.MessageLength)
///     4  u32  source actor    (ActorID)
///     8  u32  target actor    (LoginUserID)
///    12  u16  segment type    (low half of Unknown1)
///    14  u16  padding         (high half of Unknown1)
///   IPC header (16 bytes, only when segment type == 3)
///    16  u16  reserved        (Unknown2)
///    18  u16  opcode          (MessageType)
///    20  u16  padding         (low half of Unknown3)
///    22  u16  server id       (high half of Unknown3)
///    24  u32  seconds         (Seconds)
///    28  u32  padding         (Unknown4)
///   payload follows
/// </code>
///
/// Every method here is total: a short, truncated or nonsensical buffer returns false and is
/// counted as a decode error. Capture code must never throw on the data it observes.
/// </summary>
public static class FfxivFraming
{
    /// <summary>Size of the segment header in bytes.</summary>
    public const int SegmentHeaderBytes = 16;

    /// <summary>Size of the IPC header in bytes.</summary>
    public const int IpcHeaderBytes = 16;

    /// <summary>Size of both headers together.</summary>
    public const int HeaderBytes = SegmentHeaderBytes + IpcHeaderBytes;

    /// <summary>Segment type that carries an IPC header and an opcode.</summary>
    public const ushort SegmentTypeIpc = 3;

    private const int OffsetDeclaredLength = 0;
    private const int OffsetSegmentType = 12;
    private const int OffsetOpcode = 18;
    private const int OffsetSeconds = 24;

    /// <summary>
    /// Reads the framing of one message. Returns false for anything that cannot be read as a
    /// well-formed segment; the caller counts that as a decode error.
    /// </summary>
    /// <param name="message">One complete message as delivered by Machina.</param>
    /// <param name="frame">Extracted framing on success.</param>
    public static bool TryRead(ReadOnlySpan<byte> message, out FfxivFrame frame)
    {
        frame = default;

        if (message.Length < SegmentHeaderBytes)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(message[OffsetDeclaredLength..]);

        // A declared length beyond the buffer means truncation, so nothing past the header can
        // be trusted; one below the header size is nonsensical for a segment that contains it.
        if (declared < SegmentHeaderBytes || declared > (uint)message.Length)
        {
            return false;
        }

        var segmentType = BinaryPrimitives.ReadUInt16LittleEndian(message[OffsetSegmentType..]);
        if (segmentType != SegmentTypeIpc)
        {
            // Keep-alives and session control segments are valid framing but carry no IPC
            // header, hence no opcode.
            frame = new FfxivFrame(
                declared, segmentType, 0, 0, SegmentHeaderBytes, (int)declared - SegmentHeaderBytes);
            return true;
        }

        if (declared < HeaderBytes)
        {
            return false;
        }

        frame = new FfxivFrame(
            declared,
            segmentType,
            BinaryPrimitives.ReadUInt16LittleEndian(message[OffsetOpcode..]),
            BinaryPrimitives.ReadUInt32LittleEndian(message[OffsetSeconds..]),
            HeaderBytes,
            (int)declared - HeaderBytes);
        return true;
    }
}
