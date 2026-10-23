using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The replay buffer in <see cref="LiveEventBus"/>.
///
/// The crash recovery pass publishes <c>run_updated</c> and <c>stats_invalidated</c> from
/// inside <c>CollectorHost.Open</c>, which completes before the pipe server is constructed,
/// so the client that needs those events cannot yet be listening. A subscriber that arrives
/// late is owed those events in order, once each, and never more than
/// <see cref="LiveEventBus.ReplayCapacity"/> of them.
/// </summary>
public sealed class RecoveryReplayBufferTests
{
    [Fact]
    public async Task ASubscriberThatArrivesAfterwardsStillReceivesWhatItMissed()
    {
        var bus = new LiveEventBus(new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));

        // Published with nobody listening, as the recovery pass does.
        Assert.Equal(0, bus.SubscriberCount);
        bus.PublishStatsInvalidated("恢复了 1 条未完结记录");
        bus.PublishState(Domain.RunState.InterruptedPendingReview);

        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        var replayed = await DrainAsync(subscription, 2);
        Assert.Equal(
            new[] { "stats_invalidated", "run_state_changed" },
            replayed.Select(payload => payload["kind"]!.GetValue<string>()));
        Assert.Equal("恢复了 1 条未完结记录", replayed[0]["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReplayedEventsComeBeforeLiveOnesAndTheSequenceNeverGoesBackwards()
    {
        var bus = new LiveEventBus(new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));

        for (var index = 0; index < 3; index++)
        {
            bus.PublishStatsInvalidated("before " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        for (var index = 0; index < 3; index++)
        {
            bus.PublishStatsInvalidated("after " + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var received = await DrainAsync(subscription, 6);

        Assert.Equal(
            new[] { "before 0", "before 1", "before 2", "after 0", "after 1", "after 2" },
            received.Select(payload => payload["message"]!.GetValue<string>()));

        // The join between replayed and live events must be seamless: one continuous run of
        // sequence numbers, otherwise a client reads the boundary as a dropped-event gap and
        // re-queries needlessly.
        var sequences = received.Select(payload => payload["sequence"]!.GetValue<long>()).ToArray();
        Assert.Equal(sequences.OrderBy(value => value), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(sequences[0] + sequences.Length - 1, sequences[^1]);
    }

    [Fact]
    public async Task TheBufferIsBoundedAndKeepsTheNewestEvents()
    {
        var bus = new LiveEventBus(new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));
        const int published = LiveEventBus.ReplayCapacity * 3;

        for (var index = 0; index < published; index++)
        {
            bus.PublishStatsInvalidated(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(LiveEventBus.ReplayCapacity, bus.ReplayCount);

        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));
        var received = await DrainAsync(subscription, LiveEventBus.ReplayCapacity);

        // A bounded buffer must drop the oldest, not the newest: the events closest to the
        // present are the ones a client still needs.
        Assert.Equal(
            Enumerable
                .Range(published - LiveEventBus.ReplayCapacity, LiveEventBus.ReplayCapacity)
                .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            received.Select(payload => payload["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task EachSubscriberGetsItsOwnCopyOfTheSameRetainedEvent()
    {
        var bus = new LiveEventBus(new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));
        bus.PublishStatsInvalidated("统计已过期");

        using var first = bus.Subscribe(Guid.NewGuid().ToString("D"));
        using var second = bus.Subscribe(Guid.NewGuid().ToString("D"));

        var fromFirst = Assert.Single(await DrainAsync(first, 1));
        var fromSecond = Assert.Single(await DrainAsync(second, 1));

        // Same event, two independent JSON documents: one subscriber's pump must never be
        // able to mutate what another is about to read.
        Assert.Equal(
            fromFirst["event_id"]!.GetValue<string>(), fromSecond["event_id"]!.GetValue<string>());
        Assert.NotSame(fromFirst, fromSecond);
    }

    [Fact]
    public void DisposingASubscriptionStopsDeliveryButKeepsTheBufferForTheNextOne()
    {
        var bus = new LiveEventBus(new TestClock(new DateTimeOffset(2026, 9, 4, 7, 0, 0, TimeSpan.Zero)));
        bus.PublishStatsInvalidated("统计已过期");

        var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));
        Assert.Equal(1, bus.SubscriberCount);
        subscription.Dispose();

        Assert.Equal(0, bus.SubscriberCount);
        Assert.Equal(1, bus.ReplayCount);
    }

    private static async Task<List<JsonObject>> DrainAsync(LiveEventSubscription subscription, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<JsonObject>();
        while (received.Count < expected)
        {
            var next = await subscription.ReadAsync(timeout.Token);
            if (next is null)
            {
                break;
            }

            received.Add(next);
        }

        Assert.Equal(expected, received.Count);
        return received;
    }
}
