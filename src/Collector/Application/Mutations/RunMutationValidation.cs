using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;

namespace MentorRecorder.Collector.Application.Mutations;

/// <summary>
/// Applies domain mutation rules and converts typed failures into the established external
/// Collector error contract.
/// </summary>
public static class RunMutationValidation
{
    public static string RequireReason(string? reason) =>
        Adapt(() => RunMutationRules.RequireReason(reason));

    public static MentorRun ValidateFinalValue(
        MentorRun run,
        bool durationExplicit,
        bool recalculateDuration = true) =>
        Adapt(() => RunMutationRules.ValidateFinalValue(run, durationExplicit, recalculateDuration));

    public static IReadOnlyList<RunFieldChange> RequireChanges(
        IReadOnlyList<RunFieldChange> changes) =>
        Adapt(() => RunMutationRules.RequireChanges(changes));

    private static T Adapt<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (RunRuleViolationException failure)
        {
            throw ToCollectorException(failure);
        }
    }

    public static CollectorException ToCollectorException(RunRuleViolationException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var details = failure.RunId is null && failure.RunResult is null
            ? null
            : new Dictionary<string, object?>
            {
                ["run_id"] = failure.RunId,
                ["result"] = failure.RunResult is { } result
                    ? EnumWire<RunResult>.Format(result)
                    : null,
            };

        return failure.Kind switch
        {
            RunRuleViolationKind.ReasonRequired => new CollectorException(
                ErrorCodes.ReasonRequired,
                CollectorTexts.MutationReasonRequired(),
                field: "reason"),
            RunRuleViolationKind.MatchedAfterEntered => new CollectorException(
                ErrorCodes.TimeOrder,
                CollectorTexts.MutationMatchedAfterEntered(),
                details,
                field: "matched_at_utc"),
            RunRuleViolationKind.EnteredAfterEnded => new CollectorException(
                ErrorCodes.TimeOrder,
                CollectorTexts.MutationEnteredAfterEnded(),
                details,
                field: "entered_at_utc"),
            RunRuleViolationKind.EntryRequired => new CollectorException(
                ErrorCodes.BadRequest,
                CollectorTexts.MutationEntryRequired(),
                details,
                field: "entered_at_utc"),
            RunRuleViolationKind.EndRequired => new CollectorException(
                ErrorCodes.BadRequest,
                CollectorTexts.MutationEndRequired(),
                details,
                field: "ended_at_utc"),
            RunRuleViolationKind.NegativeDuration => new CollectorException(
                ErrorCodes.NegativeDuration,
                CollectorTexts.MutationDurationNegative(),
                details,
                field: "duration_ms"),
            RunRuleViolationKind.NoChanges => new CollectorException(
                ErrorCodes.NoChanges,
                CollectorTexts.MutationNoChanges(),
                field: "changes"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, null),
        };
    }
}
