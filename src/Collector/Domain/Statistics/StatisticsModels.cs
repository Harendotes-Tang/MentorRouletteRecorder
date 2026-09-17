using System.Text.Json.Serialization;

namespace MentorRecorder.Collector.Domain.Statistics;

/// <summary>One result-distribution bucket.</summary>
public sealed record ResultBucket(RunResult Result, int Count, double? Share);

/// <summary>All six result buckets and their common attempt denominator.</summary>
public sealed record ResultStatistics(int AttemptCount, IReadOnlyList<ResultBucket> Buckets);

/// <summary>Bucket width of a completion trend series.</summary>
public enum TrendGranularity
{
    /// <summary>One bucket per UTC calendar day.</summary>
    Day,

    /// <summary>One bucket per ISO week, starting Monday 00:00 UTC.</summary>
    Week,

    /// <summary>One bucket per UTC calendar month.</summary>
    Month,
}

/// <summary>One trend bucket.</summary>
/// <param name="StartUtc">Inclusive start of the bucket, always midnight UTC.</param>
/// <param name="CompletedCount">COMPLETED runs whose <c>matched_at_utc</c> falls in the bucket.</param>
public sealed record TrendBucket(DateTimeOffset StartUtc, int CompletedCount);

/// <summary>
/// The completion trend: a fixed-length, gap-free series ending with the bucket that
/// contains "now". Empty buckets are present with a count of zero, never omitted, so the
/// chart cannot silently compress a quiet week into nothing.
/// </summary>
/// <param name="Granularity">Bucket width.</param>
/// <param name="Buckets">Buckets, oldest first.</param>
public sealed record TrendSeries(TrendGranularity Granularity, IReadOnlyList<TrendBucket> Buckets);

/// <summary>Fixed dashboard statistics defined by docs/statistics-definitions.md.</summary>
public sealed record DashboardStatistics(
    int AttemptCount,
    int CompletedCount,
    int BaselineCompletedCount,
    int AchievementProgress,
    int GoalCount,
    int Remaining,
    double? CompletionRate,
    double? LeaveRate,
    [property: JsonPropertyName("avg_duration_ms")] double? AverageDurationMs,
    ResultStatistics ResultBreakdown,
    int UnfinishedPendingReview,
    TrendSeries Trend);

/// <summary>
/// Statistics aggregated by duty identity: the observed content id when there is one, and
/// otherwise the territory the run was entered through.
/// </summary>
/// <remarks>
/// <see cref="ContentId"/> stays the wire field and stays null for a territory-only group,
/// because a territory id is not a content id and presenting it as one is exactly the guess
/// review finding M-5 removed.
/// </remarks>
public sealed record DungeonStatisticsRow(
    int? ContentId,
    string DutyName,
    string? DutyCategory,
    int AttemptCount,
    int CompletedCount,
    double? CompletionRate,
    [property: JsonPropertyName("avg_duration_ms")] double? AverageDurationMs,
    [property: JsonPropertyName("last_seen_utc")] DateTimeOffset? LastSeenUtc = null);

/// <summary>Statistics aggregated by job id.</summary>
public sealed record JobStatisticsRow(
    int? JobId,
    string JobName,
    Role Role,
    int AttemptCount,
    int CompletedCount,
    double? CompletionRate,
    [property: JsonPropertyName("avg_duration_ms")] double? AverageDurationMs);
