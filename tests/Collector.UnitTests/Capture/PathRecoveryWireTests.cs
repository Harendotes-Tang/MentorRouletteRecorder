using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class PathRecoveryWireTests
{
    [Theory]
    [InlineData(PathRecoveryState.None, "NONE", 0, 0L)]
    [InlineData(PathRecoveryState.Recovering, "RECOVERING", 2, 15_000L)]
    [InlineData(PathRecoveryState.Failed, "FAILED", 2, 0L)]
    [InlineData((PathRecoveryState)999, "NONE", 0, 0L)]
    public void OnlyKnownTokensAndMeaningfulProgressAreExported(
        PathRecoveryState state, string token, int attempts, long retryAfterMs)
    {
        var wire = PathRecoveryWire.ToJson(new PathRecoverySnapshot(state, 2, 15_000));
        Assert.Equal(token, wire["state"]!.GetValue<string>());
        Assert.Equal(attempts, wire["attempts"]!.GetValue<int>());
        Assert.Equal(retryAfterMs, wire["retry_after_ms"]!.GetValue<long>());
        Assert.Equal(new[] { "attempts", "retry_after_ms", "state" }, wire.Select(pair => pair.Key).Order());
    }

    [Theory]
    [InlineData(int.MinValue, long.MinValue, 0, 0L)]
    [InlineData(-1, -1L, 0, 0L)]
    [InlineData(0, 0L, 0, 0L)]
    [InlineData(3, 300_000L, 3, 300_000L)]
    [InlineData(int.MaxValue, long.MaxValue, 3, 300_000L)]
    public void InvalidCountersCannotEscapeContractBounds(
        int attempts, long retryAfterMs, int expectedAttempts, long expectedRetryAfterMs)
    {
        var wire = PathRecoveryWire.ToJson(new PathRecoverySnapshot(
            PathRecoveryState.Recovering, attempts, retryAfterMs));
        Assert.Equal(expectedAttempts, wire["attempts"]!.GetValue<int>());
        Assert.Equal(expectedRetryAfterMs, wire["retry_after_ms"]!.GetValue<long>());
    }

    [Fact]
    public void NullSnapshotIsRefused() => Assert.Throws<ArgumentNullException>(() => PathRecoveryWire.ToJson(null!));
}
