using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>模板直接经过生产解析、预览和临时库提交；说明页不能产生虚构记录。</summary>
public sealed class RunImportTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.TemplateTests", Guid.NewGuid().ToString("N"));
    private static readonly string[] Fields = { "duty_name", "job_name", "entered_at_utc", "ended_at_utc", "result", "duration_ms", "reflection_text", "reflection_mood", "source_recorded_at", "note" };
    public RunImportTemplateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void UnfilledTemplatesYieldNoCommittableFactsOrFixedIdentity()
    {
        Assert.Empty(RunImportSourceParser.Parse("XLSX", Template("personal-records.xlsx")));
        var row = Assert.Single(RunImportSourceParser.Parse("JSON", Template("personal-records.json")));
        Assert.Equal(Fields.Order(StringComparer.Ordinal), row.Values.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.False(row.Values.ContainsKey("run_id"));
        Assert.Null(row.Values["entered_at_utc"]); Assert.Null(row.Values["ended_at_utc"]);
        using var database = new TestDatabase();
        var preview = new RunImportService(database.Database, database.Clock).PreviewSource("JSON", Template("personal-records.json"), timeZone: "+08:00");
        Assert.False(preview["rows"]![0]!["can_import"]!.GetValue<bool>());
        Assert.Empty(new RunRepository(database.Database).Query(new(), null, 1, 50).Items);
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("XLSX")]
    public void FilledTemplateCanPreviewAndCommitWithoutInventingIdentity(string kind)
    {
        var facts = new[] { "合成模板副本", "骑士", "2026-10-09T20:00:00+08:00", "2026-10-09T20:20:00+08:00", "COMPLETED", "1200000", "合成模板心得", "good", "2026-10-09T20:30:00+08:00", "合成模板备注" };
        var path = Path.Combine(_directory, "filled." + kind.ToLowerInvariant());
        File.Copy(Template("personal-records." + kind.ToLowerInvariant()), path);
        if (kind == "JSON")
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
            for (var i = 0; i < Fields.Length; i++) node[0]![Fields[i]] = facts[i];
            File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));
        }
        else
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            XDocument document; using (var stream = entry.Open()) document = XDocument.Load(stream);
            var ns = document.Root!.Name.Namespace;
            var sheetData = document.Root.Element(ns + "sheetData")!;
            sheetData.Elements(ns + "row").Where(row => row.Attribute("r")?.Value != "1").Remove();
            sheetData.Add(new XElement(ns + "row", new XAttribute("r", 2), facts.Select((fact, i) =>
                new XElement(ns + "c", new XAttribute("r", ((char)('A' + i)).ToString() + "2"), new XAttribute("t", "inlineStr"),
                    new XElement(ns + "is", new XElement(ns + "t", fact))))));
            entry.Delete(); entry = zip.CreateEntry("xl/worksheets/sheet1.xml"); using var output = entry.Open(); document.Save(output);
        }
        var parsed = Assert.Single(RunImportSourceParser.Parse(kind, path)); Assert.Empty(parsed.Errors);
        Assert.Equal("合成模板备注", parsed.Values["note"]!.GetValue<string>());
        using var database = new TestDatabase(); var service = new RunImportService(database.Database, database.Clock);
        var preview = service.PreviewSource(kind, path, timeZone: "+08:00", sourceName: "合成模板填写副本");
        var candidate = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.True(candidate["can_import"]!.GetValue<bool>());
        Assert.False(candidate["incomplete"]!.GetValue<bool>());
        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { parsed.RowNumber }, true, Guid.NewGuid().ToString("D"));
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        var run = new RunRepository(database.Database).Get(committed["run_ids"]![0]!.GetValue<string>())!;
        Assert.Equal("合成模板备注", run.Note); Assert.Equal(kind, run.ImportMetadata!.SourceKind);
        Assert.Equal("合成模板心得", run.Reflection!.Text); Assert.Equal(1200000, run.DurationMs);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), run.EnteredAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.Zero), run.ImportMetadata.SourceRecordedAtUtc);
    }

    private static string Template(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "Desktop", "resources", "import-templates", name);
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Template asset unavailable", name);
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
