namespace MentorRecorder.Collector.Domain;

/// <summary>
/// The single row of achievement_settings (docs/data-model.md section 4). It supports the
/// case where the user starts using this software part-way through the achievement.
/// </summary>
public sealed record AchievementSettings
{
    /// <summary>Default goal for the mentor roulette achievement.</summary>
    public const int DefaultGoalCount = 2000;

    /// <summary>Target number of completions.</summary>
    public required int GoalCount { get; init; }

    /// <summary>Self-reported completions from before this software was used.</summary>
    public required int BaselineCompletedCount { get; init; }

    /// <summary>When the baseline takes effect.</summary>
    public required DateTimeOffset BaselineEffectiveAt { get; init; }

    /// <summary>Time of the last change.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }
}
