namespace MentorRecorder.Collector.Domain;

/// <summary>One field-level difference recorded inside a revision.</summary>
/// <param name="Field">Column name that changed.</param>
/// <param name="OldValue">Value before the change.</param>
/// <param name="NewValue">Value after the change.</param>
public sealed record RunFieldChange(string Field, object? OldValue, object? NewValue);

/// <summary>
/// One append-only audit row. Rows are never updated or deleted; a database trigger
/// enforces that (docs/manual-correction.md section 2, invariant 5).
/// </summary>
public sealed record RunRevision
{
    /// <summary>UUID; also returned to clients as the audit event id.</summary>
    public required string RevisionId { get; init; }

    /// <summary>Run this revision belongs to.</summary>
    public required string RunId { get; init; }

    /// <summary>Value of mentor_runs.revision after this change.</summary>
    public required int Revision { get; init; }

    /// <summary>When the change was applied.</summary>
    public required DateTimeOffset ChangedAtUtc { get; init; }

    /// <summary>What kind of change this was.</summary>
    public required ChangeKind ChangeKind { get; init; }

    /// <summary>Who caused the change.</summary>
    public required RevisionActor Actor { get; init; }

    /// <summary>Mandatory for CORRECT, SOFT_DELETE, RESTORE and CREATE_MANUAL.</summary>
    public string? Reason { get; init; }

    /// <summary>IPC request id that produced this revision; unique, used for idempotency.</summary>
    public string? RequestId { get; init; }

    /// <summary>Field-level differences; for a create it is the full initial value set.</summary>
    public required IReadOnlyList<RunFieldChange> Changes { get; init; }
}
