namespace MentorRecorder.Collector.Protocol.Pipeline;

/// <summary>
/// The three things that end a run without any packet saying so.
///
/// The capture layer (Phase 2) calls these; the pipeline turns each of them into the
/// corresponding synthetic semantic event and lets the state machine apply the rule from
/// docs/state-machine.md sections 3.6 and 3.7. They exist as an interface rather than as
/// three ad-hoc calls so that the capture layer cannot invent a fourth way to end a run.
/// </summary>
public interface ICaptureLifecycleListener
{
    /// <summary>
    /// Capture stopped: the user stopped it, the Collector is shutting down, or the game
    /// process went away. A run already inside a duty becomes INTERRUPTED with LOW
    /// confidence; a run that never entered becomes CANCELLED_BEFORE_ENTRY.
    /// </summary>
    /// <param name="gameExited">True when the game process went away rather than the capture.</param>
    /// <param name="observedAtUtc">Wall-clock time of the stop.</param>
    /// <param name="mono">Monotonic reading taken at the stop.</param>
    void OnCaptureStopped(bool gameExited, DateTimeOffset observedAtUtc, TimeSpan mono);

    /// <summary>
    /// The game connection dropped. A run inside a duty becomes DISCONNECTED, which is never
    /// merged with LEFT_OR_ABANDONED (docs/state-machine.md section 3.6).
    /// </summary>
    /// <param name="observedAtUtc">Wall-clock time of the drop.</param>
    /// <param name="mono">Monotonic reading taken at the drop.</param>
    void OnConnectionLost(DateTimeOffset observedAtUtc, TimeSpan mono);

    /// <summary>
    /// The bounded capture queue dropped observations, so the event sequence has a known
    /// hole and can no longer be classified reliably.
    /// </summary>
    /// <param name="droppedCount">Number of observations known to have been dropped.</param>
    /// <param name="observedAtUtc">Wall-clock time the gap was noticed.</param>
    /// <param name="mono">Monotonic reading taken when the gap was noticed.</param>
    void OnEventsDropped(long droppedCount, DateTimeOffset observedAtUtc, TimeSpan mono);
}
