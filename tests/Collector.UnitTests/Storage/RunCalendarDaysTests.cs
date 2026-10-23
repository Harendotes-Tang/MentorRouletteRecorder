using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunCalendarDaysTests
{
    [Theory]
    [InlineData("2026-09")]
    [InlineData("0001-01")]
    [InlineData("9999-12")]
    public void ParserAcceptsStrictCalendarMonths(string month) =>
        Assert.Equal(month, RequestParsers.CalendarMonth(new PayloadReader(new JsonObject { ["month"] = month })));

    [Theory]
    [InlineData("2026-9")]
    [InlineData("2026-00")]
    [InlineData("2026-13")]
    [InlineData("0000-01")]
    [InlineData("2026-09-01")]
    [InlineData("")]
    [InlineData(" 2026-09")]
    public void ParserRejectsInvalidCalendarMonths(string month) =>
        Assert.Throws<CollectorException>(() => RequestParsers.CalendarMonth(new PayloadReader(new JsonObject { ["month"] = month })));

    [Fact]
    public void SourceDaysAreNotShiftedAndUnknownTimesDoNotBecomeImportDays()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var imports = new RunImportRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            foreach (var raw in new[] { "2026-09-01", "2026/9/30", "2026-09-31", "unknown" })
            {
                var run = TestDatabase.Run(source: RunSource.Import) with
                {
                    MatchedAtUtc = null, EnteredAtUtc = null, EndedAtUtc = null,
                };
                runs.Insert(run, tx);
                imports.Insert(run.RunId, new RunImportMetadata("SCREENSHOT", "fixture", raw,
                    null, run.CreatedAtUtc, Guid.NewGuid().ToString("N"), true), tx);
            }
        });
        Assert.Equal(new[] { "2026-09-01", "2026-09-30" }, runs.QueryCalendarDays("2026-09", null));
        Assert.Empty(runs.QueryCalendarDays("2026-10", null));
    }

    [Fact]
    public void KnownSourceInstantOverridesRawCalendarLabel()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var imports = new RunImportRepository(fixture.Database);
        var instant = DateTimeOffset.Parse("2026-09-15T23:30:00.000Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var run = TestDatabase.Run(source: RunSource.Import) with
        {
            MatchedAtUtc = null, EnteredAtUtc = null, EndedAtUtc = null,
        };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            imports.Insert(run.RunId, new RunImportMetadata("SCREENSHOT", "fixture", "2026-09-01",
                instant, run.CreatedAtUtc, Guid.NewGuid().ToString("N"), true), tx);
        });
        var localDay = instant.ToLocalTime().ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new[] { localDay }, runs.QueryCalendarDays("2026-09", null));
    }

    [Fact]
    public void HistoryPriorityAndLocalMidnightAreProjectedFromActualInstant()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var localMidnight = new DateTimeOffset(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 1)));
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run() with { MatchedAtUtc = localMidnight.AddMinutes(-1),
                EnteredAtUtc = localMidnight.AddMinutes(1), EndedAtUtc = localMidnight.AddMinutes(2) }, tx);
            runs.Insert(TestDatabase.Run() with { MatchedAtUtc = null, EnteredAtUtc = localMidnight.AddMinutes(1),
                EndedAtUtc = localMidnight.AddMinutes(2) }, tx);
        });
        Assert.Equal(new[] { "2026-08-31" }, runs.QueryCalendarDays("2026-08", null));
        Assert.Equal(new[] { "2026-09-01" }, runs.QueryCalendarDays("2026-09", null));
    }
}
