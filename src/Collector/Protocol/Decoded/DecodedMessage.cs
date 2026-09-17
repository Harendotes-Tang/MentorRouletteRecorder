namespace MentorRecorder.Collector.Protocol.Decoded;

/// <summary>Direction of a decoded FF14 IPC message relative to the local client.</summary>
public enum MessageDirection
{
    /// <summary>Server to client.</summary>
    Inbound,

    /// <summary>Client to server.</summary>
    Outbound,
}

/// <summary>
/// One FF14 IPC segment as produced by the capture layer (Phase 2) after Machina has
/// reassembled TCP and decoded the bundle. This is the ONLY hand-off type between capture
/// and protocol parsing (Phase 3). It carries raw bytes in memory for the parser and is
/// never persisted as-is (docs/privacy-boundary.md section 5).
/// </summary>
/// <param name="CaptureSessionId">Capture session that observed the message.</param>
/// <param name="Direction">Server to client or client to server.</param>
/// <param name="ObservedAtUtc">Wall-clock time of observation.</param>
/// <param name="Mono">Monotonic reading taken at observation; durations use this only.</param>
/// <param name="Epoch">Epoch carried in the bundle header (milliseconds), 0 when absent.</param>
/// <param name="SegmentType">Segment type from the segment header.</param>
/// <param name="Opcode">IPC opcode from the IPC header; 0 when the segment carries no IPC header.</param>
/// <param name="Payload">IPC payload bytes AFTER the IPC header (data only).</param>
/// <param name="ConnectionKey">Opaque, sanitized identifier of the TCP connection (never a full IP).</param>
public sealed record DecodedMessage(
    string CaptureSessionId,
    MessageDirection Direction,
    DateTimeOffset ObservedAtUtc,
    TimeSpan Mono,
    long Epoch,
    ushort SegmentType,
    ushort Opcode,
    ReadOnlyMemory<byte> Payload,
    string ConnectionKey);
