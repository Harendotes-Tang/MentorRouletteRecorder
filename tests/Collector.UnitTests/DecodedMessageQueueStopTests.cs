using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Stopping the parser thread when it holds nothing.
///
/// A worker parked in WaitToReadAsync with the channel complete and cancellation raised can
/// only leave, so Complete() must count that as stopped. Otherwise a thread starved of CPU --
/// as on the two-core CI runner beside a parallel test class's child Collector processes --
/// misses its join deadline and a healthy, empty queue is reported as an unreleasable sink.
/// </summary>
public sealed class DecodedMessageQueueStopTests
{
    [Fact]
    public void AnIdleQueueAlwaysCountsAsStoppedEvenWithNoTimeToJoin()
    {
        var queue = new DecodedMessageQueue(new NullSink());
        Assert.True(queue.Complete(TimeSpan.Zero));
        // Either the worker left in time or it was parked; both are a safe stop.
        Assert.True(queue.StoppedWhileParked || queue.Completion.IsCompleted);
        queue.Dispose();
        Assert.Equal(0, queue.AbandonedCount);
    }

    [Fact]
    public async Task AWorkerHoldingAMessageIsNeverCalledParked()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queue = new DecodedMessageQueue(new BlockingSink(entered, release));
        queue.Offer(new DecodedMessage(
            "s", MessageDirection.Inbound, DateTimeOffset.UnixEpoch, TimeSpan.Zero, 0, 0, 0x1234,
            new byte[] { 1 }, "c"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(queue.Complete(TimeSpan.FromMilliseconds(20)));
            Assert.False(queue.StoppedWhileParked);
            Assert.StartsWith("delivering 0x1234", queue.Stage);
        }
        finally
        {
            release.Set();
        }
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Dispose();
    }

    private sealed class NullSink : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message) { }
    }

    private sealed class BlockingSink(ManualResetEventSlim entered, ManualResetEventSlim release) : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message)
        {
            entered.Set();
            release.Wait();
        }
    }
}
