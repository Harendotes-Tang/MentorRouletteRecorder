using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Tests;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Import sentences travel by catalogue key beside the Chinese (contracts/collector-texts.md §2.1):
/// <c>rows[].error_refs</c> / <c>warning_refs</c> are as long as <c>errors</c> / <c>warnings</c> and in
/// the same order, with null only for a sentence worded outside the catalogue, and a source that
/// cannot be read says why in <c>message_ref</c>. The Chinese is the one the tests always asserted.
/// </summary>
public sealed class ImportTextRefTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.ImportTextRefTests", Guid.NewGuid().ToString("N"));

    public ImportTextRefTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void PreviewWarningsCarryAReferenceEachAndTheSourcesOwnWarningCarriesNull()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "合成测试副本", ["job_name"] = "不存在的职业", ["reflection_text"] = "合成心得",
            ["source_recorded_at"] = "2026-10-08 20:30:00", ["warnings"] = new JsonArray("需要核对职业"),
        }), timeZone: "+08:00");

        var row = Row(preview);
        var warnings = row["warnings"]!.AsArray();
        var refs = row["warning_refs"]!.AsArray();
        AssertParallelRow(row);
        Assert.Equal(
            new[] { "import.warning.job_unmatched", "import.warning.result_defaulted", "import.warning.time_missing", "import.warning.mood_missing" },
            refs.Take(refs.Count - 1).Select(Key));
        // The screenshot reader's warning is passed through as it is: no key, so the Desktop shows the Chinese.
        Assert.Equal("需要核对职业", warnings[^1]!.GetValue<string>());
        Assert.Null(refs[^1]);
        Assert.Empty(row["error_refs"]!.AsArray());
    }

    [Fact]
    public void RowErrorsCarryTheirReferencesWithFieldNamesAsArgumentsOrNestedNames()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(
            new JsonObject { ["duty_name"] = "合成测试副本", ["entered_at_utc"] = "2026-10-08" },
            new JsonObject { ["duty_name"] = "合成测试副本", ["result"] = "完全不知道" },
            new JsonObject { ["duty_name"] = "合成测试副本", ["duration_ms"] = "1:30" }), timeZone: "+08:00");

        Assert.All(preview["rows"]!.AsArray(), row => AssertParallelRow(row!.AsObject()));
        var dateOnly = Assert.Single(Row(preview, 0)["error_refs"]!.AsArray())!;
        Assert.Equal("import.row.time_date_only", Key(dateOnly));
        Assert.Equal("import.time_field.entered", dateOnly["args"]!["field"]!["key"]!.GetValue<string>());
        Assert.Equal("import.row.result_invalid", Key(Assert.Single(Row(preview, 1)["error_refs"]!.AsArray())));
        var count = Assert.Single(Row(preview, 2)["error_refs"]!.AsArray())!;
        Assert.Equal("import.row.field_not_count", Key(count));
        Assert.Equal("duration_ms", count["args"]!["field"]!.GetValue<string>());
        Assert.All(preview["rows"]!.AsArray(), row => Assert.Equal("invalid", row!["status"]!.GetValue<string>()));
    }

    [Fact]
    public void AnOperatingSystemMessageInARowErrorTravelsWithoutAReference()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("SCREENSHOT", rows: new JsonArray(new JsonObject
        {
            ["duty_name"] = "合成测试副本", ["entered_at_utc"] = "2026-10-08 20:30:00",
        }), timeZone: "No/Such_Zone");

        var row = Row(preview);
        var error = Assert.Single(row["errors"]!.AsArray())!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Null(Assert.Single(row["error_refs"]!.AsArray()));
    }

    [Fact]
    public void ParserRowErrorsAreCatalogueTextsAndAreStillDeduplicated()
    {
        var longCell = new string('长', 8001);
        var rows = RunImportSourceParser.Parse("PASTE", text: "duty_name\tnote\r\n" + longCell + "\t" + longCell + "\r\n");

        var row = Assert.Single(rows);
        var text = Assert.Single(row.ErrorTexts);
        Assert.Equal("import.cell.too_long", text.Key);
        Assert.Equal(new[] { text.Chinese }, row.Errors);

        var unclosed = Assert.Single(RunImportSourceParser.Parse("PASTE", text: "duty_name\r\n\"合成"));
        Assert.Equal("import.table.quote_unclosed", Assert.Single(unclosed.ErrorTexts).Key);
    }

    [Fact]
    public void AParserRefusalCarriesItsCatalogueTextAndASystemOneDoesNot()
    {
        var ours = Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("JSON", text: "{}"));
        var text = RunImportSourceParser.TextOf(ours);
        Assert.NotNull(text);
        Assert.Equal("import.json.not_array", text!.Key);
        Assert.Equal(text.Chinese, ours.Message);

        var notAZip = Path.Combine(_directory, "not-a-zip.xlsx");
        File.WriteAllText(notAZip, "synthetic text that is not a ZIP archive");
        var system = Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", notAZip));
        Assert.Null(RunImportSourceParser.TextOf(system));
    }

    [Fact]
    public void AnUnreadableSourceNestsTheParsersSentenceAndQuotesASystemMessage()
    {
        using var fixture = new TestDatabase();
        var service = new RunImportService(fixture.Database, fixture.Clock);

        var ours = Assert.Throws<CollectorException>(() => service.PreviewSource("JSON", text: "{}"));
        Assert.Equal("file_path", ours.Field);
        Assert.Equal("import.source.unreadable", ours.Text!.Key);
        var nested = Assert.IsType<UserText>(Assert.Single(ours.Text.Args).Value);
        Assert.Equal("import.json.not_array", nested.Key);
        Assert.Equal("无法读取导入内容：" + nested.Chinese, ours.Message);
        Assert.Equal(ours.Message, CollectorTextCatalogue.Render(ours.Text.ToJson()));

        var notAZip = Path.Combine(_directory, "broken.xlsx");
        File.WriteAllText(notAZip, "synthetic text that is not a ZIP archive");
        var system = Assert.Throws<CollectorException>(() => service.PreviewSource("XLSX", filePath: notAZip));
        Assert.Equal("import.source.unreadable_system", system.Text!.Key);
        Assert.StartsWith("无法读取导入内容：", system.Message, StringComparison.Ordinal);
        Assert.Equal(system.Message, CollectorTextCatalogue.Render(system.Text.ToJson()));
    }

    [Fact]
    public void AHeaderErrorNestsTheHeaderRowsOwnErrorsJoinedInOrder()
    {
        var one = Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("PASTE", text: "\"副本\"x\tnote\r\n合成\t备注"));
        var single = RunImportSourceParser.TextOf(one)!;
        Assert.Equal("import.table.header_invalid", single.Key);
        Assert.Equal("import.table.quote_invalid", Assert.IsType<UserText>(single.Args[0].Value).Key);
        Assert.StartsWith("表头格式错误：", one.Message, StringComparison.Ordinal);

        var two = Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", Workbook(Formulas(2))));
        var joined = RunImportSourceParser.TextOf(two)!;
        var pair = Assert.IsType<UserText>(joined.Args[0].Value);
        Assert.Equal("import.table.errors_joined", pair.Key);
        Assert.All(pair.Args, arg => Assert.Equal("import.cell.formula", Assert.IsType<UserText>(arg.Value).Key));
        var formula = CollectorTexts.ImportCellFormula().Chinese;
        Assert.Equal("表头格式错误：" + formula + "；" + formula, two.Message);
        Assert.Equal(two.Message, CollectorTextCatalogue.Render(joined.ToJson()));
    }

    [Fact]
    public void AHeaderWithManyErrorsKeepsItsChineseButTravelsWithoutAReference()
    {
        var many = Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", Workbook(Formulas(12))));

        Assert.Null(RunImportSourceParser.TextOf(many));
        Assert.Equal("表头格式错误：" + string.Join("；", Enumerable.Repeat(CollectorTexts.ImportCellFormula().Chinese, 12)), many.Message);
    }

    private static JsonObject Row(JsonObject preview, int index = 0) => preview["rows"]![index]!.AsObject();

    private static string Key(JsonNode? reference) => reference!["key"]!.GetValue<string>();

    /// <summary>Each reference renders to its Chinese entry; a null reference is a sentence the catalogue does not word.</summary>
    private static void AssertParallelRow(JsonObject row)
    {
        foreach (var (chinese, companion) in new[] { ("errors", "error_refs"), ("warnings", "warning_refs") })
        {
            var texts = row[chinese]!.AsArray();
            var refs = Assert.IsType<JsonArray>(row[companion]);
            Assert.Equal(texts.Count, refs.Count);
            for (var i = 0; i < texts.Count; i++)
            {
                if (refs[i] is { } reference)
                {
                    Assert.Equal(texts[i]!.GetValue<string>(), CollectorTextCatalogue.Render(reference));
                }
            }
        }
    }

    private static string Formulas(int count) =>
        "<worksheet><sheetData><row r=\"1\">" +
        string.Concat(Enumerable.Range(0, count).Select(index => "<c r=\"" + (char)('A' + index) + "1\"><f>1+1</f></c>")) +
        "</row></sheetData></worksheet>";

    private string Workbook(string worksheet)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".xlsx");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var stream = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(xml);
        }
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
        Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"合成\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
        Add("xl/worksheets/sheet1.xml", worksheet);
        return path;
    }
}
