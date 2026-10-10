using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Import;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>仅使用合成文件验证来源解析；不读取本人数据库、截图或第三方账号。</summary>
public sealed class RunImportSourceParserTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.ImportSourceTests", Guid.NewGuid().ToString("N"));

    public RunImportSourceParserTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Csv_ParsesBomQuotedSeparatorsEscapedQuotesAndMultilineWithPhysicalRowNumbers()
    {
        var path = WriteText("quoted.csv", "\uFEFF副本,职业,心得,记录时间\r\n\"合成,副本\",骑士,\"第一行\r\n\"\"第二行\"\"\",2026-10-01 12:30\r\n下一副本,学者,下一条,2026-10-02 13:30\r\n");
        var rows = RunImportSourceParser.Parse("CSV", path);
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].RowNumber);
        Assert.Equal(4, rows[1].RowNumber);
        Assert.Equal("合成,副本", Text(rows[0], "duty_name"));
        Assert.Equal("第一行\r\n\"第二行\"", Text(rows[0], "reflection_text"));
        Assert.Equal("2026-10-01 12:30", Text(rows[0], "source_recorded_at"));
        Assert.False(rows[0].Values.ContainsKey("entered_at_utc"));
        Assert.All(rows, row => Assert.Empty(row.Errors));
    }

    [Fact]
    public void Csv_MalformedRowDoesNotDiscardOtherRows()
    {
        var rows = RunImportSourceParser.Parse("CSV", text: "副本,心得\r\n合成一,\"正文\"junk\r\n合成二,有效\r\n");
        Assert.Equal(2, rows.Count);
        Assert.NotEmpty(rows[0].Errors);
        Assert.Empty(rows[1].Errors);
        Assert.Equal("有效", Text(rows[1], "reflection_text"));
    }

    [Fact]
    public void Csv_UnclosedQuoteProducesRowError()
    {
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", text: "副本,心得\n合成,\"未闭合\n仍在同一格"));
        Assert.Contains(row.Errors, error => error.Contains("未闭合", StringComparison.Ordinal));
    }

    [Fact]
    public void Csv_RejectsInvalidUtf8WhenBomDeclaresEncoding()
    {
        var path = Path.Combine(_directory, "invalid.csv");
        File.WriteAllBytes(path, new byte[] { 0xef, 0xbb, 0xbf, 0xff, 0xfe });
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("CSV", path));
    }

    [Fact]
    public void Csv_Gb18030FallbackUsesOnlyAnAvailableRuntimeProvider()
    {
        var path = Path.Combine(_directory, "legacy.csv");
        var providerType = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
        if (providerType?.GetProperty("Instance")?.GetValue(null) is not EncodingProvider provider)
        {
            File.WriteAllBytes(path, new byte[] { 0xff });
            Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("CSV", path));
            return;
        }
        Encoding.RegisterProvider(provider);
        var encoding = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        File.WriteAllBytes(path, encoding.GetBytes("副本,心得\r\n合成副本,合成中文正文"));
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", path));
        Assert.Empty(row.Errors);
        Assert.Equal("合成中文正文", Text(row, "reflection_text"));
    }

    [Fact]
    public void Csv_NativeRealExportReversesOnlyFreeTextFormulaProtection()
    {
        using var database = new TestDatabase();
        var run = TestDatabase.Run() with { DutyName = "=合成公式文本", DutyCategory = "\t类型", JobName = "+职业" };
        database.Database.RunInTransaction(tx =>
        {
            new RunRepository(database.Database).Insert(run, tx);
            new RunReflectionRepository(database.Database).Upsert(run.RunId, ReflectionMood.Good, "-1，合成正文\n完整第二行", database.Clock.UtcNow, tx);
        });
        var path = Path.Combine(_directory, "native.csv");
        new RunExporter(new RunRepository(database.Database), database.Clock).ExportCsv(path, null, false);
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", path));
        Assert.Empty(row.Errors);
        Assert.Equal(run.RunId, Text(row, "run_id"));
        Assert.Equal("=合成公式文本", Text(row, "duty_name"));
        Assert.Equal("\t类型", Text(row, "duty_category"));
        Assert.Equal("+职业", Text(row, "job_name"));
        Assert.Equal("-1，合成正文\n完整第二行", Text(row, "reflection_text"));
        Assert.Equal("120000", Text(row, "duration_ms"));
        Assert.Equal("2026-09-04T01:00:00.000Z", Text(row, "entered_at_utc"));
    }

    [Fact]
    public void Csv_LegitimateUnquotedOrDoubledApostropheSurvivesNativeFormat()
    {
        var values = Enumerable.Repeat(string.Empty, RunExporter.CsvHeader.Count).ToArray();
        values[4] = "'=真实单引号";
        values[6] = "''+真实双引号";
        var csv = string.Join(',', RunExporter.CsvHeader) + "\r\n" + string.Join(',', values);
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", text: csv));
        Assert.Equal("'=真实单引号", Text(row, "duty_name"));
        Assert.Equal("''+真实双引号", Text(row, "job_name"));
    }

    [Fact]
    public void Csv_NonnativeOrPartialHeaderNeverStripsApostrophe()
    {
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", text: "duty_name,reflection_text\n\"'=文本\",\"'+正文\""));
        Assert.Equal("'=文本", Text(row, "duty_name"));
        Assert.Equal("'+正文", Text(row, "reflection_text"));
    }

    [Fact]
    public void Csv_ExplicitMappingPreservesRawStringsAndReportsDuplicateTargets()
    {
        var mapping = new Dictionary<string, string> { ["Maze"] = "duty_name", ["Comment"] = "reflection_text", ["Seconds"] = "duration_ms" };
        var row = Assert.Single(RunImportSourceParser.Parse("CSV", text: "Maze,Comment,Seconds\n  合成  ,正文,1:30", columnMapping: mapping));
        Assert.Equal("  合成  ", Text(row, "duty_name"));
        Assert.Equal("1:30", Text(row, "duration_ms"));
        Assert.Empty(row.Errors);
        var duplicate = Assert.Single(RunImportSourceParser.Parse("CSV", text: "副本,副本名称\n一,二"));
        Assert.NotEmpty(duplicate.Errors);
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("CSV", text: "x\na", columnMapping: new Dictionary<string, string> { ["x"] = "capture_session_id" }));
    }

    [Fact]
    public void Paste_AcceptsQuotedMultilineTabularTextWithoutChangingSourceTime()
    {
        var row = Assert.Single(RunImportSourceParser.Parse("PASTE", text: "副本\t职业\t心得\t记录时间\r\n合成\t白魔法师\t\"一\r\n二\"\t2026-10-01 10:20"));
        Assert.Equal("一\r\n二", Text(row, "reflection_text"));
        Assert.Equal("2026-10-01 10:20", Text(row, "source_recorded_at"));
        Assert.Empty(row.Errors);
    }

    [Fact]
    public void Json_ReadsRealRunExportAndProjectsReflectionWithoutCaptureReferences()
    {
        using var database = new TestDatabase();
        var run = TestDatabase.Run() with { ProtocolProfileId = "foreign-profile" };
        database.Database.RunInTransaction(tx =>
        {
            new RunRepository(database.Database).Insert(run, tx);
            new RunReflectionRepository(database.Database).Upsert(run.RunId, ReflectionMood.Ok, "合成心得", database.Clock.UtcNow, tx);
        });
        var path = Path.Combine(_directory, "native.json");
        new RunExporter(new RunRepository(database.Database), database.Clock).ExportJson(path, null, false);
        var row = Assert.Single(RunImportSourceParser.Parse("JSON", path));
        Assert.Empty(row.Errors);
        Assert.Equal(run.RunId, Text(row, "run_id"));
        Assert.Equal("合成心得", Text(row, "reflection_text"));
        Assert.Equal("ok", Text(row, "reflection_mood"));
        Assert.False(row.Values.ContainsKey("capture_session_id"));
        Assert.False(row.Values.ContainsKey("protocol_profile_id"));
        Assert.Equal(120_000L, row.Values["duration_ms"]!.GetValue<long>());
    }

    [Fact]
    public void Json_InvalidObjectIsRowErrorButInvalidRootIsFileError()
    {
        var rows = RunImportSourceParser.Parse("JSON", text: "[null,{\"duty_name\":\"有效\"}]");
        Assert.NotEmpty(rows[0].Errors);
        Assert.Empty(rows[1].Errors);
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("JSON", text: "{\"runs\":[]}"));
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("JSON", text: "[{\"duty_name\":\"一\",\"duty_name\":\"二\"}]"));
    }

    [Fact]
    public void Json_PreservesIndependentImportedSourceTimeAndUnknownMoodWithoutGuessingFacts()
    {
        var row = Assert.Single(RunImportSourceParser.Parse("JSON", text: "[{\"duty_name\":\"合成\",\"reflection\":{\"text\":\"合成心得\",\"mood\":\"unknown\"},\"import_metadata\":{\"source_name\":\"合成来源\",\"source_recorded_at\":\"2026-10-01 20:30\",\"source_recorded_at_utc\":\"2026-10-01T12:30:00.000Z\",\"mentor_confirmed\":false}}]"));
        Assert.Empty(row.Errors);
        Assert.Equal("unknown", Text(row, "reflection_mood"));
        Assert.Equal("2026-10-01 20:30", Text(row, "source_recorded_at"));
        Assert.Equal("2026-10-01T12:30:00.000Z", Text(row, "source_recorded_at_utc"));
        Assert.False(row.Values["mentor_confirmed"]!.GetValue<bool>());
        Assert.False(row.Values.ContainsKey("entered_at_utc"));
        var noMood = Assert.Single(RunImportSourceParser.Parse("CSV", text: "副本,心得\n合成,心得"));
        Assert.False(noMood.Values.ContainsKey("reflection_mood"));
    }

    [Theory]
    [InlineData("ROWS")]
    [InlineData("SCREENSHOT")]
    [InlineData("CSV")]
    [InlineData("XLSX")]
    public void EditedCandidatesTakePrecedenceAndCloneEvidence(string sourceKind)
    {
        var candidate = new JsonObject
        {
            ["duty_name"] = "合成", ["source_recorded_at"] = "2026-10-01 09:00", ["incomplete"] = true,
            ["warnings"] = new JsonArray("需要核对职业"), ["evidence"] = new JsonObject { ["y"] = 100 },
            ["job_candidates"] = new JsonArray(19, 21), ["ocr_confidence"] = 0.8,
        };
        var row = Assert.Single(RunImportSourceParser.Parse(sourceKind, rows: new JsonArray(candidate)));
        Assert.Empty(row.Errors);
        row.Values["duty_name"] = "编辑";
        ((JsonObject)row.Values["evidence"]!)["y"] = 200;
        Assert.Equal("合成", candidate["duty_name"]!.GetValue<string>());
        Assert.Equal(100, candidate["evidence"]!["y"]!.GetValue<int>());
        Assert.Equal("2026-10-01 09:00", Text(row, "source_recorded_at"));
        Assert.True(row.Values["incomplete"]!.GetValue<bool>());
        Assert.Equal(0.8, row.Values["ocr_confidence"]!.GetValue<double>());
    }

    [Fact]
    public void Inputs_EnforceRowColumnFileAndCellBounds()
    {
        var longRow = Assert.Single(RunImportSourceParser.Parse("CSV", text: "心得\n" + new string('中', 8001)));
        Assert.NotEmpty(longRow.Errors);
        Assert.Equal(8000, Text(longRow, "reflection_text").Length);
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("CSV", text: "副本\n" + string.Join('\n', Enumerable.Repeat("合成", 5001))));
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("PASTE", text: string.Join('\t', Enumerable.Repeat("副本", 201))));
        var path = Path.Combine(_directory, "large.csv");
        using (var file = File.Create(path)) file.SetLength(20 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("CSV", path));
        var rows = new JsonArray();
        for (var i = 0; i < 5001; i++) rows.Add(new JsonObject());
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("ROWS", rows: rows));
        var invalidUnicode = Assert.Single(RunImportSourceParser.Parse("ROWS", rows: new JsonArray(new JsonObject { ["reflection_text"] = "\uD800" })));
        Assert.NotEmpty(invalidUnicode.Errors);
        Assert.Null(invalidUnicode.Values["reflection_text"]);
    }

    [Fact]
    public void Xlsx_ReadsSharedRichAndInlineStringsSparseRowsAndDateStyle()
    {
        var shared = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>副本</t></si><si><t>心得</t></si><si><t>记录时间</t></si><si><r><t>合成</t></r><r><t>副本</t></r><rPh><t>拼音不导入</t></rPh></si></sst>";
        var worksheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
            <row r="3"><c r="A3" t="s"><v>3</v></c><c r="B3" t="inlineStr"><is><t>第一行
            第二行</t></is></c><c r="C3" s="1"><v>46295.5</v></c></row>
            <row r="5"><c r="A5" t="inlineStr"><is><t>仅副本</t></is></c></row>
            </sheetData></worksheet>
            """;
        var rows = RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, sharedStrings: shared));
        Assert.Equal(2, rows.Count);
        Assert.Equal(3, rows[0].RowNumber);
        Assert.Equal("合成副本", Text(rows[0], "duty_name"));
        Assert.Equal("第一行\n第二行", Text(rows[0], "reflection_text"));
        Assert.Equal(new DateTime(1899, 12, 30).AddDays(46295.5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture), Text(rows[0], "source_recorded_at"));
        Assert.Equal(5, rows[1].RowNumber);
        Assert.Equal(string.Empty, Text(rows[1], "reflection_text"));
        Assert.All(rows, row => Assert.Empty(row.Errors));
    }

    [Fact]
    public void Xlsx_ResolvesWorkbookRelationshipAnd1904DateSystem()
    {
        var worksheet = """
            <worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>记录时间</t></is></c></row>
            <row r="2"><c r="A2" s="1"><v>0.5</v></c></row></sheetData></worksheet>
            """;
        var row = Assert.Single(RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, date1904: true, sheetName: "custom.xml")));
        Assert.Empty(row.Errors);
        Assert.Equal("1904-01-01T12:00:00.000", Text(row, "source_recorded_at"));
    }

    [Fact]
    public void Xlsx_DecodesOoxmlTextEscapesOnceAndReportsInvalidUnicode()
    {
        var worksheet = """
            <worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>心得</t></is></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>一_x000D__x000A_二_x005F_x000A_</t></is></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>_xD800_</t></is></c></row></sheetData></worksheet>
            """;
        var rows = RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet));
        Assert.Equal("一\r\n二_x000A_", Text(rows[0], "reflection_text"));
        Assert.Empty(rows[0].Errors);
        Assert.NotEmpty(rows[1].Errors);
    }

    [Fact]
    public void Xlsx_RecognisesCustomDateFormatWithoutTreatingQuotedUnitsAsDate()
    {
        var worksheet = """
            <worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>记录时间</t></is></c><c r="B1" t="inlineStr"><is><t>耗时</t></is></c></row>
            <row r="2"><c r="A2" s="1"><v>46295</v></c><c r="B2" s="2"><v>15000</v></c></row></sheetData></worksheet>
            """;
        var styles = "<styleSheet><numFmts><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm\"/><numFmt numFmtId=\"165\" formatCode=\"0 &amp;quot;ms&amp;quot;\"/></numFmts><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"164\"/><xf numFmtId=\"165\"/></cellXfs></styleSheet>".Replace("&amp;quot;", "&quot;", StringComparison.Ordinal);
        var row = Assert.Single(RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, styles: styles)));
        Assert.Empty(row.Errors);
        Assert.EndsWith("T00:00:00.000", Text(row, "source_recorded_at"), StringComparison.Ordinal);
        Assert.Equal("15000", Text(row, "duration_ms"));
    }

    [Theory]
    [InlineData(14, null)]
    [InlineData(15, null)]
    [InlineData(16, null)]
    [InlineData(17, null)]
    [InlineData(164, "yyyy-mm-dd")]
    [InlineData(164, "yyyy-mm-dd \"HH:mm\"")]
    public void Xlsx_DateOnlyFormatKeepsDateWithoutInventingMidnight(int formatId, string? format)
    {
        var worksheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>记录时间</t></is></c></row><row r=\"2\"><c r=\"A2\" s=\"1\"><v>46295.5</v></c></row></sheetData></worksheet>";
        var row = Assert.Single(RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, styles: TemporalStyles(formatId, format))));
        Assert.Empty(row.Errors);
        Assert.Equal(new DateTime(1899, 12, 30).AddDays(46295).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Text(row, "source_recorded_at"));
        Assert.DoesNotContain('T', Text(row, "source_recorded_at"));
    }

    [Theory]
    [InlineData(18, null)]
    [InlineData(19, null)]
    [InlineData(20, null)]
    [InlineData(21, null)]
    [InlineData(45, null)]
    [InlineData(46, null)]
    [InlineData(47, null)]
    [InlineData(164, "h:mm")]
    [InlineData(164, "[h]:mm:ss")]
    public void Xlsx_TimeOnlyFormatKeepsClockWithoutInventingCalendarDate(int formatId, string? format)
    {
        var worksheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>开始时间</t></is></c></row><row r=\"2\"><c r=\"A2\" s=\"1\"><v>0.5</v></c></row></sheetData></worksheet>";
        var row = Assert.Single(RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, styles: TemporalStyles(formatId, format))));
        Assert.Empty(row.Errors);
        Assert.Equal("12:00:00.000", Text(row, "entered_at_utc"));
        Assert.DoesNotContain("1899", Text(row, "entered_at_utc"), StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_ElapsedTimeKeepsHoursAcrossDaysWithoutInventingCalendarDate()
    {
        var worksheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>耗时</t></is></c></row><row r=\"2\"><c r=\"A2\" s=\"1\"><v>1.5</v></c></row></sheetData></worksheet>";
        var row = Assert.Single(RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet, styles: TemporalStyles(164, "[h]:mm:ss"))));
        Assert.Empty(row.Errors);
        Assert.Equal("36:00:00.000", Text(row, "duration_ms"));
    }

    [Theory]
    [InlineData(14, null, "46295", "46296")]
    [InlineData(20, null, "0.5", "0.75")]
    [InlineData(164, "[h]:mm:ss", "0.5", "0.75")]
    public void Xlsx_DateOrTimeOnlyActualEndpointsCannotCommitOrCountDefaultCompletion(
        int formatId, string? format, string entered, string ended)
    {
        using var fixture = new TestDatabase();
        var worksheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>副本</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>开始时间</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>结束时间</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>合成副本</t></is></c><c r=\"B2\" s=\"1\"><v>" + entered + "</v></c><c r=\"C2\" s=\"1\"><v>" + ended + "</v></c></row></sheetData></worksheet>";
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("XLSX", WriteWorkbook(worksheet, styles: TemporalStyles(formatId, format)), timeZone: "+08:00");
        var row = Assert.Single(preview["rows"]!.AsArray())!.AsObject();
        Assert.Equal("invalid", row["status"]!.GetValue<string>());
        Assert.False(row["can_import"]!.GetValue<bool>());
        Assert.NotEmpty(row["errors"]!.AsArray());
        Assert.Throws<CollectorException>(() => service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 2 }, true, Guid.NewGuid().ToString("D")));
        using var count = fixture.Database.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM mentor_runs;";
        Assert.Equal(0L, (long)count.ExecuteScalar()!);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        Assert.Equal(0, new StatisticsRepository(fixture.Database, settings).GetDashboard().CompletedCount);
    }

    [Fact]
    public void Xlsx_DateOnlySourceTimeRemainsRawDateAndReviewedCompletionCounts()
    {
        using var fixture = new TestDatabase();
        var worksheet = "<worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>副本</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>记录时间</t></is></c></row><row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>合成副本</t></is></c><c r=\"B2\" s=\"1\"><v>46295</v></c></row></sheetData></worksheet>";
        var service = new RunImportService(fixture.Database, fixture.Clock);
        var preview = service.PreviewSource("XLSX", WriteWorkbook(worksheet, styles: TemporalStyles(14, null)), timeZone: "+08:00");
        var row = Assert.Single(preview["rows"]!.AsArray())!.AsObject();
        Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.True(row["incomplete"]!.GetValue<bool>());
        var committed = service.Commit(preview["preview_id"]!.GetValue<string>(), new[] { 2 }, true, Guid.NewGuid().ToString("D"));
        var run = new RunRepository(fixture.Database).Get(committed["run_ids"]![0]!.GetValue<string>())!;
        Assert.Equal(new DateTime(1899, 12, 30).AddDays(46295).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), run.ImportMetadata!.SourceRecordedAt);
        Assert.Null(run.ImportMetadata.SourceRecordedAtUtc);
        Assert.Null(run.EnteredAtUtc);
        Assert.Null(run.EndedAtUtc);
        var settings = new SettingsRepository(fixture.Database, fixture.Clock);
        settings.EnsureDefaults();
        Assert.False(run.PendingReview);
        Assert.Equal(1, new StatisticsRepository(fixture.Database, settings).GetDashboard().CompletedCount);
    }

    [Fact]
    public void Xlsx_FormulaInvalidSharedStringAndFictionalLeapDayStayRowErrors()
    {
        var worksheet = """
            <worksheet><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>心得</t></is></c><c r="B1" t="inlineStr"><is><t>记录时间</t></is></c></row>
            <row r="2"><c r="A2" t="str"><f>HYPERLINK("https://invalid.test")</f><v>不可信缓存</v></c><c r="B2" s="1"><v>60</v></c></row>
            <row r="3"><c r="A3" t="s"><v>99</v></c></row>
            <row r="4"><c r="A4" t="inlineStr"><is><t>有效</t></is></c></row></sheetData></worksheet>
            """;
        var rows = RunImportSourceParser.Parse("XLSX", WriteWorkbook(worksheet));
        Assert.Equal(3, rows.Count);
        Assert.Equal(string.Empty, Text(rows[0], "reflection_text"));
        Assert.Contains(rows[0].Errors, error => error.Contains("公式", StringComparison.Ordinal));
        Assert.Contains(rows[0].Errors, error => error.Contains("1900", StringComparison.Ordinal));
        Assert.NotEmpty(rows[1].Errors);
        Assert.Empty(rows[2].Errors);
    }

    [Fact]
    public void Xlsx_RejectsMacrosExternalRelationshipsAndDtd()
    {
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", WriteWorkbook("<worksheet/>", macro: true)));
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", WriteWorkbook("<worksheet/>", external: true)));
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("XLSX", WriteWorkbook("<!DOCTYPE x [<!ENTITY value SYSTEM 'file:///not-read'>]><worksheet>&value;</worksheet>")));
    }

    [Fact]
    public void Backup_ReadsSoftwareSnapshotAndReflectionWithoutChangingFileOrProjectingCaptureOrSettings()
    {
        using var database = new TestDatabase();
        var run = TestDatabase.Run() with { ProtocolProfileId = "foreign-profile" };
        database.Database.RunInTransaction(tx =>
        {
            new RunRepository(database.Database).Insert(run, tx);
            new RunReflectionRepository(database.Database).Upsert(run.RunId, ReflectionMood.Bad, "备份合成心得", database.Clock.UtcNow, tx);
        });
        var path = Path.Combine(_directory, "software.db");
        database.Database.BackupDatabase(path, false);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var row = Assert.Single(RunImportSourceParser.Parse("BACKUP", path));
        Assert.Empty(row.Errors);
        Assert.Equal(run.RunId, Text(row, "run_id"));
        Assert.Equal("备份合成心得", Text(row, "reflection_text"));
        Assert.Equal("bad", Text(row, "reflection_mood"));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.False(row.Values.ContainsKey("capture_session_id"));
        Assert.False(row.Values.ContainsKey("protocol_profile_id"));
        Assert.False(row.Values.ContainsKey("achievement_settings"));
    }

    [Fact]
    public void Backup_AcceptsKnownInitialSchemaWithoutPendingReviewOrReflection()
    {
        var path = Path.Combine(_directory, "version1.db");
        var id = Guid.NewGuid().ToString("D");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var stream = typeof(RunExporter).Assembly.GetManifestResourceStream("MentorRecorder.Collector.Migrations.0001_initial.sql")!;
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO mentor_runs(run_id,revision,result,source,duty_name,created_at_utc,updated_at_utc) VALUES($id,1,'UNKNOWN','MANUAL','旧备份合成','2026-10-01T00:00:00.000Z','2026-10-01T00:00:00.000Z');";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        var row = Assert.Single(RunImportSourceParser.Parse("BACKUP", path));
        Assert.Empty(row.Errors);
        Assert.Equal(id, Text(row, "run_id"));
        Assert.False(row.Values.ContainsKey("pending_review"));
        Assert.False(row.Values.ContainsKey("reflection_text"));
    }

    [Fact]
    public void Backup_ProjectsSourceTimesWithoutReusingForeignFingerprint()
    {
        using var database = new TestDatabase();
        var run = TestDatabase.Run();
        database.Database.RunInTransaction(tx => new RunRepository(database.Database).Insert(run, tx));
        // 固定字段夹具兼容迁移引入前后的测试构建，不改变项目迁移文件。
        using (var command = database.Database.CreateCommand())
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS run_import_metadata(run_id TEXT PRIMARY KEY,source_kind TEXT,source_name TEXT,source_recorded_at TEXT,source_recorded_at_utc TEXT,imported_at_utc TEXT,source_fingerprint TEXT,mentor_confirmed INTEGER);";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO run_import_metadata(run_id,source_kind,source_name,source_recorded_at,source_recorded_at_utc,imported_at_utc,source_fingerprint,mentor_confirmed) VALUES($id,'SCREENSHOT','合成来源','2026-10-01 20:30','2026-10-01T12:30:00.000Z','2026-10-01T13:00:00.000Z',$fingerprint,0);";
            command.Parameters.AddWithValue("$id", run.RunId);
            command.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            command.ExecuteNonQuery();
        }
        var path = Path.Combine(_directory, "metadata.db");
        database.Database.BackupDatabase(path, false);
        var row = Assert.Single(RunImportSourceParser.Parse("BACKUP", path));
        Assert.Empty(row.Errors);
        Assert.Equal("2026-10-01 20:30", Text(row, "source_recorded_at"));
        Assert.Equal("2026-10-01T12:30:00.000Z", Text(row, "source_recorded_at_utc"));
        Assert.Equal(0L, row.Values["mentor_confirmed"]!.GetValue<long>());
        Assert.False(row.Values.ContainsKey("source_fingerprint"));
        Assert.False(row.Values.ContainsKey("source_key"));
    }

    [Fact]
    public void Backup_RejectsActiveWalAndNonsoftwareStructure()
    {
        var path = Path.Combine(_directory, "fake.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE settings(value TEXT); CREATE VIEW mentor_runs AS SELECT value FROM settings;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("BACKUP", path));
        File.WriteAllText(path + "-wal", "synthetic");
        Assert.Throws<InvalidDataException>(() => RunImportSourceParser.Parse("BACKUP", path));
    }

    private static string Text(ImportSourceRow row, string field) => row.Values[field]!.GetValue<string>();

    private string WriteText(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string WriteWorkbook(string worksheet, string? sharedStrings = null, bool date1904 = false,
        string sheetName = "sheet1.xml", string? styles = null, bool macro = false, bool external = false)
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
        Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>");
        Add("xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"" + (date1904 ? "1" : "0") + "\"/><sheets><sheet name=\"合成\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/" + sheetName + "\"" + (external ? " TargetMode=\"External\"" : "") + "/></Relationships>");
        Add("xl/worksheets/" + sheetName, worksheet);
        Add("xl/styles.xml", styles ?? "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"22\"/></cellXfs></styleSheet>");
        if (sharedStrings is not null) Add("xl/sharedStrings.xml", sharedStrings);
        if (macro) Add("xl/vbaProject.bin", "synthetic macro marker, never executed");
        return path;
    }

    private static string TemporalStyles(int formatId, string? format) => "<styleSheet>" +
        (format is null ? string.Empty : "<numFmts><numFmt numFmtId=\"" + formatId + "\" formatCode=\"" + System.Security.SecurityElement.Escape(format) + "\"/></numFmts>") +
        "<cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"" + formatId + "\"/></cellXfs></styleSheet>";

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
