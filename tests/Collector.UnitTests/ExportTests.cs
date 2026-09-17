using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
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
        Assert.True(result.IntegrityCheckPassed);
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
