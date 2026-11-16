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
/// <param name="CompletedCount">Confirmed COMPLETED runs whose actual-or-source history date falls in the bucket.</param>
public sealed record TrendBucket(DateTimeOffset StartUtc, int CompletedCount);

/// <summary>
/// The completion trend: a fixed-length, gap-free series ending with the bucket that
/// contains "now". Empty buckets are present with a count of zero, never omitted, so the
/// chart cannot silently compress a quiet week into nothing.
/// </summary>
/// <param name="Granularity">Bucket width.</param>
/// <param name="Buckets">Buckets, oldest first.</param>
public sealed record TrendSeries(TrendGranularity Granularity, IReadOnlyList<TrendBucket> Buckets);

/// <summary>
/// One region's achievement, independent of date, duty and job listing filters: the numbers of the
/// region's current character profile.
/// </summary>
/// <param name="ProfileId">The region's current profile; null while the region has none.</param>
/// <param name="ProfileCount">How many profiles the region holds.</param>
/// <param name="UnassignedRunCount">Live runs of the region that belong to no profile.</param>
/// <param name="Identification">How a newly recorded run of the region finds its profile.</param>
public sealed record RegionalAchievementStatistics(
    Region Region,
    int GoalCount,
    int BaselineCompletedCount,
    int AchievementProgress,
    int Remaining,
    int CompletedCount,
    long? ProfileId = null,
    int ProfileCount = 0,
    int UnassignedRunCount = 0,
    CharacterIdentificationMode Identification = CharacterIdentificationMode.ManualRegion);

/// <summary>One character profile and its achievement progress.</summary>
/// <param name="Profile">The profile as stored.</param>
/// <param name="CompletedCount">Confirmed, contributing completions that belong to the profile.</param>
/// <param name="AchievementProgress">Baseline plus <paramref name="CompletedCount"/>, capped at Int32.</param>
/// <param name="Remaining">Completions still missing to the goal; never negative.</param>
/// <param name="RunCount">Live runs that belong to the profile, whatever their result.</param>
/// <param name="IdentityCount">Characters told apart in the game traffic that are bound to the profile.</param>
public sealed record CharacterProfileStatistics(
    CharacterProfile Profile,
    int CompletedCount,
    int AchievementProgress,
    int Remaining,
    int RunCount,
    int IdentityCount = 0);

/// <summary>A character told apart in the game traffic that is bound to no profile.</summary>
/// <param name="Identity">The identity as stored; never <see cref="CharacterIdentityState.Bound"/>.</param>
/// <param name="RunCount">Live runs recorded for it that are assigned to no profile.</param>
public sealed record CharacterIdentityStatistics(CharacterIdentity Identity, int RunCount);

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
    TrendSeries Trend,
    int JoinedInProgressCount,
    double? JoinedInProgressRate,
    IReadOnlyList<RegionalAchievementStatistics> Achievements,
    IReadOnlyList<CharacterProfileStatistics>? Profiles = null,
    IReadOnlyList<CharacterIdentityStatistics>? Identities = null);

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
    [property: JsonPropertyName("last_seen_utc")] DateTimeOffset? LastSeenUtc = null,
    [property: JsonPropertyName("joined_in_progress_count")] int JoinedInProgressCount = 0,
    Region Region = Region.Unknown);

/// <summary>Statistics aggregated by job id.</summary>
public sealed record JobStatisticsRow(
    int? JobId,
    string JobName,
    Role Role,
    int AttemptCount,
    int CompletedCount,
    double? CompletionRate,
    [property: JsonPropertyName("avg_duration_ms")] double? AverageDurationMs,
    [property: JsonPropertyName("joined_in_progress_count")] int JoinedInProgressCount = 0);
