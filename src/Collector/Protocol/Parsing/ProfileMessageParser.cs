using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>
/// Turns decoded messages into verified semantic events, using nothing but the profile.
///
/// There is not one opcode, one offset or one length in this file. Everything the parser is
/// willing to read comes out of a <see cref="ProtocolProfile"/>, which is why a game update
/// is a new data file rather than a code change, and why an unverified profile can only ever
/// produce refusals (docs/protocol-profile-format.md section 1).
///
/// The class implements the Phase 2 hand-off contract <see cref="IDecodedMessageSink"/> and
/// therefore must never throw: the capture thread owns the bounded queue and cannot afford
/// to lose it to a malformed packet. Every failure path ends in a counted
/// <see cref="ParserError"/> instead, kept in a bounded ring so a broken profile cannot grow
/// memory without limit.
/// </summary>
public sealed class ProfileMessageParser : IDecodedMessageSink, IParserStats
{
    /// <summary>Number of refusals kept for diagnostics.</summary>
    public const int ErrorRingCapacity = 100;

    /// <summary>Number of event keys retained for duplicate detection.</summary>
    public const int DedupCapacity = 512;

    private readonly ProtocolProfile? _profile;
    private readonly ProfileBinding _binding;
    private readonly ISemanticEventSink _sink;
    private readonly IClock _clock;
    private readonly Queue<ParserError> _errors = new(ErrorRingCapacity);
    private readonly BoundedDedupSet _seen = new(DedupCapacity);
    private readonly object _gate = new();

    private long _parseOk;
    private long _parseFailed;
    private long _duplicates;
    private long _ignored;
    private DateTimeOffset? _lastValidEventAtUtc;
    private string? _lastValidEventKind;

    /// <summary>
    /// Every semantic event type this parser can produce, in the order <c>Build</c> lists them. The
    /// contract's <c>CaptureStatus.last_valid_event_kind</c> enum is held to this list by a test.
    /// </summary>
    public static IReadOnlyList<string> EventKinds { get; } = Array.AsReadOnly(new[]
    {
        "CONTENT_FINDER_POP", "ZONE_INITIALIZATION", "ZONE_TERRITORY", "DUTY_RESULT",
        "PLAYER_JOB", "ZONE_LEFT", "INSTANCE_LEFT", "MATCH_CANCELLED",
    });

