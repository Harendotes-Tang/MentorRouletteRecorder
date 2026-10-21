using System.Globalization;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Repositories;
using NPOI.HSSF.UserModel;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>截图转述的历史开始日期；使用临时数据库，不将日期补成实际游戏时刻。</summary>
public sealed class ImportedStartDateTests
{
    private static JsonObject Candidate(string start = "2022.9.28") => new()
    {
        ["duty_name"] = "异界孤城特罗亚宫廷", ["job_name"] = "战士", ["entered_at_utc"] = start,
        ["ended_at_utc"] = "", ["result"] = "", ["duration_ms"] = "", ["reflection_text"] = "",
    };

    private static JsonObject Row(JsonObject preview) => Assert.Single(preview["rows"]!.AsArray())!.AsObject();
    private static JsonObject Preview(RunImportService service, string source, JsonObject input) =>
        service.PreviewSource(source, rows: new JsonArray(input), timeZone: "+08:00");
    private static JsonObject Commit(RunImportService service, JsonObject preview) => service.Commit(
        preview["preview_id"]!.GetValue<string>(), [Row(preview)["row_number"]!.GetValue<int>()], true, Guid.NewGuid().ToString("D"));

    public static IEnumerable<object[]> TableStartDates()
    {
        foreach (var kind in new[] { "XLS", "XLSX", "CSV", "PASTE" })
        foreach (var day in new[] { "2022-09-28", "2022/9/28", "2022.9.28" })
            yield return [kind, day];
    }

