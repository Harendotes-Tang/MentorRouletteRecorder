using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// How a mutation moves the numbers: soft delete removes a run from every statistic,
/// restore brings it back, and a baseline change recomputes the achievement progress.
/// </summary>
public sealed class StatisticsAfterMutationTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;
    private readonly StatisticsRepository _statistics;
    private readonly RunMutationService _mutations;

    public StatisticsAfterMutationTests()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        _settings.EnsureDefaults();
        _statistics = new StatisticsRepository(_database.Database, _settings);
        _mutations = new RunMutationService(_database.Database, _settings, _database.Clock);
    }

    private RunMutationOutcome Create(RunResult result, bool contributes = true) =>
        _mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Reason = "补录一次导随",
            Result = result,
            ContentId = 900001,
            JobId = 19,
            ContributesToGoal = contributes,
            MatchedAtUtc = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero),
            EnteredAtUtc = result == RunResult.CancelledBeforeEntry
                ? null
                : new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero),
            EndedAtUtc = result == RunResult.CancelledBeforeEntry
                ? null
                : new DateTimeOffset(2026, 9, 3, 10, 31, 0, TimeSpan.Zero),
        });

    private RunMutationOutcome CreateAt(RunResult result, DateTimeOffset endedAt) =>
        _mutations.CreateManualRun(new CreateManualRunCommand
        {
            RequestId = Guid.NewGuid().ToString("D"),
            Reason = "补录一次导随",
            Result = result,
            ContentId = 900001,
            JobId = 19,
            ContributesToGoal = true,
            MatchedAtUtc = endedAt.AddMinutes(-31),
            EnteredAtUtc = endedAt.AddMinutes(-30),
            EndedAtUtc = endedAt,
        });

    [Fact]
    public void SoftDeletedRun_LeavesEveryStatistic()
    {
        Create(RunResult.Completed);
        var removed = Create(RunResult.Completed);

        Assert.Equal(2, _statistics.GetDashboard().CompletedCount);

        _mutations.SoftDeleteRun(new RunReasonCommand(
            Guid.NewGuid().ToString("D"), removed.RunId, 1, "这条其实没打"));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(1, dashboard.AttemptCount);
        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.AchievementProgress);
        Assert.Equal(1.0, dashboard.CompletionRate);

        // The per-duty and per-job tables use the same candidate set, so they drop it too.
        Assert.Equal(1, Assert.Single(_statistics.GetDungeonStats()).AttemptCount);
        Assert.Equal(1, Assert.Single(_statistics.GetJobStats()).AttemptCount);
    }

    [Fact]
    public void RestoredRun_ReturnsToEveryStatistic()
    {
        var run = Create(RunResult.Completed);
        _mutations.SoftDeleteRun(new RunReasonCommand(Guid.NewGuid().ToString("D"), run.RunId, 1, "删掉"));
        Assert.Equal(0, _statistics.GetDashboard().CompletedCount);

        _mutations.RestoreRun(new RunReasonCommand(Guid.NewGuid().ToString("D"), run.RunId, 2, "误删，恢复"));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(1, dashboard.AttemptCount);
        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.AchievementProgress);
    }

    [Fact]
    public void BaselineChange_RecomputesProgressAndRemaining()
    {
        Create(RunResult.Completed);
        Create(RunResult.Completed);
        Create(RunResult.Completed);

        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"),
            2000,
            1500,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "开始使用本软件前已完成 1500 次"));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(1500, dashboard.BaselineCompletedCount);
        Assert.Equal(1503, dashboard.AchievementProgress);
        Assert.Equal(497, dashboard.Remaining);
    }

    /// <summary>
    /// Audit 2026-10-03 OG-3. The baseline is the in-game total at its effective time, so it
    /// already includes every completion that ended before then; adding those again counted them
    /// twice (the baseline dialog promises 「之前的自动记录不会重复计入」). Completions that end
    /// later are added on top.
    /// </summary>
    [Fact]
    public void CompletionsThatEndedBeforeTheBaselineTookEffectAreNotCountedTwice()
    {
        Create(RunResult.Completed);
        Create(RunResult.Completed);
        Create(RunResult.Completed);

        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"),
            2000,
            1500,
            new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            "游戏内成就面板显示 1500 次"));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(1500, dashboard.AchievementProgress);
        Assert.Equal(500, dashboard.Remaining);
        Assert.Equal(3, dashboard.CompletedCount);

        CreateAt(RunResult.Completed, new DateTimeOffset(2026, 9, 3, 13, 0, 0, TimeSpan.Zero));
        Assert.Equal(1501, _statistics.GetDashboard().AchievementProgress);
    }

    /// <summary>
    /// The moment that decides is when the duty ended: a run that started before the baseline
    /// and finished after it was not yet in the in-game total.
    /// </summary>
    [Fact]
    public void ACompletionThatEndedAfterTheBaselineTookEffectCounts()
    {
        var effective = new DateTimeOffset(2026, 9, 3, 10, 15, 0, TimeSpan.Zero);
        Create(RunResult.Completed);

        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"), 2000, 1500, effective, "副本进行中填写的基数"));

        Assert.Equal(1501, _statistics.GetDashboard().AchievementProgress);
    }

    /// <summary>
    /// A row that lacks its end time is placed by the latest time it does carry - entry, then
    /// match. That time is never later than the real completion, so a doubtful row is left out
    /// rather than counted twice.
    /// </summary>
    [Fact]
    public void ACompletionWithoutAnEndTimeIsPlacedByItsEntry()
    {
        var effective = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);
        var runs = new RunRepository(_database.Database);
        _database.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(enteredAt: effective.AddHours(-1)) with { EndedAtUtc = null }, tx);
            runs.Insert(TestDatabase.Run(enteredAt: effective.AddHours(1)) with { EndedAtUtc = null }, tx);
        });

        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"), 2000, 1500, effective, "基数"));

        Assert.Equal(1501, _statistics.GetDashboard().AchievementProgress);
    }

    /// <summary>
    /// A baseline of 0 holds no completion, so there is nothing a recorded run could be counted
    /// twice against: every contributing completion counts, including those entered by hand for
    /// days before the software was installed. This is also the state of a database whose user
    /// never filled in the baseline, whose effective time is merely the first start.
    /// </summary>
    [Fact]
    public void AZeroBaselineCountsEveryContributingCompletion()
    {
        Create(RunResult.Completed);
        Create(RunResult.Completed);

        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"), 2000, 0,
            new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), "从 0 开始"));

        Assert.Equal(2, _statistics.GetDashboard().AchievementProgress);
    }

    /// <summary>
    /// Audit 2026-10-03 CS-7. The Desktop sends the save time as the effective time with every
    /// save of the achievement settings, a goal-only edit included. A baseline that did not change
    /// keeps the moment it took effect: moved, every completion recorded since would fall before the
    /// new cutoff and drop out of the progress. Typing the same number in again is no change either.
    /// </summary>
    [Theory]
    [InlineData(2500)]
    [InlineData(2000)]
    public void AnUnchangedBaselineKeepsItsEffectiveTimeAndTheProgress(int goal)
    {
        var effective = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        SetBaseline(2000, 1500, effective);
        CreateAt(RunResult.Completed, effective.AddHours(1));
        Assert.Equal(1501, _statistics.GetDashboard().AchievementProgress);

        var outcome = SetBaseline(goal, 1500, effective.AddDays(2));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(goal, dashboard.GoalCount);
        Assert.Equal(1501, dashboard.AchievementProgress);
        Assert.Equal(goal - 1501, dashboard.Remaining);
        Assert.Equal(effective, outcome.Settings.BaselineEffectiveAt);
        Assert.Equal(effective, _settings.GetAchievementSettings().BaselineEffectiveAt);
    }

    /// <summary>
    /// A new baseline is the in-game total at the moment it is sent with: completions that ended
    /// before then are inside that number and are no longer added, later ones are.
    /// </summary>
    [Fact]
    public void AChangedBaselineMovesTheEffectiveTime()
    {
        var first = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(2);
        SetBaseline(2000, 1500, first);
        CreateAt(RunResult.Completed, first.AddHours(1));

        var outcome = SetBaseline(2000, 1510, second);

        Assert.Equal(second, outcome.Settings.BaselineEffectiveAt);
        Assert.Equal(second, _settings.GetAchievementSettings().BaselineEffectiveAt);
        Assert.Equal(1510, _statistics.GetDashboard().AchievementProgress);

        CreateAt(RunResult.Completed, second.AddHours(1));
        Assert.Equal(1511, _statistics.GetDashboard().AchievementProgress);
    }

    /// <summary>
    /// A baseline of 0 counts every completion whatever its effective time, and an unchanged 0
    /// keeps that time like any other unchanged baseline. The first positive baseline takes the
    /// time it is sent with.
    /// </summary>
    [Fact]
    public void AZeroBaselineKeepsItsTimeUntilAPositiveBaselineSetsOne()
    {
        var installed = _settings.GetAchievementSettings().BaselineEffectiveAt;
        Create(RunResult.Completed);

        var goalOnly = SetBaseline(2500, 0, new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(installed, goalOnly.Settings.BaselineEffectiveAt);
        Assert.Equal(1, _statistics.GetDashboard().AchievementProgress);

        var effective = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var outcome = SetBaseline(2500, 100, effective);

        Assert.Equal(effective, outcome.Settings.BaselineEffectiveAt);
        Assert.Equal(100, _statistics.GetDashboard().AchievementProgress);
    }

    private BaselineMutationOutcome SetBaseline(int goal, int baseline, DateTimeOffset effectiveAt) =>
        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"), goal, baseline, effectiveAt, "保存成就设置"));

    [Fact]
    public void BaselineAboveTheGoal_NeverProducesANegativeRemaining()
    {
        _mutations.UpdateAchievementBaseline(new UpdateAchievementBaselineCommand(
            Guid.NewGuid().ToString("D"), 100, 250, DateTimeOffset.UnixEpoch, "目标已超额完成"));

        Assert.Equal(0, _statistics.GetDashboard().Remaining);
    }

    [Fact]
    public void CompletedRunExcludedFromTheGoal_StillCountsAsACompletion()
    {
        Create(RunResult.Completed, contributes: false);

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.AttemptCount);
        Assert.Equal(0, dashboard.AchievementProgress);
    }

    [Fact]
    public void CancelledBeforeEntry_IsNeverAnAttemptAndItsShareIsNull()
    {
        Create(RunResult.CancelledBeforeEntry);

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(0, dashboard.AttemptCount);
        Assert.Null(dashboard.CompletionRate);
        Assert.Null(dashboard.LeaveRate);

        var bucket = dashboard.ResultBreakdown.Buckets
            .Single(row => row.Result == RunResult.CancelledBeforeEntry);
        Assert.Equal(1, bucket.Count);
        Assert.Null(bucket.Share);
    }

    [Fact]
    public void DisconnectedAndInterrupted_AreNeverFoldedIntoTheLeaveRate()
    {
        Create(RunResult.Completed);
        Create(RunResult.Completed);
        Create(RunResult.Completed);
        Create(RunResult.LeftOrAbandoned);
        Create(RunResult.Disconnected);

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(5, dashboard.AttemptCount);
        Assert.Equal(0.6, dashboard.CompletionRate);
        Assert.Equal(0.2, dashboard.LeaveRate);

        var disconnected = dashboard.ResultBreakdown.Buckets
            .Single(row => row.Result == RunResult.Disconnected);
        Assert.Equal(1, disconnected.Count);
        Assert.Equal(0.2, disconnected.Share);
    }

    [Fact]
    public void CorrectingAPendingReviewRun_ClearsItFromTheReviewQueue()
    {
        var run = Create(RunResult.Interrupted);
        MarkPendingReview(run.RunId);
        Assert.Equal(1, _statistics.GetDashboard().UnfinishedPendingReview);

        _mutations.CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"),
            run.RunId,
            1,
            "其实打完了",
            new RunChangeSet
            {
                Specified = new HashSet<string>(StringComparer.Ordinal) { RunFields.Result },
                Result = RunResult.Completed,
            }));

        var dashboard = _statistics.GetDashboard();
        Assert.Equal(0, dashboard.UnfinishedPendingReview);
        Assert.Equal(1, dashboard.CompletedCount);
    }

    private void MarkPendingReview(string runId) =>
        _database.Database.RunInTransaction(tx =>
        {
            using var command = _database.Database.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                "UPDATE mentor_runs SET pending_review = 1, detection_confidence = 'LOW' " +
                "WHERE run_id = $id;";
            command.Parameters.AddWithValue("$id", runId);
            command.ExecuteNonQuery();
        });

    public void Dispose() => _database.Dispose();
}
