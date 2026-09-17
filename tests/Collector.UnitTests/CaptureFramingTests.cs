using System.Buffers.Binary;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The framing reader, against hand-built byte arrays.
///
/// This is the only place in the capture layer that inspects raw bytes, so it is the only
/// place a malformed stream can crash. Every case asserts the same property from a different
/// angle: garbage is answered with false, never with an exception and never with a
/// plausible-looking opcode.
/// </summary>
public sealed class CaptureFramingTests
{
    [Fact]
    public void ReadsSegmentTypeOpcodeAndPayload_FromAWellFormedIpcMessage()
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode: 0x0142, payloadLength: 8);
        message[FfxivFraming.HeaderBytes] = 0xAB;

        Assert.True(FfxivFraming.TryRead(message, out var frame));
        Assert.Equal(FfxivFraming.SegmentTypeIpc, frame.SegmentType);
        Assert.Equal(0x0142, frame.Opcode);
        Assert.Equal(FfxivFraming.HeaderBytes, frame.PayloadOffset);
        Assert.Equal(8, frame.PayloadLength);
        Assert.Equal((uint)message.Length, frame.DeclaredLength);
    }

    [Fact]
    public void ReadsTheIpcTimestamp()
    {
        var message = FakeCaptureSource.BuildIpcMessage(1, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(24), 1_700_000_000);

        Assert.True(FfxivFraming.TryRead(message, out var frame));
        Assert.Equal(1_700_000_000u, frame.Seconds);
    }

    [Fact]
    public void ASegmentWithoutAnIpcHeader_HasNoOpcodeAndKeepsItsPayload()
    {
        // Keep-alive and session-control segments are well-formed framing that carries no
        // opcode. Reporting them as unreadable would inflate the decode-error counter and
        // make a healthy capture look broken.
        var message = FakeCaptureSource.BuildIpcMessage(0, payloadLength: 4, segmentType: 7);

        Assert.True(FfxivFraming.TryRead(message, out var frame));
        Assert.Equal(7, frame.SegmentType);
        Assert.Equal(0, frame.Opcode);
        Assert.Equal(FfxivFraming.SegmentHeaderBytes, frame.PayloadOffset);
        Assert.Equal(4, frame.PayloadLength);
    }

    [Fact]
    public void AZeroLengthPayloadIsValid()
    {
        Assert.True(FfxivFraming.TryRead(FakeCaptureSource.BuildIpcMessage(42), out var frame));
        Assert.Equal(0, frame.PayloadLength);
        Assert.Equal(42, frame.Opcode);
    }

    [Fact]
    public void RefusesABufferShorterThanTheSegmentHeader()
    {
        for (var length = 0; length < FfxivFraming.SegmentHeaderBytes; length++)
        {
            Assert.False(FfxivFraming.TryRead(new byte[length], out _));
        }
    }

    [Fact]
    public void RefusesAnIpcSegmentTruncatedInsideItsIpcHeader()
    {
        var message = FakeCaptureSource.BuildIpcMessage(0x99);
        var truncated = message.AsSpan(0, FfxivFraming.HeaderBytes - 1).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(truncated, (uint)truncated.Length);

        Assert.False(FfxivFraming.TryRead(truncated, out _));
    }

    [Fact]
    public void RefusesASegmentThatDeclaresMoreBytesThanItHas()
    {
        var message = FakeCaptureSource.BuildIpcMessage(0x99, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(message, (uint)message.Length + 1);

        // Trusting the declared length here would slice past the end of the buffer.
        Assert.False(FfxivFraming.TryRead(message, out _));
    }

    [Fact]
    public void RefusesASegmentThatDeclaresLessThanItsOwnHeader()
    {
        var message = FakeCaptureSource.BuildIpcMessage(0x99, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(message, 3);

        Assert.False(FfxivFraming.TryRead(message, out _));
    }

    [Fact]
    public void RefusesAZeroedBuffer()
    {
        Assert.False(FfxivFraming.TryRead(new byte[128], out _));
    }

    [Fact]
    public void NeverThrowsOnRandomBytes()
    {
        var random = new Random(20260904);
        for (var attempt = 0; attempt < 20_000; attempt++)
        {
            var buffer = new byte[random.Next(0, 96)];
            random.NextBytes(buffer);

            // The property under test is the absence of exceptions: any verdict the reader
            // reaches on random bytes is acceptable, a crash is not.
            var read = FfxivFraming.TryRead(buffer, out var frame);
            if (read)
            {
                Assert.InRange(frame.PayloadOffset, 0, buffer.Length);
                Assert.InRange(frame.PayloadOffset + frame.PayloadLength, 0, buffer.Length);
            }
        }
    }

    [Fact]
    public void ConnectionKeyIsStableForTheSameTuple() =>
        Assert.Equal(
            ConnectionKey.From("session", 3232235777, 51000, 3232235778, 55021),
            ConnectionKey.From("session", 3232235777, 51000, 3232235778, 55021));

    [Fact]
    public void ConnectionKeyDiffersForDifferentTuplesAndSessions()
    {
        var baseline = ConnectionKey.From("session", 3232235777, 51000, 3232235778, 55021);

        Assert.NotEqual(baseline, ConnectionKey.From("session", 3232235777, 51001, 3232235778, 55021));
        Assert.NotEqual(baseline, ConnectionKey.From("other", 3232235777, 51000, 3232235778, 55021));
    }

    [Fact]
    public void ConnectionKeyCarriesNoAddress()
    {
        var key = ConnectionKey.From("session", 3232235777, 51000, 3232235778, 55021);

        Assert.Equal(ConnectionKey.KeyLength, key.Length);
        Assert.Matches("^[0-9a-f]+$", key);

        // Neither the decimal nor the dotted form of the address may survive into the key:
        // the key exists so that connections can be told apart without one.
        Assert.DoesNotContain("3232235777", key, StringComparison.Ordinal);
        Assert.DoesNotContain("51000", key, StringComparison.Ordinal);
    }
}
