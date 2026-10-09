using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ImportedJsonExportRoundTripTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeJsonPreservesMentorConfirmationAndStatisticsAcrossDatabases(bool mentorConfirmed)
    {
        using var source = new TestDatabase();
        using var target = new TestDatabase();
        var original = ImportCompleteRun(source, mentorConfirmed);
        Assert.False(original.IsIncompleteImport);
        Assert.Equal(mentorConfirmed, original.ImportMetadata!.MentorConfirmed);
        var originalStatistics = Dashboard(source);
        var expectedCount = mentorConfirmed ? 1 : 0;
        Assert.Equal(expectedCount, originalStatistics.CompletedCount);
        Assert.Equal(expectedCount, originalStatistics.AchievementProgress);

        var exportPath = Export(source);
        var exported = JsonNode.Parse(File.ReadAllText(exportPath))!.AsArray();
        Assert.Equal(mentorConfirmed, exported[0]!["import_metadata"]!["mentor_confirmed"]!.GetValue<bool>());

        var imported = ImportFile(target, exportPath);
        Assert.Equal(original.RunId, imported.RunId);
        Assert.False(imported.IsIncompleteImport);
        Assert.Equal(mentorConfirmed, imported.ImportMetadata!.MentorConfirmed);
        var importedStatistics = Dashboard(target);
        Assert.Equal(originalStatistics.CompletedCount, importedStatistics.CompletedCount);
        Assert.Equal(originalStatistics.AchievementProgress, importedStatistics.AchievementProgress);
    }

    [Fact]
    public void LegacyNativeJsonWithoutMentorConfirmationKeepsExistingTrueDefault()
    {
        using var source = new TestDatabase();
        using var target = new TestDatabase();
        ImportCompleteRun(source, mentorConfirmed: true);
        var exportPath = Export(source);
        var exported = JsonNode.Parse(File.ReadAllText(exportPath))!.AsArray();
        Assert.True(exported[0]!["import_metadata"]!.AsObject().Remove("mentor_confirmed"));
        File.WriteAllText(exportPath, exported.ToJsonString(Wire.IndentedJsonOptions));

        var imported = ImportFile(target, exportPath);
        Assert.True(imported.ImportMetadata!.MentorConfirmed);
        Assert.False(imported.IsIncompleteImport);
        var statistics = Dashboard(target);
        Assert.Equal(1, statistics.CompletedCount);
        Assert.Equal(1, statistics.AchievementProgress);
    }

    private static MentorRun ImportCompleteRun(TestDatabase fixture, bool mentorConfirmed)
    {
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var candidate = new JsonObject
        {
            ["duty_name"] = "合成 JSON 往返副本",
            ["job_id"] = 19,
            ["matched_at_utc"] = "2026-09-04T00:59:50.000Z",
            ["entered_at_utc"] = "2026-09-04T01:00:00.000Z",
            ["ended_at_utc"] = "2026-09-04T01:02:00.000Z",
            ["result"] = "COMPLETED",
            ["mentor_confirmed"] = mentorConfirmed,
        };
        return Commit(fixture, service, service.PreviewSource("ROWS", rows: new JsonArray(candidate)));
    }

    private static MentorRun ImportFile(TestDatabase fixture, string exportPath)
    {
        var service = new RunImportService(fixture.Database, fixture.Clock);
        return Commit(fixture, service, service.PreviewSource("JSON", filePath: exportPath));
    }

    private static MentorRun Commit(TestDatabase fixture, RunImportService service, JsonObject preview)
    {
        var result = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 1 },
            confirmOwnRecords: true, requestId: Guid.NewGuid().ToString("D"));
        Assert.Equal(1, result["imported_count"]!.GetValue<int>());
        var runId = result["run_ids"]![0]!.GetValue<string>();
        return new RunRepository(fixture.Database).Get(runId)!;
    }

    private static string Export(TestDatabase fixture)
    {
        var exportPath = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "native-import-history.json");
        var exporter = new RunExporter(new RunRepository(fixture.Database), fixture.Clock, fixture.Database);
        Assert.Equal(1, exporter.ExportJson(exportPath, filter: null, overwrite: false).RowCount);
        return exportPath;
    }

    private static DashboardStatistics Dashboard(TestDatabase fixture)
    {
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        return new StatisticsRepository(fixture.Database, settings, clock: fixture.Clock).GetDashboard();
    }
}
