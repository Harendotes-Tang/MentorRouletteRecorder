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
    public const string ClientBuild = CnDutyCombatPackets.ClientBuild;
    public const int FormatVersion = 1;
    public const int MaxMessageRows = 100_000;
    private const int MaxObservationRows = 15_000;
    private const int MaxActors = 8192;

    private const ushort NpcSpawn = 0x0176;
    private const ushort BossSpawn = 0x02fd;
    private const ushort ActorCast = 0x0160;
    private const ushort ActorControlSelf = 0x00a9;

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
            _ when CnDutyCombatPackets.IsDefeat(message.Opcode, payload) => "defeat",
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
        var action = CnDutyCombatPackets.ReadAction(message.Opcode, payload);
        if (action.Count == 0)
        {
            return null;
        }

        var source = Alias(message.ConnectionKey, message.SourceActor);
        JsonArray? targets = null;
        for (var index = 0; index < action.Count; index++)
        {
            var target = Alias(message.ConnectionKey, action.TargetActor(index));
            if (source is null && target is null)
            {
                continue;
            }

            (targets ??= new JsonArray()).Add(new JsonObject
            {
                ["index"] = index,
                ["npc"] = target,
                ["damage_candidate"] = action.HasDamage(index),
            });
        }

        return targets is null ? null : new JsonObject
        {
            ["source_npc"] = source,
            ["effect_sequence"] = action.Sequence,
            ["targets"] = targets,
        };
    }

    private int? Alias(string connection, uint actor)
    {
        if (!CnDutyCombatPackets.IsNpc(actor))
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
