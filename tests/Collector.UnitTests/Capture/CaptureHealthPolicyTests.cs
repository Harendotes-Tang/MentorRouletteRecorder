using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CaptureHealthPolicyTests
{
    [Theory]
    [InlineData(1, 20, 60, 1, true, CaptureSilentReason.None)]
    [InlineData(0, 20, 0, 1, false, CaptureSilentReason.Midstream)]
    [InlineData(0, 19, 59, 1, false, CaptureSilentReason.None)]
    [InlineData(0, 0, 60, 1, false, CaptureSilentReason.Midstream)]
    [InlineData(0, 0, 60, 0, false, CaptureSilentReason.None)]
    [InlineData(0, 0, 60, 0, true, CaptureSilentReason.NoPacketsOnAdapter)]
    [InlineData(0, 0, 59, 0, true, CaptureSilentReason.None)]
    public void ReadableTrafficHardEvidenceAndGraceKeepTheirPriority(
        long decoded, long errors, int seconds, int preexisting, bool connectionSeen, CaptureSilentReason expected)
    {
        Assert.Equal(expected, CaptureHealthPolicy.Classify(
            new CaptureHealthReading(decoded, errors, TimeSpan.FromSeconds(seconds)),
            CaptureIngressCounters.Empty, preexisting, connectionSeen));
    }

    [Theory]
    [InlineData(19, 1, CaptureSilentReason.None)]
    [InlineData(20, 0, CaptureSilentReason.None)]
    [InlineData(20, 1, CaptureSilentReason.Midstream)]
    public void EarlyHandshakeVerdictRequiresEnoughFramesAndAGameConnection(
        long packets, long connections, CaptureSilentReason expected)
    {
        var ingress = CaptureIngressCounters.Empty with
        {
            RawPackets = packets,
            DroppedNoStream = packets,
            GameConnectionsNow = connections,
        };
        Assert.Equal(expected, CaptureHealthPolicy.Classify(
            new CaptureHealthReading(0, 0, TimeSpan.Zero), ingress, null, false));
    }

    [Theory]
    [InlineData(false, 0, CaptureSilentReason.None)]
    [InlineData(true, 0, CaptureSilentReason.NoStreamOwnership)]
    [InlineData(false, 1, CaptureSilentReason.NoStreamOwnership)]
    public void OtherProgramsTrafficDoesNotProveTheGameHasStreamOwnershipProblems(
        bool connectionSeen, long gameConnections, CaptureSilentReason expected)
    {
        var ingress = CaptureIngressCounters.Empty with
        {
            RawPackets = 1,
            UnconfirmedTuples = 1,
            GameConnections = gameConnections,
        };
        Assert.Equal(expected, CaptureHealthPolicy.Classify(
            new CaptureHealthReading(0, 0, TimeSpan.FromMinutes(1)), ingress, null, connectionSeen));
    }
}
