using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>合成的实际 XLSX 文件，经独立管道和临时库验证进入方式三态；不代表反馈者原表验收。</summary>
public sealed class RunImportEntryModeIpcTests
{
    [Theory]
    [InlineData("进入方式")]
    [InlineData("进本方式")]
    [InlineData("玩家自定义列")]
    public async Task ActualXlsxPreviewEditCommitRetryReadbackAndStatisticsPreserveUnknownEntry(string header)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "synthetic-entry-mode.xlsx");
        WriteWorkbook(path, header);
        var request = new JsonObject { ["source_kind"] = "XLSX", ["file_path"] = path, ["source_name"] = "合成进入方式工作簿" };
        if (header == "玩家自定义列") request["column_mapping"] = new JsonObject { [header] = "joined_in_progress" };
        var preview = (await client.SendAsync("PreviewRunImport", request.DeepClone().AsObject())).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "entry-mode actual XLSX preview");
        var rows = preview["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        Assert.Equal(new bool?[] { false, true, null }, rows.Select(row => row!["run"]!["joined_in_progress"]?.GetValue<bool>()));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var candidates = new JsonArray(rows.Select(row => row!["candidate"]!.DeepClone()).ToArray());
        candidates[0]!["joined_in_progress"] = "开局进入";
        candidates[1]!["joined_in_progress"] = "中途加入";
        candidates[2]!["joined_in_progress"] = " \t ";
        var checkedPreview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["rows"] = candidates, ["source_name"] = "合成进入方式工作簿",
        })).Require();
        Assert.All(checkedPreview["rows"]!.AsArray(), row => Assert.True(row!["can_import"]!.GetValue<bool>()));
        var commitRequest = new JsonObject
        {
            ["preview_id"] = checkedPreview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1, 2, 3),
            ["confirm_own_records"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", commitRequest.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(3, committed["imported_count"]!.GetValue<int>());
        var retry = (await client.SendAsync("CommitRunImport", commitRequest.DeepClone().AsObject(), requestId)).Require();
        Assert.True(retry["replayed"]!.GetValue<bool>());
        var stored = (await client.SendAsync("QueryRuns")).Require();
        ContractSchema.Validate("$defs/Responses/QueryRuns", stored, "entry-mode stored three states");
        Assert.Equal(3, stored["page_info"]!["total"]!.GetValue<int>());
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"]?.GetValue<bool>() == false);
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"]?.GetValue<bool>() == true);
        Assert.Single(stored["items"]!.AsArray(), row => row!["joined_in_progress"] is null);
        var stats = (await client.SendAsync("GetDashboardStats")).Require();
        Assert.Equal(3, stats["attempt_count"]!.GetValue<int>());
        Assert.Equal(1, stats["joined_in_progress_count"]!.GetValue<int>());
        Assert.Equal(0.5, stats["joined_in_progress_rate"]!.GetValue<double>());
        var repeated = (await client.SendAsync("PreviewRunImport", request.DeepClone().AsObject())).Require();
        Assert.All(repeated["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        foreach (var kind in new[] { "JSON", "CSV" })
        {
            var exportPath = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "entry-export." + kind.ToLowerInvariant());
            (await client.SendAsync("Export" + (kind == "JSON" ? "Json" : "Csv"), new JsonObject { ["target_path"] = exportPath })).Require();
            var native = (await client.SendAsync("PreviewRunImport", new JsonObject
            {
                ["source_kind"] = kind, ["file_path"] = exportPath, ["time_zone"] = "+08:00",
            })).Require();
            Assert.All(native["rows"]!.AsArray(), row => Assert.Equal("duplicate", row!["status"]!.GetValue<string>()));
        }
    }

    private static void WriteWorkbook(string path, string header)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var output = zip.CreateEntry(name).Open();
            using var writer = new StreamWriter(output, new UTF8Encoding(false));
            writer.Write(xml);
        }
        Add("[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);
        Add("_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);
        Add("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="合成记录" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);
        Add("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement Row(int number, params string[] values) => new(ns + "row", new XAttribute("r", number),
            values.Select((value, column) => new XElement(ns + "c", new XAttribute("r", (char)('A' + column) + number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)))));
        var sheet = new XDocument(new XElement(ns + "worksheet", new XElement(ns + "sheetData",
            Row(1, "副本", "职业", "记录时间", header),
            Row(2, "水晶塔 古代人迷宫", "骑士", "2026-09-01", "正常"),
            Row(3, "水晶塔 古代人迷宫", "骑士", "2026-09-02", "中途"),
            Row(4, "水晶塔 古代人迷宫", "骑士", "2026-09-03", ""))));
        Add("xl/worksheets/sheet1.xml", sheet.ToString());
    }
}
