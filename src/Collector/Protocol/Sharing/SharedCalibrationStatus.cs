using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>Where shared calibration stands for the running client.</summary>
public enum SharedCalibrationPhase
{
    /// <summary>Nothing to show: not calibrating, the setting is off, or nobody shared this build yet.</summary>
    None,

    /// <summary>A download has been running for a noticeable moment.</summary>
    Fetching,

    /// <summary>The last download reached no source or obtained no code; local calibration carries on.</summary>
    Unavailable,

    /// <summary>At least one shared calibration is being checked against local traffic.</summary>
    Verifying,

    /// <summary>A queue-inferred shared calibration passed and waits for the player to accept inference once.</summary>
    AwaitingConsent,

    /// <summary>A shared calibration passed and its profile was written; it records now or from the next session.</summary>
    Verified,

    /// <summary>Every shared calibration contradicted local traffic, or the one in use was withdrawn.</summary>
    Rejected,
}

/// <summary>How a candidate reached this machine.</summary>
public enum SharedCandidateSource
{
    /// <summary>Downloaded from the public repository.</summary>
    Downloaded,

    /// <summary>Pasted by the player (导入校准码).</summary>
    Manual,
}

/// <summary>
/// What stands behind a candidate, which decides how much local traffic must vouch for it before it
/// records (plan §18.3). Distinct from <see cref="SharedCandidateSource"/>: a pasted code that the
/// public repository's index lists is published all the same.
/// </summary>
public enum SharedCandidateProvenance
{
    /// <summary>
    /// Listed by the public repository's index: downloaded, or pasted and found in the last index this
    /// machine read. The login burst suffices to bind; the match and the duty entry are audited afterwards.
    /// </summary>
    Published,

    /// <summary>
    /// Pasted and not in any index this machine has read. Nothing is recorded until the match and the
    /// duty entry have both been seen to behave.
    /// </summary>
    Imported,
}

/// <summary>Where one candidate stands.</summary>
public enum SharedCandidateStatus
{
    /// <summary>Not yet proven either way.</summary>
    Verifying,

    /// <summary>Passed; a queue-inferred code waits for the player's consent.</summary>
    AwaitingConsent,

    /// <summary>Passed, but cannot bind in this capture session (its staging overflowed or the write was refused).</summary>
    CannotBindThisSession,

    /// <summary>Passed; its profile is being written.</summary>
    Writing,

    /// <summary>Its profile is in use and still being watched until it records one complete entry and exit.</summary>
    InUse,

    /// <summary>Its profile recorded a complete entry and exit.</summary>
    Proven,

    /// <summary>Contradicted by two healthy capture sessions, revoked, or withdrawn after use.</summary>
    Rejected,
}

/// <summary>One shared calibration as the card and the diagnostics report see it. No opcode, path or payload.</summary>
/// <param name="Sha12">First twelve hex digits of the code's identity.</param>
/// <param name="Source">How it arrived; null for a profile adopted from disk whose origin was not recorded.</param>
/// <param name="MatchSource">How the code names the match; null when it could not be recovered.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="Verdict">The verifier's latest verdict.</param>
/// <param name="Criteria">Per-message verdicts with plain-language reasons.</param>
/// <param name="StagingOverflowed">True when its staged events outgrew the bound in this session.</param>
public sealed record SharedCandidateSummary(
    string Sha12,
    SharedCandidateSource? Source,
    CalibrationMatchSource? MatchSource,
    SharedCandidateStatus Status,
    SharedVerdict Verdict,
    IReadOnlyList<SharedCriterion> Criteria,
    bool StagingOverflowed)
{
    /// <summary>Which gate set it is judged by (plan §18.3); null for a profile adopted from disk whose code was not recovered.</summary>
    public SharedCandidateProvenance? Provenance { get; init; }

    /// <summary>True while it records (or may bind) with an audited criterion still waiting.</summary>
    public bool AuditPending { get; init; }
}

