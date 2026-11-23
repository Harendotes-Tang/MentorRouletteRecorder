using System.Buffers.Binary;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The 40-byte body of a CN director message (docs/protocol-profile-format.md sections 12 and 13): category
/// 0x006D, the duty's director number, director type 0x8003, the command and its parameter, zeros after.
/// Written from literals, not from the production constants, so a test pins the bytes themselves.
/// </summary>
internal static class DirectorBodies
{
    /// <summary>The director command of a duty clear.</summary>
    internal const uint ClearCommand = 0x40000003;

    /// <summary>The invented duty the synthetic traffic plays.</summary>
    internal const ushort InventedDuty = 0xF00D;

    /// <summary>One director body.</summary>
    /// <param name="command">Director command.</param>
    /// <param name="parameter">Its parameter.</param>
    /// <param name="dutyNumber">Director number of the duty.</param>
    internal static byte[] Body(uint command, uint parameter = 0, ushort dutyNumber = InventedDuty)
    {
        var body = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(body, 0x006D);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), dutyNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), 0x8003);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), parameter);
        return body;
    }
}
