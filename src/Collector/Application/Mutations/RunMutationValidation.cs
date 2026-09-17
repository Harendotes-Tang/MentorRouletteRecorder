using MentorRecorder.Collector.Contracts.Errors;
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
                "该操作必须填写 1 到 500 字的修改理由。",
                field: "reason"),
            RunRuleViolationKind.MatchedAfterEntered => new CollectorException(
                ErrorCodes.TimeOrder,
                "匹配时间不能晚于进入副本的时间。",
                details,
                field: "matched_at_utc"),
            RunRuleViolationKind.EnteredAfterEnded => new CollectorException(
                ErrorCodes.TimeOrder,
                "进入副本的时间不能晚于结束时间。",
                details,
                field: "entered_at_utc"),
            RunRuleViolationKind.EntryRequired => new CollectorException(
                ErrorCodes.BadRequest,
                "只有「未进入副本即取消」可以没有进入时间，其余结果都必须填写进入时间。",
                details,
                field: "entered_at_utc"),
            RunRuleViolationKind.EndRequired => new CollectorException(
                ErrorCodes.BadRequest,
                "判定为「已完成」的记录必须填写结束时间。",
                details,
                field: "ended_at_utc"),
            RunRuleViolationKind.NegativeDuration => new CollectorException(
                ErrorCodes.NegativeDuration,
                "时长不能为负数。",
                details,
                field: "duration_ms"),
            RunRuleViolationKind.NoChanges => new CollectorException(
                ErrorCodes.NoChanges,
                "未做任何修改，不会写入修订记录。",
                field: "changes"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, null),
        };
    }
}
