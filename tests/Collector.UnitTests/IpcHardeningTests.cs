using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Load-bearing invariants of the IPC layer: the order live events are handed out in, the
/// memory a receive buffer keeps, and the size of the strings a client may push through an
/// array field.
/// </summary>
public sealed class IpcHardeningTests
{
    /// <summary>
    /// Two publishers on two threads must not hand a subscriber sequence numbers out of order.
    ///
    /// The contract defines a gap in <c>sequence</c> as "you fell behind, re-query". A number
    /// that goes <em>backwards</em> is not a gap and has no defined meaning, so the counter
    /// must be incremented under the lock that fans the event out (review finding M1).
    /// </summary>
    [Fact]
    public async Task ConcurrentPublishersHandOutSequenceNumbersInOrder()
    {
        const int perPublisher = 100;
        var bus = new LiveEventBus(SystemClock.Instance);
        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        using var start = new Barrier(2);
        var publishers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perPublisher; i++)
            {
                bus.Publish(LiveEventKind.StatsInvalidated, payload => payload["message"] = "x");
            }
        })).ToArray();

        await Task.WhenAll(publishers);

        var previous = 0L;
        for (var i = 0; i < 2 * perPublisher; i++)
        {
            var received = await subscription.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(received);
            var sequence = received!["sequence"]!.GetValue<long>();
            Assert.True(
                sequence > previous,
                $"sequence {sequence} arrived after {previous}; the fan-out is not ordered");
            previous = sequence;
        }
    }

    /// <summary>
    /// A single oversized burst must not leave a per-connection buffer holding megabytes for
    /// the rest of the connection's life (review finding L3).
    /// </summary>
    [Fact]
    public void TheReceiveBufferGivesItsMemoryBackWhenItRunsDry()
    {
        var buffer = new FrameBuffer();
        var body = new byte[512 * 1024];
        buffer.Append(FrameCodec.Encode(body));

        Assert.True(buffer.Capacity > FrameBuffer.InitialCapacity);
        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var taken));
        Assert.Equal(body.Length, taken.Length);
        Assert.Equal(0, buffer.Buffered);
        Assert.Equal(FrameBuffer.InitialCapacity, buffer.Capacity);
    }

    /// <summary>
    /// The buffer only shrinks when nothing is left in it: a partially consumed stream must
    /// keep its bytes.
    /// </summary>
    [Fact]
    public void APartiallyConsumedBufferKeepsWhatIsLeft()
    {
        var buffer = new FrameBuffer();
        buffer.Append(FrameCodec.Encode(new byte[256 * 1024]));
        buffer.Append(FrameCodec.Encode(new byte[8]));

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out _));
        Assert.Equal(FrameCodec.PrefixBytes + 8, buffer.Buffered);
        Assert.True(buffer.Capacity > FrameBuffer.InitialCapacity);

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var second));
        Assert.Equal(8, second.Length);
        Assert.Equal(FrameBuffer.InitialCapacity, buffer.Capacity);
    }

    /// <summary>
    /// An array field caps how many elements it takes; it must cap how long each one is too,
    /// or sixteen elements of a megabyte each are a legal request (review finding L4).
    /// </summary>
    [Fact]
    public void AnArrayElementLongerThanTheCapIsRefused()
    {
        var payload = new JsonObject
        {
            ["event_types"] = new JsonArray(new string('x', 200)),
        };

        var error = Assert.Throws<CollectorException>(
            () => new PayloadReader(payload).StringArray("event_types", 16, maxLength: 64));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.event_types", error.Field);
    }

    [Fact]
    public void AnArrayElementInsideTheCapIsAccepted()
    {
        var payload = new JsonObject { ["event_types"] = new JsonArray("RunStarted", "Heartbeat") };

        var values = new PayloadReader(payload).StringArray("event_types", 16, maxLength: 64);

        Assert.Equal(new[] { "RunStarted", "Heartbeat" }, values);
    }
}
