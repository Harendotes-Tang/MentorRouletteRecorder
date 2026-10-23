using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class RunImportEntryModeTests
{
    [Theory]
    [InlineData("正常", false)]
    [InlineData("开局进入", false)]
    [InlineData("中途", true)]
    [InlineData("中途加入", true)]
    [InlineData(" true ", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("是", true)]
    [InlineData("否", false)]
    [InlineData(" 中途 ", true)]
    [InlineData("", null)]
    [InlineData(" \t\r\n", null)]
    [InlineData(null, null)]
    public void EntryModeTextRetainsThreeStates(string? input, bool? expected)
    {
        using var fixture = new TestDatabase();
        var row = Candidate();
        row["joined_in_progress"] = input;
        var preview = new RunImportService(fixture.Database, fixture.Clock)
            .PreviewSource("ROWS", rows: new JsonArray(row));
        var result = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.Empty(result["errors"]!.AsArray());
        Assert.True(result["can_import"]!.GetValue<bool>());
        Assert.Equal(expected, result["run"]!["joined_in_progress"]?.GetValue<bool>());
        Assert.Equal(expected, result["candidate"]!["joined_in_progress"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("null", null)]
    [InlineData("missing", null)]
    public void NativeJsonBooleanNumberNullAndAbsentEntryRemainSupported(string jsonValue, bool? expected)
    {
        using var fixture = new TestDatabase();
        var row = Candidate();
        if (jsonValue != "missing") row["joined_in_progress"] = JsonNode.Parse(jsonValue);
        var preview = new RunImportService(fixture.Database, fixture.Clock)
            .PreviewSource("JSON", text: new JsonArray(row).ToJsonString());
        var result = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.Empty(result["errors"]!.AsArray());
        Assert.Equal(expected, result["run"]!["joined_in_progress"]?.GetValue<bool>());
    }

    [Theory]
    [InlineData("未知")]
    [InlineData("正常／中途")]
    [InlineData("maybe")]
    public void UnknownEntryTextIsActionableRowErrorAndCannotCommit(string invalid)
    {
        using var fixture = new TestDatabase();
        var row = Candidate(); row["joined_in_progress"] = invalid;
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("ROWS", rows: new JsonArray(row));
        var result = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.False(result["can_import"]!.GetValue<bool>());
        Assert.Contains(result["errors"]!.AsArray(), error => error!.GetValue<string>().Contains("joined_in_progress", StringComparison.Ordinal));
        Assert.Throws<CollectorException>(() => Commit(service, preview));
        Assert.Equal(0, new RunRepository(fixture.Database).Query(null, null, 1, 20).Total);
    }

    public static IEnumerable<object[]> OtherBooleanTexts() =>
        from field in new[] { "mentor_confirmed", "contributes_to_goal", "soft_deleted" }
        from text in new[] { "正常", "开局进入", "中途", "中途加入" }
        select new object[] { field, text };

    [Theory]
    [MemberData(nameof(OtherBooleanTexts))]
    public void EntryModeVocabularyDoesNotLeakIntoOtherBooleanFields(string field, string text)
    {
        using var fixture = new TestDatabase();
        var row = Candidate(); row[field] = text;
        var preview = new RunImportService(fixture.Database, fixture.Clock).PreviewSource("ROWS", rows: new JsonArray(row));
        var result = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.False(result["can_import"]!.GetValue<bool>());
        Assert.Contains(result["errors"]!.AsArray(), error => error!.GetValue<string>().Contains(field, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("进入方式")]
    [InlineData("进本方式")]
    [InlineData("joined_in_progress")]
    [InlineData("我的进本标记")]
    public void HeaderMappingRevalidationCommitRetryAndNativeRoundTripsPreserveEntry(string header)
    {
        using var fixture = new TestDatabase();
        var mapping = header == "我的进本标记" ? new Dictionary<string, string> { [header] = "joined_in_progress" } : null;
        var csv = "副本,职业,记录时间," + header + "\n"
            + "水晶塔 古代人迷宫,骑士,2026-09-01,正常\n"
            + "水晶塔 古代人迷宫,骑士,2026-09-02,中途\n"
            + "水晶塔 古代人迷宫,骑士,2026-09-03,\n";
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("CSV", text: csv, columnMapping: mapping);
        var expected = new bool?[] { false, true, null };
        AssertEntryRows(preview, expected);
        var candidates = new JsonArray(preview["rows"]!.AsArray().Select(row => row!["candidate"]!.DeepClone()).ToArray());
        var checkedPreview = service.PreviewSource("CSV", rows: candidates);
        AssertEntryRows(checkedPreview, expected);
        var requestId = Guid.NewGuid().ToString("D");
        var committed = Commit(service, checkedPreview, requestId);
        Assert.Equal(3, committed["imported_count"]!.GetValue<int>());
        Assert.True(Commit(service, checkedPreview, requestId)["replayed"]!.GetValue<bool>());
        var runs = new RunRepository(fixture.Database);
        var stored = committed["run_ids"]!.AsArray().Select(id => runs.Get(id!.GetValue<string>())!).ToArray();
        Assert.Equal(expected, stored.Select(run => run.JoinedInProgress));
        Assert.Equal(1, runs.Query(new RunFilter { JoinedInProgress = false }, null, 1, 20).Total);
        Assert.Equal(1, runs.Query(new RunFilter { JoinedInProgress = true }, null, 1, 20).Total);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock); settings.EnsureDefaults();
        var stats = new StatisticsRepository(fixture.Database, settings).GetDashboard();
        Assert.Equal(3, stats.AttemptCount); Assert.Equal(1, stats.JoinedInProgressCount); Assert.Equal(0.5, stats.JoinedInProgressRate);
        Assert.All(service.PreviewSource("CSV", text: csv, columnMapping: mapping)["rows"]!.AsArray(),
            row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        foreach (var kind in new[] { "JSON", "CSV" })
        {
            var path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "native-entry." + kind.ToLowerInvariant());
            var exporter = new RunExporter(runs, fixture.Clock, fixture.Database);
            if (kind == "JSON") exporter.ExportJson(path, null, false); else exporter.ExportCsv(path, null, false);
            using var target = new TestDatabase();
            var targetService = new RunImportService(target.Database, target.Clock);
            var nativePreview = targetService.PreviewSource(kind, filePath: path, timeZone: "+08:00");
            var nativeCommit = Commit(targetService, nativePreview);
            Assert.Equal(3, nativeCommit["imported_count"]!.GetValue<int>());
            var copied = new RunRepository(target.Database).Query(null, null, 1, 20).Items;
            Assert.Single(copied, run => run.JoinedInProgress == false);
            Assert.Single(copied, run => run.JoinedInProgress == true);
            Assert.Single(copied, run => run.JoinedInProgress is null);
        }
    }

    private static JsonObject Candidate() => new()
    {
        ["duty_name"] = "合成进入方式副本", ["job_id"] = 19,
        ["result"] = "COMPLETED", ["source_recorded_at"] = "2026-09-04",
    };

    private static JsonObject Commit(RunImportService service, JsonObject preview, string? requestId = null) =>
        service.Commit(preview["preview_id"]!.GetValue<string>(), preview["rows"]!.AsArray()
            .Select(row => row!["row_number"]!.GetValue<int>()).ToArray(), true, requestId ?? Guid.NewGuid().ToString("D"));

    private static void AssertEntryRows(JsonObject preview, IEnumerable<bool?> expected)
    {
        Assert.All(preview["rows"]!.AsArray(), row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        Assert.Equal(expected, preview["rows"]!.AsArray().Select(row => row!["run"]!["joined_in_progress"]?.GetValue<bool>()));
    }
}
