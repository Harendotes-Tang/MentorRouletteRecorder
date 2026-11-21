using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The days of a month that have records (<c>QueryRunCalendarDays</c>) and the history_date sort anchor of a
/// date-only record, both cut in the 显示时区. The tests that choose a zone of their own pin exact days, so they
/// pass whatever the machine's zone is; they share the process-wide zone and therefore run alone.
/// </summary>
[Collection(DisplayTimeZoneCollection.Name)]
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

    [Theory]
    [InlineData("Asia/Tokyo", "2026-09-16")]
    [InlineData("America/New_York", "2026-09-15")]
    [InlineData("UTC", "2026-09-15")]
    public void DaysAreCutInTheDisplayTimeZone(string zone, string expected)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var instant = Utc("2026-09-15T16:30:00.000Z");
        fixture.Database.RunInTransaction(tx => runs.Insert(TestDatabase.Run() with
        {
            MatchedAtUtc = instant, EnteredAtUtc = instant.AddMinutes(1), EndedAtUtc = instant.AddMinutes(20),
        }, tx));

        using (DisplayTimeZone.Use(zone))
        {
            Assert.Equal(new[] { expected }, runs.QueryCalendarDays("2026-09", null));
        }
    }

    [Fact]
    public void DaylightSavingChangesMoveTheMidnightOfTheDay()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            // America/New_York springs forward at 2026-03-08 02:00 EST and falls back at 2026-11-01 02:00 EDT:
            // the last minute of the previous day and the first of the day on both sides, at the offset in force.
            foreach (var text in new[]
                     {
                         "2026-03-08T04:59:00.000Z", "2026-03-08T05:00:00.000Z",
                         "2026-11-01T03:59:00.000Z", "2026-11-01T04:00:00.000Z",
                     })
            {
                var at = Utc(text);
                runs.Insert(TestDatabase.Run() with { MatchedAtUtc = at, EnteredAtUtc = at, EndedAtUtc = at.AddMinutes(20) }, tx);
            }
        });

        using (DisplayTimeZone.Use("America/New_York"))
        {
            Assert.Equal(new[] { "2026-03-07", "2026-03-08" }, runs.QueryCalendarDays("2026-03", null));
            Assert.Equal(new[] { "2026-10-31" }, runs.QueryCalendarDays("2026-10", null));
            Assert.Equal(new[] { "2026-11-01" }, runs.QueryCalendarDays("2026-11", null));
        }

        using (DisplayTimeZone.Use("Asia/Tokyo"))
        {
            Assert.Equal(new[] { "2026-03-08" }, runs.QueryCalendarDays("2026-03", null));
            Assert.Empty(runs.QueryCalendarDays("2026-10", null));
            Assert.Equal(new[] { "2026-11-01" }, runs.QueryCalendarDays("2026-11", null));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    [InlineData("Pacific/Kiritimati")]
    [InlineData("Pacific/Pago_Pago")]
    public void AnArchivalDayKeepsItsSourceDayInEveryZone(string? zone)
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        InsertArchivalDay(fixture, "2026-09-01");
        InsertArchivalDay(fixture, "2026/9/30");

        using (DisplayTimeZone.Use(zone))
        {
            Assert.Equal(new[] { "2026-09-01", "2026-09-30" }, runs.QueryCalendarDays("2026-09", null));
            Assert.Empty(runs.QueryCalendarDays("2026-08", null));
            Assert.Empty(runs.QueryCalendarDays("2026-10", null));
        }
    }

    [Fact]
    public void AnArchivalDaySortsFromTheFirstInstantOfItsDayInTheZone()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var archival = InsertArchivalDay(fixture, "2026-09-15");
        // 2026-09-15 05:00 in Tokyo, 2026-09-14 16:00 in New York.
        var timedAt = Utc("2026-09-14T20:00:00.000Z");
        var timed = TestDatabase.Run() with { MatchedAtUtc = timedAt, EnteredAtUtc = timedAt, EndedAtUtc = timedAt.AddMinutes(20) };
        fixture.Database.RunInTransaction(tx => runs.Insert(timed, tx));
        var ascending = new RunSort(RunSortField.HistoryDate, SortDirection.Asc);

        // Tokyo starts 2026-09-15 at 2026-09-14T15:00Z, before the timed run; New York at 2026-09-15T04:00Z, after it.
        using (DisplayTimeZone.Use("Asia/Tokyo"))
        {
            Assert.Equal(new[] { archival, timed.RunId }, runs.Query(null, ascending, 1, 50).Items.Select(run => run.RunId));
        }

        using (DisplayTimeZone.Use("America/New_York"))
        {
            Assert.Equal(new[] { timed.RunId, archival }, runs.Query(null, ascending, 1, 50).Items.Select(run => run.RunId));
        }
    }

    [Fact]
    public void HistoryPagesInOneReadSnapshotKeepTheSameZone()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var archival = InsertArchivalDay(fixture, "2026-09-15");
        var timedAt = Utc("2026-09-14T20:00:00.000Z");
        var timed = TestDatabase.Run() with { MatchedAtUtc = timedAt, EnteredAtUtc = timedAt, EndedAtUtc = timedAt.AddMinutes(20) };
        fixture.Database.RunInTransaction(tx => runs.Insert(timed, tx));
        var sort = new RunSort(RunSortField.HistoryDate, SortDirection.Asc);

        using var restore = DisplayTimeZone.Use("Asia/Tokyo");
        fixture.Database.ReadSnapshot((snapshot, tx) =>
        {
            var reader = new RunRepository(snapshot);
            var first = reader.Query(null, sort, page: 1, pageSize: 1, transaction: tx);
            Assert.Equal(archival, Assert.Single(first.Items).RunId);

            DisplayTimeZone.Set("America/New_York");
            Assert.Equal(timed.RunId, Assert.Single(runs.Query(null, sort, page: 1, pageSize: 1).Items).RunId);

            var second = reader.Query(null, sort, page: 2, pageSize: 1, transaction: tx);
            Assert.Equal(timed.RunId, Assert.Single(second.Items).RunId);
            return second.Total;
        });
    }

    [Fact]
    public void FollowingTheComputerGivesWhatSqliteLocaltimeGave()
    {
        using var fixture = new TestDatabase();
        Assert.Null(DisplayTimeZone.Id);
        fixture.Database.Read(_ =>
        {
            // Every half hour of 2026: the day an instant falls on, as date(x, 'localtime') cut it.
            using (var command = fixture.Database.CreateCommand())
            {
                command.CommandText =
                    "WITH RECURSIVE steps(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM steps WHERE i < 17567) " +
                    "SELECT COUNT(*) FROM (SELECT strftime('%Y-%m-%dT%H:%M:%fZ', '2026-01-01', '+' || (i * 30) || ' minutes') AS x " +
                    $"FROM steps) WHERE date(x, 'localtime') IS NOT {DisplayZoneSqlFunctions.Day}(x);";
                Assert.Equal(0L, (long)command.ExecuteScalar()!);
            }

            // Every day of 2026 whose midnight exists here: its anchor, as strftime(..., day, 'utc') gave it.
            using var dayCommand = fixture.Database.CreateCommand();
            dayCommand.CommandText =
                $"SELECT strftime('%Y-%m-%dT%H:%M:%fZ', $day, 'utc'), {DisplayZoneSqlFunctions.DayStart}($day);";
            var parameter = dayCommand.Parameters.Add("$day", Microsoft.Data.Sqlite.SqliteType.Text);
            for (var day = new DateOnly(2026, 1, 1); day.Year == 2026; day = day.AddDays(1))
            {
                if (TimeZoneInfo.Local.IsInvalidTime(day.ToDateTime(TimeOnly.MinValue)))
                {
                    continue;
                }

                parameter.Value = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                using var reader = dayCommand.ExecuteReader();
                Assert.True(reader.Read());
                Assert.Equal(reader.GetString(0), reader.GetString(1));
            }

            return 0;
        });
    }

    [Fact]
    public void TheZoneFunctionsAnswerNullForWhatIsNotADayOrAnInstant()
    {
        Assert.Null(DisplayZoneSqlFunctions.DayOf(null));
        Assert.Null(DisplayZoneSqlFunctions.DayOf("unknown"));
        Assert.Null(DisplayZoneSqlFunctions.DayStartOf(null));
        Assert.Null(DisplayZoneSqlFunctions.DayStartOf("2026-09-31"));
        Assert.Null(DisplayZoneSqlFunctions.DayStartOf("2026/9/1"));
        using (DisplayTimeZone.Use("Asia/Tokyo"))
        {
            Assert.Equal("2026-09-16", DisplayZoneSqlFunctions.DayOf("2026-09-15T16:30:00.000Z"));
            Assert.Equal("2026-09-16", DisplayZoneSqlFunctions.DayOf("2026-09-15 16:30:00"));
            Assert.Equal("2026-09-14T15:00:00.000Z", DisplayZoneSqlFunctions.DayStartOf("2026-09-15"));
        }
    }

    private static DateTimeOffset Utc(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An imported record known only by the day its source wrote; returns its id.</summary>
    private static string InsertArchivalDay(TestDatabase fixture, string sourceDay)
    {
        var runs = new RunRepository(fixture.Database);
        var imports = new RunImportRepository(fixture.Database);
        var run = TestDatabase.Run(source: RunSource.Import) with
        {
            MatchedAtUtc = null, EnteredAtUtc = null, EndedAtUtc = null,
        };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            imports.Insert(run.RunId, new RunImportMetadata("SCREENSHOT", "fixture", sourceDay,
                null, run.CreatedAtUtc, Guid.NewGuid().ToString("N"), true), tx);
        });
        return run.RunId;
    }
}
