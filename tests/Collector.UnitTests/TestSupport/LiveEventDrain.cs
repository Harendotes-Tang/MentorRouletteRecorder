using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Reading what a live-event subscription holds.</summary>
internal static class LiveEventDrain
{
    /// <summary>Reads every payload the subscription holds, stopping at the first 250 ms quiet interval.</summary>
    /// <param name="subscription">Subscription to drain.</param>
    public static async Task<IReadOnlyList<JsonObject>> DrainAsync(this LiveEventSubscription subscription)
    {
        var payloads = new List<JsonObject>();
        while (await subscription.ReadAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None) is { } payload)
        {
            payloads.Add(payload);
        }

        return payloads;
    }
}