/// <summary>The <c>shared</c> part of calibration status (<c>$defs/SharedCalibrationStatus</c>) and of the diagnostics report.</summary>
/// <param name="Phase">Where shared calibration stands.</param>
/// <param name="Candidates">The profile in use first, then candidates being verified, then rejected ones.</param>
/// <param name="LastFetchStatus">How the last download for this build ended, when one was claimed.</param>
/// <param name="LastIndexAttempts">Per-source result codes of that download; never an address.</param>
/// <param name="ProfileId">Shared profile id in use, when any.</param>
/// <param name="BoundAtUtc">When it began recording inside a running session, when it did.</param>
/// <param name="LastRefusal">Short token saying why the last bind or withdrawal did not go through; diagnostics only.</param>
public sealed record SharedCalibrationSnapshot(
    SharedCalibrationPhase Phase,
    IReadOnlyList<SharedCandidateSummary> Candidates,
    SharedFetchStatus? LastFetchStatus,
    IReadOnlyList<SharedSourceAttempt> LastIndexAttempts,
    string? ProfileId,
    DateTimeOffset? BoundAtUtc,
    string? LastRefusal)
{
    /// <summary>Nothing to report.</summary>
    public static SharedCalibrationSnapshot None { get; } = new(
        SharedCalibrationPhase.None, Array.Empty<SharedCandidateSummary>(), null, Array.Empty<SharedSourceAttempt>(), null, null, null);

    /// <summary>
    /// True while the player's 不用共享的，我自己校准 stands for the build: nothing shared is fetched, imported or
    /// bound for it until 重新观察. <see cref="Phase"/> then reads <see cref="SharedCalibrationPhase.Rejected"/>.
    /// </summary>
    public bool UserRejected { get; init; }

    /// <summary>
    /// True while the profile in use records with an audited criterion still waiting (plan §18.4): a published
    /// code bound at login whose match or duty entry has not yet been seen to behave.
    /// </summary>
    public bool AuditPending { get; init; }

    /// <summary>When this process last actually sent a shared-calibration request, for any build; null when it never did.</summary>
    public DateTimeOffset? LastSentAtUtc { get; init; }

    /// <summary>How that request ended; null when nothing was ever sent.</summary>
    public SharedFetchStatus? LastSentStatus { get; init; }
}

/// <summary>What 不用共享的，我自己校准 (<c>RejectSharedCalibration</c>) did.</summary>
/// <param name="WithdrawnProfileId">The shared profile that was in force and is being withdrawn; null when there was none.</param>
/// <param name="DroppedCandidates">Candidates that were being verified and are no longer.</param>
public sealed record SharedRejectResult(string? WithdrawnProfileId, int DroppedCandidates)
{
    /// <summary>Nothing to reject: no build is known and nothing shared is armed or bound.</summary>
    internal static SharedRejectResult Nothing { get; } = new(null, 0);
}

/// <summary>What importing a pasted code came to.</summary>
public enum SharedImportOutcome
{
    /// <summary>The code fits this client and is being verified like a downloaded one.</summary>
    Applied,

    /// <summary>A well-formed code that is not for this client, template or moment; <see cref="SharedImportResult.Reason"/> says why.</summary>
    NotApplicable,

    /// <summary>Not a code this software can read.</summary>
    Malformed,
}

/// <summary>The answer to <c>ImportCalibrationCode</c>.</summary>
/// <param name="Outcome">Applied, not applicable or malformed.</param>
/// <param name="Reason">
/// Machine token: a share-code rejection code for malformed input, or NOT_CALIBRATING, OTHER_REGION,
/// OTHER_BUILD, OTHER_TEMPLATE, REJECTED, TOO_MANY_CANDIDATES, CHANGED, UNBUILDABLE. Null when applied.
/// </param>
/// <param name="Message">What to tell the player, in Chinese, without opcodes.</param>
/// <param name="CodeSha256">Identity of the code, when it decoded.</param>
/// <param name="Provenance">
/// When applied: published (the last index this machine read lists the code) or imported (unknown to any index,
/// so every criterion must pass before it records). Null otherwise.
/// </param>
public sealed record SharedImportResult(
    SharedImportOutcome Outcome, string? Reason, string Message, string? CodeSha256 = null, SharedCandidateProvenance? Provenance = null);

/// <summary>What 立即检查 did.</summary>
public enum SharedCheckOutcome
{
    /// <summary>A download started.</summary>
    Started,

    /// <summary>One was already running.</summary>
    AlreadyFetching,

    /// <summary>The setting is off, or this build of the Collector has no download wired.</summary>
    Disabled,

    /// <summary>Nothing to fetch for: not calibrating, or a profile is already recording.</summary>
    NotNeeded,
}

/// <summary>What accepting queue inference did.</summary>
public enum SharedConsentOutcome
{
    /// <summary>Consent recorded for this region and build; the waiting code proceeds to binding.</summary>
    Accepted,

    /// <summary>No queue-inferred code was waiting for consent.</summary>
    NothingToAccept,
}
