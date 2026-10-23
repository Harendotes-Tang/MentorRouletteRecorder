using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class DutyCategoryProjectionTests
{
    public static IEnumerable<object[]> StandardAllianceRaids() => new[]
        { 92, 102, 111, 120, 168, 220, 281, 550, 636, 700, 736, 779, 866, 911, 962, 1015, 1058, 1117 }
        .Select(id => new object[] { id });

    [Theory]
    [MemberData(nameof(StandardAllianceRaids))]
    public void DisplayHistoryReclassifiesEveryStandardAllianceWithoutChangingRawFacts(int contentId)
    {
        var duty = DutyCatalog.Default.Find(contentId, Region.Cn)!;
        Assert.Equal("大型任务", duty.DutyCategory);
        var run = TestDatabase.Run(contentId: contentId) with
            { DutyName = duty.LocalizedName, DutyCategory = "大型任务", TerritoryId = duty.TerritoryId };
        var display = Wire.Runs(new Page<MentorRun>(new[] { run }, 1, 20, 1));
        Assert.Equal("团队任务", display["items"]![0]!["duty_category"]!.GetValue<string>());
        Assert.Equal("大型任务", Wire.Run(run)["duty_category"]!.GetValue<string>());
        Assert.Equal("大型任务", run.DutyCategory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("大型任务")]
    public void DisplayProjectsMissingOrLegacyCategory(string? category)
    {
        var run = TestDatabase.Run(contentId: 92) with { DutyCategory = category };
        Assert.Equal("团队任务", Wire.Runs(new Page<MentorRun>(new[] { run }, 1, 20, 1))
            ["items"]![0]!["duty_category"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(92, "玩家自定义")]
    [InlineData(760, "南方战线")]
    [InlineData(721, "采集活动")]
    [InlineData(1010, "团队任务")]
    [InlineData(9999999, "大型任务")]
    public void DisplayPreservesExplicitNonLegacyAndUnrelatedCategories(int contentId, string category)
    {
        var run = TestDatabase.Run(contentId: contentId) with { DutyCategory = category };
        Assert.Equal(category, Wire.Runs(new Page<MentorRun>(new[] { run }, 1, 20, 1))
            ["items"]![0]!["duty_category"]!.GetValue<string>());
    }

    [Fact]
    public void HistoryCountsPagingCalendarAndStatisticsAgreeAndKeepAllAttempts()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        var alliance = TestDatabase.Run(contentId: 92) with { DutyCategory = "大型任务", TerritoryId = 174 };
        var zoneOnly = TestDatabase.Run(contentId: null) with
            { DutyCategory = null, TerritoryId = 174, DutyName = "水晶塔 古代人迷宫" };
        var eight = DutyCatalog.Default.Documents.SelectMany(d => d.Rows.Values)
            .First(d => d.Region == Region.Cn && d.DutyCategory == "大型任务" && d.PartySize == 8);
        var normal = TestDatabase.Run(contentId: eight.ContentId) with { DutyCategory = "大型任务" };
        var custom = alliance with { RunId = Guid.NewGuid().ToString("D"), DutyCategory = "自定义" };
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(alliance, tx); runs.Insert(zoneOnly, tx); runs.Insert(normal, tx); runs.Insert(custom, tx);
        });
        var filter = new RunFilter { DutyCategories = new[] { "团队任务" } };
        var first = runs.Query(filter, null, 1, 1);
        var second = runs.Query(filter, null, 2, 1);
        Assert.Equal(2, first.Total);
        Assert.Equal(2, second.Total);
        Assert.NotEqual(Assert.Single(first.Items).RunId, Assert.Single(second.Items).RunId);
        Assert.Equal(new[] { "2026-09-04" }, runs.QueryCalendarDays("2026-09", filter));
        Assert.Equal(1, runs.Query(new RunFilter { DutyCategories = new[] { "大型任务" } }, null, 1, 20).Total);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        var statistics = new StatisticsRepository(fixture.Database, settings);
        Assert.Equal(2, statistics.GetDashboard(filter).AttemptCount);
        Assert.Equal(2, statistics.GetDungeonStats(filter).Sum(row => row.AttemptCount));
        Assert.All(statistics.GetDungeonStats(filter), row => Assert.Equal("团队任务", row.DutyCategory));
        Assert.Equal(4, statistics.GetDashboard().AttemptCount);
        Assert.Equal(4, new[] { "团队任务", "大型任务", "自定义" }.Sum(category =>
            statistics.GetDashboard(new RunFilter { DutyCategories = new[] { category } }).CompletedCount));
        Assert.Equal("大型任务", runs.Get(alliance.RunId)!.DutyCategory);
        Assert.Null(runs.Get(zoneOnly.RunId)!.DutyCategory);
    }

    [Fact]
    public void CategorySqlUsesSafeRegionalIdentityAndRefusesConflictsAndAmbiguousTerritories()
    {
        var catalog = DutyCatalog.Compose(new[]
        {
            DutyCatalog.ParseDocument("""
            {"schema_version":1,"region":"CN","duties":[
              {"content_id":1,"territory_id":101,"localized_name":"A","duty_category":"大型任务","party_size":24},
              {"content_id":2,"territory_id":102,"localized_name":"B","duty_category":"大型任务","party_size":24},
              {"content_id":3,"territory_id":102,"localized_name":"C","duty_category":"大型任务","party_size":24},
              {"content_id":4,"territory_id":104,"localized_name":"D","duty_category":"大型任务","party_size":24},
              {"content_id":5,"territory_id":105,"localized_name":"E","duty_category":"大型任务","party_size":24}]}
            """),
            DutyCatalog.ParseDocument("""
            {"schema_version":1,"region":"GLOBAL","duties":[
              {"content_id":1,"territory_id":101,"localized_name":"A global","duty_category":"大型任务","party_size":8},
              {"content_id":5,"territory_id":105,"localized_name":"E global","duty_category":"大型任务","party_size":24}]}
            """),
        });
        using var fixture = new TestDatabase();
        var repository = new RunRepository(fixture.Database);
        var records = new[]
        {
            TestDatabase.Run("cn-content", contentId: 1),
            TestDatabase.Run("global-content", contentId: 1) with { Region = Region.Global },
            TestDatabase.Run("unknown-conflict", contentId: 1) with { Region = Region.Unknown },
            TestDatabase.Run("unknown-consistent", contentId: 5) with { Region = Region.Unknown },
            TestDatabase.Run("unique-territory", contentId: null) with { TerritoryId = 101 },
            TestDatabase.Run("ambiguous-territory", contentId: null) with { TerritoryId = 102 },
            TestDatabase.Run("unknown-content-known-territory", contentId: 999) with { TerritoryId = 101 },
            TestDatabase.Run("wrong-region", contentId: 4) with { Region = Region.Global },
            TestDatabase.Run("unknown-territory-conflict", contentId: null) with { Region = Region.Unknown, TerritoryId = 101 },
            TestDatabase.Run("unknown-territory-consistent", contentId: null) with { Region = Region.Unknown, TerritoryId = 105 },
        };
        fixture.Database.RunInTransaction(tx =>
        {
            foreach (var run in records) repository.Insert(run with { DutyCategory = "大型任务" }, tx);
        });
        var filter = RunFilterSql.Build(new RunFilter { DutyCategories = new[] { "团队任务" } }, false, catalog);
        using var query = fixture.Database.CreateCommand();
        query.CommandText = "SELECT run_id FROM mentor_runs WHERE " + filter.Where + " ORDER BY run_id;";
        RunFilterSql.Bind(query, filter);
        using var reader = query.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        Assert.Equal(new[] { "cn-content", "unique-territory", "unknown-consistent", "unknown-territory-consistent" }, ids);
        foreach (var run in records)
            Assert.Equal(ids.Contains(run.RunId) ? "团队任务" : "大型任务",
                catalog.Categories.Resolve("大型任务", run.ContentId, run.TerritoryId, run.Region));
    }

    [Fact]
    public void CatalogueReadsOptionalPartySizeAndKeepsRawCategory()
    {
        Assert.Equal(24, DutyCatalog.Default.Find(92, Region.Cn)!.PartySize);
        Assert.Equal("大型任务", DutyCatalog.Default.Find(92, Region.Cn)!.DutyCategory);
        var legacy = DutyCatalog.Parse("""
            {"schema_version":1,"region":"CN","duties":[
              {"content_id":1,"localized_name":"No size","duty_category":"大型任务"}]}
            """);
        Assert.Null(legacy.Find(1, Region.Cn)!.PartySize);
        Assert.Equal("大型任务", legacy.Categories.Resolve("大型任务", 1, null, Region.Cn));
    }

    [Fact]
    public void StatsPreserveCustomCategoryWithoutStoredNameAndProjectOnlyKnownIdentity()
    {
        using var fixture = new TestDatabase();
        var runs = new RunRepository(fixture.Database);
        fixture.Database.RunInTransaction(tx =>
        {
            runs.Insert(TestDatabase.Run(contentId: 92) with { DutyName = null, DutyCategory = "自定义" }, tx);
            runs.Insert(TestDatabase.Run(contentId: 9999999) with
                { DutyName = null, DutyCategory = "大型任务", TerritoryId = 174 }, tx);
        });
        var statistics = new StatisticsRepository(fixture.Database, new SettingsRepository(fixture.Database, fixture.Clock));
        Assert.Equal("自定义", Assert.Single(statistics.GetDungeonStats(), row => row.ContentId == 92).DutyCategory);
        Assert.Equal("大型任务", Assert.Single(statistics.GetDungeonStats(), row => row.ContentId == 9999999).DutyCategory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeExportUnrelatedEditAuditAndUnchangedReimportKeepRawCategory(bool dateOnly)
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var candidate = new JsonObject
        {
            ["region"] = "CN", ["content_id"] = 92, ["duty_category"] = "大型任务", ["job_id"] = 19,
            ["matched_at_utc"] = "2026-09-04T00:59:50Z", ["entered_at_utc"] = "2026-09-04T01:00:00Z",
            ["ended_at_utc"] = "2026-09-04T01:02:00Z", ["result"] = "COMPLETED",
        };
        if (dateOnly)
        {
            candidate.Remove("matched_at_utc"); candidate.Remove("entered_at_utc"); candidate.Remove("ended_at_utc");
            candidate["source_recorded_at"] = "2026-09-04";
        }
        JsonObject Preview() => service.PreviewSource("ROWS", rows: new JsonArray(candidate.DeepClone()));
        var preview = Preview();
        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 1 }, true, Guid.NewGuid().ToString("D"));
        var runId = committed["run_ids"]![0]!.GetValue<string>();
        var runs = new RunRepository(fixture.Database);
        Assert.Equal("团队任务", Wire.Runs(runs.Query(null, null, 1, 20))["items"]![0]!["duty_category"]!.GetValue<string>());
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        new RunMutationService(fixture.Database, settings, fixture.Clock).CorrectRun(new CorrectRunCommand(
            Guid.NewGuid().ToString("D"), runId, 1, "only note", new RunChangeSet
            { Specified = new HashSet<string> { RunFields.Note }, Note = "unchanged category" }));
        Assert.Equal("大型任务", runs.Get(runId)!.DutyCategory);
        var firstRevision = new RunRevisionRepository(fixture.Database).GetFirst(runId, null)!;
        Assert.Contains(firstRevision.Changes, c => c.Field == RunFields.DutyCategory && Equals(c.NewValue, "大型任务"));
        var path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "raw.json");
        new RunExporter(runs, fixture.Clock, fixture.Database).ExportJson(path, null, false);
        Assert.Equal("大型任务", JsonNode.Parse(File.ReadAllText(path))![0]!["duty_category"]!.GetValue<string>());
        Assert.Equal("duplicate", Preview()["rows"]![0]!["status"]!.GetValue<string>());
        using var target = new TestDatabase();
        var targetService = new RunImportService(target.Database, target.Clock);
        var reimport = targetService.PreviewSource("JSON", filePath: path);
        Assert.Equal("大型任务", reimport["rows"]![0]!["run"]!["duty_category"]!.GetValue<string>());
        var targetCommit = targetService.Commit(reimport["preview_id"]!.GetValue<string>(), new[] { 1 }, true, Guid.NewGuid().ToString("D"));
        Assert.Equal(1, targetCommit["imported_count"]!.GetValue<int>());
        Assert.Equal("大型任务", new RunRepository(target.Database).Get(targetCommit["run_ids"]![0]!.GetValue<string>())!.DutyCategory);
        Assert.Equal("duplicate", targetService.PreviewSource("JSON", filePath: path)["rows"]![0]!["status"]!.GetValue<string>());
    }
}