    [Theory]
    [MemberData(nameof(TableStartDates))]
    public void TableStartDayRemainsArchivalAndResolvedFactsSurviveRevalidation(string source, string start)
    {
        using var fixture = new TestDatabase();
        fixture.Clock.UtcNow = new DateTimeOffset(2022, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = Preview(service, source, Candidate(start));
        var row = Row(preview);
        Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Equal("new", row["status"]!.GetValue<string>());
        Assert.Empty(row["errors"]!.AsArray());
        var run = row["run"]!;
        Assert.Equal(21, run["job_id"]!.GetValue<int>());
        Assert.NotNull(run["content_id"]);
        Assert.Null(run["entered_at_utc"]);
        Assert.Null(run["ended_at_utc"]);
        Assert.Null(run["duration_ms"]);
        Assert.Equal("2022-09-28", run["import_metadata"]!["source_recorded_at"]!.GetValue<string>());
        Assert.Null(run["import_metadata"]!["source_recorded_at_utc"]);
        Assert.Contains(row["warning_refs"]!.AsArray(), value => value?["key"]?.GetValue<string>() == "import.warning.result_defaulted");
        var editable = row["candidate"]!.DeepClone().AsObject();
        Assert.Null(editable["entered_at_utc"]);
        Assert.Equal("2022-09-28", editable["source_recorded_at"]!.GetValue<string>());
        var second = Preview(service, source, editable);
        Assert.Equal("new", Row(second)["status"]!.GetValue<string>());
        Assert.Equal(row["candidate"]!["source_key"]!.GetValue<string>(), Row(second)["candidate"]!["source_key"]!.GetValue<string>());
        Assert.Equal(run["import_metadata"]!.ToJsonString(), Row(second)["run"]!["import_metadata"]!.ToJsonString());
        Assert.Null(Row(second)["run"]!["entered_at_utc"]);
        Assert.Equal(21, Row(second)["run"]!["job_id"]!.GetValue<int>());
        var id = Commit(service, second)["run_ids"]![0]!.GetValue<string>();
        var runs = new RunRepository(fixture.Database);
        var saved = runs.Get(id)!;
        Assert.Null(saved.EnteredAtUtc);
        Assert.Null(saved.EndedAtUtc);
        Assert.Null(saved.DurationMs);
        Assert.False(saved.PendingReview);
        Assert.Equal("2022-09-28", saved.ImportMetadata!.SourceRecordedAt);
        Assert.Equal("duplicate", Row(Preview(service, source, Row(second)["candidate"]!.DeepClone().AsObject()))["status"]!.GetValue<string>());
        var day = DateTimeOffset.Parse("2022-09-28T00:00:00Z", CultureInfo.InvariantCulture);
        var filter = new RunFilter { DateField = RunDateField.HistoryDate, FromUtc = day, ToUtc = day,
            HistoryFromDay = "2022-09-28", HistoryToDay = "2022-09-28" };
        Assert.Equal(id, Assert.Single(runs.Query(filter, null, 1, 50).Items).RunId);
        Assert.Empty(runs.Query(filter with { DateField = RunDateField.EnteredAtUtc }, null, 1, 50).Items);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var stats = new StatisticsRepository(fixture.Database, settings, clock: fixture.Clock);
        Assert.Equal(1, stats.GetDashboard(filter).CompletedCount);
        Assert.Null(stats.GetDashboard(filter).AverageDurationMs);
        Assert.Equal(1, Assert.Single(stats.GetTrend(granularity: TrendGranularity.Day).Buckets, bucket => bucket.StartUtc.Date == day.Date).CompletedCount);
    }

    [Theory]
    [InlineData("CSV", "副本,职业,开始时间,结果\n异界孤城特罗亚宫廷,战士,2022.9.28,\n")]
    [InlineData("PASTE", "副本\t职业\t开始时间\t结果\n异界孤城特罗亚宫廷\t战士\t2022/9/28\t\n")]
    public void TextTableAliasesReachDatePreservation(string source, string text)
    {
        using var fixture = new TestDatabase();
        var preview = new RunImportService(fixture.Database, fixture.Clock).PreviewSource(source, text: text);
        Assert.True(Row(preview)["can_import"]!.GetValue<bool>());
        Assert.Equal("2022-09-28", Row(preview)["candidate"]!["source_recorded_at"]!.GetValue<string>());
    }

    [Fact]
    public void BinaryXlsNumericStartDateReachesTheSameArchivalProjection()
    {
        var path = Path.Combine(Path.GetTempPath(), "MentorRecorder.StartDate." + Guid.NewGuid().ToString("N") + ".xls");
        try
        {
            using (var workbook = new HSSFWorkbook())
            {
                var sheet = workbook.CreateSheet("截图转述合成");
                var header = sheet.CreateRow(0);
                header.CreateCell(0).SetCellValue("副本");
                header.CreateCell(1).SetCellValue("职业");
                header.CreateCell(2).SetCellValue("开始时间");
                var row = sheet.CreateRow(1);
                row.CreateCell(0).SetCellValue("异界孤城特罗亚宫廷");
                row.CreateCell(1).SetCellValue("战士");
                var start = row.CreateCell(2);
                start.SetCellValue(44832);
                var style = workbook.CreateCellStyle();
                style.DataFormat = workbook.CreateDataFormat().GetFormat("yyyy.m.d");
                start.CellStyle = style;
                using var stream = File.Create(path);
                workbook.Write(stream);
            }
            using var fixture = new TestDatabase();
            var service = new RunImportService(fixture.Database, fixture.Clock);
            var preview = service.PreviewSource("XLS", path);
            Assert.True(Row(preview)["can_import"]!.GetValue<bool>());
            Assert.Equal(21, Row(preview)["run"]!["job_id"]!.GetValue<int>());
            var id = Commit(service, preview)["run_ids"]![0]!.GetValue<string>();
            var saved = new RunRepository(fixture.Database).Get(id)!;
            Assert.Equal("2022-09-28", saved.ImportMetadata!.SourceRecordedAt);
            Assert.Null(saved.EnteredAtUtc);
            Assert.Null(saved.DurationMs);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExplicitUnknownOutcomeAndProgressFactsAreNotChangedByStartDateProjection()
    {
        using var fixture = new TestDatabase();
        var input = Candidate();
        input["result"] = "UNKNOWN";
        input["mentor_confirmed"] = false;
        input["joined_in_progress"] = true;
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = Preview(service, "CSV", input);
        var id = Commit(service, preview)["run_ids"]![0]!.GetValue<string>();
        var saved = new RunRepository(fixture.Database).Get(id)!;
        Assert.Equal(RunResult.Unknown, saved.Result);
        Assert.False(saved.IsConfirmedMentor);
        Assert.True(saved.JoinedInProgress);
        Assert.Equal("2022-09-28", saved.ImportMetadata!.SourceRecordedAt);
        Assert.Null(saved.DurationMs);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        Assert.Equal(0, new StatisticsRepository(fixture.Database, settings).GetDashboard().CompletedCount);
    }

    [Fact]
    public void ExistingUtcProvenanceIsNotDiscardedToAcceptADateOnlyStart()
    {
        using var fixture = new TestDatabase();
        var input = Candidate();
        input["source_recorded_at_utc"] = "2022-09-28T12:00:00Z";
        var row = Row(Preview(new RunImportService(fixture.Database, fixture.Clock), "CSV", input));
        Assert.False(row["can_import"]!.GetValue<bool>());
        Assert.Equal("2022.9.28", row["candidate"]!["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("2022-09-28T12:00:00Z", row["candidate"]!["source_recorded_at_utc"]!.GetValue<string>());
        Assert.Contains(row["error_refs"]!.AsArray(), value => value?["key"]?.GetValue<string>() == "import.row.start_date_source_conflict");
    }

    [Theory]
    [InlineData(false, "2022-09-29")]
    [InlineData(true, "2022/9/29")]
    [InlineData(false, "2022-09-29 20:30")]
    public void DifferentExistingSourceDateIsRecoverableAndPreservesOriginalInput(bool nested, string existing)
    {
        using var fixture = new TestDatabase();
        var input = Candidate();
        if (nested) input["import_metadata"] = new JsonObject { ["source_recorded_at"] = existing };
        else input["source_recorded_at"] = existing;
        var preview = Preview(new RunImportService(fixture.Database, fixture.Clock), "XLSX", input);
        var row = Row(preview);
        Assert.False(row["can_import"]!.GetValue<bool>());
        Assert.Null(row["run"]);
        Assert.Equal("2022.9.28", row["candidate"]!["entered_at_utc"]!.GetValue<string>());
        Assert.Equal(existing, nested ? row["candidate"]!["import_metadata"]!["source_recorded_at"]!.GetValue<string>()
            : row["candidate"]!["source_recorded_at"]!.GetValue<string>());
        Assert.Contains(row["error_refs"]!.AsArray(), value => value?["key"]?.GetValue<string>() == "import.row.start_date_source_conflict");
        Assert.Contains(row["errors"]!.AsArray(), value => value!.GetValue<string>().Contains(existing, StringComparison.Ordinal));
    }

    /// <summary>
    /// Audit 2026-10-20 H2-5: a 记录时间 with a clock on the very day of the date-only 开始时间 agrees with it.
    /// The row is accepted, keeps the clock time as its source provenance and fabricates no game time.
    /// </summary>
    [Theory]
    [InlineData("2022-09-28 20:30")]
    [InlineData("2022/9/28 20:30:00")]
    public void ASameDayTimedSourceAgreesWithTheDateOnlyStart(string existing)
    {
        using var fixture = new TestDatabase();
        fixture.Clock.UtcNow = new DateTimeOffset(2022, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var input = Candidate();
        input["source_recorded_at"] = existing;
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var row = Row(Preview(service, "XLSX", input));
        Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Empty(row["errors"]!.AsArray());
        Assert.Null(row["run"]!["entered_at_utc"]);
        Assert.Equal(existing, row["run"]!["import_metadata"]!["source_recorded_at"]!.GetValue<string>());
        Assert.Equal("2022-09-28T12:30:00.000Z", row["run"]!["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
        var revalidated = Row(Preview(service, "XLSX", row["candidate"]!.DeepClone().AsObject()));
        Assert.True(revalidated["can_import"]!.GetValue<bool>());
        Assert.Equal(row["run"]!["import_metadata"]!.ToJsonString(), revalidated["run"]!["import_metadata"]!.ToJsonString());
    }

    /// <summary>
    /// The refusal that remains names both values and claims nothing it cannot know: a source time held only
    /// in UTC may well fall on the same local day, so the sentence must not say the dates differ.
    /// </summary>
    [Fact]
    public void TheRemainingRefusalNamesBothValuesWithoutClaimingTheDatesDiffer()
    {
        using var fixture = new TestDatabase();
        var input = Candidate();
        input["source_recorded_at_utc"] = "2022-09-28T12:00:00Z";
        var row = Row(Preview(new RunImportService(fixture.Database, fixture.Clock), "CSV", input));
        var sentence = Assert.Single(row["errors"]!.AsArray())!.GetValue<string>();
        Assert.Contains("2022.9.28", sentence, StringComparison.Ordinal);
        Assert.Contains("2022-09-28T12:00:00Z", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("不一致", sentence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "2022-09-28")]
    [InlineData(true, "2022/9/28")]
    [InlineData(false, "2022.9.28")]
    [InlineData(true, "2022.9.28")]
    public void EquivalentExistingSourceDateRemainsUnchanged(bool nested, string existing)
    {
        using var fixture = new TestDatabase();
        fixture.Clock.UtcNow = new DateTimeOffset(2022, 9, 30, 0, 0, 0, TimeSpan.Zero);
        var input = Candidate();
        if (nested) input["import_metadata"] = new JsonObject { ["source_recorded_at"] = existing };
        else input["source_recorded_at"] = existing;
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = Preview(service, "CSV", input);
        Assert.True(Row(preview)["can_import"]!.GetValue<bool>());
        Assert.Equal(existing, Row(preview)["run"]!["import_metadata"]!["source_recorded_at"]!.GetValue<string>());
        Assert.Null(Row(preview)["run"]!["entered_at_utc"]);
        var editable = Row(preview)["candidate"]!.DeepClone().AsObject();
        Assert.Equal(existing, editable["source_recorded_at"]!.GetValue<string>());
        var revalidated = Preview(service, "CSV", editable);
        Assert.True(Row(revalidated)["can_import"]!.GetValue<bool>());
        Assert.Equal(existing, Row(revalidated)["candidate"]!["source_recorded_at"]!.GetValue<string>());
        var id = Commit(service, revalidated)["run_ids"]![0]!.GetValue<string>();
        var next = Candidate("2022.9.29");
        next["source_recorded_at"] = "2022.9.29";
        var nextId = Commit(service, Preview(service, "CSV", next))["run_ids"]![0]!.GetValue<string>();
        var runs = new RunRepository(fixture.Database);
        Assert.Equal(existing, runs.Get(id)!.ImportMetadata!.SourceRecordedAt);
        Assert.Null(runs.Get(id)!.ImportMetadata!.SourceRecordedAtUtc);
        Assert.Null(runs.Get(id)!.EnteredAtUtc);
        Assert.Equal(new[] { id, nextId }, runs.Query(null,
            new RunSort(RunSortField.HistoryDate, SortDirection.Asc), 1, 50).Items.Select(run => run.RunId));
        Assert.Equal(new[] { nextId, id }, runs.Query(null,
            new RunSort(RunSortField.HistoryDate, SortDirection.Desc), 1, 50).Items.Select(run => run.RunId));
        // A negative-offset picker day must still retain the archival day rather than
        // interpreting it as an actual UTC midnight outside the selected range.
        var filter = new RunFilter { DateField = RunDateField.HistoryDate,
            HistoryFromDay = "2022-09-28", HistoryToDay = "2022-09-28",
            FromUtc = new DateTimeOffset(2022, 9, 28, 4, 0, 0, TimeSpan.Zero),
            ToUtc = new DateTimeOffset(2022, 9, 29, 3, 59, 59, 999, TimeSpan.Zero) };
        Assert.Equal(id, Assert.Single(runs.Query(filter, null, 1, 50).Items).RunId);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var stats = new StatisticsRepository(fixture.Database, settings, clock: fixture.Clock);
        Assert.Equal(1, stats.GetDashboard(filter).CompletedCount);
        var buckets = stats.GetTrend(granularity: TrendGranularity.Day).Buckets;
        Assert.Equal(1, Assert.Single(buckets, bucket => bucket.StartUtc.Date == new DateTime(2022, 9, 28)).CompletedCount);
        Assert.Equal(1, Assert.Single(buckets, bucket => bucket.StartUtc.Date == new DateTime(2022, 9, 29)).CompletedCount);
    }

    [Theory]
    [InlineData("XLSX", "entered_at_utc", "2022.2.29")]
    [InlineData("CSV", "entered_at_utc", "2022-09-31")]
    [InlineData("PASTE", "entered_at_utc", "2022/9")]
    [InlineData("XLS", "entered_at_utc", "9:30")]
    [InlineData("CSV", "entered_at_utc", "09:30Z")]
    [InlineData("JSON", "entered_at_utc", "2022-09-28")]
    [InlineData("ROWS", "entered_at_utc", "2022-09-28")]
    [InlineData("SCREENSHOT", "entered_at_utc", "2022-09-28")]
    [InlineData("BACKUP", "entered_at_utc", "2022-09-28")]
    [InlineData("XLSX", "ended_at_utc", "2022-09-28")]
    [InlineData("CSV", "matched_at_utc", "2022-09-28")]
    public void OtherIncompleteActualTimesRemainInvalid(string source, string field, string value)
    {
        using var fixture = new TestDatabase();
        var input = Candidate("2022-09-28T10:00:00Z");
        input[field] = value;
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = Preview(service, source, input);
        Assert.False(Row(preview)["can_import"]!.GetValue<bool>());
        Assert.NotEmpty(Row(preview)["errors"]!.AsArray());
        Assert.Throws<CollectorException>(() => Commit(service, preview));
        Assert.Empty(new RunRepository(fixture.Database).Query(null, null, 1, 50).Items);
    }
}
