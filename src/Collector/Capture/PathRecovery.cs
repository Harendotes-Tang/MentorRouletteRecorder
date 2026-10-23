namespace MentorRecorder.Collector.Capture;

/// <summary>Automatic recovery progress, independent of the measured capture health.</summary>
public enum PathRecoveryState { None, Recovering, Failed }

/// <summary>Endpoint-free, bounded progress for the current game; no success is inferred from opening a reader.</summary>
public sealed record PathRecoverySnapshot(PathRecoveryState State, int Attempts, long RetryAfterMs)
{
    public static PathRecoverySnapshot None { get; } = new(PathRecoveryState.None, 0, 0);
}

/// <summary>
/// Monotonic scheduling owned by the controller's state gate. Notifications merely invalidate a
/// discovery deadline; they never renew the attempt budget. Replacement throttling survives resets.
/// </summary>
internal sealed class PathRecovery
{
    internal const int DebounceMs = 2_000;
    internal const int MaxDebounceMs = 5_000;
    internal const int DiscoveryIntervalMs = 15_000;
    internal const int MinReplacementIntervalMs = 30_000;
    internal const int MaxAttempts = PathRecoveryWire.MaxAttempts;
    internal const int MaxEpisodeMs = 120_000;
    private long _revision;
    private long _firstNotificationAt = -1;
    private long _notificationDue = long.MaxValue;
    private long _discoveryDue;
    private long _attemptDue;
    private long _replacementAllowedAt;
    private long _episodeDeadline;

    public PathRecoveryState State { get; private set; }
    public int Attempts { get; private set; }

    public void Reset(long now, long revision)
    {
        State = PathRecoveryState.None;
        Attempts = 0;
        _attemptDue = 0;
        _episodeDeadline = 0;
        _revision = revision;
        _firstNotificationAt = -1;
        _notificationDue = long.MaxValue;
        _discoveryDue = now + DiscoveryIntervalMs;
    }

    public void Begin(long now, bool newPath = false)
    {
        if (State != PathRecoveryState.None && !newPath) return;
        State = PathRecoveryState.Recovering;
        Attempts = 0;
        _attemptDue = now;
        _episodeDeadline = now + MaxEpisodeMs;
    }

    public bool ShouldInspect(long now, long revision)
    {
        if (revision != _revision)
        {
            _revision = revision;
            if (_firstNotificationAt < 0) _firstNotificationAt = now;
            _notificationDue = Math.Min(now + DebounceMs, _firstNotificationAt + MaxDebounceMs);
        }
        Expire(now);
        return now >= _notificationDue || now >= _discoveryDue ||
            (State == PathRecoveryState.Recovering && Attempts < MaxAttempts && now >= _attemptDue);
    }

    public void Inspected(long now)
    {
        _firstNotificationAt = -1;
        _notificationDue = long.MaxValue;
        _discoveryDue = now + DiscoveryIntervalMs;
    }

    public bool AttemptDue(long now) => State == PathRecoveryState.Recovering &&
        Attempts < MaxAttempts && now >= _attemptDue;

    public void Attempted(long now)
    {
        if (!AttemptDue(now)) return;
        Attempts++;
        _attemptDue = now + (15_000L << (Attempts - 1));
    }

    public bool CanReplace(long now) => now >= _replacementAllowedAt;

    public void WaitForReplacement() => _attemptDue = Math.Max(_attemptDue, _replacementAllowedAt);

    public void Replaced(long now) => _replacementAllowedAt = now + MinReplacementIntervalMs;

    public void Succeeded(long now, long revision) => Reset(now, revision);

    public void Expire(long now)
    {
        if (State == PathRecoveryState.Recovering &&
            (now >= _episodeDeadline || (Attempts == MaxAttempts && now >= _attemptDue)))
            State = PathRecoveryState.Failed;
    }

    public PathRecoverySnapshot Snapshot(long now) => new(State, Attempts,
        State == PathRecoveryState.Recovering ? Math.Clamp(_attemptDue - now, 0, 300_000) : 0);
}
