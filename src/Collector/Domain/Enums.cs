namespace MentorRecorder.Collector.Domain;

/// <summary>
/// Outcome of one mentor roulette attempt. Mirrors <c>mentor_runs.result</c> and
/// <c>$defs/RunResult</c> in <c>contracts/ipc-v1.schema.json</c>.
/// The six buckets are never merged (see docs/statistics-definitions.md §9).
/// </summary>
public enum RunResult
{
    Completed,
    LeftOrAbandoned,
    CancelledBeforeEntry,
    Disconnected,
    Interrupted,
    Unknown,
}

/// <summary>Where a run came from. Mirrors <c>mentor_runs.source</c>.</summary>
public enum RunSource
{
    AutoNetwork,
    Manual,
    Import,
}

/// <summary>
/// State machine states, exactly as specified in docs/state-machine.md §1.
/// <see cref="InterruptedPendingReview"/> is reached only by crash recovery.
/// </summary>
public enum RunState
{
    Idle,
    MentorMatched,
    EnteredDuty,
    Completed,
    CancelledBeforeEntry,
    LeftOrAbandoned,
    Disconnected,
    Interrupted,
    UnknownFinalState,
    InterruptedPendingReview,
}

/// <summary>
/// How the job of a run was determined. Not persisted as a column; it is derived
/// for display and diagnostics from <c>job_id</c> and <c>source</c>.
/// </summary>
public enum JobDetection
{
    Network,
    Manual,
    Unknown,
}

/// <summary>
/// Confidence of an automatic determination. Mirrors <c>mentor_runs.detection_confidence</c>.
/// Manually created runs use <see cref="None"/> (docs/manual-correction.md §6).
/// </summary>
public enum DetectionConfidence
{
    High,
    Medium,
    Low,
    None,
}

/// <summary>Service region. Mirrors <c>mentor_runs.region</c>.</summary>
public enum Region
{
    Cn,
    Global,
    Unknown,
}

/// <summary>Role bucket derived from <c>job_id</c>. Mirrors <c>mentor_runs.role</c>.</summary>
public enum Role
{
    Tank,
    Healer,
    Dps,
    Unknown,
}

/// <summary>How a run's duty identity was established. Mirrors <c>mentor_runs.duty_source</c>.</summary>
public enum DutySource
{
    /// <summary>The duty came from a <c>content_id</c> the profile read off the wire.</summary>
    ContentId,

    /// <summary>
    /// The duty was inferred from an observed territory through the local reference file.
    /// A display mapping, not protocol evidence: it must never raise detection confidence.
    /// </summary>
    Territory,

    /// <summary>A human typed or corrected the duty.</summary>
    Manual,
}

/// <summary>Kind of an append-only revision row. Mirrors <c>run_revisions.change_kind</c>.</summary>
public enum ChangeKind
{
    CreateAuto,
    CreateManual,
    Correct,
    SoftDelete,
    Restore,
    Import,
}

/// <summary>Who caused a revision. Mirrors <c>run_revisions.actor</c>.</summary>
public enum RevisionActor
{
    User,
    System,
}

/// <summary>
/// Protocol profile status. Mirrors <c>$defs/ProfileStatus</c>.
/// Only <see cref="Verified"/> permits automatic recording; everything else is fail-closed.
/// </summary>
public enum ProfileStatus
{
    None,
    Unverified,
    Verified,
    UnsupportedBuild,
}

/// <summary>Capture pipeline state. Mirrors <c>$defs/CaptureState</c>.</summary>
public enum CaptureState
{
    Stopped,
    Starting,
    Running,
    Degraded,
    Failed,
}

/// <summary>Reason a capture session ended. Mirrors <c>capture_sessions.end_reason</c>.</summary>
public enum CaptureEndReason
{
    UserStop,
    ProcessExit,
    Error,
    Unknown,
}
