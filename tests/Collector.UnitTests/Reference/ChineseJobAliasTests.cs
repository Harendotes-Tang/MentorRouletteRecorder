using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;
using NPOI.HSSF.UserModel;

namespace MentorRecorder.Collector.UnitTests;

public sealed class ChineseJobAliasTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.AliasTests", Guid.NewGuid().ToString("N"));
    // Independent approved expectations: production must read the single catalog, not this test table.
    public static TheoryData<string, int> ApprovedAliases => new()
    {
        { "白魔", 24 }, { "黑魔", 25 }, { "赤魔", 35 }, { "青魔", 36 }, { "龙骑", 22 },
        { "诗人", 23 }, { "召唤", 27 }, { "机工", 31 }, { "暗骑", 32 }, { "黑骑", 32 },
        { "占星", 33 }, { "绝枪", 37 }, { "枪刃", 37 }, { "钐镰", 39 }, { "镰刀", 39 },
        { "蝰蛇", 41 }, { "绘灵", 42 }, { "画家", 42 }, { "驯兽", 43 },
    };

    public ChineseJobAliasTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void CatalogContainsExactlyTheApprovedAliasesAndLegacyRowsRemainCompatible()
    {
        var actual = JobCatalog.Default.Jobs.SelectMany(job => job.AliasesZh.Select(alias => (alias, job.JobId))).Order().ToArray();
        var expected = ApprovedAliases.Select(row => ((string)row[0], (int)row[1])).Order().ToArray();
        Assert.Equal(expected, actual);
        var legacy = JobCatalog.Parse("""
            {"version":1,"jobs":[{"job_id":19,"abbreviation":"PLD","name_en":"paladin","name_zh":"骑士","role":"坦克"}]}
            """);
        Assert.Empty(legacy.Find(19)!.AliasesZh);
        Assert.Equal(19, legacy.FindByName(" pLd ")!.JobId);
        Assert.Null(legacy.FindByChineseSearchText("白魔"));
        Assert.Null(JobCatalog.Default.FindByName(null));
        Assert.Null(JobCatalog.Default.FindByName(" "));
    }

    [Fact]
    public void AmbiguousAliasesAndAliasesCollidingWithFullNamesNeverChooseTheFirstJob()
    {
        var catalog = JobCatalog.Parse("""
            {"version":1,"jobs":[
              {"job_id":19,"name_zh":"骑士","aliases_zh":["共享"]},
              {"job_id":32,"name_zh":"暗黑骑士","aliases_zh":["共享","骑士"]}]}
            """);
        Assert.Null(catalog.FindByName("共享"));
        Assert.Null(catalog.FindByName("骑士"));
        Assert.Null(catalog.FindByChineseSearchText("共享"));
        Assert.Equal(32, catalog.FindByName("暗黑骑士")!.JobId);
    }

    [Theory]
    [InlineData("XLSX")]
    [InlineData("XLS")]
    [InlineData("CSV")]
    [InlineData("PASTE")]
    public void AllApprovedAliasesSurviveRealSourcePreviewRevalidationAndCommit(string kind)
    {
        var cases = ApprovedAliases.ToArray();
        var names = cases.Select(row => (string)row[0]).ToArray();
        var path = Path.Combine(_directory, "aliases." + kind.ToLowerInvariant());
        var table = "副本\t职业\n" + string.Join('\n', names.Select(name => "合成简称副本\t  " + name + "  "));
        if (kind == "XLSX") WriteTemplate(path, names);
        else if (kind == "XLS")
        {
            using var workbook = new HSSFWorkbook();
            var sheet = workbook.CreateSheet("记录");
            var header = sheet.CreateRow(0);
            header.CreateCell(0).SetCellValue("副本"); header.CreateCell(1).SetCellValue("职业");
            for (var i = 0; i < names.Length; i++)
            {
                var row = sheet.CreateRow(i + 1);
                row.CreateCell(0).SetCellValue("合成简称副本"); row.CreateCell(1).SetCellValue("  " + names[i] + "  ");
            }
            using var file = File.Create(path); workbook.Write(file, leaveOpen: true);
        }
        else if (kind == "CSV") File.WriteAllText(path, table.Replace('\t', ','), new UTF8Encoding(false));

        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var first = kind == "PASTE" ? service.PreviewSource(kind, text: table) : service.PreviewSource(kind, path);
        var rows = first["rows"]!.AsArray();
        Assert.Equal(cases.Length, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.True(rows[i]!["can_import"]!.GetValue<bool>());
            Assert.Equal((int)cases[i][1], rows[i]!["run"]!["job_id"]?.GetValue<int>());
            Assert.Equal(names[i], rows[i]!["candidate"]!["job_name"]!.GetValue<string>());
            Assert.False(rows[i]!["candidate"]!.AsObject().ContainsKey("job_id"));
        }
        var second = service.PreviewSource(kind, rows: new JsonArray(rows.Select(row => row!["candidate"]!.DeepClone()).ToArray()));
        var receipt = service.Commit(second["preview_id"]!.GetValue<string>(),
            second["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(),
            true, Guid.NewGuid().ToString("D"));
        Assert.Equal(cases.Length, receipt["imported_count"]!.GetValue<int>());
        var repository = new RunRepository(fixture.Database);
        for (var i = 0; i < cases.Length; i++)
        {
            var run = repository.Get(receipt["run_ids"]![i]!.GetValue<string>())!;
            Assert.Equal((int)cases[i][1], run.JobId);
            Assert.Equal(JobCatalog.Default.RoleOf(run.JobId), run.Role);
            Assert.Equal(names[i], run.JobName);
            var fullName = JobCatalog.Default.NameOf(run.JobId);
            Assert.Contains(repository.Query(new RunFilter { Text = fullName }, null, 1, 50).Items, found => found.RunId == run.RunId);
        }
    }

    [Theory]
    [MemberData(nameof(ApprovedAliases))]
    public void AliasSearchFindsKnownIdentitiesAndExactNamesWithoutRewritingRows(string alias, int id)
    {
        using var fixture = new TestDatabase();
        var repository = new RunRepository(fixture.Database);
        var job = JobCatalog.Default.Find(id)!;
        var identified = TestDatabase.Run(jobId: id) with { JobName = job.NameEn, Role = job.Role };
        var nameOnly = TestDatabase.Run(jobId: null) with { JobName = job.NameZh };
        var aliasOnly = TestDatabase.Run(jobId: null) with { JobName = alias };
        fixture.Database.RunInTransaction(tx =>
        {
            repository.Insert(identified, tx); repository.Insert(nameOnly, tx); repository.Insert(aliasOnly, tx);
            repository.Insert(TestDatabase.Run(jobId: 19) with { JobName = "骑士", Note = "无关" }, tx);
        });
        var page = repository.Query(new RunFilter { Text = "  " + alias + "  " }, null, 1, 50);
        Assert.Equal(new[] { identified.RunId, nameOnly.RunId, aliasOnly.RunId }.Order(), page.Items.Select(run => run.RunId).Order());
        var filtered = repository.Query(new RunFilter { Text = alias, JobIds = [id] }, null, 1, 50);
        Assert.Equal(identified.RunId, Assert.Single(filtered.Items).RunId);
        Assert.Equal(job.NameEn, repository.Get(identified.RunId)!.JobName);
        Assert.Null(repository.Get(aliasOnly.RunId)!.JobId);
    }

    [Fact]
    public void AliasSearchKeepsOriginalNotesAndDoesNotExtractAliasesFromPhrases()
    {
        using var fixture = new TestDatabase();
        var repository = new RunRepository(fixture.Database);
        var gunbreaker = TestDatabase.Run(jobId: 37) with { JobName = "绝枪战士" };
        var originalNote = TestDatabase.Run(jobId: 19) with { Note = "今天和枪刃一起打本" };
        fixture.Database.RunInTransaction(tx =>
        {
            repository.Insert(gunbreaker, tx); repository.Insert(originalNote, tx);
            repository.Insert(TestDatabase.Run(jobId: 19) with { Note = "只在备注提到绝枪战士" }, tx);
            repository.Insert(TestDatabase.Run(jobId: 19) with { JobName = "绝枪战士", Note = "显式其他职业编号" }, tx);
        });
        Assert.Equal(2, repository.Query(new RunFilter { Text = "枪刃" }, null, 1, 50).Total);
        Assert.Equal(originalNote.RunId, Assert.Single(repository.Query(new RunFilter { Text = "和枪刃" }, null, 1, 50).Items).RunId);
        Assert.Equal(0, repository.Query(new RunFilter { Text = "刃", JobIds = [37] }, null, 1, 50).Total);
        Assert.Equal(0, repository.Query(new RunFilter { NameText = "枪刃" }, null, 1, 50).Total);
    }

    [Theory]
    [InlineData("枪刃", 19, 19)]
    [InlineData("枪刃", 999999, 999999)]
    [InlineData("枪", null, null)]
    [InlineData("我玩枪刃", null, null)]
    [InlineData("不存在的职业", null, null)]
    public void ImportDoesNotOverrideExplicitIdsOrGuessPartialWords(string name, int? suppliedId, int? expectedId)
    {
        using var fixture = new TestDatabase();
        var input = new JsonObject { ["duty_name"] = "合成简称副本", ["job_name"] = name };
        if (suppliedId is not null) input["job_id"] = suppliedId;
        var preview = new RunImportService(fixture.Database, fixture.Clock).PreviewSource("ROWS", rows: new JsonArray(input));
        Assert.Equal(expectedId, preview["rows"]![0]!["run"]!["job_id"]?.GetValue<int>());
        Assert.Equal(name, preview["rows"]![0]!["run"]!["job_name"]!.GetValue<string>());
    }

    private static void WriteTemplate(string path, string[] aliases)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Desktop/resources/import-templates/personal-records.xlsx")))
            root = root.Parent;
        Assert.NotNull(root);
        File.Copy(Path.Combine(root.FullName, "src/Desktop/resources/import-templates/personal-records.xlsx"), path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
        XDocument document; using (var stream = entry.Open()) document = XDocument.Load(stream);
        var ns = document.Root!.Name.Namespace;
        var data = document.Root.Element(ns + "sheetData")!;
        data.Elements(ns + "row").Where(row => row.Attribute("r")?.Value != "1").Remove();
        for (var i = 0; i < aliases.Length; i++)
            data.Add(new XElement(ns + "row", new XAttribute("r", i + 2),
                new[] { "合成简称副本", "  " + aliases[i] + "  " }.Select((value, column) =>
                    new XElement(ns + "c", new XAttribute("r", ((char)('A' + column)).ToString() + (i + 2)),
                        new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value))))));
        entry.Delete(); using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open(); document.Save(output);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
