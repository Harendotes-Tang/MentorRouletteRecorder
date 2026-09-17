using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Domain.StateMachine;

/// <summary>
/// Everything the state machine is allowed to know about the protocol profile in force.
///
/// It carries no opcode and no structure offset. It exists so that fail-closed is a property
/// of the state machine itself and can be unit tested without any capture code
/// (docs/state-machine.md section 0).
/// </summary>
public sealed record ProfileBinding
{
    /// <summary>Profile identifier, or null when there is no profile at all.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Region the profile belongs to.</summary>
    public Region Region { get; init; } = Region.Unknown;

    /// <summary>Status as loaded and validated.</summary>
    public ProfileStatus Status { get; init; } = ProfileStatus.None;

    /// <summary>The roulette id that identifies the mentor roulette; null when unknown.</summary>
    public int? MentorRouletteId { get; init; }

    /// <summary>
    /// True when the profile declares DUTY_RESULT, so a duty can end in COMPLETED. When
    /// false, every exit from a duty closes as UNKNOWN pending the user's confirmation
    /// (docs/state-machine.md section 3.10).
    /// </summary>
    public bool CanDetectDutyResult { get; init; }

    /// <summary>
    /// True when the profile has no message from the server announcing a match, and stands the
    /// player's own queue request in for it. Such a profile cannot see the difference between
    /// "the queue matched" and "the player queued", so the machine demands more of the entry
    /// that follows: it must be a duty the reference table recognises, not any zone change.
    /// </summary>
    public bool MatchFromQueue { get; init; }

    /// <summary>
    /// True only for a synthetic profile driven by an offline fixture. Such a profile is
    /// usable by the replay tool and by tests, and is never usable for live capture: it can
    /// only be constructed by <see cref="Synthetic"/>, which live code never calls.
    /// </summary>
    public bool IsSynthetic { get; init; }

    /// <summary>
    /// True when the state machine may act on events at all. Only a VERIFIED live profile
    /// or an explicitly synthetic offline profile qualifies; everything else is fail-closed.
    /// </summary>
    public bool IsUsable =>
        MentorRouletteId is not null &&
        (Status == ProfileStatus.Verified || IsSynthetic);

    /// <summary>A profile that permits nothing. This is the default of the whole system.</summary>
    public static ProfileBinding FailClosed { get; } = new()
    {
        ProfileId = null,
        Status = ProfileStatus.None,
        MentorRouletteId = null,
    };

    /// <summary>Binds a live, evidence-verified profile.</summary>
    /// <param name="profileId">Profile identifier.</param>
    /// <param name="region">Region.</param>
    /// <param name="status">Status determined by the profile loader.</param>
    /// <param name="mentorRouletteId">Mentor roulette id declared by the profile.</param>
    /// <param name="canDetectDutyResult">True when the profile declares DUTY_RESULT.</param>
    /// <param name="matchFromQueue">True when the profile infers the match from the queue request.</param>
    public static ProfileBinding Live(
        string profileId,
        Region region,
        ProfileStatus status,
        int? mentorRouletteId,
        bool canDetectDutyResult = false,
        bool matchFromQueue = false) => new()
        {
            ProfileId = profileId,
            Region = region,
            Status = status,
            MentorRouletteId = status == ProfileStatus.Verified ? mentorRouletteId : null,
            CanDetectDutyResult = status == ProfileStatus.Verified && canDetectDutyResult,
            MatchFromQueue = status == ProfileStatus.Verified && matchFromQueue,
            IsSynthetic = false,
        };

    /// <summary>
    /// Binds a synthetic profile declared by an offline fixture. The resulting binding is
    /// reported as UNVERIFIED to any client, because it is not evidence of anything about
    /// the real game protocol.
    /// </summary>
    /// <param name="profileId">Synthetic profile identifier, for example synthetic/v1.</param>
    /// <param name="mentorRouletteId">Synthetic mentor roulette id declared by the fixture.</param>
    public static ProfileBinding Synthetic(string profileId, int mentorRouletteId) => new()
    {
        CanDetectDutyResult = true,
        ProfileId = profileId,
        Region = Region.Unknown,
        Status = ProfileStatus.Unverified,
        MentorRouletteId = mentorRouletteId,
        IsSynthetic = true,
    };
}
