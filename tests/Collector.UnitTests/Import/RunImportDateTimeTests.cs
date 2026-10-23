using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Dates and times in imported records: dates at either end of the calendar, game times read in
/// the region the user picks, and timestamps that lack a calendar date.
/// </summary>
public sealed class RunImportDateTimeTests
{
    private static JsonObject Candidate(string entered) => new()
    {
        ["duty_name"] = "边界日期副本", ["entered_at_utc"] = entered,
    };

    // ---------------------------------------- dates at either end of the calendar ----

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z")]
    [InlineData("9999-12-31T23:59:59.999Z")]
    public void ExtremeDateDoesNotAbortPreviewOrCommitOfOtherRows(string entered)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(
            Candidate(entered), Candidate("2026-10-23T12:00:00.000Z")));
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("new", row!["status"]!.GetValue<string>()));
        Assert.Equal(entered, rows[0]!["run"]!["entered_at_utc"]!.GetValue<string>());

        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(),
            new[] { 1, 2 }, true, Guid.NewGuid().ToString("D"));
        var ids = committed["run_ids"]!.AsArray();
        Assert.Equal(2, ids.Count);
        var repository = new RunRepository(fixture.Database);
        Assert.Contains(ids, id => repository.Get(id!.GetValue<string>())!.EnteredAtUtc == UtcTimestamp.Parse(entered));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:00:00.000Z", true)]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:01:00.000Z", true)]
    [InlineData("0001-01-01T00:00:00.000Z", "0001-01-01T00:01:00.001Z", false)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:59:59.999Z", true)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:58:59.999Z", true)]
    [InlineData("9999-12-31T23:59:59.999Z", "9999-12-31T23:58:59.998Z", false)]
    public void ExtremeDateKeepsTheInclusiveOneMinuteDuplicateWindow(string entered, string localTime, bool nearby)
    {
        using var fixture = new TestDatabase();
        var local = TestDatabase.Run() with
        {
            ContentId = null, TerritoryId = null, DutyName = "边界日期副本",
            EnteredAtUtc = UtcTimestamp.Parse(localTime), MatchedAtUtc = null, EndedAtUtc = null, DurationMs = null,
        };
        fixture.Database.RunInTransaction(tx => new RunRepository(fixture.Database).Insert(local, tx));
        var preview = new RunImportService(fixture.Database, fixture.Clock)
            .PreviewSource("ROWS", rows: new JsonArray(Candidate(entered)));
        var row = preview["rows"]![0]!;
        Assert.Equal(nearby ? "possible_duplicate" : "new", row["status"]!.GetValue<string>());
        Assert.Equal(nearby ? local.RunId : null, row["matching_run_id"]?.GetValue<string>());
    }

    // --------------------------------------------- the region of local game times ----
    // Audit 2026-10-20 H2-4: game times written without a zone are read in the region the user picks. The
    // candidate keeps them as written, so choosing another region and checking again reads them again.

    private static JsonObject Row(JsonObject preview) => preview["rows"]![0]!.AsObject();

    [Fact]
    public void ChangingTheRegionRereadsLocalGameTimes()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = service.PreviewSource("PASTE", text: "副本\t进本时间\t结束时间\tmatched_at\n合成副本\t2026-10-10 20:00\t2026-10-10 20:30\t2026-10-10 19:59",
            timeZone: "+08:00", sourceName: "粘贴的表格");
        Assert.Equal("2026-10-10T12:00:00.000Z", Row(first)["run"]!["entered_at_utc"]!.GetValue<string>());
        var candidate = Row(first)["candidate"]!.DeepClone().AsObject();
        Assert.Equal("2026-10-10 20:00", candidate["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10 20:30", candidate["ended_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10 19:59", candidate["matched_at_utc"]!.GetValue<string>());

        var rechecked = service.PreviewSource("PASTE", rows: new JsonArray(candidate), timeZone: "+09:00", sourceName: "粘贴的表格");
        var run = Row(rechecked)["run"]!;
        Assert.Equal("2026-10-10T11:00:00.000Z", run["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10T11:30:00.000Z", run["ended_at_utc"]!.GetValue<string>());
        Assert.Equal("2026-10-10T10:59:00.000Z", run["matched_at_utc"]!.GetValue<string>());
        Assert.Equal(1_800_000, run["duration_ms"]!.GetValue<long>());
        Assert.Equal(candidate["source_key"]!.GetValue<string>(), Row(rechecked)["candidate"]!["source_key"]!.GetValue<string>());
    }

    /// <summary>
    /// Audit 2026-10-20 H2-7: a file the import will not read because it is not on a local disk is refused
    /// as an import request, about the file_path field and in import words, not as a failed export.
    /// </summary>
    [Theory]
    [InlineData(@"\\synthetic-server\share\personal-records.csv")]
    [InlineData(@"\\?\UNC\synthetic-server\share\personal-records.csv")]
    public void AFileOffTheLocalDiskIsRefusedInImportWords(string path)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var refusal = Assert.Throws<CollectorException>(() => service.PreviewSource("CSV", filePath: path));
        Assert.Equal(ErrorCodes.BadRequest, refusal.Code);
        Assert.Equal("file_path", refusal.Field);
        Assert.Equal("import.preview.path_not_local", refusal.Text?.Key);
        Assert.DoesNotContain("导出", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesWithTheirOwnOffsetIgnoreTheRegionAndStayCanonical()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = service.PreviewSource("ROWS", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "合成副本", ["entered_at_utc"] = "2026-10-10T20:00:00+08:00",
        }), timeZone: "+08:00");
        var candidate = Row(first)["candidate"]!.DeepClone().AsObject();
        Assert.Equal("2026-10-10T12:00:00.000Z", candidate["entered_at_utc"]!.GetValue<string>());
        var rechecked = service.PreviewSource("ROWS", rows: new JsonArray(candidate), timeZone: "+09:00");
        Assert.Equal("2026-10-10T12:00:00.000Z", Row(rechecked)["run"]!["entered_at_utc"]!.GetValue<string>());
    }

    // ----------------------------------------- timestamps without a calendar date ----

    public static IEnumerable<object[]> IncompleteTimestamps()
    {
        foreach (var source in new[] { "ROWS", "CSV" })
        foreach (var timestamp in new[] { "09:00Z", "09:00+08:00", "09:00-05:30", "10-11 09:00Z", "10/11 09:00+08:00", "2026-10 09:00Z" })
            yield return new object[] { source, "entered_at_utc", timestamp };
        foreach (var field in new[] { "ended_at_utc", "matched_at_utc", "created_at_utc", "source_recorded_at", "source_recorded_at_utc" })
            yield return new object[] { "ROWS", field, "09:00Z" };
    }

    [Theory]
    [MemberData(nameof(IncompleteTimestamps))]
    public void MissingCalendarDateIsRejectedBeforeCommitAndCannotContributeToStatistics(string source, string field, string timestamp)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var input = new JsonObject
        {
            ["duty_name"] = "日期缺失回归", ["result"] = "COMPLETED",
            ["entered_at_utc"] = "2026-10-11T09:00:00Z", ["ended_at_utc"] = "2026-10-11T09:30:00Z",
        };
        input[field] = timestamp;
        var preview = source == "CSV"
            ? service.PreviewSource("CSV", text: string.Join(',', input.Select(pair => pair.Key)) + "\n" + string.Join(',', input.Select(pair => pair.Value!.GetValue<string>())) + "\n")
            : service.PreviewSource("ROWS", rows: new JsonArray(input));
        var row = preview["rows"]![0]!.AsObject();
        Assert.False(row["can_import"]!.GetValue<bool>());
        Assert.NotEmpty(row["errors"]!.AsArray());
        Assert.Null(row["run"]);
        Assert.Throws<CollectorException>(() => service.Commit(preview["preview_id"]!.GetValue<string>(),
            new[] { row["row_number"]!.GetValue<int>() }, true, Guid.NewGuid().ToString("D")));
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var stats = new StatisticsRepository(fixture.Database, settings).GetDashboard();
        Assert.Equal(0, stats.CompletedCount);
        Assert.Equal(0, stats.AchievementProgress);
    }

    [Theory]
    [InlineData("2026-10-11T09:00:00Z", "2026-10-11T09:00:00.000Z")]
    [InlineData("2026-10-11 09:00Z", "2026-10-11T09:00:00.000Z")]
    [InlineData("2026-10-11T09:00:00.1234567+08:00", "2026-10-11T01:00:00.123Z")]
    [InlineData("2026/10/11 9:00+08:00", "2026-10-11T01:00:00.000Z")]
    [InlineData("2026-10-11T09:00:00-05:30", "2026-10-11T14:30:00.000Z")]
    public void CompleteDateAndOffsetKeepTheirExplicitInstant(string timestamp, string expected)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "明确日期回归", ["result"] = "UNKNOWN", ["entered_at_utc"] = timestamp,
        }));
        var row = preview["rows"]![0]!.AsObject();
        Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Empty(row["errors"]!.AsArray());
        Assert.Equal(expected, row["run"]!["entered_at_utc"]!.GetValue<string>());
    }
}
