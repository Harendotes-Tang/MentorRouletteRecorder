namespace MentorRecorder.Collector.Domain;

/// <summary>
/// One row of character_profiles: the achievement goal and baseline of one character the player
/// keeps records for, inside one region (docs/data-model.md, character_profiles).
///
/// The profile is local bookkeeping. Its id is not a game identifier and its name is typed by the
/// player; nothing here is read from the game (docs/privacy-boundary.md).
/// </summary>
public sealed record CharacterProfile
{
    /// <summary>Longest accepted display name, in UTF-16 code units.</summary>
    public const int MaxDisplayNameLength = 24;

    /// <summary>Most profiles one region may hold.</summary>
    public const int MaxProfilesPerRegion = 16;

    /// <summary>Local identifier; never reused after a deletion.</summary>
    public required long ProfileId { get; init; }

    /// <summary>Region the profile belongs to; fixed for its lifetime.</summary>
    public required Region Region { get; init; }

    /// <summary>
    /// Name the player gave. Empty for the profile the software created by itself (the region's
    /// records from before profiles existed, or the first profile of a region); the Desktop words
    /// that one.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>True for the one profile of its region that newly recorded runs are filed under.</summary>
    public required bool IsCurrent { get; init; }

    /// <summary>Target number of completions.</summary>
    public required int GoalCount { get; init; }

    /// <summary>Self-reported completions from before this software was used.</summary>
    public required int BaselineCompletedCount { get; init; }

    /// <summary>When the baseline count last changed. Information only.</summary>
    public required DateTimeOffset BaselineEffectiveAt { get; init; }

    /// <summary>Creation time.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Time of the last change.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>The goal and baseline of this profile, in the shape the baseline writers use.</summary>
    public AchievementSettings ToSettings() => new()
    {
        ProfileId = ProfileId,
        Region = Region,
        GoalCount = GoalCount,
        BaselineCompletedCount = BaselineCompletedCount,
        BaselineEffectiveAt = BaselineEffectiveAt,
        UpdatedAtUtc = UpdatedAtUtc,
    };
}
