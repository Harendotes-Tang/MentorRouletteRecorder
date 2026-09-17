namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>
/// What the capture layer and the IPC status message may know about the profile in force.
///
/// Everything here is metadata: identifiers, counts and a short refusal reason. No opcode,
/// no offset and no payload ever leaves the parser through this type
/// (docs/privacy-boundary.md section 5).
/// </summary>
/// <param name="ProfileId">Selected profile identifier, or null when none was selected.</param>
/// <param name="Region">Region wire token that was asked for.</param>
/// <param name="GameBuild">Client build that was asked for, or null when unknown.</param>
/// <param name="Status">Compatibility status wire token, including AMBIGUOUS.</param>
/// <param name="MessageCount">Number of messages the selected profile declares.</param>
/// <param name="FixtureVerified">True when every fixture the profile references was verified.</param>
/// <param name="LastError">Short reason the profile is not usable, or null when it is.</param>
/// <param name="Origin">
/// Where the selected profile's file came from, or null when none was selected or the
/// origin is not known.
/// </param>
public sealed record ProfileStatusSnapshot(
    string? ProfileId,
    string Region,
    string? GameBuild,
    string Status,
    int MessageCount,
    bool FixtureVerified,
    string? LastError,
    ProfileOrigin? Origin = null);

/// <summary>Read-only view of the profile currently in force.</summary>
public interface IProfileStatusProvider
{
    /// <summary>Takes a snapshot of the current profile status.</summary>
    ProfileStatusSnapshot GetProfileStatus();
}
