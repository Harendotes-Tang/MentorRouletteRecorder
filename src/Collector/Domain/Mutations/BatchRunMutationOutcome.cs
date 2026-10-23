namespace MentorRecorder.Collector.Domain.Mutations;

/// <summary>Committed result of an atomic history batch, independent of its stored receipt and IPC shape.</summary>
/// <param name="ChangedCount">Number of targets changed by the original execution, retained on replay.</param>
/// <param name="RunIds">Affected run identifiers in ordinal order, including identifiers removed by a purge.</param>
/// <param name="Action">Applied action: <c>soft_delete</c>, <c>restore</c> or <c>purge</c>.</param>
/// <param name="IdempotentReplay">True when the original result was read without applying the batch again.</param>
public sealed record BatchRunMutationOutcome(
    int ChangedCount,
    IReadOnlyList<string> RunIds,
    string Action,
    bool IdempotentReplay);
