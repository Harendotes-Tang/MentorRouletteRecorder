using System.Text.Json.Nodes;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Audit 2026-10-20 H2-1 and I3-1: importing the same personal records again must not import them twice.
/// A row's identity is its own content, not the whole file, so appending rows to a spreadsheet leaves the
/// earlier rows recognisable; rows that may be the same run are offered as possible duplicates, never as new.
/// </summary>
public sealed class RunImportReimportTests
{
    private const string Duty = "异界孤城特罗亚宫廷";

    private static JsonObject Row(JsonObject preview, int index) => preview["rows"]![index]!.AsObject();
    private static string Status(JsonObject preview, int index) => Row(preview, index)["status"]!.GetValue<string>();

    private static JsonObject CommitAll(RunImportService service, JsonObject preview) =>
        service.Commit(preview["preview_id"]!.GetValue<string>(),
            preview["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(), true, Guid.NewGuid().ToString("D"));

    private static JsonObject PreviewCsv(RunImportService service, string path, string text)
    {
        File.WriteAllText(path, text);
        return service.PreviewSource("CSV", filePath: path, timeZone: "+08:00", sourceName: Path.GetFileName(path));
    }

    private static string CsvPath(TestDatabase fixture, string name = "personal-records.csv") =>
        Path.Combine(Path.GetDirectoryName(fixture.Path)!, name);

    [Fact]
    public void ReimportingASpreadsheetWithAppendedRowsSkipsTheRowsAlreadyImported()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var path = CsvPath(fixture);
        const string header = "副本,职业,开始时间,结果\n";
        var first = PreviewCsv(service, path, header + Duty + ",战士,2026/9/1,通关\n" + Duty + ",战士,2026/9/2,通关\n");
        Assert.Equal(new[] { "new", "new" }, new[] { Status(first, 0), Status(first, 1) });
        Assert.Equal(2, CommitAll(service, first)["imported_count"]!.GetValue<int>());

        var appended = PreviewCsv(service, path, header + Duty + ",战士,2026/9/1,通关\n" + Duty + ",战士,2026/9/2,通关\n"
            + Duty + ",战士,2026/9/3,通关\n");
        Assert.Equal("duplicate", Status(appended, 0));
        Assert.Equal("duplicate", Status(appended, 1));
        Assert.Equal("new", Status(appended, 2));
        Assert.False(Row(appended, 0)["can_import"]!.GetValue<bool>());
    }

    [Fact]
    public void IdenticalRowsKeepTheirOwnIdentityWhereverTheyMoveInTheFile()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var path = CsvPath(fixture);
        const string header = "副本,职业,开始时间,结果\n";
        const string twice = Duty + ",战士,2026/9/1,通关\n";
        Assert.Equal(2, CommitAll(service, PreviewCsv(service, path, header + twice + twice))["imported_count"]!.GetValue<int>());

        var reordered = PreviewCsv(service, path, header + Duty + ",骑士,2026/9/5,通关\n" + twice + twice);
        Assert.Equal("new", Status(reordered, 0));
        Assert.Equal("duplicate", Status(reordered, 1));
        Assert.Equal("duplicate", Status(reordered, 2));

        // A third identical row is a run the library may already hold: offered, never pre-judged new.
        var third = PreviewCsv(service, path, header + twice + twice + twice);
        Assert.Equal("duplicate", Status(third, 0));
        Assert.Equal("duplicate", Status(third, 1));
        Assert.Equal("possible_duplicate", Status(third, 2));
        Assert.True(Row(third, 2)["can_import"]!.GetValue<bool>());
    }

    [Fact]
    public void SameDutySameSourceDaySameJobIsAPossibleDuplicateWhenEitherSideHasNoClockTime()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var stored = CommitAll(service, PreviewCsv(service, CsvPath(fixture, "old.csv"),
            "副本,职业,开始时间,结果\n" + Duty + ",战士,2026/9/1,通关\n"));
        var storedId = stored["run_ids"]![0]!.GetValue<string>();

