using System.Buffers.Binary;
using System.Security.Cryptography;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The CN duty-clear signal is recognised by its content alone, so the matcher is held to the
/// one observed body and refuses every near miss: another category, director type or command,
/// any non-zero byte where the observation had zeros, another length, the other direction and
/// a segment that is not IPC (docs/protocol-profile-format.md section 12).
/// </summary>
public sealed class DutyClearSignalTests
{
    /// <summary>The director number of the observed duty, the only free part of the body.</summary>
    private const ushort ObservedDutyNumber = 0x0056;

    /// <summary>The clear exactly as trace row 62088 carried it.</summary>
    private static byte[] ObservedBody() => Convert.FromHexString(
        "6d000000560003800300004000000000" + "00000000000000000000000000000000" + "0000000000000000");

    /// <summary>The body rebuilt from the constants, with any director number in the free half.</summary>
    private static byte[] Body(ushort dutyNumber = ObservedDutyNumber)
    {
        var body = new byte[DutyClearSignal.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, DutyClearSignal.Category);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), dutyNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), DutyClearSignal.DirectorType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), DutyClearSignal.Command);
        return body;
    }

    private static bool Matches(byte[] payload, ushort segmentType = 3) =>
        DutyClearSignal.Matches(PacketDirection.ServerToClient, segmentType, payload);

    [Fact]
    public void TheObservedBodyIsTheSignal()
    {
        Assert.Equal(40, ObservedBody().Length);
        Assert.True(Matches(ObservedBody()));
        Assert.Equal(ObservedBody(), Body());
    }

    /// <summary>
    /// Pins every constant to the evidence: the trace stores the first 12 hex digits of each
    /// payload's SHA-256, and row 62088 reads 1b7b8dd132a5. A wrong category, director type,
    /// command, length or zero run breaks this equality.
    /// </summary>
    [Fact]
    public void TheObservedBodyHashesToTheEvidenceRow()
    {
        var digest = Convert.ToHexString(SHA256.HashData(Body())).ToLowerInvariant();

        Assert.StartsWith("1b7b8dd132a5", digest, StringComparison.Ordinal);
        Assert.Equal(
            "c398a7b5be6c5d1967eac585598947d1df27f1ff7695bec67ebe423a30541ad2",
            DutyClearSignal.EvidenceSha256);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0056)]
    [InlineData(0xF00D)]
    [InlineData(0xFFFF)]
    public void AnyDutyNumberInTheLowHalfIsAccepted(int dutyNumber)
    {
        Assert.True(Matches(Body((ushort)dutyNumber)));
    }

    [Theory]
    [InlineData(0x006C)]
    [InlineData(0x016D)]
    [InlineData(0x0000)]
    public void AnotherCategoryIsNotTheSignal(int category)
    {
        var body = Body();
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)category);

        Assert.False(Matches(body));
    }

    [Fact]
    public void ANonZeroSecondHalfwordIsNotTheSignal()
    {
        var body = Body();
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 1);

        Assert.False(Matches(body));
    }

    [Theory]
    [InlineData(0x8002)]
    [InlineData(0x8004)]
    [InlineData(0x0003)]
    [InlineData(0x8103)]
    public void AnotherDirectorTypeIsNotTheSignal(int directorType)
    {
        var body = Body();
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), (ushort)directorType);

        Assert.False(Matches(body));
    }

    /// <summary>The six commands the same director sent in the observed duty, and two never seen.</summary>
    [Theory]
    [InlineData(0x40000001u)]
    [InlineData(0x40000007u)]
    [InlineData(0x80000001u)]
    [InlineData(0x80000004u)]
    [InlineData(0x8000000Cu)]
    [InlineData(0x80000015u)]
    [InlineData(0x40000002u)]
    [InlineData(0x40000005u)]
    public void AnotherCommandIsNotTheSignal(uint command)
    {
        var body = Body();
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), command);

        Assert.False(Matches(body));
    }

    /// <summary>Covers the clear command with a parameter, and every byte of the zero run.</summary>
    [Theory]
    [MemberData(nameof(ZeroRunOffsets))]
    public void ANonZeroByteAfterTheCommandIsNotTheSignal(int offset)
    {
        var body = Body();
        body[offset] = 1;

        Assert.False(Matches(body));
    }

    /// <summary>
    /// Offsets 12 to 39, as theory data. Literal, not read off the constants: a zero run that
    /// started later would accept the clear with a parameter, and this theory must then fail.
    /// </summary>
    public static TheoryData<int> ZeroRunOffsets
    {
        get
        {
            var data = new TheoryData<int>();
            for (var offset = 12; offset <= 39; offset++)
            {
                data.Add(offset);
            }

            return data;
        }
    }

    /// <summary>
    /// 32 is the historical length and the observed body's prefix; 48 is the body with eight
    /// more zero bytes. Neither was observed on this build, so neither is the signal.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(48)]
    public void AnyOtherLengthIsNotTheSignal(int length)
    {
        var body = new byte[length];
        Body().AsSpan(0, Math.Min(length, DutyClearSignal.Length)).CopyTo(body);

        Assert.False(Matches(body));
    }

    [Fact]
    public void TheSameBodyFromTheClientIsNotTheSignal()
    {
        Assert.False(DutyClearSignal.Matches(PacketDirection.ClientToServer, 3, Body()));
        Assert.False(DutyClearSignal.Matches(PacketDirection.None, 3, Body()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61440)]
    public void TheSameBodyInANonIpcSegmentIsNotTheSignal(int segmentType)
    {
        Assert.False(Matches(Body(), (ushort)segmentType));
    }

    [Theory]
    [InlineData(MessageDirection.Inbound, true)]
    [InlineData(MessageDirection.Outbound, false)]
    public void TheDecodedMessageOverloadReadsTheDirection(MessageDirection direction, bool expected)
    {
        var message = new DecodedMessage(
            "20000000-0000-4000-8000-000000000099",
            direction,
            new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero),
            TimeSpan.Zero,
            2046833,
            3,
            0x0204,
            Body(),
            "test-connection");

        Assert.Equal(expected, DutyClearSignal.Matches(message));
    }
}
