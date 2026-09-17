namespace MentorRecorder.Collector.Domain.Mutations;

/// <summary>Stable business reasons for rejecting a run mutation.</summary>
public enum RunRuleViolationKind
{
    ReasonRequired,
    MatchedAfterEntered,
    EnteredAfterEnded,
    EntryRequired,
    EndRequired,
    NegativeDuration,
    NoChanges,
}

/// <summary>Domain property associated with a run-rule violation.</summary>
public enum RunRuleProperty
{
    Reason,
    MatchedAtUtc,
    EnteredAtUtc,
    EndedAtUtc,
    DurationMs,
    Changes,
}

/// <summary>
/// A typed domain refusal. It carries domain context only; transport codes, wire field names
/// and user-facing messages are supplied by the application boundary.
/// </summary>
public sealed class RunRuleViolationException : Exception
{
    public RunRuleViolationException(
        RunRuleViolationKind kind,
        RunRuleProperty property,
        string? runId = null,
        RunResult? runResult = null)
        : base("Run mutation validation failed.")
    {
        Kind = kind;
        Property = property;
        RunId = runId;
        RunResult = runResult;
    }

    public RunRuleViolationKind Kind { get; }

    public RunRuleProperty Property { get; }

    public string? RunId { get; }

    public RunResult? RunResult { get; }
}
