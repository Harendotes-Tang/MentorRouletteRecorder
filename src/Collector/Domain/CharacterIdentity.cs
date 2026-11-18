namespace MentorRecorder.Collector.Domain;

/// <summary>What the player has said about a character the Collector told apart.</summary>
public enum CharacterIdentityState
{
    /// <summary>Not answered yet: its runs are assigned to no profile and the Desktop asks.</summary>
    Pending,

    /// <summary>Bound to a profile: its runs are filed under that profile.</summary>
    Bound,

    /// <summary>The player keeps no progress for it: its runs are assigned to no profile, nobody asks.</summary>
    Ignored,
}

/// <summary>How a newly recorded run of a region finds its profile.</summary>
public enum CharacterIdentificationMode
{
    /// <summary>The Collector tells the logged-in character apart.</summary>
    Automatic,

    /// <summary>The region has not been verified for it; the current profile is chosen by hand.</summary>
    ManualRegion,

    /// <summary><c>capture.character_identification_enabled</c> is off.</summary>
    ManualOff,
}

/// <summary>
/// One row of character_identities: a character the Collector has seen logged in, and the profile
/// it is (docs/data-model.md, character_identities).
///
/// The row is local bookkeeping. What distinguishes the character is a keyed digest that never
/// leaves the Collector: it is not in this record, on the pipe, in a log or in an export
/// (docs/privacy-boundary.md).
/// </summary>
/// <param name="IdentityId">Local identifier; never reused.</param>
/// <param name="Region">Region the character was seen in.</param>
/// <param name="ProfileId">Profile it is bound to, or null.</param>
/// <param name="Ignored">True when the player chose to keep no progress for it.</param>
/// <param name="FirstSeenAtUtc">The first login it was seen at.</param>
/// <param name="LastSeenAtUtc">The latest login it was seen at.</param>
/// <param name="RegistrationProfileId">Upgraded profile awaiting confirmation for this identity, if any.</param>
public sealed record CharacterIdentity(
    long IdentityId,
    Region Region,
    long? ProfileId,
    bool Ignored,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    long? RegistrationProfileId = null)
{
    /// <summary>Most identities one region keeps; a further unknown character is not recorded.</summary>
    public const int MaxIdentitiesPerRegion = 64;

    /// <summary>The player's answer so far.</summary>
    public CharacterIdentityState State =>
        ProfileId is not null ? CharacterIdentityState.Bound
        : Ignored ? CharacterIdentityState.Ignored
        : CharacterIdentityState.Pending;
}

/// <summary>Where automatic identification applies.</summary>
public static class CharacterIdentification
{
    /// <summary>
    /// Setting that turns identification on and off; on when it was never written. Owned by the
    /// capture settings (<c>CaptureSettingsStore</c>), named here so the statistics can read it.
    /// </summary>
    public const string EnabledSetting = "capture.character_identification_enabled";

    /// <summary>
    /// Setting that counts the capture sessions in a row that found the identity key file
    /// unusable; absent or 0 while it is fine. Bookkeeping of the Collector, not a preference.
    /// </summary>
    public const string KeyFailuresSetting = "capture.character_identity_key_failures";

    /// <summary>
    /// The mode of a region. Only the Chinese service has been verified on a real machine
    /// (2026-11-15: three characters, each with its own identifier, the same one after logging in
    /// again and through zone changes); the other regions stay manual until a tester has shown the
    /// same there.
    /// </summary>
    /// <param name="region">Service region.</param>
    /// <param name="enabled"><c>capture.character_identification_enabled</c> as it stands.</param>
    public static CharacterIdentificationMode ModeFor(Region region, bool enabled) =>
        !enabled ? CharacterIdentificationMode.ManualOff
        : region == Region.Cn ? CharacterIdentificationMode.Automatic
        : CharacterIdentificationMode.ManualRegion;
}
