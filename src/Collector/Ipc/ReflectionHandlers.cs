using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The two 导随心得 messages, kept out of <see cref="MessageDispatcher"/>. They answer against
/// the host the dispatcher owns and hold no state of their own.
/// </summary>
public static class ReflectionHandlers
{
    /// <summary>
    /// Answers <c>SetRunReflection</c>: write, replace or clear one reflection.
    ///
    /// The two live events are published only for a write that actually happened: a replayed
    /// request id must not cause a spurious <c>stats_invalidated</c> and a full UI re-query.
    /// </summary>
    /// <param name="host">Open collector host.</param>
    /// <param name="requestId">Envelope request id; the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject SetRunReflection(CollectorHost host, string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);

        var outcome = host.ReflectionWrites.Set(RequestParsers.SetRunReflection(requestId, reader));

        if (!outcome.IdempotentReplay && outcome.Run is { } run)
        {
            host.LiveEvents.PublishRun(LiveEventKind.RunUpdated, run);
            host.LiveEvents.PublishStatsInvalidated("reflection_changed");
        }

        var payload = new JsonObject
        {
            ["run_id"] = outcome.RunId,
            ["reflection"] = Wire.Reflection(outcome.Reflection),
        };

        if (outcome.Run is { } rendered)
        {
            payload["run"] = Wire.Run(rendered);
        }

        return payload;
    }

    /// <summary>
    /// Answers <c>GetReflectionSummary</c>: what the dashboard panel needs in one round trip.
    ///
    /// Runs come back through the run repository rather than a join written here, so each is
    /// byte-for-byte what <c>QueryRuns</c> would return, its own <c>reflection</c> included.
    /// </summary>
    /// <param name="host">Open collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject GetReflectionSummary(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);

        var limit = RequestParsers.ReflectionRecentLimit(reader);
        var summary = host.Reflections.GetSummary(limit);

        var recent = new JsonArray();
        foreach (var runId in summary.RecentRunIds)
        {
            // A run that vanished between the two reads is skipped rather than rendered with
            // a null reflection: $defs/ReflectionEntry requires both halves to be present.
            if (host.Runs.Get(runId) is { Reflection: { } reflection } run)
            {
                recent.Add(Wire.ReflectionEntry(run, reflection));
            }
        }

        return new JsonObject
        {
            ["reflection_count"] = summary.ReflectionCount,
            ["pending_completed_count"] = summary.PendingCompletedCount,
            ["recent"] = recent,
            ["next_pending"] = NextPending(host, summary.NextPendingRunId),
        };
    }

    private static JsonNode? NextPending(CollectorHost host, string? runId) =>
        runId is null ? null : host.Runs.Get(runId) is { } run ? Wire.Run(run) : null;
}
