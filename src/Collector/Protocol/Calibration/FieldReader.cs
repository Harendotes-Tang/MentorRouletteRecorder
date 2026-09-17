using System.Buffers.Binary;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Reads one profile-declared scalar out of a payload without ever keeping the payload.
/// Mirrors the parser's <c>ReadScalar</c> but answers "could not read" instead of throwing,
/// because calibration looks at messages that mostly are not the one it is after.
/// </summary>
internal static class FieldReader
{
    /// <summary>Reads the field, refusing byte runs and anything past the payload.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="field">Field to read.</param>
    /// <param name="value">Value read, when the read succeeded.</param>
    public static bool TryRead(ReadOnlySpan<byte> payload, ProfileField field, out long value)
    {
        value = 0;
        if (field.Type == ProfileFieldType.Bytes || field.Offset < 0 || field.Offset + field.Size > payload.Length)
        {
            return false;
        }

        var slice = payload.Slice(field.Offset, field.Size);
        var little = field.Endian == ProfileEndian.Little;
        switch (field.Type)
        {
            case ProfileFieldType.U8:
                value = slice[0];
                return true;
            case ProfileFieldType.U16:
                value = little ? BinaryPrimitives.ReadUInt16LittleEndian(slice) : BinaryPrimitives.ReadUInt16BigEndian(slice);
                return true;
            case ProfileFieldType.U32:
                value = little ? BinaryPrimitives.ReadUInt32LittleEndian(slice) : BinaryPrimitives.ReadUInt32BigEndian(slice);
                return true;
            case ProfileFieldType.I32:
                value = little ? BinaryPrimitives.ReadInt32LittleEndian(slice) : BinaryPrimitives.ReadInt32BigEndian(slice);
                return true;
            case ProfileFieldType.U64:
                var wide = little ? BinaryPrimitives.ReadUInt64LittleEndian(slice) : BinaryPrimitives.ReadUInt64BigEndian(slice);
                if (wide > long.MaxValue)
                {
                    return false;
                }

                value = (long)wide;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Reads the field and checks its constraints in one step.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="field">Field to read.</param>
    /// <param name="value">Value read, when it satisfied the constraints.</param>
    public static bool TryReadSatisfied(ReadOnlySpan<byte> payload, ProfileField field, out long value) =>
        TryRead(payload, field, out value) && field.Constraints.IsSatisfiedBy(value);

    /// <summary>True when every field of the message reads and satisfies its constraints.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="message">Message whose fields to check.</param>
    public static bool AllFieldsSatisfied(ReadOnlySpan<byte> payload, ProfileMessage message)
    {
        foreach (var field in message.Fields)
        {
            if (field.Type == ProfileFieldType.Bytes)
            {
                continue;
            }

            if (!TryReadSatisfied(payload, field, out _))
            {
                return false;
            }
        }

        return true;
    }
}