        JsonObject Paste(string text) => service.PreviewSource("PASTE", text: text, timeZone: "+08:00", sourceName: "粘贴的表格");
        var sameDay = Paste("副本\t职业\t开始时间\n" + Duty + "\t战士\t2026-09-01");
        Assert.Equal("possible_duplicate", Status(sameDay, 0));
        Assert.Equal(storedId, Row(sameDay, 0)["matching_run_id"]!.GetValue<string>());
        Assert.Equal("possible_duplicate", Status(Paste("副本\t职业\t记录时间\n" + Duty + "\t战士\t2026-09-01 20:30"), 0));
        Assert.Equal("possible_duplicate", Status(Paste("副本\t开始时间\n" + Duty + "\t2026.9.1"), 0));
        Assert.Equal("new", Status(Paste("副本\t职业\t开始时间\n" + Duty + "\t骑士\t2026-09-01"), 0));
        Assert.Equal("new", Status(Paste("副本\t职业\t开始时间\n" + Duty + "\t战士\t2026-09-02"), 0));
        Assert.Equal("new", Status(Paste("副本\t职业\t开始时间\n" + "合成测试副本" + "\t战士\t2026-09-01"), 0));
    }

    [Fact]
    public void TwoClockTimesOnTheSameDayAreDifferentRuns()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        CommitAll(service, PreviewCsv(service, CsvPath(fixture, "old.csv"),
            "副本,职业,记录时间,结果\n" + Duty + ",战士,2026-09-01 20:30,通关\n"));
        var later = service.PreviewSource("PASTE", text: "副本\t职业\t记录时间\n" + Duty + "\t战士\t2026-09-01 22:10",
            timeZone: "+08:00", sourceName: "粘贴的表格");
        Assert.Equal("new", Status(later, 0));
    }

    /// <summary>
    /// A row imported by an earlier version carries the fingerprint of the whole file it came from plus its
    /// row number. Re-importing that same file must still find it, and must still honour a purge of it.
    /// </summary>
    [Fact]
    public void RowsImportedUnderTheEarlierWholeFileIdentityStillMatchTheSameFile()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var path = CsvPath(fixture);
        const string text = "副本,职业,开始时间,结果\n" + Duty + ",战士,2026/9/1,通关\n" + Duty + ",骑士,2026/9/2,通关\n";
        var committed = CommitAll(service, PreviewCsv(service, path, text));
        var parsed = RunImportSourceParser.Parse("CSV", path);
        var wholeFile = RunImportCandidateNormalizer.Hash(
            new JsonArray(parsed.Select(row => (JsonNode)row.Values.DeepClone()).ToArray()).ToJsonString(Wire.JsonOptions));
        var runs = new RunRepository(fixture.Database);
        var earlier = new List<string>();
        foreach (var outcome in committed["rows"]!.AsArray())
        {
            var runId = outcome!["run_id"]!.GetValue<string>();
            var legacy = RunImportCandidateNormalizer.Hash(string.Join('\n', "CSV", "personal-records.csv",
                wholeFile + ":" + outcome["row_number"]!.GetValue<int>(), RunImportCandidateNormalizer.Facts(runs.Get(runId)!)));
            earlier.Add(legacy);
            using var command = fixture.Database.CreateCommand();
            command.CommandText = "UPDATE run_import_metadata SET source_fingerprint = $fingerprint WHERE run_id = $id;";
            command.Parameters.AddWithValue("$fingerprint", legacy);
            command.Parameters.AddWithValue("$id", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var again = PreviewCsv(service, path, text);
        Assert.Equal("duplicate", Status(again, 0));
        Assert.Equal("duplicate", Status(again, 1));

        // The second row as if it had been purged: its stored fingerprint survives only in the tombstone.
        using (var command = fixture.Database.CreateCommand())
        {
            command.CommandText = "UPDATE run_import_metadata SET source_fingerprint = 'elsewhere' WHERE source_fingerprint = $fingerprint;" +
                "INSERT INTO purged_run_tombstones VALUES ($id, $fingerprint, '2026-09-04T00:00:00.000Z');";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$fingerprint", earlier[1]);
            command.ExecuteNonQuery();
        }
        Assert.Equal("conflict", Status(PreviewCsv(service, path, text), 1));
    }

    /// <summary>
    /// Re-review 2026-10-21, RV5-3. 1.7.3 left a 职业 cell such as 白魔 unresolved (job_id null); the approved Chinese aliases
    /// now resolve it, and job_id is one of the facts a fingerprint hashes. A row imported, or imported and purged, under
    /// 1.7.3 must still be recognised when the same file comes back: duplicate when it is stored, conflict when only its
    /// tombstone is left - never new and ticked. A row whose job resolved the same way under 1.7.3 is unchanged.
    /// </summary>
    [Fact]
    public void ARowWhoseJobOnlyAnAliasNamesStillMatchesWhatTheEarlierVersionImportedOrPurged()
    {
        const string text = "副本,职业,开始时间,结果\n" + Duty + ",白魔,2026/9/1,通关\n" + Duty + ",战士,2026/9/2,通关\n";
        using var stored = new TestDatabase();
        var service = new RunImportService(stored.Database, stored.Clock);
        var path = CsvPath(stored);
        var committed = CommitAll(service, PreviewCsv(service, path, text));
        var parsed = RunImportSourceParser.Parse("CSV", path);
        var wholeFile = RunImportCandidateNormalizer.Hash(
            new JsonArray(parsed.Select(row => (JsonNode)row.Values.DeepClone()).ToArray()).ToJsonString(Wire.JsonOptions));
        var runs = new RunRepository(stored.Database);
        var earlier = new List<string>();
        foreach (var outcome in committed["rows"]!.AsArray())
        {
            var runId = outcome!["run_id"]!.GetValue<string>();
            var run = runs.Get(runId)!;
            Assert.NotNull(run.JobId);
            // What 1.7.3 recorded: the alias named no job, the full name 战士 did.
            var asBefore = run.JobName == "白魔" ? run with { JobId = null } : run;
            var legacy = RunImportCandidateNormalizer.Hash(string.Join('\n', "CSV", "personal-records.csv",
                wholeFile + ":" + outcome["row_number"]!.GetValue<int>(), RunImportCandidateNormalizer.Facts(asBefore)));
            earlier.Add(legacy);
            using var command = stored.Database.CreateCommand();
            command.CommandText = "UPDATE run_import_metadata SET source_fingerprint = $fingerprint WHERE run_id = $id;";
            command.Parameters.AddWithValue("$fingerprint", legacy);
            command.Parameters.AddWithValue("$id", runId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var again = PreviewCsv(service, path, text);
        Assert.Equal("duplicate", Status(again, 0));
        Assert.Equal("duplicate", Status(again, 1));

        // Both rows imported by 1.7.3 and purged since: only their tombstones are left.
        using var purged = new TestDatabase();
        foreach (var fingerprint in earlier)
        {
            using var command = purged.Database.CreateCommand();
            command.CommandText = "INSERT INTO purged_run_tombstones VALUES ($id, $fingerprint, '2026-09-04T00:00:00.000Z');";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.ExecuteNonQuery();
        }
        var other = new RunImportService(purged.Database, purged.Clock);
        var offered = PreviewCsv(other, CsvPath(purged), text);
        Assert.Equal("conflict", Status(offered, 0));
        Assert.Equal("conflict", Status(offered, 1));
        Assert.False(Row(offered, 0)["can_import"]!.GetValue<bool>());
    }

    private static JsonObject Card(string text = "合成截图心得", string time = "2026-10-08 20:30:00", string job = "战士") => new()
    {
        ["duty_name"] = Duty, ["job_name"] = job, ["reflection_text"] = text, ["source_recorded_at"] = time, ["result"] = "COMPLETED",
    };

    /// <summary>
    /// I3-1: the same card twice in one batch - two overlapping screenshots, or one picture pasted twice - is
    /// offered once as new and once as a possible duplicate of it, so it is not imported twice by default.
    /// </summary>
    [Fact]
    public void TheSameCardTwiceInOneBatchIsOfferedOnceAsNew()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var overlapping = Card(job: "骑士");
        overlapping["warnings"] = new JsonArray("职业是按图标推测的");
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(Card(), Card("另一条心得"), Card(), overlapping,
            Card(time: "2026-10-08 21:30:00")), timeZone: "+08:00", sourceName: "合成截图 等 2 张截图");
        Assert.Equal("new", Status(preview, 0));
        Assert.Equal("new", Status(preview, 1));
        Assert.Equal("possible_duplicate", Status(preview, 2));
        Assert.Equal("possible_duplicate", Status(preview, 3));
        Assert.Equal("new", Status(preview, 4));
        Assert.Equal(Row(preview, 0)["run"]!["run_id"]!.GetValue<string>(), Row(preview, 2)["matching_run_id"]!.GetValue<string>());
        Assert.True(Row(preview, 2)["can_import"]!.GetValue<bool>());
        Assert.Equal(2, preview["summary"]!["possible_duplicate"]!.GetValue<int>());
    }

    /// <summary>Rows with no clock time are not twins by their day: two runs of one duty on one day are ordinary.</summary>
    [Fact]
    public void DateOnlyRowsOfOneDayInOneBatchStayNew()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = PreviewCsv(service, CsvPath(fixture),
            "副本,职业,开始时间,结果\n" + Duty + ",战士,2026/9/1,通关\n" + Duty + ",战士,2026/9/1,通关\n");
        Assert.Equal("new", Status(preview, 0));
        Assert.Equal("new", Status(preview, 1));
    }
}
