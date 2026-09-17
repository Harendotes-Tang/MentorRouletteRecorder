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
