namespace MentorRecorder.Collector.Protocol.Decoded;

/// <summary>
/// Consumer of decoded messages. The capture layer (Phase 2) owns the bounded queue and the
/// parser thread and calls <see cref="Accept"/> from that single thread, in observation order.
/// Implementations (Phase 3 profile parser, diagnostics counters, test recorders) must never
/// throw: a message that cannot be handled is a parser error to count, not a crash.
/// </summary>
public interface IDecodedMessageSink
{
    /// <summary>Handle one decoded message. Must not throw and must not block for long.</summary>
    void Accept(DecodedMessage message);
}
