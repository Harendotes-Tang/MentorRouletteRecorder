using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>
/// The CN duty-clear signal, recognised by its content and never by its opcode.
///
/// The clear is not a message of its own. It is one content of the generic self-control
/// message, whose opcode moves with every game patch, so it cannot be declared in a profile
/// and calibration cannot learn it. Its content is fully determined instead: a 40-byte body
/// whose first twelve bytes are fixed (except the duty's director number) and whose remaining
/// 28 bytes are zero. This is the one place in the Collector that recognises a message by
/// constants in code (docs/protocol-profile-format.md section 12). The opcode is never read.
///
/// Layout (little-endian):
/// <code>
/// offset  type  value
///      0  u16   0x006D       category
///      2  u16   0
///      4  u16   any          director number of the duty (0x0056 in the sample); free
///      6  u16   0x8003       director type: the high half of the u32 at offset 4
///      8  u32   0x40000003   command: clear
///     12  ...   0            every byte up to the end of the payload
/// </code>
///
/// Evidence: one observation, method OBSERVED_LOCAL_TRAFFIC, on CN client build
/// 2026.09.15.0000.0000 with Collector 1.6.0-beta.1, recorded 2026-10-05, sample count 1.
/// Source: duty-trace-2026-10-05.jsonl, SHA-256 <see cref="EvidenceSha256"/> (kept on the
/// owner's machine; docs/privacy-boundary.md section 5). The clear is trace row seq 62088,
/// t_ms 2046833, connection 7a91482e, S2C, segment type 3, opcode 0x0204, length 40,
/// h12 1b7b8dd132a5. The trace stores no payload, only the first 12 hex digits of each
/// payload's SHA-256. The body built from the constants below plus 0x0056 hashes to
/// 1b7b8dd132a57e39..., so every constant is tied to that row. The 15 other messages of
/// director 0x80030056 in the same trace (start 7200, remaining time, progress 1 to 5) are
/// consistent with it. All 858 messages on the carrier opcode were 40 bytes; the historical
/// 32-byte length matched nothing on this build.
///
/// Rules for this class. Fail closed: anything that is not exactly this shape is not the
/// signal, and the length has no tolerance. A constant changes only on new evidence, with a
/// new evidence row in docs/protocol-profile-format.md section 12 and a failing test first.
/// A constant is never widened to accept more.
/// </summary>
public static class DutyClearSignal
{
    /// <summary>Exact payload length, after the IPC header. No other length is the signal.</summary>
    public const int Length = 40;

    /// <summary>u16 little-endian at offset 0.</summary>
    public const ushort Category = 0x006D;

    /// <summary>u16 little-endian at offset 6, the high half of the director id at offset 4.</summary>
    public const ushort DirectorType = 0x8003;

    /// <summary>u32 little-endian at offset 8.</summary>
    public const uint Command = 0x40000003;

    /// <summary>Bytes from this offset to the end of the payload are all zero.</summary>
    public const int ZeroFrom = 12;

    /// <summary>Semantic key of the victory event the parser produces from the signal.</summary>
    public const string SemanticKey = "DUTY_RESULT:clear_signal";

    /// <summary>SHA-256 of the local trace file every constant above is tied to.</summary>
    public const string EvidenceSha256 = "c398a7b5be6c5d1967eac585598947d1df27f1ff7695bec67ebe423a30541ad2";

    /// <summary>
    /// True when the message is exactly the observed clear: server to client, an IPC segment,
    /// 40 bytes, and every fixed field and zero byte as observed. Allocation-free; the cheap
    /// tests come first, so ordinary traffic fails at the direction or the length.
    /// </summary>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="segmentType">Segment type from the segment header.</param>
    /// <param name="payload">IPC payload after the IPC header.</param>
    public static bool Matches(PacketDirection direction, ushort segmentType, ReadOnlySpan<byte> payload) =>
        direction == PacketDirection.ServerToClient
        && segmentType == FfxivFraming.SegmentTypeIpc
        && payload.Length == Length
        && BinaryPrimitives.ReadUInt16LittleEndian(payload) == Category
        && BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) == 0
        && BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) == DirectorType
        && BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]) == Command
        && payload[ZeroFrom..].IndexOfAnyExcept((byte)0) < 0;

    /// <summary>The same test on a decoded message, for code that holds messages rather than spans.</summary>
    /// <param name="message">Decoded message.</param>
    public static bool Matches(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Matches(
            message.Direction == MessageDirection.Inbound
                ? PacketDirection.ServerToClient
                : PacketDirection.ClientToServer,
            message.SegmentType,
            message.Payload.Span);
    }
}
