namespace MentorRecorder.Collector.Domain;

/// <summary>将已确定的记录结果投影为普通终态；不决定恢复流程是否仍需人工确认。</summary>
internal static class RunStateProjection
{
    /// <summary>
    /// 返回结果对应的终态。Unknown 和未声明的枚举值均回退 UnknownFinalState；
    /// InterruptedPendingReview 由恢复流程根据待复核事实决定，不属于结果映射。
    /// </summary>
    internal static RunState TerminalFromResult(RunResult result) => result switch
    {
        RunResult.Completed => RunState.Completed,
        RunResult.LeftOrAbandoned => RunState.LeftOrAbandoned,
        RunResult.CancelledBeforeEntry => RunState.CancelledBeforeEntry,
        RunResult.Disconnected => RunState.Disconnected,
        RunResult.Interrupted => RunState.Interrupted,
        _ => RunState.UnknownFinalState,
    };
}
