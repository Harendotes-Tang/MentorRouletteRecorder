using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The completion trend of docs/statistics-definitions.md section 12.1.
///
/// Two properties are pinned: the window is complete regardless of how many runs exist, and
/// empty periods are present rather than skipped. A series paged out of <c>QueryRuns</c>
/// satisfies neither, because it stops counting past one page.
/// </summary>
public sealed class StatisticsTrendTests
{
    [Fact]
    public void AnEmptyDatabaseStillReturnsTheWholeWindow()
    {
        using var fixture = new TestDatabase();
        var statistics = Repository(fixture);

        var day = statistics.GetTrend(granularity: TrendGranularity.Day);
        var week = statistics.GetTrend(granularity: TrendGranularity.Week);
        var month = statistics.GetTrend(granularity: TrendGranularity.Month);

        Assert.Equal(StatisticsRepository.TrendDayBuckets, day.Buckets.Count);
        Assert.Equal(StatisticsRepository.TrendWeekBuckets, week.Buckets.Count);
        Assert.Equal(StatisticsRepository.TrendMonthBuckets, month.Buckets.Count);
        Assert.All(day.Buckets, bucket => Assert.Equal(0, bucket.CompletedCount));
        Assert.All(week.Buckets, bucket => Assert.Equal(0, bucket.CompletedCount));
        Assert.All(month.Buckets, bucket => Assert.Equal(0, bucket.CompletedCount));
    }

    [Fact]
    public void EveryBucketStartsAtMidnightUtcAndTheSeriesIsOrderedAndGapFree()
    {
        using var fixture = new TestDatabase();

        var buckets = Repository(fixture).GetTrend(granularity: TrendGranularity.Day).Buckets;

        Assert.All(buckets, bucket =>
        {
            Assert.Equal(TimeSpan.Zero, bucket.StartUtc.Offset);
            Assert.Equal(TimeSpan.Zero, bucket.StartUtc.TimeOfDay);
        });

        for (var i = 1; i < buckets.Count; i++)
        {
            Assert.Equal(buckets[i - 1].StartUtc.AddDays(1), buckets[i].StartUtc);
        }

        // The window ends on the bucket that contains "now", never on a future one.
        Assert.Equal(DateTimeOffset.UtcNow.UtcDateTime.Date, buckets[^1].StartUtc.UtcDateTime.Date);
    }

    [Fact]
    public void CountsCompletedRunsIntoTheUtcDayOfTheirMatch()
    {
        using var fixture = new TestDatabase();
        // Anchored at noon UTC, not at "now": a run two hours before 01:19 UTC would belong to
        // yesterday's bucket.
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddHours(12);

        // Two today, one three days ago.
        fixture.InsertRuns(
            TestDatabase.Run(enteredAt: today.AddHours(-1)),
            TestDatabase.Run(enteredAt: today.AddHours(-2)),
            TestDatabase.Run(enteredAt: today.AddDays(-3)));

        var buckets = Repository(fixture).GetTrend(granularity: TrendGranularity.Day).Buckets;

        Assert.Equal(2, buckets[^1].CompletedCount);
        Assert.Equal(1, buckets[^4].CompletedCount);
        Assert.Equal(3, buckets.Sum(bucket => bucket.CompletedCount));
    }

    [Fact]
    public void CountsOnlyCompletedRuns()
    {
        using var fixture = new TestDatabase();
        var today = DateTimeOffset.UtcNow;

        fixture.InsertRuns(
            TestDatabase.Run(result: RunResult.Completed, enteredAt: today.AddHours(-1)),
            TestDatabase.Run(result: RunResult.LeftOrAbandoned, enteredAt: today.AddHours(-2)),
            TestDatabase.Run(result: RunResult.Disconnected, enteredAt: today.AddHours(-3)),
            TestDatabase.Run(result: RunResult.Interrupted, enteredAt: today.AddHours(-4)));

        Assert.Equal(1, Repository(fixture).GetTrend().Buckets.Sum(bucket => bucket.CompletedCount));
    }

    [Fact]
    public void ASoftDeletedRunIsCountedNowhere()
    {
        using var fixture = new TestDatabase();
        var today = DateTimeOffset.UtcNow;

        fixture.InsertRuns(TestDatabase.Run(enteredAt: today.AddHours(-1), softDeleted: true));

        Assert.Equal(0, Repository(fixture).GetTrend().Buckets.Sum(bucket => bucket.CompletedCount));
    }

    [Fact]
    public void ARunWithNoMatchTimeUsesItsActualEntryDateInTheSeries()
    {
        using var fixture = new TestDatabase();
        var today = DateTimeOffset.UtcNow;

        fixture.InsertRuns(TestDatabase.Run(enteredAt: today.AddHours(-1)) with { MatchedAtUtc = null });

        var dashboard = Repository(fixture).GetDashboard();

        Assert.Equal(1, dashboard.CompletedCount);
        Assert.Equal(1, dashboard.Trend.Buckets.Sum(bucket => bucket.CompletedCount));
    }

