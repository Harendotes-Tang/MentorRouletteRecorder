using System.Buffers.Binary;
using System.Security.Cryptography;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The two CN duty-start contents are recognised by content alone, on the clear's body: the
/// matcher accepts exactly the commencement and the remaining-time report and refuses every near
/// miss - the clear and the other director commands, a non-zero byte after the parameter, another
/// length, the other direction, a segment that is not IPC (docs/protocol-profile-format.md
/// section 13).
/// </summary>
public sealed class DutyStartSignalTests
{
    private const ushort ObservedDutyNumber = 0x0056;

    /// <summary>The commencement as trace row 2294 carried it: command 1, time limit 7200.</summary>
    private static byte[] ObservedCommencement() => Convert.FromHexString(
        "6d000000560003800100004020" + "1c0000" + "00000000000000000000000000000000" + "0000000000000000");

    private static byte[] Body(uint command, uint parameter, ushort dutyNumber = ObservedDutyNumber)
    {
        var body = new byte[DutyStartSignal.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body, DutyClearSignal.Category);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), dutyNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), DutyClearSignal.DirectorType);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), command);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(DutyStartSignal.ParameterOffset), parameter);
        return body;
    }

    private static (bool Matched, DutyStartSignalKind Kind, int Seconds) Match(
        byte[] payload, PacketDirection direction = PacketDirection.ServerToClient, ushort segmentType = 3)
    {
        var matched = DutyStartSignal.TryMatch(direction, segmentType, payload, out var kind, out var seconds);
        return (matched, kind, seconds);
    }

    [Fact]
    public void TheObservedCommencementIsRecognisedWithItsTimeLimit()
    {
        Assert.Equal(40, ObservedCommencement().Length);
        Assert.Equal(ObservedCommencement(), Body(DutyStartSignal.CommencedCommand, 7200));

        var (matched, kind, seconds) = Match(ObservedCommencement());

        Assert.True(matched);
        Assert.Equal(DutyStartSignalKind.Commenced, kind);
        Assert.Equal(7200, seconds);
    }

    /// <summary>
    /// Audit 2026-10-20, D2-4. The test above builds the expected body from the same constants the
    /// matcher reads, so a wrong command, parameter offset or zero run would pass it. The bodies are
    /// held to the evidence instead, as the clear's is: the trace keeps the first 12 hex digits of each
    /// payload's SHA-256, row 2294 (the commencement, time limit 7200) reads fd0e8cbff183 and row 2300
    /// (the first remaining-time report, 7199 seconds left) reads a107e0da5257
    /// (docs/reviews/2026-10-05/duty-trace-2026-10-05.jsonl).
    /// </summary>
    [Fact]
    public void TheObservedBodiesHashToTheEvidenceRows()
    {
        var commencement = Convert.FromHexString("6d000000" + "56000380" + "01000040" + "201c0000" + new string('0', 48));
        var remaining = Convert.FromHexString("6d000000" + "56000380" + "04000080" + "1f1c0000" + new string('0', 48));

        Assert.StartsWith("fd0e8cbff183", Convert.ToHexString(SHA256.HashData(commencement)).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.StartsWith("a107e0da5257", Convert.ToHexString(SHA256.HashData(remaining)).ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Equal(commencement, Body(DutyStartSignal.CommencedCommand, 7200));
        Assert.Equal(remaining, Body(DutyStartSignal.TimeRemainingCommand, 7199));
        Assert.Equal((true, DutyStartSignalKind.Commenced, 7200), Match(commencement));
        Assert.Equal((true, DutyStartSignalKind.TimeRemaining, 7199), Match(remaining));
    }

    [Theory]
    [InlineData(7199)]
    [InlineData(5400)]
    [InlineData(0)]
    public void ARemainingTimeReportIsRecognisedWithItsSecondsLeft(int left)
    {
        var (matched, kind, seconds) = Match(Body(DutyStartSignal.TimeRemainingCommand, (uint)left));

        Assert.True(matched);
        Assert.Equal(DutyStartSignalKind.TimeRemaining, kind);
        Assert.Equal(left, seconds);
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0056)]
    [InlineData(0xF00D)]
    [InlineData(0xFFFF)]
    public void AnyDutyNumberIsAccepted(int dutyNumber)
    {
        Assert.True(Match(Body(DutyStartSignal.CommencedCommand, 7200, (ushort)dutyNumber)).Matched);
        Assert.True(Match(Body(DutyStartSignal.TimeRemainingCommand, 7199, (ushort)dutyNumber)).Matched);
    }

    /// <summary>The clear and every other director command observed or theorised stay out.</summary>
    [Theory]
    [InlineData(0x40000003u)]
    [InlineData(0x40000007u)]
    [InlineData(0x80000015u)]
    [InlineData(0x8000000Cu)]
    [InlineData(0x40000002u)]
    [InlineData(0x40000005u)]
    [InlineData(0x80000001u)]
    [InlineData(0x00000000u)]
    public void AnotherCommandIsNotAStartSignal(uint command)
    {
        Assert.False(Match(Body(command, 7200)).Matched);
        Assert.False(Match(Body(command, 0)).Matched);
    }

    [Fact]
    public void TheClearAndTheStartSignalsNeverOverlap()
    {
        var clear = Body(DutyClearSignal.Command, 0);
        Assert.True(DutyClearSignal.Matches(PacketDirection.ServerToClient, 3, clear));
        Assert.False(Match(clear).Matched);

        Assert.False(DutyClearSignal.Matches(PacketDirection.ServerToClient, 3, Body(DutyStartSignal.CommencedCommand, 7200)));
        Assert.False(DutyClearSignal.Matches(PacketDirection.ServerToClient, 3, Body(DutyStartSignal.TimeRemainingCommand, 7199)));
    }

    public static TheoryData<int> TrailingOffsets()
    {
        var data = new TheoryData<int>();
        for (var offset = DutyStartSignal.ZeroFrom; offset < DutyStartSignal.Length; offset++)
        {
            data.Add(offset);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TrailingOffsets))]
    public void ANonZeroByteAfterTheParameterIsNotAStartSignal(int offset)
    {
        foreach (var command in new[] { DutyStartSignal.CommencedCommand, DutyStartSignal.TimeRemainingCommand })
        {
            var body = Body(command, 7200);
            body[offset] = 1;
            Assert.False(Match(body).Matched);
        }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(48)]
    public void AnotherLengthIsNotAStartSignal(int length)
    {
        var body = new byte[length];
        Body(DutyStartSignal.CommencedCommand, 7200).AsSpan(0, Math.Min(length, 40)).CopyTo(body);
        Assert.False(Match(body).Matched);
    }

    [Theory]
    [InlineData(0x006C, 0x8003)]
    [InlineData(0x016D, 0x8003)]
    [InlineData(0x006D, 0x8002)]
    [InlineData(0x006D, 0x0003)]
    public void AnotherCategoryOrDirectorTypeIsNotAStartSignal(int category, int directorType)
    {
        var body = Body(DutyStartSignal.CommencedCommand, 7200);
        BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)category);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), (ushort)directorType);
        Assert.False(Match(body).Matched);
    }

    [Fact]
    public void ANonZeroWordAtOffsetTwoIsNotAStartSignal()
    {
        var body = Body(DutyStartSignal.CommencedCommand, 7200);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), 1);
        Assert.False(Match(body).Matched);
    }

    [Fact]
    public void TheClientDirectionAndOtherSegmentsAreNotAStartSignal()
    {
        var body = Body(DutyStartSignal.CommencedCommand, 7200);
        Assert.False(Match(body, PacketDirection.ClientToServer).Matched);
        Assert.False(Match(body, segmentType: 0).Matched);
        Assert.False(Match(body, segmentType: 61440).Matched);
    }

    /// <summary>A parameter no duty timer has: whatever it is, it is not seconds.</summary>
    [Fact]
    public void AParameterAboveInt32IsNotAStartSignal()
    {
        Assert.False(Match(Body(DutyStartSignal.CommencedCommand, 0x80000000u)).Matched);
        Assert.False(Match(Body(DutyStartSignal.TimeRemainingCommand, 0xFFFFFFFFu)).Matched);
    }

    [Fact]
    public void TheConstantsAreTheObservedOnes()
    {
        Assert.Equal(DutyClearSignal.Length, DutyStartSignal.Length);
        Assert.Equal(0x40000001u, DutyStartSignal.CommencedCommand);
        Assert.Equal(0x80000004u, DutyStartSignal.TimeRemainingCommand);
        Assert.Equal(12, DutyStartSignal.ParameterOffset);
        Assert.Equal(16, DutyStartSignal.ZeroFrom);
        Assert.Equal("DUTY_COMMENCED:start_signal", DutyStartSignal.CommencedSemanticKey);
        Assert.Equal("DUTY_TIME_REMAINING:timer_signal", DutyStartSignal.TimeRemainingSemanticKey);
    }
}
