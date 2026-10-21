using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
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
    /// The player looked at the dashboard in the middle of a duty: 已记录次数 6, 通关率 83.3%,
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

        // Territory 1036 hosts that one duty, so the group carries the duty's content id from
        // the reference file: the same key the runs that observed it use.
        Assert.Equal(4, byTerritory.ContentId);

        // Territory 792 hosts nine stages. Which one was run is unknown, so the group is
        // keyed by the territory and its wire content_id stays null: a territory id is not
        // a content id and must never be presented as one.
        var shared = Assert.Single(duties, row => row.DutyName == "虚景跳跳乐大挑战");
        Assert.Equal(1, shared.AttemptCount);
        Assert.Null(shared.ContentId);
        Assert.Equal(900001, Assert.Single(duties, row => row.ContentId == 900001).ContentId);
    }

    /// <summary>
    /// The same duty identified two ways is one row. Since 1.4.0 an automatic run stores only
    /// the territory it saw (docs/data-model.md section 1.4); a run that did observe the
    /// content id, or was entered by hand, keyed separately, so 副本统计 listed the duty twice
    /// with one run each.
    /// </summary>
    [Fact]
    public void DungeonStats_OneRowForADutyWhetherItWasSeenByContentIdOrByTerritory()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(contentId: 4) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = "天然要害沙斯塔夏溶洞",
                DutySource = DutySource.ContentId,
            }, tx);
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = "天然要害沙斯塔夏溶洞",
                DutySource = DutySource.Territory,
            }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var statistics = new StatisticsRepository(fixture.Database, settings);

        var row = Assert.Single(statistics.GetDungeonStats());
        Assert.Equal(4, row.ContentId);
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal(2, row.CompletedCount);

        // Clicking that row filters by its content id; the territory-only run must be in
        // what comes back, in the statistics and in the listing alike.
        var filter = new RunFilter { ContentIds = new[] { 4 } };
        Assert.Equal(2, Assert.Single(statistics.GetDungeonStats(filter)).AttemptCount);
        Assert.Equal(2, statistics.GetDashboard(filter).AttemptCount);
        Assert.Equal(2, runs.Query(filter, null, 1, 50).Items.Count);

        // A stage on a shared map is not found by any single stage's content id.
        fixture.Database.RunInTransaction(tx => runs.Insert(TestDatabase.Run(contentId: null) with
        {
            Region = Region.Cn, TerritoryId = 792, DutyName = "虚景跳跳乐大挑战",
            DutySource = DutySource.Territory,
        }, tx));
        Assert.Empty(runs.Query(new RunFilter { ContentIds = new[] { 600 } }, null, 1, 50).Items);
    }

    /// <summary>
    /// Audit 2026-10-20 H1-4. A run in a zone the reference file does not know at all names no duty either:
    /// it belongs to the one 未知副本 row (statistics-definitions §10), not to a second row of the same name.
    /// </summary>
    [Fact]
    public void DungeonStats_AZoneTheReferenceFileDoesNotKnowJoinsTheOneUnknownDutyRow()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(contentId: null), tx);
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 999_999, DutySource = DutySource.Territory,
            }, tx);
            runs.Insert(TestDatabase.Run(contentId: null) with
            {
                Region = Region.Cn, TerritoryId = 1036, DutyName = "天然要害沙斯塔夏溶洞",
                DutySource = DutySource.Territory,
            }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var duties = new StatisticsRepository(fixture.Database, settings).GetDungeonStats();

        Assert.Equal(2, duties.Count);
        var unknown = Assert.Single(duties, row => row.ContentId is null);
        Assert.Equal(DutyCatalog.UnknownDutyName, unknown.DutyName);
        Assert.Equal(2, unknown.AttemptCount);
        Assert.Equal(4, Assert.Single(duties, row => row.ContentId is not null).ContentId);
    }

    /// <summary>
    /// Re-review 2026-10-20, RV2-4. An imported row whose duty name matched no catalogue entry has no duty identity
    /// either, so it shares the 未知副本 row with a run recorded in a zone the reference file does not know - and that
    /// row is named 未知副本 (statistics-definitions §10), never after the free text typed into one imported row.
    /// </summary>
    [Fact]
    public void DungeonStats_TheUnknownDutyRowIsNeverNamedAfterAnImportedFreeTextName()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(contentId: null, source: RunSource.AutoNetwork) with
            {
                Region = Region.Cn, TerritoryId = 999_999, DutySource = DutySource.Territory,
            }, tx);
            runs.Insert(TestDatabase.Run(contentId: null, source: RunSource.Import) with
            {
                Region = Region.Cn, DutyName = "神兵", DutyCategory = "迷宫挑战",
                UpdatedAtUtc = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero),
            }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var duties = new StatisticsRepository(fixture.Database, settings).GetDungeonStats();

        var unknown = Assert.Single(duties);
        Assert.Null(unknown.ContentId);
        Assert.Equal(DutyCatalog.UnknownDutyName, unknown.DutyName);
        Assert.Null(unknown.DutyCategory);
        Assert.Equal(2, unknown.AttemptCount);
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

        Assert.Equal(4, row.ContentId);
        Assert.Equal("天然要害沙斯塔夏溶洞", row.DutyName);
        Assert.Equal("四人迷宫", row.DutyCategory);
    }

    /// <summary>
    /// docs/statistics-definitions.md section 12.2: joined in progress is counted over attempts, and its
    /// share over the attempts whose entry the software saw at all. A row that says nothing is
    /// neither kind; a match cancelled before entry is no attempt whatever it says; a deleted row
    /// counts nowhere. Nothing else moves.
    /// </summary>
    [Fact]
    public void Dashboard_CountsJoinedInProgressOverAttemptsAndItsShareOverKnownEntries()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(result: RunResult.Completed) with { JoinedInProgress = true }, tx);
            runs.Insert(TestDatabase.Run(result: RunResult.LeftOrAbandoned) with { JoinedInProgress = true }, tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed) with { JoinedInProgress = false }, tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed), tx);
            runs.Insert(TestDatabase.Run(result: RunResult.CancelledBeforeEntry) with { JoinedInProgress = true }, tx);
            runs.Insert(TestDatabase.Run(result: RunResult.Completed, softDeleted: true) with { JoinedInProgress = true }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var result = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(4, result.AttemptCount);
        Assert.Equal(3, result.CompletedCount);
        Assert.Equal(0.75, result.CompletionRate);
        Assert.Equal(2, result.JoinedInProgressCount);
        Assert.Equal(2.0 / 3.0, result.JoinedInProgressRate!.Value, 10);
    }

    [Fact]
    public void Dashboard_JoinedInProgressRateIsNullUntilAnEntryWasSeen()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx => runs.Insert(TestDatabase.Run(result: RunResult.Completed), tx));
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();

        var result = new StatisticsRepository(fixture.Database, settings).GetDashboard();

        Assert.Equal(1, result.AttemptCount);
        Assert.Equal(0, result.JoinedInProgressCount);
        Assert.Null(result.JoinedInProgressRate);
    }

    [Fact]
    public void GroupedStats_CarryTheJoinedInProgressCountOfEachGroup()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(contentId: 900001, jobId: 19) with { JoinedInProgress = true }, tx);
            runs.Insert(TestDatabase.Run(contentId: 900001, jobId: 19) with { JoinedInProgress = false }, tx);
            runs.Insert(TestDatabase.Run(contentId: 900002, jobId: 21) with { JoinedInProgress = true }, tx);
            runs.Insert(TestDatabase.Run(contentId: 900002, jobId: 21), tx);
            runs.Insert(TestDatabase.Run(contentId: 900002, jobId: 21, result: RunResult.CancelledBeforeEntry)
                with { JoinedInProgress = true }, tx);
        });
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var statistics = new StatisticsRepository(fixture.Database, settings);

        var duties = statistics.GetDungeonStats();
        var jobs = statistics.GetJobStats();

        Assert.Equal(1, duties.Single(row => row.ContentId == 900001).JoinedInProgressCount);
        Assert.Equal(1, duties.Single(row => row.ContentId == 900002).JoinedInProgressCount);
        Assert.Equal(2, duties.Single(row => row.ContentId == 900002).AttemptCount);
        Assert.Equal(1, jobs.Single(row => row.JobId == 19).JoinedInProgressCount);
        Assert.Equal(1, jobs.Single(row => row.JobId == 21).JoinedInProgressCount);
    }
}
