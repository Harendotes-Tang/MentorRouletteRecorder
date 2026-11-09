using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// CSV and JSON export content, local destinations, and backup retention.
/// </summary>
public sealed class ExportTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly RunRepository _runs;
    private readonly RunExporter _exporter;
    private readonly string _outputDirectory;

    public ExportTests()
    {
        _runs = new RunRepository(_database.Database);
        _exporter = new RunExporter(_runs, _database.Clock);

        // A checkout on another drive must support real exports outside the user profile.
        _outputDirectory = Path.Combine(
            AppContext.BaseDirectory, "MentorRecorder.ExportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDirectory);
    }

    private string Target(string name) => Path.Combine(_outputDirectory, name);

    private void Seed(params MentorRun[] runs) =>
        _database.Database.RunInTransaction(tx =>
        {
            foreach (var run in runs)
            {
                _runs.Insert(run, tx);
            }
        });

    [Fact]
    public void ExportCsv_WritesTheFixedHeaderWithAByteOrderMark()
    {
        Seed(TestDatabase.Run());
        var path = Target("runs.csv");

        var outcome = _exporter.ExportCsv(path, null, overwrite: false);

        var bytes = File.ReadAllBytes(outcome.TargetPath);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

        var text = Encoding.UTF8.GetString(bytes.Skip(3).ToArray());
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(string.Join(',', RunExporter.CsvHeader), lines[0]);
        Assert.Equal(1, outcome.RowCount);
        Assert.Equal(bytes.Length, outcome.ByteCount);
    }

    [Fact]
    public void ExportCsv_WritesEveryColumnInOrder()
    {
        var run = TestDatabase.Run(result: RunResult.Completed, durationMs: 1_500_000);
        Seed(run);

        var outcome = _exporter.ExportCsv(Target("one.csv"), null, overwrite: false);

        var line = File.ReadAllLines(outcome.TargetPath)[1];
        var cells = line.Split(',');
        Assert.Equal("2026-09-04", cells[0]);
        Assert.Equal("2026-09-04T00:59:50.000Z", cells[1]);
        Assert.Equal("2026-09-04T01:00:00.000Z", cells[2]);
        Assert.Equal("2026-09-04T01:25:00.000Z", cells[3]);
        Assert.Equal("样例迷宫挑战 A", cells[4]);
        Assert.Equal("迷宫挑战", cells[5]);
        Assert.Equal("骑士", cells[6]);
        Assert.Equal("COMPLETED", cells[7]);
        Assert.Equal("1500000", cells[8]);
        Assert.Equal("MANUAL", cells[9]);
        Assert.Equal("0", cells[10]);
        Assert.Equal(run.RunId, cells[11]);
        Assert.Equal("900001", cells[12]);
        Assert.Equal("19", cells[13]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("has\nnewline", "\"has\nnewline\"")]
    public void CsvEscaping_FollowsRfc4180(string raw, string expected) =>
        Assert.Equal(expected, RunExporter.Escape(raw));

    [Fact]
    public void ExportCsv_QuotesADutyNameContainingASeparator()
    {
        Seed(TestDatabase.Run() with { DutyName = "副本, 带逗号" });

        var outcome = _exporter.ExportCsv(Target("quoted.csv"), null, overwrite: false);

        Assert.Contains(
            "\"副本, 带逗号\"",
            File.ReadAllText(outcome.TargetPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExportJson_WritesRunObjectsMatchingTheContract()
    {
        var run = TestDatabase.Run();
        Seed(run);

        var outcome = _exporter.ExportJson(Target("runs.json"), null, overwrite: false);

        var array = JsonNode.Parse(File.ReadAllText(outcome.TargetPath))!.AsArray();
        var item = array.Single()!.AsObject();
        Assert.Equal(run.RunId, item["run_id"]!.GetValue<string>());
        Assert.Equal("COMPLETED", item["result"]!.GetValue<string>());
        Assert.Equal("MANUAL", item["source"]!.GetValue<string>());
        Assert.False(item["soft_deleted"]!.GetValue<bool>());
        Assert.True(item["manually_created"]!.GetValue<bool>());
        Assert.Equal(1, item["revision"]!.GetValue<int>());
    }

    [Fact]
    public void Export_HonoursTheSameFilterAsQueryRuns()
    {
        Seed(
            TestDatabase.Run(result: RunResult.Completed),
            TestDatabase.Run(result: RunResult.LeftOrAbandoned),
            TestDatabase.Run(result: RunResult.Completed, softDeleted: true));

        var completedOnly = _exporter.ExportCsv(
            Target("filtered.csv"),
            new RunFilter { Results = new[] { RunResult.Completed } },
            overwrite: false);

        Assert.Equal(1, completedOnly.RowCount);
    }

    [Theory]
    [InlineData("CSV")]
    [InlineData("JSON")]
    public void RegionalExportsRoundTripWithoutCrossRegionDuplicates(string format)
    {
        var cn = TestDatabase.Run() with { Region = Region.Cn, GameBuild = "same-build" };
        var tc = TestDatabase.Run() with { Region = Region.Tc, GameBuild = "same-build" };
        Seed(cn, tc);
        var path = Target("regional." + format.ToLowerInvariant());
        if (format == "CSV") _exporter.ExportCsv(path, null, false);
        else _exporter.ExportJson(path, null, false);
        using var target = new TestDatabase();
        var importer = new RunImportService(target.Database, target.Clock);
        var preview = importer.PreviewSource(format, filePath: path);
        Assert.All(preview["rows"]!.AsArray(), row => Assert.Equal("new", row!["status"]!.GetValue<string>()));
        var response = importer.Commit(preview["preview_id"]!.GetValue<string>(),
            preview["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(),
            true, Guid.NewGuid().ToString("D"));
        Assert.Equal(2, response["imported_count"]!.GetValue<int>());
        var stored = new RunRepository(target.Database).Query(null, null, 1, 50).Items;
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, run => run.Region == Region.Cn && run.GameBuild == "same-build");
        Assert.Contains(stored, run => run.Region == Region.Tc && run.GameBuild == "same-build");
        var filtered = Target("tc-only." + format.ToLowerInvariant());
        var result = format == "CSV" ? _exporter.ExportCsv(filtered, new RunFilter { Region = Region.Tc }, false)
            : _exporter.ExportJson(filtered, new RunFilter { Region = Region.Tc }, false);
        Assert.Equal(1, result.RowCount);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"local\")")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1)")]
    [InlineData("\t=SUM(1)")]
    [InlineData("\r=SUM(1)")]
    public void GameBuildIsFormulaProtectedAndNativeCsvRestoresItsOriginalText(string build)
    {
        Seed(TestDatabase.Run() with { Region = Region.Tc, GameBuild = build });
        var path = Target("build.csv");
        _exporter.ExportCsv(path, null, false);
        Assert.Contains(RunExporter.Escape(build), File.ReadAllText(path), StringComparison.Ordinal);
        var parsed = Assert.Single(RunImportSourceParser.Parse("CSV", path));
        Assert.Equal(build, parsed.Values["game_build"]!.GetValue<string>());
        using var target = new TestDatabase();
        var preview = new RunImportService(target.Database, target.Clock).PreviewSource("CSV", filePath: path);
        Assert.Equal(build, preview["rows"]![0]!["run"]!["game_build"]!.GetValue<string>());
    }

    private static readonly int RunIdColumn = RunExporter.CsvHeader.ToList().IndexOf("run_id");

    private (MentorRun Earliest, MentorRun Middle, MentorRun Latest) SeedThreeDays()
    {
        var earliest = TestDatabase.Run(enteredAt: new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero));
        var middle = TestDatabase.Run(enteredAt: new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero));
        var latest = TestDatabase.Run(enteredAt: new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero));

        // Inserted out of order on purpose: the file's order must come from the sort, not
        // from insertion.
        Seed(middle, latest, earliest);
        return (earliest, middle, latest);
    }

    private static string[] CsvRunIds(string path) =>
        File.ReadAllLines(path).Skip(1).Select(line => line.Split(',')[RunIdColumn]).ToArray();

    private static string[] JsonRunIds(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsArray()
            .Select(item => item!["run_id"]!.GetValue<string>()).ToArray();

    /// <summary>
    /// contracts/CHANGELOG.md 2026-11-03: a request without <c>sort</c> is written the way every
    /// export before the field was: newest entered first.
    /// </summary>
    [Fact]
    public void Export_WithoutASortKeepsNewestEnteredFirst()
    {
        var (earliest, middle, latest) = SeedThreeDays();
        var expected = new[] { latest.RunId, middle.RunId, earliest.RunId };

        var csv = _exporter.ExportCsv(Target("default.csv"), null, overwrite: false);
        var json = _exporter.ExportJson(Target("default.json"), null, overwrite: false);

        Assert.Equal(expected, CsvRunIds(csv.TargetPath));
        Assert.Equal(expected, JsonRunIds(json.TargetPath));
    }

    /// <summary>
    /// The history list switched to 最早在前 sends history_date ascending; the first data row of
    /// the file is then the earliest run, in CSV and in JSON alike.
    /// </summary>
    [Fact]
    public void Export_AscendingHistoryDatePutsTheEarliestRunFirst()
    {
        var (earliest, middle, latest) = SeedThreeDays();
        var earliestFirst = new RunSort(RunSortField.HistoryDate, SortDirection.Asc);
        var expected = new[] { earliest.RunId, middle.RunId, latest.RunId };

        var csv = _exporter.ExportCsv(Target("asc.csv"), null, overwrite: false, earliestFirst);
        var json = _exporter.ExportJson(Target("asc.json"), null, overwrite: false, earliestFirst);

        Assert.Equal(expected, CsvRunIds(csv.TargetPath));
        Assert.Equal(expected, JsonRunIds(json.TargetPath));
        Assert.Equal(earliest.RunId, CsvRunIds(csv.TargetPath)[0]);
    }

    /// <summary>The sort is applied with the filter, and a descending history_date is explicit too.</summary>
    [Fact]
    public void Export_SortsTheFilteredRowsOnly()
    {
        var (earliest, _, latest) = SeedThreeDays();
        var selected = new RunFilter { RunIds = new[] { earliest.RunId, latest.RunId } };

        var ascending = _exporter.ExportCsv(
            Target("selected-asc.csv"), selected, overwrite: false,
            new RunSort(RunSortField.HistoryDate, SortDirection.Asc));
        var descending = _exporter.ExportCsv(
            Target("selected-desc.csv"), selected, overwrite: false,
            new RunSort(RunSortField.HistoryDate, SortDirection.Desc));

        Assert.Equal(new[] { earliest.RunId, latest.RunId }, CsvRunIds(ascending.TargetPath));
        Assert.Equal(new[] { latest.RunId, earliest.RunId }, CsvRunIds(descending.TargetPath));
    }

    [Fact]
    public void Export_RefusesANetworkDestination()
    {
        var error = Assert.Throws<CollectorException>(() =>
            _exporter.ExportCsv(@"\\export-server\share\runs.csv", null, overwrite: false));

        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
    }

    [Fact]
    public void Export_RefusesToOverwriteUnlessAsked()
    {
        Seed(TestDatabase.Run());
        var path = Target("twice.csv");
        _exporter.ExportCsv(path, null, overwrite: false);
        var original = File.ReadAllBytes(path);

        var error = Assert.Throws<CollectorException>(() =>
            _exporter.ExportCsv(path, null, overwrite: false));
        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
        Assert.Equal(original, File.ReadAllBytes(path));

        var second = _exporter.ExportCsv(path, null, overwrite: true);
        Assert.Equal(path, second.TargetPath);
    }

    [Fact]
    public void Export_ReportsAnUnwritableParentWithoutChangingItsContents()
    {
        var parent = Target("ordinary-file");
        File.WriteAllText(parent, "keep");

        var error = Assert.Throws<CollectorException>(() =>
            _exporter.ExportCsv(Path.Combine(parent, "runs.csv"), null, overwrite: false));

        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
        Assert.Equal("keep", File.ReadAllText(parent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("directory")]
    [InlineData("file")]
    public void BackupDatabase_ResolvesDefaultDirectoryAndExplicitFileTargets(string? targetKind)
    {
        var service = new BackupService(_database.Database, _database.Clock);
        var fileName = BackupService.FileNameFor(_database.Clock.UtcNow);
        var target = targetKind switch
        {
            "directory" => _outputDirectory,
            "file" => Target("chosen.db"),
            _ => targetKind,
        };
        var expected = targetKind switch
        {
            "directory" => Target(fileName),
            "file" => Target("chosen.db"),
            _ => Path.Combine(service.DefaultDirectory, fileName),
        };

        var result = service.CreateBackup(target);

        Assert.Equal(expected, result.TargetPath);
        Assert.True(File.Exists(expected));
        Assert.True(result.IntegrityCheckPassed);
    }

    [Fact]
    public void BackupDatabase_WritesAVerifiedCopyAndKeepsTheNewestFourteen()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        var directory = service.DefaultDirectory;
        Directory.CreateDirectory(directory);

        // Sixteen stale files plus the one this call writes must leave exactly fourteen.
        for (var index = 0; index < 16; index++)
        {
            File.WriteAllText(
                Path.Combine(directory, BackupService.FileNameFor(
                    new DateTimeOffset(2026, 1, 1, 0, 0, index, TimeSpan.Zero))),
                "stale");
        }

        var result = service.CreateBackup();

        Assert.True(File.Exists(result.TargetPath));
        Assert.True(result.ByteCount > 0);
        Assert.Equal(
            BackupService.RetainedBackups,
            Directory.GetFiles(directory, BackupService.FileNamePrefix + "*.db").Length);
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Test debris under AppContext.BaseDirectory; a locked file is left for the next run to remove.
        }
    }
}