    [Fact]
    public void ARunOlderThanTheDayWindowIsStillInTheMonthWindow()
    {
        using var fixture = new TestDatabase();
        var old = DateTimeOffset.UtcNow.AddDays(-40);

        fixture.InsertRuns(TestDatabase.Run(enteredAt: old));
        var statistics = Repository(fixture);

        Assert.Equal(
            0, statistics.GetTrend(granularity: TrendGranularity.Day).Buckets.Sum(b => b.CompletedCount));
        Assert.Equal(
            1, statistics.GetTrend(granularity: TrendGranularity.Month).Buckets.Sum(b => b.CompletedCount));
    }

    [Fact]
    public void WeekBucketsStartOnMonday()
    {
        var starts = StatisticsRepository.BucketStarts(
            TrendGranularity.Week, new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.Zero));

        Assert.Equal(StatisticsRepository.TrendWeekBuckets, starts.Count);
        Assert.All(starts, start => Assert.Equal(DayOfWeek.Monday, start.UtcDateTime.DayOfWeek));

        // 2026-09-04 is a Friday; its ISO week began on Monday the 31st of August.
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), starts[^1]);
        Assert.Equal(starts[^1].AddDays(-7 * 11), starts[0]);
    }

    [Fact]
    public void MonthBucketsStartOnTheFirstAndWalkBackSixMonths()
    {
        var starts = StatisticsRepository.BucketStarts(
            TrendGranularity.Month, new DateTimeOffset(2026, 1, 17, 6, 0, 0, TimeSpan.Zero));

        Assert.Equal(StatisticsRepository.TrendMonthBuckets, starts.Count);
        Assert.All(starts, start => Assert.Equal(1, start.UtcDateTime.Day));
        Assert.Equal(new DateTimeOffset(2025, 8, 1, 0, 0, 0, TimeSpan.Zero), starts[0]);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), starts[^1]);
    }

    [Fact]
    public void ADayOnTheUtcBoundaryLandsInItsUtcBucketRegardlessOfTheServersTimeZone()
    {
        // The bucket is a fact about the stored UTC timestamp, not about where the Collector
        // happens to be running. A client in another zone must see the same series.
        using var fixture = new TestDatabase();
        var todayUtc = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var justAfterMidnight = new DateTimeOffset(todayUtc, TimeSpan.Zero).AddMinutes(20);

        fixture.InsertRuns(TestDatabase.Run(enteredAt: justAfterMidnight.AddSeconds(30)));

        var buckets = Repository(fixture).GetTrend().Buckets;

        Assert.Equal(new DateTimeOffset(todayUtc, TimeSpan.Zero), buckets[^1].StartUtc);
        Assert.Equal(1, buckets[^1].CompletedCount);
    }

    [Fact]
    public void TheDashboardCarriesTheGranularityItWasAskedFor()
    {
        using var fixture = new TestDatabase();
        var statistics = Repository(fixture);

        Assert.Equal(TrendGranularity.Day, statistics.GetDashboard().Trend.Granularity);
        Assert.Equal(
            TrendGranularity.Week,
            statistics.GetDashboard(granularity: TrendGranularity.Week).Trend.Granularity);
        Assert.Equal(
            TrendGranularity.Month,
            statistics.GetDashboard(granularity: TrendGranularity.Month).Trend.Granularity);
    }

    /// <summary>
    /// Review finding L-17. The trend window must end on the injected clock rather than on
    /// <c>DateTimeOffset.UtcNow</c>; otherwise its last bucket cannot be pinned by a test and
    /// the dashboard's four reads can straddle a capture commit.
    /// </summary>
    [Fact]
    public void TheTrendWindowEndsOnTheInjectedClock()
    {
        using var fixture = new TestDatabase();
        var pinned = new DateTimeOffset(2031, 3, 17, 22, 45, 0, TimeSpan.Zero);
        var statistics = new StatisticsRepository(
            fixture.Database, fixture.Settings(), clock: new TestClock(pinned));

        var trend = statistics.GetTrend();

        Assert.Equal(
            new DateTimeOffset(2031, 3, 17, 0, 0, 0, TimeSpan.Zero), trend.Buckets[^1].StartUtc);
        Assert.Equal(
            new DateTimeOffset(2031, 3, 17, 0, 0, 0, TimeSpan.Zero),
            statistics.GetDashboard().Trend.Buckets[^1].StartUtc);
    }

    // No clock: these cases anchor their data on the real "now", so the repository keeps
    // its default system clock.
    private static StatisticsRepository Repository(TestDatabase fixture) =>
        new(fixture.Database, fixture.Settings());
}
