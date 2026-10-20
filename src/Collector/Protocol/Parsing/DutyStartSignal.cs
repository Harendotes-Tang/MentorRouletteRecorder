using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>What a director message recognised by <see cref="DutyStartSignal"/> says.</summary>
public enum DutyStartSignalKind
{
    /// <summary>The duty commenced with the player inside; the parameter is the time limit.</summary>
    Commenced,

    /// <summary>The duty's timer is running; the parameter is the time left.</summary>
    TimeRemaining,
}

/// <summary>
/// The two CN director messages that say whether a duty was already under way when the player
/// entered it, recognised by their content and never by their opcode.
///
/// They are two more contents of the generic director message the clear is one content of
/// (<see cref="DutyClearSignal"/>, docs/protocol-profile-format.md section 12), on the same
/// opcode, with the same 40-byte body and the same fixed first twelve bytes. Only the command
/// and the one parameter after it differ:
/// <code>
/// offset  type  value
///      0  u16   0x006D       category
///      2  u16   0
///      4  u16   any          director number of the duty; free
///      6  u16   0x8003       director type
///      8  u32   0x40000001   command: the duty commenced (time limit follows), or
///               0x80000004   command: time remaining (seconds left follow)
///     12  u32   any          the parameter: 7200 at the start of the sample duty, 7199 and
///                            then every 300 s less on the reports that followed
///     16  ...   0            every byte up to the end of the payload
/// </code>
/// The commencement is sent once the party has loaded and the countdown has run; the first
/// remaining-time report follows it within a second and the next ones every five minutes. A
/// player who joins a duty already under way is never sent that duty's commencement, so the
/// first of the two to arrive after the entry marker says how the player got in
/// (docs/state-machine.md section 3.14). Neither is read as a result: the clear keeps its own
/// matcher and is checked first.
///
/// Evidence: the same trace as the clear (duty-trace-2026-10-05.jsonl, SHA-256
/// <see cref="DutyClearSignal.EvidenceSha256"/>): seq 2294 (the commencement, parameter 7200)
/// and seq 2300, 63 ms later (the first report, parameter 7199), then a report every 300 s with
/// the parameter 300 less each time, all on the clear's director 0x80030056 and all 40 bytes.
/// The commencement arrived 74.5 s after the entry marker, with no director message in
/// between. A duty joined in progress has not been traced yet; that the commencement is not
/// sent to a late joiner rests on the client's duty-start behaviour as third-party tooling
/// documents it, and the first such duty on the gaming machine is what confirms it
/// (docs/live-validation-guide.md section 4.1, scenario 4).
///
/// Rules, as for the clear: fail closed, no tolerance on the length, no widening of a constant
/// without a new evidence row and a failing test first.
/// </summary>
public static class DutyStartSignal
{
    /// <summary>Exact payload length, after the IPC header: the same as the clear's.</summary>
    public const int Length = DutyClearSignal.Length;

    /// <summary>u32 little-endian at offset 8 of the commencement.</summary>
    public const uint CommencedCommand = 0x40000001;

    /// <summary>u32 little-endian at offset 8 of a remaining-time report.</summary>
    public const uint TimeRemainingCommand = 0x80000004;

    /// <summary>Offset of the u32 parameter: the time limit, or the seconds left.</summary>
    public const int ParameterOffset = 12;

    /// <summary>Bytes from this offset to the end of the payload are all zero.</summary>
    public const int ZeroFrom = 16;

    /// <summary>Semantic key of the event the parser produces from a commencement.</summary>
    public const string CommencedSemanticKey = "DUTY_COMMENCED:start_signal";

    /// <summary>Semantic key of the event the parser produces from a remaining-time report.</summary>
    public const string TimeRemainingSemanticKey = "DUTY_TIME_REMAINING:timer_signal";

    /// <summary>
    /// Recognises one of the two messages. Allocation-free; the cheap tests come first, so
    /// ordinary traffic fails at the direction or the length.
    /// </summary>
    /// <param name="direction">Direction of the message.</param>
    /// <param name="segmentType">Segment type from the segment header.</param>
    /// <param name="payload">IPC payload after the IPC header.</param>
    /// <param name="kind">Which message it is, when it is one.</param>
    /// <param name="parameter">The time limit or the seconds left, when it is one.</param>
    public static bool TryMatch(
        PacketDirection direction, ushort segmentType, ReadOnlySpan<byte> payload,
        out DutyStartSignalKind kind, out int parameter)
    {
        kind = default;
        parameter = 0;
        if (direction != PacketDirection.ServerToClient
            || segmentType != FfxivFraming.SegmentTypeIpc
            || payload.Length != Length
            || BinaryPrimitives.ReadUInt16LittleEndian(payload) != DutyClearSignal.Category
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) != DutyClearSignal.DirectorType
            || payload[ZeroFrom..].IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        var command = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        if (command == CommencedCommand)
        {
            kind = DutyStartSignalKind.Commenced;
        }
        else if (command == TimeRemainingCommand)
        {
            kind = DutyStartSignalKind.TimeRemaining;
        }
        else
        {
            return false;
        }

        var value = BinaryPrimitives.ReadUInt32LittleEndian(payload[ParameterOffset..]);
        if (value > int.MaxValue)
        {
            // Not a number of seconds any duty has; whatever this is, it is not the signal.
            kind = default;
            return false;
        }

        parameter = (int)value;
        return true;
    }

    /// <summary>The same test on a decoded message, for code that holds messages rather than spans.</summary>
    /// <param name="message">Decoded message.</param>
    /// <param name="kind">Which message it is, when it is one.</param>
    /// <param name="parameter">The time limit or the seconds left, when it is one.</param>
    public static bool TryMatch(DecodedMessage message, out DutyStartSignalKind kind, out int parameter)
    {
        ArgumentNullException.ThrowIfNull(message);
        return TryMatch(
            message.Direction == MessageDirection.Inbound
                ? PacketDirection.ServerToClient
                : PacketDirection.ClientToServer,
            message.SegmentType,
            message.Payload.Span,
            out kind,
            out parameter);
    }
}
