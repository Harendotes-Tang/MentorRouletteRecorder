using System.Buffers.Binary;

namespace MentorRecorder.Collector.Protocol.Parsing;

/// <summary>
/// The combat packet layouts shared by the CN 7.58 phase probe and live phase recognition.
/// Callers first check the build, direction and IPC segment type. These views borrow the
/// decoded body for the current call and never retain actor identifiers or payloads.
/// </summary>
internal static class CnDutyCombatPackets
{
    public const string ClientBuild = "2026.11.03.0000.0000";
    public const ushort ActorControl = 0x02df;
    private const ushort DeathCategory = 0x0006;
    private const int EffectSequenceOffset = 12;
    private const int EffectCountOffset = 33;
    private const int EffectsOffset = 42;
    private const int EffectsPerTarget = 8;
    private const int EffectSize = 8;
    private const int TargetListBase = 48;
    private const int TargetIdSize = 8;

    /// <summary>Tests the actor ID namespace used by NPCs in the measured captures.</summary>
    public static bool IsNpc(uint actor) => (actor & 0xf0000000u) == 0x40000000u;

    /// <summary>Tests the ActorControl death category. The subject is the header's source actor.</summary>
    public static bool IsDefeat(ushort opcode, ReadOnlySpan<byte> body) =>
        opcode == ActorControl && body.Length == 24 &&
        BinaryPrimitives.ReadUInt16LittleEndian(body) == DeathCategory;

    /// <summary>Returns an empty view when the action's carrier, length or target count is invalid.</summary>
    public static ActionEffects ReadAction(ushort opcode, ReadOnlySpan<byte> body)
    {
        var capacity = (opcode, body.Length) switch
        {
            (0x0190, 124) => 1,
            (0x0233, 636) => 8,
            (0x0119, 1212) => 16,
            (0x039c, 1788) => 24,
            (0x00dd, 2364) => 32,
            _ => 0,
        };
        return capacity == 0 || body[EffectCountOffset] is 0 || body[EffectCountOffset] > capacity
            ? default : new ActionEffects(body, capacity, body[EffectCountOffset]);
    }

    /// <summary>A validated action effect array. Callers read indices from zero through Count minus one.</summary>
    internal readonly ref struct ActionEffects
    {
        private readonly ReadOnlySpan<byte> _body;
        private readonly int _targetList;

        public ActionEffects(ReadOnlySpan<byte> body, int capacity, int count)
        {
            _body = body;
            _targetList = TargetListBase + capacity * EffectsPerTarget * EffectSize;
            Count = count;
        }

        public int Count { get; }
        public uint Sequence => BinaryPrimitives.ReadUInt32LittleEndian(_body[EffectSequenceOffset..]);

        public uint TargetActor(int index) =>
            BinaryPrimitives.ReadUInt32LittleEndian(_body[(_targetList + TargetIdSize * index)..]);

        public bool HasDamage(int index)
        {
            var first = EffectsOffset + index * EffectsPerTarget * EffectSize;
            for (var slot = 0; slot < EffectsPerTarget; slot++)
            {
                // Damage, blocked damage, parried damage.
                if (_body[first + slot * EffectSize] is 0x03 or 0x05 or 0x06)
                    return true;
            }

            return false;
        }
    }
}
