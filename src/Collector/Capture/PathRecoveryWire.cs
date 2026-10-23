using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Capture;

/// <summary>The bounded recovery whitelist shared by IPC and diagnostic export.</summary>
public static class PathRecoveryWire
{
    public const int MaxAttempts = 3;
    public const long MaxRetryAfterMs = 300_000;

    /// <summary>Renders progress without endpoint, adapter or exception details.</summary>
    /// <param name="snapshot">Recovery progress; invalid counters are clamped to the contract bounds.</param>
    public static JsonObject ToJson(PathRecoverySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var state = snapshot.State switch
        {
            PathRecoveryState.Recovering => "RECOVERING",
            PathRecoveryState.Failed => "FAILED",
            _ => "NONE",
        };
        return new JsonObject
        {
            ["state"] = state,
            ["attempts"] = state == "NONE" ? 0 : Math.Clamp(snapshot.Attempts, 0, MaxAttempts),
            ["retry_after_ms"] = state == "RECOVERING"
                ? Math.Clamp(snapshot.RetryAfterMs, 0, MaxRetryAfterMs)
                : 0,
        };
    }
}
