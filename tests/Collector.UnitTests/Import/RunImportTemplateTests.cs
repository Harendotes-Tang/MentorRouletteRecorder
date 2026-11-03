using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Reference;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>模板直接经过生产解析、预览和临时库提交；说明页不能产生虚构记录。</summary>
public sealed class RunImportTemplateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.TemplateTests", Guid.NewGuid().ToString("N"));
    private static readonly string[] Fields = { "duty_name", "job_name", "entered_at_utc", "ended_at_utc", "result", "duration_ms", "reflection_text", "reflection_mood", "source_recorded_at", "note", "joined_in_progress", "region" };
    public RunImportTemplateTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void UnfilledTemplatesYieldNoCommittableFactsOrFixedIdentity()
    {
        Assert.Empty(RunImportSourceParser.Parse("XLSX", Template("personal-records.xlsx")));
        var row = Assert.Single(RunImportSourceParser.Parse("JSON", Template("personal-records.json")));
        Assert.Equal(Fields.Order(StringComparer.Ordinal), row.Values.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.False(row.Values.ContainsKey("run_id"));
        Assert.Null(row.Values["entered_at_utc"]); Assert.Null(row.Values["ended_at_utc"]);
        Assert.Null(row.Values["joined_in_progress"]);
        using var database = new TestDatabase();
        var preview = new RunImportService(database.Database, database.Clock).PreviewSource("JSON", Template("personal-records.json"), timeZone: "+08:00");
        Assert.False(preview["rows"]![0]!["can_import"]!.GetValue<bool>());
        Assert.Empty(new RunRepository(database.Database).Query(new(), null, 1, 50).Items);
    }

    [Theory]
    [InlineData("JSON", "正常", false)]
    [InlineData("JSON", "中途", true)]
    [InlineData("JSON", "", null)]
    [InlineData("XLSX", "正常", false)]
    [InlineData("XLSX", "中途", true)]
    [InlineData("XLSX", "", null)]
    public void FilledTemplateCanPreviewAndCommitWithoutInventingIdentity(string kind, string entryMode, bool? joinedInProgress)
    {
        var facts = new[] { "合成模板副本", "骑士", "2026-10-09T20:00:00+08:00", "2026-10-09T20:20:00+08:00", "COMPLETED", "1200000", "合成模板心得", "good", "2026-10-09T20:30:00+08:00", "合成模板备注", entryMode, "国服" };
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
        Assert.Equal(joinedInProgress, run.JoinedInProgress);
        Assert.Equal(MentorRecorder.Collector.Domain.Region.Cn, run.Region);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), run.EnteredAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 30, 0, TimeSpan.Zero), run.ImportMetadata.SourceRecordedAtUtc);
    }

    [Theory]
    [InlineData("Chinese")]
    [InlineData("English")]
    [InlineData("Abbreviation")]
    public void ExcelTemplateJobNamesSurvivePreviewRevalidationAndCommit(string spelling)
    {
        // 填写随软件分发的真实模板；合成记录不代表反馈者的原始工作簿。
        var jobs = JobCatalog.Default.Jobs;
        Assert.NotEmpty(jobs);
        var names = jobs.Select(job => spelling switch
        {
            "Chinese" => job.NameZh,
            "English" => job.NameEn,
            _ => job.Abbreviation.ToLowerInvariant(),
        }).ToArray();
        var path = Path.Combine(_directory, "jobs.xlsx");
        File.Copy(Template("personal-records.xlsx"), path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("xl/worksheets/sheet1.xml")!;
            XDocument document;
            using (var stream = entry.Open()) document = XDocument.Load(stream);
            var ns = document.Root!.Name.Namespace;
            var sheetData = document.Root.Element(ns + "sheetData")!;
            sheetData.Elements(ns + "row").Where(row => row.Attribute("r")?.Value != "1").Remove();
            for (var i = 0; i < names.Length; i++)
                sheetData.Add(new XElement(ns + "row", new XAttribute("r", i + 2),
                    new[] { "合成职业识别副本", "  " + names[i] + "  " }.Select((value, column) =>
                        new XElement(ns + "c", new XAttribute("r", ((char)('A' + column)).ToString() + (i + 2)),
                            new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value))))));
            entry.Delete();
            using var output = zip.CreateEntry("xl/worksheets/sheet1.xml").Open();
            document.Save(output);
        }

        using var database = new TestDatabase();
        var service = new RunImportService(database.Database, database.Clock);
        var first = service.PreviewSource("XLSX", path);
        var rows = first["rows"]!.AsArray();
        Assert.Equal(jobs.Count, rows.Count);
        for (var i = 0; i < jobs.Count; i++)
        {
            Assert.True(rows[i]!["can_import"]!.GetValue<bool>());
            Assert.Equal(jobs[i].JobId, rows[i]!["run"]!["job_id"]!.GetValue<int>());
            Assert.False(rows[i]!["candidate"]!.AsObject().ContainsKey("job_id"));
            Assert.Empty(rows[i]!["errors"]!.AsArray());
            Assert.DoesNotContain(rows[i]!["warning_refs"]!.AsArray(), warning =>
                warning?["key"]?.GetValue<string>() == "import.warning.job_unmatched");
        }

        // Desktop 重新校验时发送原始候选；展示用的职业编号不应改变来源字段或去重语义。
        var second = service.PreviewSource("XLSX", rows: new JsonArray(
            rows.Select(row => row!["candidate"]!.DeepClone()).ToArray()));
        var committed = service.Commit(second["preview_id"]!.GetValue<string>(),
            second["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(),
            true, Guid.NewGuid().ToString("D"));
        Assert.Equal(jobs.Count, committed["imported_count"]!.GetValue<int>());
        var repository = new RunRepository(database.Database);
        var runIds = committed["run_ids"]!.AsArray();
        for (var i = 0; i < jobs.Count; i++)
        {
            var run = repository.Get(runIds[i]!.GetValue<string>())!;
            Assert.Equal(jobs[i].JobId, run.JobId);
            Assert.Equal(jobs[i].Role, run.Role);
            Assert.Equal(names[i], run.JobName);
        }
    }

    /// <summary>
    /// Audit 2026-11-03 H2-5: the templates carry a 区服 column. 国服 / 繁中服 / 国际服 name the record's region (the
    /// Traditional spellings too); the Collector leaves a blank cell UNKNOWN, and the Desktop's preview fills it with
    /// the dashboard's selected region (国服 when none is selected), as the instructions say.
    /// </summary>
    [Fact]
    public void TheRegionColumnNamesTheRecordRegionAndABlankCellLeavesItUnknown()
    {
        const string csv = "副本,区服\n合成甲,国服\n合成乙,繁中服\n合成丙,国际服\n合成丁,國服\n合成戊,\n";
        using var database = new TestDatabase();
        var preview = new RunImportService(database.Database, database.Clock).PreviewSource("CSV", text: csv);
        Assert.Equal(new[] { "CN", "TC", "GLOBAL", "CN", "UNKNOWN" },
            preview["rows"]!.AsArray().Select(row => row!["run"]!["region"]!.GetValue<string>()));
        Assert.Contains("区服 → region：", File.ReadAllText(Template("instructions.txt")), StringComparison.Ordinal);
        Assert.Contains("留空时按总览所选区服（未选具体区服时为国服）填入，导入前可在预览中修改。",
            File.ReadAllText(Template("instructions.txt")), StringComparison.Ordinal);
    }

    /// <summary>
    /// Audit 2026-11-03 H2-7: the generator is the source of the shipped templates. Its FIELDS list names exactly the
    /// columns of the Excel template, the keys of the JSON template and the lines of the instructions' field table,
    /// in the same order, and every label is a header the parser maps to that key, so regenerating cannot drop a column.
    /// </summary>
    [Fact]
    public void TheGeneratorFieldsAreTheShippedTemplateColumns()
    {
        var script = File.ReadAllText(Repository("scripts", "generate-import-templates.py"));
        var fields = System.Text.RegularExpressions.Regex.Matches(script, "^    [(]" + Quote + "([^" + Quote + "]+)" + Quote + ", " + Quote + "([a-z_]+)" + Quote + ", ",
                System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(match => (Label: match.Groups[1].Value, Key: match.Groups[2].Value)).ToArray();
        Assert.Equal(Fields, fields.Select(field => field.Key));

        using (var zip = ZipFile.OpenRead(Template("personal-records.xlsx")))
        {
            XDocument sheet;
            using (var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()) sheet = XDocument.Load(stream);
            var ns = sheet.Root!.Name.Namespace;
            var header = sheet.Root.Element(ns + "sheetData")!.Elements(ns + "row").First()
                .Elements(ns + "c").Select(cell => cell.Value).ToArray();
            Assert.Equal(fields.Select(field => field.Label), header);
        }
        Assert.Equal(fields.Select(field => field.Key),
            JsonNode.Parse(File.ReadAllText(Template("personal-records.json")))!.AsArray()[0]!.AsObject().Select(pair => pair.Key));
        var instructions = File.ReadAllText(Template("instructions.txt"));
        Assert.Equal(fields.Select(field => field.Label + " → " + field.Key), FieldTable(instructions));
        // A Windows checkout has the text templates with CRLF (git text=auto; re-review 2026-11-03 RV2-1).
        Assert.Equal(FieldTable(instructions), FieldTable(instructions.Replace("\r", "").Replace("\n", "\r\n")));
        var parsed = Assert.Single(RunImportSourceParser.Parse("CSV", text: string.Join(',', fields.Select(field => field.Label))
            + "\n" + string.Join(',', fields.Select(_ => "x"))));
        Assert.Equal(fields.Select(field => field.Key).Order(StringComparer.Ordinal),
            parsed.Values.Select(pair => pair.Key).Order(StringComparer.Ordinal));
    }

    private const string Quote = "\"";

    /// <summary>The 「字段对照」 lines of the instructions as "label → key", whatever the line endings.</summary>
    private static string[] FieldTable(string instructions) =>
        instructions.Replace("\r\n", "\n").Split("字段对照\n")[1]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line[..line.IndexOf('：')]).ToArray();

    private static string Repository(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(path)) return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository file unavailable", Path.Combine(parts));
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