    /// <summary>Creates a parser bound to one profile.</summary>
    /// <param name="profile">
    /// Profile in force, or null. A null profile, or one whose binding is not usable, refuses
    /// every message with <see cref="ParserErrorCode.ProfileUnsupported"/>.
    /// </param>
    /// <param name="sink">Consumer of the verified events.</param>
    /// <param name="clock">
    /// Clock used to stamp each refusal. The stamp is the only thing the diagnostics page can
    /// use to tell "this profile has always been wrong" from "the game updated ten minutes
    /// ago"; the system clock is used when none is supplied.
    /// </param>
    public ProfileMessageParser(
        ProtocolProfile? profile, ISemanticEventSink sink, IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _profile = profile;
        _binding = profile?.ToBinding() ?? ProfileBinding.FailClosed;
        _sink = sink;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>Profile binding the state machine must be constructed with.</summary>
    public ProfileBinding Binding => _binding;

    /// <summary>True when the parser will attempt to parse anything at all.</summary>
    public bool IsUsable => _profile is not null && _binding.IsUsable;

    /// <inheritdoc />
    public void Accept(DecodedMessage message)
    {
        if (message is null)
        {
            Fail(ParserErrorCode.Internal, 0, PacketDirection.None, "null message");
            return;
        }

        try
        {
            Parse(message);
        }
        catch (Exception ex)
        {
            // The capture thread must survive anything a packet can do to us. Nothing about
            // the payload is recorded, only the exception type.
            Fail(
                ParserErrorCode.Internal,
                message.Opcode,
                ToPacketDirection(message.Direction),
                "unhandled " + ex.GetType().Name);
        }
    }

    /// <inheritdoc />
    public ParserStatsSnapshot GetParserStats()
    {
        lock (_gate)
        {
            return new ParserStatsSnapshot(
                _parseOk, _parseFailed, _duplicates, _ignored, _lastValidEventAtUtc, _errors.ToArray(),
                _lastValidEventKind);
        }
    }

    private void Parse(DecodedMessage message)
    {
        // Framing assigns opcode 0 to segments without an IPC header. Heartbeats and
        // session control remain visible to capture/trace, but are not failed business
        // messages. Keep genuine IPC opcode 0 and synthetic profile segments on the normal
        // fail-closed parsing path; checking opcode alone would hide unknown IPC messages.
        if (message.SegmentType != FfxivFraming.SegmentTypeIpc && message.Opcode == 0)
        {
            return;
        }

        var direction = ToPacketDirection(message.Direction);
        if (_profile is not { } profile || !_binding.IsUsable)
        {
            Fail(
                ParserErrorCode.ProfileUnsupported, message.Opcode, direction,
                "no usable protocol profile is in force");
            return;
        }

        if (profile.IsObfuscated(message.Opcode))
        {
            // Fail-closed by declaration. A scrambled body cannot be read without the
            // client's own tables, which this project neither ships nor derives, so the only
            // honest answer is a refusal the diagnostics page can show
            // (docs/protocol-profile-format.md section 8).
            Fail(
                ParserErrorCode.ProfileUnsupported, message.Opcode, direction,
                "the profile declares this opcode as obfuscated by the client");
            return;
        }

        var definition = Match(profile, direction, message);
        if (definition is null)
        {
            // Not a refusal. A profile declares only the few messages a record needs, and the
            // client sends hundreds of other opcodes every minute; counting those as failures
            // would bury the refusals that matter (length, offset and constraint violations of
            // a *declared* message). They are counted, never ringed and never persisted
            // (contracts/CHANGELOG.md entry 18).
            Ignore();
            return;
        }

        var payload = message.Payload.Span;
        if (!definition.AcceptsLength(payload.Length))
        {
            Fail(
                ParserErrorCode.LengthMismatch, message.Opcode, direction,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{definition.Name}: payload length {payload.Length} violates the declared length rule"));
            return;
        }

        var values = new Dictionary<string, long>(definition.Fields.Count, StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            if (field.Offset < 0 || field.Size <= 0 || field.Offset + field.Size > payload.Length)
            {
                Fail(
                    ParserErrorCode.OffsetOutOfBounds, message.Opcode, direction,
                    definition.Name + "." + field.Name + ": reads past the end of the payload");
                return;
            }

            if (field.Type == ProfileFieldType.Bytes)
            {
                // Opaque runs exist so a profile can describe padding without the parser ever
                // interpreting it. Their content is deliberately not read into a value.
                continue;
            }

            var value = ReadScalar(payload, field);
            if (!field.Constraints.IsSatisfiedBy(value))
            {
                if (field.Role == ProfileFieldRole.Selector)
                {
                    // The profile declared this field as the filter that decides whether the
                    // message is the one we want. A miss is a message about something else,
                    // not a broken profile, so counting it as a refusal would hide the length
                    // and offset violations that do matter (review finding M-2).
                    Ignore();
                    return;
                }

                Fail(
                    ParserErrorCode.FieldConstraint, message.Opcode, direction,
                    definition.Name + "." + field.Name + ": value is outside the declared constraints");
                return;
            }

            values[field.Name] = value;
        }

        var semanticKey = BuildSemanticKey(definition, values);
        var key = new EventKey(
            message.CaptureSessionId,
            direction,
            message.Opcode.ToString(CultureInfo.InvariantCulture),
            message.Epoch,
            HashPayload(payload),
            semanticKey);

        var semanticEvent = Build(definition, values, key, message);
        if (semanticEvent is null)
        {
            Fail(
                ParserErrorCode.FieldConstraint, message.Opcode, direction,
                definition.Name + ": a required field was not declared by the profile");
            return;
        }

        lock (_gate)
        {
            if (!_seen.Add(key.ToCanonicalString()))
            {
                _duplicates++;
            }

            _parseOk++;
            _lastValidEventAtUtc = message.ObservedAtUtc;
            _lastValidEventKind = semanticEvent.EventType;
        }

        // Duplicates are forwarded on purpose: the state machine owns the one deduplication
        // rule (docs/state-machine.md) and the database owns the UNIQUE index. Counting them
        // here is diagnostics, not a second, competing policy.
        _sink.Accept(semanticEvent);
    }

    private static ProfileMessage? Match(
        ProtocolProfile profile, PacketDirection direction, DecodedMessage message)
    {
        foreach (var candidate in profile.Messages)
        {
            if (candidate.Opcode != message.Opcode || candidate.Direction != direction)
            {
                continue;
            }

            if (candidate.SegmentType is { } segmentType && segmentType != message.SegmentType)
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static long ReadScalar(ReadOnlySpan<byte> payload, ProfileField field)
    {
        var slice = payload.Slice(field.Offset, field.Size);
        var little = field.Endian == ProfileEndian.Little;
        return field.Type switch
        {
            ProfileFieldType.U8 => slice[0],
            ProfileFieldType.U16 => little
                ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
                : BinaryPrimitives.ReadUInt16BigEndian(slice),
            ProfileFieldType.U32 => little
                ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
                : BinaryPrimitives.ReadUInt32BigEndian(slice),
            ProfileFieldType.I32 => little
                ? BinaryPrimitives.ReadInt32LittleEndian(slice)
                : BinaryPrimitives.ReadInt32BigEndian(slice),
            // A u64 is narrowed to the signed 64-bit domain the rest of the system uses. A
            // value that does not survive that is refused rather than silently wrapped.
            ProfileFieldType.U64 => checked((long)(little
                ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
                : BinaryPrimitives.ReadUInt64BigEndian(slice))),
            _ => 0L,
        };
    }

    private static SemanticEvent? Build(
        ProfileMessage definition,
        IReadOnlyDictionary<string, long> values,
        EventKey key,
        DecodedMessage message)
    {
        var observed = message.ObservedAtUtc;
        var mono = message.Mono;

        switch (definition.Name)
        {
            case "CONTENT_FINDER_POP":
                if (!values.TryGetValue("roulette_id", out var rouletteId))
                {
                    return null;
                }

                return new ContentFinderPop
                {
                    Key = key,
                    ObservedAtUtc = observed,
                    Mono = mono,
                    RouletteId = ToInt(rouletteId),
                    ContentId = Optional(values, "content_id"),
                };

            case "ZONE_INITIALIZATION":
                return new ZoneInitialization
                {
                    Key = key,
                    ObservedAtUtc = observed,
                    Mono = mono,
                    ContentId = Optional(values, "content_id"),
                    TerritoryId = Optional(values, "territory_id"),
                    InstanceId = Optional(values, "instance_id"),
                    IsDutyInstance = values.TryGetValue("is_duty_instance", out var flag) ? flag != 0 : null,
                };

            // A territory announcement is not an entry decision: the state machine keeps its
            // own verified entry marker and only borrows the territory from this event.
            case "ZONE_TERRITORY":
                if (!values.TryGetValue("territory_id", out var territoryId))
                {
                    return null;
                }

                return new TerritoryObserved
                {
                    Key = key,
                    ObservedAtUtc = observed,
                    Mono = mono,
                    TerritoryId = ToInt(territoryId),
                };

            case "DUTY_RESULT":
                if (!values.TryGetValue("outcome", out var outcome))
                {
                    return null;
                }

                return new DutyResult
                {
                    Key = key,
                    ObservedAtUtc = observed,
                    Mono = mono,
                    Victory = definition.VictoryValues.Contains(outcome),
                };

            case "PLAYER_JOB":
                if (!values.TryGetValue("job_id", out var jobId))
                {
                    return null;
                }

                return new PlayerJob
                {
                    Key = key, ObservedAtUtc = observed, Mono = mono, JobId = ToInt(jobId),
                };

            case "ZONE_LEFT":
                return new ZoneLeft
                {
                    Key = key,
                    ObservedAtUtc = observed,
                    Mono = mono,
                    TerritoryId = Optional(values, "territory_id"),
                };

            case "INSTANCE_LEFT":
                return new InstanceLeft { Key = key, ObservedAtUtc = observed, Mono = mono };

            case "MATCH_CANCELLED":
                return new MatchCancelled { Key = key, ObservedAtUtc = observed, Mono = mono };

            default:
                return null;
        }
    }

    private static string BuildSemanticKey(ProfileMessage definition, IReadOnlyDictionary<string, long> values)
    {
        var builder = new StringBuilder(64);
        builder.Append(definition.Name);
        foreach (var field in definition.Fields)
        {
            if (values.TryGetValue(field.Name, out var value))
            {
                builder.Append(':').Append(field.Name).Append('=')
                    .Append(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static string HashPayload(ReadOnlySpan<byte> payload)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(payload, digest);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static int? Optional(IReadOnlyDictionary<string, long> values, string name) =>
        values.TryGetValue(name, out var value) ? ToInt(value) : null;

    private static int ToInt(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static PacketDirection ToPacketDirection(MessageDirection direction) =>
        direction == MessageDirection.Inbound ? PacketDirection.ServerToClient : PacketDirection.ClientToServer;

    private void Ignore()
    {
        lock (_gate)
        {
            _ignored++;
        }
    }

    private void Fail(ParserErrorCode code, ushort opcode, PacketDirection direction, string message)
    {
        lock (_gate)
        {
            _parseFailed++;
            _errors.Enqueue(new ParserError(
                code, opcode, direction, message, UtcTimestamp.Truncate(_clock.UtcNow)));
            while (_errors.Count > ErrorRingCapacity)
            {
                _errors.Dequeue();
            }
        }
    }
}
