using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// Projects a fixed set of combat packets into diagnostic clues on existing trace rows.
/// Actor aliases are assigned by encounter order and scoped to this probe and connection.
/// This provides no production stage classification. The trace sink serializes all calls.
/// </summary>
internal sealed class CaptureDutyPhaseProbe
{
    public const string ClientBuild = "2026.11.03.0000.0000";
    public const int FormatVersion = 1;
    public const int MaxMessageRows = 100_000;
    private const int MaxObservationRows = 15_000;
    private const int MaxActors = 8192;

    private const ushort NpcSpawn = 0x0176;
    private const ushort BossSpawn = 0x02fd;
    private const ushort ActorCast = 0x0160;
    private const ushort ActorControl = 0x02df;
    private const ushort ActorControlSelf = 0x00a9;
    private const ushort DeathCategory = 0x0006;

    // Offsets are relative to the body after the IPC header. See the CN 7.58 tables and
    // Machina ActionEffect structures cited in docs/live-validation-guide.md.
    private const int EffectSequenceOffset = 12;
    private const int EffectCountOffset = 33;
    private const int EffectsOffset = 42;
    private const int EffectsPerTarget = 8;
    private const int EffectSize = 8;
    private const int TargetIdSize = 8;
    private const int TargetListBase = 48;

    private readonly Dictionary<(string Connection, uint Actor), int> _aliases = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private long _untrackedActors;
    private int _written;
    private bool _truncated;

    public static bool Supports(Region region, string? build) =>
        region == Region.Cn && build == ClientBuild;

    /// <summary>Projects one inbound IPC message, or returns null for unrelated traffic.</summary>
    /// <param name="message">Decoded message valid for the duration of this call.</param>
    public JsonObject? Observe(DecodedMessage message)
    {
        if (message.Direction != MessageDirection.Inbound ||
            message.SegmentType != FfxivFraming.SegmentTypeIpc)
        {
            return null;
        }

        if (_written >= MaxObservationRows)
        {
            _truncated = true;
            return null;
        }

        var payload = message.Payload.Span;
        var kind = (message.Opcode, payload.Length) switch
        {
            (NpcSpawn, 656) => "spawn",
            // Only the segment header is measured for this optional carrier.
            (BossSpawn, > 0) => "boss_spawn_candidate",
            (ActorCast, 32) => "cast",
            (ActorControl, 24) when BinaryPrimitives.ReadUInt16LittleEndian(payload) == DeathCategory
                => "defeat",
            (ActorControlSelf, DutyClearSignal.Length) when DutyClearSignal.Matches(message) => "duty_clear",
            (ActorControlSelf, DutyStartSignal.Length)
                when DutyStartSignal.TryMatch(message, out var start, out _) &&
                     start == DutyStartSignalKind.Commenced => "duty_start",
            _ => null,
        };

        JsonObject? row;
        if (kind is null)
        {
            row = Action(message, payload);
            kind = "action";
        }
        else if (kind is "duty_start" or "duty_clear")
        {
            row = new JsonObject { ["director"] = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]) };
        }
        else
        {
            var source = Alias(message.ConnectionKey, message.SourceActor);
            var target = kind == "cast"
                ? Alias(message.ConnectionKey, BinaryPrimitives.ReadUInt32LittleEndian(payload[12..]))
                : Alias(message.ConnectionKey, message.TargetActor);
            row = source is null && target is null ? null : new JsonObject
            {
                ["source_npc"] = source,
                ["target_npc"] = target,
            };
        }

        if (row is not null)
        {
            _written++;
            row["kind"] = kind;
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
        }
        return row;
    }

    /// <summary>Returns the format version and aggregate coverage of the written observations.</summary>
    public JsonObject Summary()
    {
        var counts = new JsonObject();
        foreach (var (kind, count) in _counts)
        {
            counts[kind] = count;
        }

        return new JsonObject
        {
            ["version"] = FormatVersion,
            ["rows"] = _written,
            ["truncated"] = _truncated,
            ["actors"] = _aliases.Count,
            ["untracked_actor_references"] = _untrackedActors,
            ["counts"] = counts,
        };
    }

    private JsonObject? Action(DecodedMessage message, ReadOnlySpan<byte> payload)
    {
        var capacity = (message.Opcode, payload.Length) switch
        {
            (0x0190, 124) => 1,
            (0x0233, 636) => 8,
            (0x0119, 1212) => 16,
            (0x039c, 1788) => 24,
            (0x00dd, 2364) => 32,
            _ => 0,
        };
        if (capacity == 0 || payload[EffectCountOffset] is 0 || payload[EffectCountOffset] > capacity)
        {
            return null;
        }

        var source = Alias(message.ConnectionKey, message.SourceActor);
        JsonArray? targets = null;
        var targetList = TargetListBase + capacity * EffectsPerTarget * EffectSize;
        for (var index = 0; index < payload[EffectCountOffset]; index++)
        {
            var actor = BinaryPrimitives.ReadUInt32LittleEndian(payload[(targetList + TargetIdSize * index)..]);
            var target = Alias(message.ConnectionKey, actor);
            if (source is null && target is null)
            {
                continue;
            }

            (targets ??= new JsonArray()).Add(new JsonObject
            {
                ["index"] = index,
                ["npc"] = target,
                ["damage_candidate"] = HasDamage(payload, index),
            });
        }

        return targets is null ? null : new JsonObject
        {
            ["source_npc"] = source,
            ["effect_sequence"] = BinaryPrimitives.ReadUInt32LittleEndian(payload[EffectSequenceOffset..]),
            ["targets"] = targets,
        };
    }

    private static bool HasDamage(ReadOnlySpan<byte> payload, int targetIndex)
    {
        var first = EffectsOffset + targetIndex * EffectsPerTarget * EffectSize;
        for (var index = 0; index < EffectsPerTarget; index++)
        {
            // Damage, blocked damage, parried damage. The amount and action ID stay in memory.
            if (payload[first + index * EffectSize] is 0x03 or 0x05 or 0x06)
            {
                return true;
            }
        }

        return false;
    }

    private int? Alias(string connection, uint actor)
    {
        if ((actor & 0xf0000000u) != 0x40000000u)
        {
            return null;
        }

        var key = (connection, actor);
        if (_aliases.TryGetValue(key, out var alias))
        {
            return alias;
        }

        if (_aliases.Count == MaxActors)
        {
            _untrackedActors++;
            return null;
        }

        alias = _aliases.Count + 1;
        _aliases.Add(key, alias);
        return alias;
    }
}
