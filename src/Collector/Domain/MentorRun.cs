using System.Text.Json.Serialization;

namespace MentorRecorder.Collector.Domain;

/// <summary>
/// One mentor roulette attempt. Immutable: every change produces a new instance
/// plus an append-only <see cref="RunRevision"/> row.
///
/// Column-for-column mirror of <c>mentor_runs</c> (docs/data-model.md section 1) and of
/// <c>$defs/Run</c> in the IPC contract.
/// </summary>
public sealed record MentorRun
{
    /// <summary>UUID primary key.</summary>
    public required string RunId { get; init; }

    /// <summary>Optimistic concurrency version; starts at 1 and increases by one per change.</summary>
    public required int Revision { get; init; }

    /// <summary>Capture session that produced this run; null for manual entries.</summary>
    public string? CaptureSessionId { get; init; }

    /// <summary>Service region.</summary>
    public Region Region { get; init; } = Region.Unknown;

    /// <summary>Client build identifier the run was observed on.</summary>
    public string? GameBuild { get; init; }

    /// <summary>Protocol profile used to produce this run; null for manual entries.</summary>
    public string? ProtocolProfileId { get; init; }

    /// <summary>Roulette id that made this a mentor roulette; null for manual entries.</summary>
    public int? MentorRouletteId { get; init; }

    /// <summary>Duty content id. Statistics aggregate on this column, never on the name.</summary>
    public int? ContentId { get; init; }

    /// <summary>Territory (map) id.</summary>
    public int? TerritoryId { get; init; }

    /// <summary>Localized duty name, display only.</summary>
    public string? DutyName { get; init; }

    /// <summary>Duty category, display and filtering only.</summary>
    public string? DutyCategory { get; init; }

    /// <summary>
    /// Where the duty identity came from, or null when the run has none yet.
    ///
    /// Provenance, not a business field: statistics key on
    /// <c>content_id ?? territory_id</c>, and this column is what lets a maintainer tell an
    /// observed content id from one inferred through the local territory mapping
    /// (docs/data-model.md, review finding M-5). It is deliberately not on the IPC wire and
    /// carries no revision of its own.
    /// </summary>
    [JsonIgnore]
    public DutySource? DutySource { get; init; }

    /// <summary>Job id; null means unknown and forms its own statistics bucket.</summary>
    public int? JobId { get; init; }

    /// <summary>Localized job name; the unknown marker when <see cref="JobId"/> is null.</summary>
    public string? JobName { get; init; }

    /// <summary>Role derived from <see cref="JobId"/>.</summary>
    public Role Role { get; init; } = Role.Unknown;

    /// <summary>When the duty finder popped.</summary>
    public DateTimeOffset? MatchedAtUtc { get; init; }

    /// <summary>When the duty was entered. Non-null is the criterion for counting an attempt.</summary>
    public DateTimeOffset? EnteredAtUtc { get; init; }

    /// <summary>When the run ended.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>
    /// Duration in milliseconds. For live-observed runs it is measured with a monotonic
    /// clock; for manual and imported runs it may be derived from the two timestamps
    /// (docs/manual-correction.md section 6, the only exception).
    /// </summary>
    public long? DurationMs { get; init; }

    /// <summary>Outcome bucket.</summary>
    public required RunResult Result { get; init; }

    /// <summary>Confidence of the automatic determination; None for manual runs.</summary>
    public DetectionConfidence DetectionConfidence { get; init; } = DetectionConfidence.None;

    /// <summary>Origin of the record.</summary>
    public required RunSource Source { get; init; }

    /// <summary>Whether this run counts towards the achievement goal.</summary>
    public bool ContributesToGoal { get; init; } = true;

    /// <summary>True when a human created the record.</summary>
    public bool ManuallyCreated { get; init; }

    /// <summary>True once a human has corrected the record; never reverts to false.</summary>
    public bool ManuallyCorrected { get; init; }

    /// <summary>Soft delete flag. There is no hard delete anywhere in the code base.</summary>
    public bool SoftDeleted { get; init; }

    /// <summary>
    /// True when crash recovery closed this run as INTERRUPTED and a human has not looked at
    /// it yet (docs/state-machine.md section 3.9). The Collector never clears it by itself.
    /// </summary>
    public bool PendingReview { get; init; }

    /// <summary>Free-text note typed by the user through a correction; never derived from traffic.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// 导随心得 attached to this run, or null when it has none.
    ///
    /// Not a <c>mentor_runs</c> column: it lives in <c>run_reflections</c> and is hydrated by
    /// <c>RunRepository</c> on every read, so that every serialised Run carries it. Writing
    /// one does not bump <see cref="Revision"/>.
    /// </summary>
    public RunReflection? Reflection { get; init; }

    /// <summary>Creation time.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Time of the most recent change.</summary>
    public required DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>How the job was determined. Derived, not stored.</summary>
    [JsonIgnore]
    public JobDetection JobDetection =>
        JobId is null ? JobDetection.Unknown
        : Source == RunSource.AutoNetwork ? JobDetection.Network
        : JobDetection.Manual;

    /// <summary>
    /// True when this run is a confirmed mentor roulette and therefore eligible for
    /// statistics (docs/statistics-definitions.md section 1).
    /// </summary>
    [JsonIgnore]
    public bool IsConfirmedMentor => Source switch
    {
        RunSource.AutoNetwork => MentorRouletteId is not null,
        RunSource.Manual => true,
        RunSource.Import => MentorRouletteId is not null,
        _ => false,
    };
}
