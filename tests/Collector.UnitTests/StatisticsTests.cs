using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class StatisticsTests
{
    [Fact]
    public void Dashboard_ReturnsNormativeEmptyValues()
    {
        using var fixture = new TestDatabase();
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var statistics = new StatisticsRepository(fixture.Database, settings);

        var result = statistics.GetDashboard();

        Assert.Equal(0, result.AttemptCount);
        Assert.Equal(0, result.CompletedCount);
        Assert.Null(result.CompletionRate);
        Assert.Null(result.LeaveRate);
        Assert.Null(result.AverageDurationMs);
        Assert.Equal(2000, result.Remaining);
        Assert.Equal(Enum.GetValues<RunResult>().Length, result.ResultBreakdown.Buckets.Count);
    }

    /// <summary>
    /// The player looked at the dashboard in the middle of a duty: 导随总次数 6, 通关率 83.3%,
    /// 未知结果 1. The run in flight is stored as UNKNOWN with no end time, and was counted as an
    /// attempt whose outcome is unknown - so every duty lowered the completion rate for as long
    /// as it lasted. A run that has not ended has no outcome yet and belongs to no statistic.
    /// </summary>
    [Fact]
    public void Dashboard_LeavesOutTheRunThatIsStillInProgress()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(result: RunResult.Completed, source: RunSource.AutoNetwork), tx);
            // Finished, outcome unknown until the player confirms it: this one DOES count.
            runs.Insert(TestDatabase.Run(result: RunResult.Unknown, source: RunSource.AutoNetwork), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Unknown, source: RunSource.AutoNetwork) with
            {
                EndedAtUtc = null,
                DurationMs = null,
            }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var result = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(0.5, result.CompletionRate);
        Assert.Equal(1, result.ResultBreakdown.Buckets.Single(b => b.Result == RunResult.Unknown).Count);
    }

    [Fact]
    public void Dashboard_KeepsCompletionLeaveAndDisconnectSeparate()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(result: RunResult.Completed), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.LeftOrAbandoned), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Disconnected), tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var result = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(5, result.AttemptCount);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(0.6, result.CompletionRate);
        Assert.Equal(0.2, result.LeaveRate);
        Assert.Equal(1, result.ResultBreakdown.Buckets.Single(b => b.Result == RunResult.Disconnected).Count);
    }

    [Fact]
    public void Dashboard_ExcludesCancelledDeletedAndNonContributingFromTheirSpecificMetrics()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(result: RunResult.CancelledBeforeEntry), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed, contributesToGoal: false), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed, softDeleted: true), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed, durationMs: null), tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var result = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, result.CompletedCount);
        Assert.Equal(1, result.AchievementProgress);
        Assert.Equal(120_000, result.AverageDurationMs);
        var cancelled = result.ResultBreakdown.Buckets.Single(b => b.Result == RunResult.CancelledBeforeEntry);
        Assert.Equal(1, cancelled.Count);
        Assert.Null(cancelled.Share);
    }

    [Fact]
    public void GroupedStats_KeepUnknownJobAndUseDutyFallback()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            for (var i = 0; i < 4; i++)
            {
                runs.Insert(TestDatabase.Run(jobId: null, contentId: 900001) with
                {
                    DutyName = null,
                    DutyCategory = null,
                }, tx);
            }
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        var statistics = new StatisticsRepository(
            fixture.Database, settings, JobCatalog.Default, DutyCatalog.Default);

        var jobs = statistics.GetJobStats();
        var duties = statistics.GetDungeonStats();

        var unknown = Assert.Single(jobs);
        Assert.Null(unknown.JobId);
        Assert.Equal("未知", unknown.JobName);
        Assert.Equal(Role.Unknown, unknown.Role);
        Assert.Equal(4, unknown.AttemptCount);
        Assert.Equal("样例迷宫挑战 A", Assert.Single(duties).DutyName);
    }

    /// <summary>
    /// Review finding M-5. The capture does not back-infer a content id from a territory, so
    /// grouping must key on the territory as well: keying on <c>content_id</c> alone folds
    /// every territory-identified run into one nameless "unknown duty" bucket.
    /// </summary>
    [Fact]
    public void DungeonStats_GroupByTerritoryWhenNoContentIdWasObserved()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            // Two runs in the same duty, identified by territory only.
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = "天然要害沙斯塔夏溶洞",
                DutySource = DutySource.Territory,
            }, tx);
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = "天然要害沙斯塔夏溶洞",
                DutySource = DutySource.Territory,
            }, tx);

            // A different territory is a different bucket.
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 792, DutyName = "虚景跳跳乐大挑战",
                DutySource = DutySource.Territory,
            }, tx);

            // A run with an observed content id keeps its own.
            runs.Insert(TestDatabase.Run(contentId: 900001), tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var duties = new StatisticsRepository(fixture.Database, settings).GetDungeonStats();

        Assert.Equal(3, duties.Count);
        var byTerritory = Assert.Single(duties, row => row.DutyName == "天然要害沙斯塔夏溶洞");
        Assert.Equal(2, byTerritory.AttemptCount);

        // The wire content_id of a territory-identified group stays null: a territory id is
        // not a content id and must never be presented as one.
        Assert.Null(byTerritory.ContentId);
        Assert.Equal(1, Assert.Single(duties, row => row.DutyName == "虚景跳跳乐大挑战").AttemptCount);
        Assert.Equal(900001, Assert.Single(duties, row => row.ContentId == 900001).ContentId);
    }

    /// <summary>A territory the reference file knows still supplies the name and category.</summary>
    [Fact]
    public void DungeonStats_NamesATerritoryOnlyGroupFromTheReferenceFile()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => runs.Insert(
            TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = null, DutyCategory = null,
                DutySource = DutySource.Territory,
            }, tx));
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var row = Assert.Single(new StatisticsRepository(fixture.Database, settings).GetDungeonStats());

        Assert.Null(row.ContentId);
        Assert.Equal("天然要害沙斯塔夏溶洞", row.DutyName);
    }
}
