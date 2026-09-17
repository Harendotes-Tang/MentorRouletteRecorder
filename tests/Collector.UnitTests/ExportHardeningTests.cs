using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// What an export must refuse to do to files it did not create.
///
/// Every case is the same rule in a different place: a path the user typed is not the
/// exporter's property. A spreadsheet cell that starts a formula, a retention sweep in a
/// folder the user chose and an unconditional overwrite of a destination the user named can
/// each destroy something the user cannot get back (review findings M5, M6, H3).
/// </summary>
public sealed class ExportHardeningTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 4, 13, 45, 0, TimeSpan.Zero));
    private readonly string _directory;

    public ExportHardeningTests()
    {
        _directory = Path.Combine(
            AppContext.BaseDirectory, "MentorRecorder.ExportHardening", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    // ---- M5: CSV formula injection -------------------------------------------------

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"click\")")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tleading tab")]
    [InlineData("\rleading return")]
    public void ACellThatASpreadsheetWouldExecuteIsNeutralised(string value)
    {
        var escaped = RunExporter.Escape(value);

        Assert.StartsWith("\"'", escaped, StringComparison.Ordinal);
        Assert.EndsWith("\"", escaped, StringComparison.Ordinal);

        // The original text is still there, one quote to its right: the user must be able to
        // read the duty name they recorded.
        Assert.Contains(value.Replace("\"", "\"\"", StringComparison.Ordinal), escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryCellIsLeftExactlyAsItWas()
    {
        Assert.Equal("零式伊甸希望乐园", RunExporter.Escape("零式伊甸希望乐园"));
        Assert.Equal("\"a,b\"", RunExporter.Escape("a,b"));
        Assert.Equal(string.Empty, RunExporter.Escape(string.Empty));
    }

    /// <summary>
    /// Review finding M-10. Neutralisation belongs to free-text columns only. A reflection of
    /// <c>"+1，很顺"</c> is still made safe, but the identifier, timestamp, enum and numeric
    /// columns are this program's own output and must round-trip unchanged.
    /// </summary>
    [Fact]
    public void OnlyFreeTextColumnsAreNeutralisedInARealExport()
    {
        var runs = new RunRepository(_database.Database);
        var reflections = new RunReflectionRepository(_database.Database);
        var run = TestDatabase.Run(durationMs: 1_500_000) with
        {
            DutyName = "=HYPERLINK(\"http://evil\",\"click\")",
        };
        _database.Database.RunInTransaction(tx =>
        {
            runs.Insert(run, tx);
            reflections.Upsert(run.RunId, ReflectionMood.Good, "+1，很顺", _clock.UtcNow, tx);
        });

        var path = Path.Combine(_directory, "columns.csv");
        new RunExporter(runs, _clock).ExportCsv(path, null, overwrite: false);
        var cells = SplitCsvRow(File.ReadAllLines(path)[1]);

        // Free text: neutralised.
        Assert.StartsWith("\"'=HYPERLINK", cells[4], StringComparison.Ordinal);
        Assert.Equal("\"'+1，很顺\"", cells[15]);

        // Everything else: verbatim, with no apostrophe added.
        Assert.Equal("COMPLETED", cells[7]);
        Assert.Equal("1500000", cells[8]);
        Assert.Equal("MANUAL", cells[9]);
        Assert.Equal("0", cells[10]);
        Assert.Equal(run.RunId, cells[11]);
        Assert.Equal("900001", cells[12]);
        Assert.Equal("19", cells[13]);
        Assert.DoesNotContain('\'', cells[0]);
        Assert.DoesNotContain('\'', cells[3]);
    }

    /// <summary>
    /// A negative duration is a number, not text: it starts with a leading minus and must not
    /// be quoted into a string a spreadsheet reads differently from the database.
    /// </summary>
    [Fact]
    public void ANumericColumnIsNeverQuotedForItsLeadingSign()
    {
        Assert.Equal("-5", RunExporter.Escape("-5", freeText: false));
        Assert.Equal("2026-09-04T01:00:00.000Z", RunExporter.Escape("2026-09-04T01:00:00.000Z", freeText: false));
        Assert.Equal("\"'-5\"", RunExporter.Escape("-5"));
    }

    /// <summary>Splits one RFC 4180 row, keeping the quotes so the escaping stays visible.</summary>
    private static IReadOnlyList<string> SplitCsvRow(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                current.Append(c);
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }

    // ---- M6: backup retention stays inside the managed folder ----------------------

    [Fact]
    public void RetentionNeverSweepsAFolderTheUserChose()
    {
        var chosen = Path.Combine(_directory, "chosen");
        Directory.CreateDirectory(chosen);
        SeedStaleBackups(chosen, 20);

        var service = new BackupService(_database.Database, _database.Clock);
        var result = service.CreateBackup(chosen);

        Assert.Equal(0, result.PrunedCount);
        Assert.Equal(21, Directory.GetFiles(chosen, BackupService.FileNamePrefix + "*.db").Length);
    }

    [Fact]
    public void RetentionStillSweepsTheManagedFolder()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        Directory.CreateDirectory(service.DefaultDirectory);
        SeedStaleBackups(service.DefaultDirectory, 20);

        var result = service.CreateBackup();

        Assert.True(result.PrunedCount > 0);
        Assert.Equal(
            BackupService.RetainedBackups,
            Directory.GetFiles(service.DefaultDirectory, BackupService.FileNamePrefix + "*.db").Length);
    }

    /// <summary>
    /// Review finding L-18. An atomic write stages beside its target and deletes on the way
    /// out, but a process that is terminated rather than stopped never gets there, so the
    /// managed folder must sweep abandoned staging files, each the size of the database.
    /// </summary>
    [Fact]
    public void AnAbandonedStagingFileIsSweptFromTheManagedFolderOnly()
    {
        var service = new BackupService(_database.Database, _database.Clock);
        Directory.CreateDirectory(service.DefaultDirectory);
        var stale = StageFile(service.DefaultDirectory, _database.Clock.UtcNow.AddHours(-2));
        var fresh = StageFile(service.DefaultDirectory, _database.Clock.UtcNow.AddMinutes(-5));

        var chosen = Path.Combine(_directory, "chosen-temp");
        Directory.CreateDirectory(chosen);
        var untouched = StageFile(chosen, _database.Clock.UtcNow.AddDays(-3));

        service.CreateBackup();

        Assert.False(File.Exists(stale));

        // A staging file younger than the grace period may still belong to a live export.
        Assert.True(File.Exists(fresh));

        // A folder the user chose is never swept, for the same reason retention is not.
        Assert.True(File.Exists(untouched));
    }

    private string StageFile(string directory, DateTimeOffset lastWrite)
    {
        var path = Path.Combine(
            directory, BackupService.TemporaryFilePrefix + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(path, "abandoned");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    /// <summary>
    /// Review finding L-16. The default evidence path must follow <c>MR_DATA_DIR</c> as the
    /// backups and the logs do; reading %LOCALAPPDATA% directly would write a research export
    /// into the real user's folder from an isolated harness. The timestamp uses the invariant
    /// calendar so the file name does not change with the system's regional settings.
    /// </summary>
    [Fact]
    public void CandidateEvidenceDefaultsToTheDataDirectoryAndAnInvariantTimestamp()
    {
        var root = Path.Combine(_directory, "data-root");
        var previous = Environment.GetEnvironmentVariable(DatabasePaths.DataDirectoryVariable);
        Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, root);
        try
        {
            Assert.Equal(
                Path.Combine(root, "exports"), CandidateEvidenceExporter.DefaultDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DatabasePaths.DataDirectoryVariable, previous);
        }

        var name = CandidateEvidenceExporter.FileNameFor(
            new DateTimeOffset(2026, 9, 8, 21, 5, 4, TimeSpan.Zero));
        Assert.StartsWith("candidate-evidence-20260908-210504-", name, StringComparison.Ordinal);
        Assert.EndsWith(".json", name, StringComparison.Ordinal);
    }

    // ---- H3: a diagnostics report never clobbers a file it did not name -------------

    [Fact]
    public void AReportRefusesToOverwriteAFileItDidNotName()
    {
        var thesis = Path.Combine(_directory, "thesis.docx");
        File.WriteAllText(thesis, "十年心血");

        var error = Assert.Throws<CollectorException>(
            () => NewExport().Write(Snapshot(), "0.2.2", thesis));

        Assert.Equal(ErrorCodes.ExportFailed, error.Code);
        Assert.Equal("十年心血", File.ReadAllText(thesis));
    }

    [Fact]
    public void AnExplicitOverwriteIsHonoured()
    {
        var thesis = Path.Combine(_directory, "thesis.docx");
        File.WriteAllText(thesis, "十年心血");

        var result = NewExport().Write(Snapshot(), "0.2.2", thesis, overwrite: true);

        Assert.Equal(thesis, result.TargetPath);
        Assert.NotEqual("十年心血", File.ReadAllText(thesis));
    }

    [Fact]
    public void TheExportersOwnGeneratedNameIsStillReplacedInPlace()
    {
        // A report is a fresh observation each time. Two reports in the same minute resolve to
        // the same generated name, and refusing the second would be an error the user cannot
        // act on.
        var export = NewExport();
        var first = export.Write(Snapshot(), "0.2.2", _directory);
        var second = export.Write(Snapshot(), "0.2.2", _directory);

        Assert.Equal(first.TargetPath, second.TargetPath);
        Assert.StartsWith(DiagnosticsReportExport.FileNamePrefix, Path.GetFileName(second.TargetPath), StringComparison.Ordinal);
    }

    private DiagnosticsReportExport NewExport() =>
        new(Path.Combine(_directory, "test.db"), _clock);

    private static CaptureDiagnosticsSnapshot Snapshot() => new()
    {
        State = CaptureControllerState.Idle,
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()).Detect(),
        Game = GameProcessDetection.NotRunning,
        Profile = NoProfileStatusProvider.Instance.Current,
    };

    private static void SeedStaleBackups(string directory, int count)
    {
        for (var index = 0; index < count; index++)
        {
            File.WriteAllText(
                Path.Combine(directory, BackupService.FileNameFor(
                    new DateTimeOffset(2026, 1, 1, 0, 0, index, TimeSpan.Zero))),
                "stale");
        }
    }

    public void Dispose()
    {
        _database.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temp debris only.
        }
    }
}
