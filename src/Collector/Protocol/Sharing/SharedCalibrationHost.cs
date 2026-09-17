using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// What a download or a bind is claimed for: its
/// result counts only while region, build, template and arm epoch are all still the ones in force.
/// <c>Generation</c> is deliberately absent - starting a capture session bumps it, and a fetch
/// registered while waiting for the game would be thrown away the moment the player logged in.
/// </summary>
/// <param name="Region">Region of the client.</param>
/// <param name="GameBuild">Build being calibrated.</param>
/// <param name="TemplateSha256">Template in force.</param>
/// <param name="ArmEpoch">Bumped by every arm and disarm of calibration.</param>
internal sealed record SharedKey(Region Region, string GameBuild, string TemplateSha256, int ArmEpoch);

/// <summary>Everything shared calibration reads from the pipeline at one moment, under its gate.</summary>
/// <param name="Key">What is being calibrated.</param>
/// <param name="Template">Template in force.</param>
/// <param name="Selection">The formal selection as it stands.</param>
/// <param name="StagingSessionId">The running session when it has no parser bound, so candidates must stage; otherwise null.</param>
internal sealed record SharedContext(
    SharedKey Key, CalibrationTemplate Template, ProfileSelection Selection, string? StagingSessionId);

/// <summary>What committing a written shared profile did.</summary>
internal enum SharedBindOutcome
{
    /// <summary>Selected and bound inside the running session, staged events drained.</summary>
    Bound,

    /// <summary>Selected; it records from the next capture session (none running, or another profile records this one).</summary>
    Selected,

    /// <summary>The catalogue did not select it; nothing changed.</summary>
    NotSelected,

    /// <summary>Selected, but the staging no longer belongs to the running session or overflowed; nothing changed.</summary>
    SessionChanged,
}

/// <summary>What <see cref="ISharedCalibrationHost.CommitSharedBind"/> did.</summary>
/// <param name="Outcome">Bound, selected for the next session, or refused.</param>
/// <param name="Reason">Short token for diagnostics.</param>
/// <param name="Proven">
/// True when the run table already holds a complete entry and exit under the profile - a duty the
/// drained staging finished - so the watch after binding is already over.
/// </param>
internal sealed record SharedBindResult(SharedBindOutcome Outcome, string Reason, bool Proven = false);

/// <summary>A written shared profile to commit.</summary>
/// <param name="ProfileId">Id of the profile just written.</param>
/// <param name="Select">Selector reloaded from disk after the write.</param>
/// <param name="Stage">The candidate's staging for the running session, if any.</param>
internal sealed record SharedBindRequest(
    string ProfileId, Func<GameProcessDetection, ProfileSelection> Select, SharedCandidateStage? Stage);

/// <summary>
/// The narrow part of <c>LiveProtocolPipeline</c> that shared calibration needs. Every member is
/// called with the pipeline's gate held.
/// </summary>
internal interface ISharedCalibrationHost
{
    /// <summary>What is being calibrated, or null when calibration is not armed.</summary>
    SharedContext? SharedContext();

    /// <summary>The formal selection in force, whether or not calibration is armed.</summary>
    ProfileSelection SharedSelection();

    /// <summary>The observer's evidence, or null before observation started.</summary>
    CalibrationSnapshot? SharedEvidence();

    /// <summary>Starts counting a candidate, in this observer and every later one for the same arm.</summary>
    void RegisterSharedCandidate(DeclaredCandidate candidate);

    /// <summary>Stops counting a candidate.</summary>
    void UnregisterSharedCandidate(string candidateId);

    /// <summary>True when a run recorded under the profile entered a duty and its exit was observed.</summary>
    bool HasFinishedSharedRun(string profileId);

    /// <summary>
    /// Adopts the reloaded selector and, when it selects the written profile, binds it: in the running
    /// session when that session has no parser, draining the stage in order; otherwise from the next session.
    /// </summary>
    SharedBindResult CommitSharedBind(SharedBindRequest request);

    /// <summary>Stops recording with a withdrawn shared profile at once and stops calling it selected.</summary>
    void UnbindSharedProfile(string profileId);

    /// <summary>Adopts a selector reloaded after a shared profile was removed, and re-arms calibration.</summary>
    void ReselectAfterSharedChange(Func<GameProcessDetection, ProfileSelection> select);

    /// <summary>Tells the Desktop when what it shows changed.</summary>
    void SharedCalibrationChanged();
}
