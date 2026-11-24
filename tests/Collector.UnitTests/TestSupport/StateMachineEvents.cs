using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Semantic events as the parser hands them to the run state machine, stamped on one synthetic session:
/// each one observed <c>seconds</c> (or <c>milliseconds</c>) after <see cref="Start"/>, with the same
/// monotonic offset and a key no other builder repeats. Use with <c>using static</c>.
/// </summary>
internal static class StateMachineEvents
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 4, 1, 0, 0, 0, TimeSpan.Zero);

    internal static EventKey Key(string semantic) =>
        new("00000000-0000-4000-8000-000000000010", PacketDirection.None, "SYNTHETIC", 0, null, semantic);

    internal static ContentFinderPop Pop(int seconds, int rouletteId, int? contentId = null) => new()
    {
        Key = Key("pop-" + seconds + "-" + rouletteId), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), RouletteId = rouletteId, ContentId = contentId,
    };

    /// <summary>A duty instance entered on territory 800001.</summary>
    internal static ZoneInitialization Zone(int seconds, int? contentId) => new()
    {
        Key = Key("zone-" + seconds + "-" + contentId), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), ContentId = contentId, TerritoryId = 800001,
        IsDutyInstance = true,
    };

    /// <summary>A zone change that does not say whether it is a duty.</summary>
    internal static ZoneInitialization ZoneUnknown(int seconds) => new()
    {
        Key = Key("zone-unknown-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), IsDutyInstance = null,
    };

    internal static TerritoryObserved Territory(int milliseconds, int territoryId) => new()
    {
        Key = Key("territory-" + milliseconds), ObservedAtUtc = Start.AddMilliseconds(milliseconds),
        Mono = TimeSpan.FromMilliseconds(milliseconds), TerritoryId = territoryId,
    };

    internal static PlayerJob Job(int seconds, int jobId) => new()
    {
        Key = Key("job-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), JobId = jobId,
    };

    internal static DutyResult Result(int seconds, bool victory) => new()
    {
        Key = Key("result-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), Victory = victory,
    };

    internal static DutyCommenced Commenced(int seconds, int limit) => new()
    {
        Key = Key("commenced-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), TimeLimitSeconds = limit,
    };

    internal static DutyTimeRemaining Remaining(int seconds, int left) => new()
    {
        Key = Key("remaining-" + seconds + "-" + left), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds), RemainingSeconds = left,
    };

    /// <summary>
    /// The victory the parser makes of the clear signal: keyed by the message's own opcode,
    /// epoch and payload hash, and by the fixed semantic key.
    /// </summary>
    internal static DutyResult Clear(int seconds, long epoch = 0) => new()
    {
        Key = new EventKey(
            "00000000-0000-4000-8000-000000000010", PacketDirection.ServerToClient, "61455",
            epoch == 0 ? seconds * 1000L : epoch,
            "1b7b8dd132a57e397bdaf964397fb17e4ea20f5a6a62f4bebc9c0f1ab0bf8566", "DUTY_RESULT:clear_signal"),
        ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
        Victory = true,
    };

    internal static ConnectionLost Connection(int seconds) => new()
    {
        Key = Key("connection-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    /// <summary>Capture stopped. Not named <c>Capture</c>: that simple name is the Collector's Capture namespace.</summary>
    internal static CaptureStopped CaptureStop(int seconds) => new()
    {
        Key = Key("capture-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };

    internal static ZoneLeft Leave(int seconds) => new()
    {
        Key = Key("leave-" + seconds), ObservedAtUtc = Start.AddSeconds(seconds),
        Mono = TimeSpan.FromSeconds(seconds),
    };
}
