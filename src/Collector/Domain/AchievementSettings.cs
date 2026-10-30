namespace MentorRecorder.Collector.Domain;

/// <summary>
/// One region's row of achievement_settings. It supports the
/// case where the user starts using this software part-way through the achievement.
/// </summary>
public sealed record AchievementSettings
{
    /// <summary>Owning service region; snapshots written before regional settings belong to CN.</summary>
    public Region Region { get; init; } = Region.Cn;

    /// <summary>Default goal for the mentor roulette achievement.</summary>
    public const int DefaultGoalCount = 2000;

    /// <summary>Target number of completions.</summary>
    public required int GoalCount { get; init; }

    /// <summary>Self-reported completions from before this software was used.</summary>
    public required int BaselineCompletedCount { get; init; }

    /// <summary>
    /// When the baseline count last changed. Information only: the progress adds every recorded
    /// completion to the baseline, whenever it ended.
    /// </summary>
    public required DateTimeOffset BaselineEffectiveAt { get; init; }

    /// <summary>Time of the last change.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
