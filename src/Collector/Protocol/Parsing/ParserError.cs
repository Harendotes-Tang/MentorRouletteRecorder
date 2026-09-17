using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>Why one decoded message did not become a semantic event.</summary>
public enum ParserErrorCode
{
    /// <summary>
    /// No profile message claims this (direction, opcode) pair. Since contracts/CHANGELOG.md
    /// entry 18 the live parser no longer counts these as refusals (they are
    /// <see cref="ParserStatsSnapshot.Ignored"/>); the code stays for rows already persisted
    /// and for the replay tooling's error table.
    /// </summary>
    UnknownOpcode,

    /// <summary>The payload length does not satisfy the declared length rule.</summary>
    LengthMismatch,

    /// <summary>A declared field would read past the end of the payload.</summary>
    OffsetOutOfBounds,

    /// <summary>A field parsed but fell outside its declared constraints.</summary>
    FieldConstraint,

    /// <summary>No usable profile is in force, so nothing may be parsed at all.</summary>
    ProfileUnsupported,

    /// <summary>The parser itself failed. Counted, never thrown at the capture thread.</summary>
    Internal,
}

/// <summary>
/// One counted refusal.
///
/// Payload bytes are never stored here, not even for an unknown opcode: the whole point of
/// counting unknown opcodes is to notice a game update, and a count is enough for that
/// (docs/privacy-boundary.md section 5).
/// </summary>
/// <param name="Code">Refusal code.</param>
/// <param name="Opcode">Opcode of the offending message.</param>
/// <param name="Direction">Direction of the offending message.</param>
/// <param name="Message">Short, non-sensitive explanation.</param>
/// <param name="AtUtc">
/// When the refusal was counted. Optional so a caller without a clock keeps compiling; the
/// live parser always stamps it, because the diagnostics page has to say <em>when</em> a
/// refusal happened.
/// </param>
public sealed record ParserError(
    ParserErrorCode Code,
    ushort Opcode,
    PacketDirection Direction,
    string Message,
    DateTimeOffset? AtUtc = null)
{
    /// <summary>Wire token written to <c>parser_errors.kind</c>.</summary>
    public string Kind => Code switch
    {
        ParserErrorCode.UnknownOpcode => "E_UNKNOWN_OPCODE",
        ParserErrorCode.LengthMismatch => "E_LEN_MISMATCH",
        ParserErrorCode.OffsetOutOfBounds => "E_OFFSET_OOB",
        ParserErrorCode.FieldConstraint => "E_FIELD_CONSTRAINT",
        ParserErrorCode.ProfileUnsupported => "E_PROFILE_UNSUPPORTED",
        _ => "E_INTERNAL",
    };
}

/// <summary>A point-in-time copy of the parser counters.</summary>
/// <param name="ParseOk">Messages that produced a semantic event.</param>
/// <param name="ParseFailed">Messages refused for any reason.</param>
/// <param name="Duplicates">Messages whose event key had already been seen.</param>
/// <param name="Ignored">
/// Messages no profile message claims. The profile only declares the handful of messages a
/// record needs; everything else the client sends is ordinary traffic, not a refusal, so it
/// is counted here and never enters <paramref name="ParseFailed"/> or the error ring.
/// </param>
/// <param name="LastValidEventAtUtc">Time of the most recent successful parse, or null.</param>
/// <param name="RecentErrors">The most recent refusals, oldest first.</param>
/// <param name="LastValidEventKind">
/// Semantic event type of that most recent successful parse (one of
/// <see cref="ProfileMessageParser.EventKinds"/>), or null. Set together with
/// <paramref name="LastValidEventAtUtc"/>, so the two always describe the same event.
/// </param>
public sealed record ParserStatsSnapshot(
    long ParseOk,
    long ParseFailed,
    long Duplicates,
    long Ignored,
    DateTimeOffset? LastValidEventAtUtc,
    IReadOnlyList<ParserError> RecentErrors,
    string? LastValidEventKind = null);

/// <summary>Read-only view of what the parser has been doing.</summary>
public interface IParserStats
{
    /// <summary>Takes a snapshot of the counters and the bounded error ring.</summary>
    ParserStatsSnapshot GetParserStats();
}

/// <summary>Consumer of verified semantic events.</summary>
public interface ISemanticEventSink
{
    /// <summary>
    /// Handle one verified semantic event. Called on the capture parser thread, in
    /// observation order. Implementations must not throw.
    /// </summary>
    /// <param name="semanticEvent">The event.</param>
    void Accept(SemanticEvent semanticEvent);
}
