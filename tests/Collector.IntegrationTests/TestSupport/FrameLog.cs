using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Every frame one <see cref="PipeClient"/> receives, in arrival order: each envelope as it went over the wire, the
/// live events among them included. Create it before sending anything whose frames it has to hold.
/// </summary>
internal sealed class FrameLog
{
    private readonly List<JsonObject> _envelopes = new();

    /// <summary>Starts recording what <paramref name="client"/> receives.</summary>
    /// <param name="client">The connected client.</param>
    public FrameLog(PipeClient client) =>
        client.EnvelopeReceived += envelope =>
        {
            var copy = envelope.DeepClone().AsObject();
            lock (_envelopes)
            {
                _envelopes.Add(copy);
            }
        };

    /// <summary>Every envelope received so far.</summary>
    public JsonObject[] Envelopes()
    {
        lock (_envelopes)
        {
            return _envelopes.ToArray();
        }
    }

    /// <summary>The payloads of the live events received so far.</summary>
    public JsonObject[] Events() =>
        Envelopes().Where(IsEvent).Select(envelope => envelope["payload"]!.AsObject()).ToArray();

    /// <summary>True for the envelope of a live event.</summary>
    /// <param name="envelope">A received envelope.</param>
    public static bool IsEvent(JsonObject envelope) => envelope["message_type"]!.GetValue<string>() == "Event";

    /// <summary>Waits until the envelopes received so far satisfy <paramref name="condition"/>.</summary>
    /// <param name="condition">What the envelopes must show.</param>
    /// <param name="what">The failure message.</param>
    /// <param name="patience">How long to wait; 10 s when null.</param>
    public Task WaitForEnvelopesAsync(Func<JsonObject[], bool> condition, string what, TimeSpan? patience = null) =>
        Poll.UntilAsync(() => condition(Envelopes()), what, patience);

    /// <summary>Waits until the live events received so far satisfy <paramref name="condition"/>.</summary>
    /// <param name="condition">What the event payloads must show.</param>
    /// <param name="what">The failure message.</param>
    /// <param name="patience">How long to wait; 10 s when null.</param>
    public Task WaitForEventsAsync(Func<JsonObject[], bool> condition, string what, TimeSpan? patience = null) =>
        Poll.UntilAsync(() => condition(Events()), what, patience);
}
