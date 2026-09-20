using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Why the index is read again although a profile is already recording
/// (docs/privacy-boundary.md §8.2, plans/shared-calibration-rollback.md §3). Both reasons exist because
/// what is in force can still turn out to be withdrawn or second-best, and nothing on this machine can
/// learn that on its own.
/// </summary>
public enum SharedRecheckReason
{
    /// <summary>
    /// Another player's calibration is in force. The public repository may have revoked its code since it
    /// bound, and this machine is the one still recording with it.
    /// </summary>
    SharedInUse,

    /// <summary>
    /// What is in force infers the match from the player's own queue request, whatever wrote it. A code that
    /// reads the server's own announcement outranks it, and only the index can offer one.
    /// </summary>
    QueueInferredInUse,
}

/// <summary>The last index read that happened although a profile was already recording.</summary>
/// <param name="LastUtc">When that read was claimed.</param>
/// <param name="Status">How it ended, in the vocabulary of the last fetch status.</param>
/// <param name="Reason">What allowed it.</param>
public sealed record SharedRecheckRecord(DateTimeOffset LastUtc, SharedFetchStatus Status, SharedRecheckReason Reason);

/// <summary>Whether a profile already in force still permits the one outbound request.</summary>
public static class SharedRecheck
{
    /// <summary>
    /// Why the index may be read beside the profile in force, or null when it must not be read at all.
    /// The only two answers are a shared profile - which the repository can revoke - and one that infers
    /// the match, which a code reading the server's own announcement outranks. A shipped profile and a
    /// local profile that reads the match are answers nothing published can improve on, so nothing is sent
    /// beside them, exactly as before.
    /// </summary>
    /// <param name="inUse">Whether the selection in force is usable, i.e. something is recording with it.</param>
    /// <param name="origin">Where that profile came from; null when the selection names none.</param>
    /// <param name="matchFromQueue">Whether it infers the match from the player's own queue request.</param>
    public static SharedRecheckReason? ReasonFor(bool inUse, ProfileOrigin? origin, bool matchFromQueue) =>
        !inUse ? null
        : origin == ProfileOrigin.Shared ? SharedRecheckReason.SharedInUse
        : matchFromQueue ? SharedRecheckReason.QueueInferredInUse
        : null;
}
