using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>The production scheduler is driven only by declared monotonic milliseconds.</summary>
public sealed class PathRecoveryTests
{
    [Fact]
    public void NotificationsWaitTwoSecondsAndInspectionClearsTheBurst()
    {
        var policy = NewPolicy();
        Assert.False(policy.ShouldInspect(100, 1));
        Assert.False(policy.ShouldInspect(2_099, 1));
        Assert.True(policy.ShouldInspect(2_100, 1));
        policy.Inspected(2_100);
        Assert.False(policy.ShouldInspect(2_101, 1));
        Assert.False(policy.ShouldInspect(17_099, 1));
        Assert.True(policy.ShouldInspect(17_100, 1));
    }

    [Fact]
    public void NotificationStormCannotPostponeInspectionPastFiveSeconds()
    {
        var policy = NewPolicy();
        for (var second = 1; second <= 5; second++)
            Assert.False(policy.ShouldInspect(second * 1_000, second));
        Assert.True(policy.ShouldInspect(6_000, 6));
    }

    [Fact]
    public void MissingNotificationsStillInspectEveryFifteenSeconds()
    {
        var policy = NewPolicy();
        Assert.False(policy.ShouldInspect(14_999, 0));
        Assert.True(policy.ShouldInspect(15_000, 0));
        policy.Inspected(15_000);
        Assert.False(policy.ShouldInspect(29_999, 0));
        Assert.True(policy.ShouldInspect(30_000, 0));
    }

    [Fact]
    public void ExactlyThreeAttemptsWaitFifteenThirtyAndSixtySecondsBeforeFailure()
    {
        var policy = NewPolicy();
        policy.Begin(0);
        Assert.True(policy.AttemptDue(0));
        policy.Attempted(0);
        Assert.Equal(new(PathRecoveryState.Recovering, 1, 15_000), policy.Snapshot(0));
        Assert.False(policy.AttemptDue(14_999));
        Assert.True(policy.AttemptDue(15_000));
        policy.Attempted(15_000);
        Assert.Equal(new(PathRecoveryState.Recovering, 2, 30_000), policy.Snapshot(15_000));
        Assert.False(policy.AttemptDue(44_999));
        policy.Attempted(45_000);
        Assert.Equal(new(PathRecoveryState.Recovering, 3, 60_000), policy.Snapshot(45_000));
        policy.Expire(104_999);
        Assert.Equal(PathRecoveryState.Recovering, policy.State);
        Assert.False(policy.AttemptDue(105_000));
        policy.Expire(105_000);
        Assert.Equal(new(PathRecoveryState.Failed, 3, 0), policy.Snapshot(105_000));
        policy.Attempted(500_000);
        Assert.Equal(3, policy.Attempts);
    }

    [Fact]
    public void NotificationsAndOrdinaryBeginCannotRenewAnExhaustedBudget()
    {
        var policy = FailedPolicy();
        policy.Begin(105_001);
        for (var revision = 1; revision < 100; revision++)
        {
            policy.ShouldInspect(105_001 + revision, revision);
            policy.Begin(105_001 + revision);
        }
        Assert.Equal(PathRecoveryState.Failed, policy.State);
        Assert.Equal(3, policy.Attempts);
        Assert.False(policy.AttemptDue(1_000_000));
    }

    [Fact]
    public void GenuineNewPathCanRenewFailureButNotTheGlobalReplacementThrottle()
    {
        var policy = FailedPolicy();
        policy.Replaced(105_000);
        policy.Begin(105_100, newPath: true);
        Assert.Equal(PathRecoveryState.Recovering, policy.State);
        Assert.Equal(0, policy.Attempts);
        Assert.False(policy.CanReplace(134_999));
        policy.WaitForReplacement();
        Assert.False(policy.AttemptDue(134_999));
        Assert.True(policy.AttemptDue(135_000));
        Assert.True(policy.CanReplace(135_000));
    }

    [Fact]
    public void ResetAndDecodedSuccessRetainReplacementSeparation()
    {
        var policy = NewPolicy();
        policy.Begin(0);
        policy.Attempted(0);
        policy.Replaced(0);
        policy.Succeeded(1_000, 4);
        Assert.Equal(PathRecoverySnapshot.None, policy.Snapshot(1_000));
        Assert.False(policy.CanReplace(29_999));
        policy.Reset(2_000, 5);
        Assert.False(policy.CanReplace(29_999));
        Assert.True(policy.CanReplace(30_000));
        Assert.False(policy.ShouldInspect(2_001, 5));
    }

    [Fact]
    public void RepeatedPrematureAttemptsCannotConsumeTheBudgetOrMoveTheDeadline()
    {
        var policy = NewPolicy();
        policy.Begin(0);
        policy.Attempted(0);
        policy.Begin(1_000);
        policy.Attempted(1_000);
        Assert.Equal(new(PathRecoveryState.Recovering, 1, 14_000), policy.Snapshot(1_000));
    }

    [Fact]
    public void AnEpisodeWithoutALiveReaderStillHasATwoMinuteCeiling()
    {
        var policy = NewPolicy();
        policy.Begin(500);
        policy.Attempted(500);
        policy.Expire(120_499);
        Assert.Equal(PathRecoveryState.Recovering, policy.State);
        policy.Expire(120_500);
        Assert.Equal(PathRecoveryState.Failed, policy.State);
        Assert.False(policy.AttemptDue(120_500));
    }

    private static PathRecovery NewPolicy()
    {
        var policy = new PathRecovery();
        policy.Reset(0, 0);
        return policy;
    }

    private static PathRecovery FailedPolicy()
    {
        var policy = NewPolicy();
        policy.Begin(0);
        policy.Attempted(0);
        policy.Attempted(15_000);
        policy.Attempted(45_000);
        policy.Expire(105_000);
        return policy;
    }
}
