using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>真实 XLS 文件经唯一命名管道和临时数据库覆盖预览、确认、重复和冲突。</summary>
public sealed class RunImportXlsIpcTests
{
    [Fact]
    public async Task BinaryFilePreviewCommitRetryDuplicateAndConflictPreserveLocalFacts()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "synthetic.xls");
        var id = Guid.NewGuid().ToString("D"); Write(path, id, "本地应保留的合成备注");
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLS", ["file_path"] = path, ["source_name"] = "合成二进制表格", ["time_zone"] = "+08:00",
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "binary XLS preview");
        Assert.Equal("XLS", preview["source_kind"]!.GetValue<string>());
        var row = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.Equal(2, row["row_number"]!.GetValue<int>()); Assert.True(row["can_import"]!.GetValue<bool>());
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var payload = new JsonObject { ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = false };
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        payload["confirm_own_records"] = true; var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        var retry = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.True(retry["replayed"]!.GetValue<bool>()); ContractSchema.Validate("$defs/Responses/CommitRunImport", retry, "binary XLS retry");
        var stored = Assert.Single((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray())!;
        ContractSchema.Validate("$defs/Run", stored, "binary XLS saved run");
        Assert.Equal(id, stored["run_id"]!.GetValue<string>());
        Assert.Equal("XLS", stored["import_metadata"]!["source_kind"]!.GetValue<string>());
        Assert.Equal("本地应保留的合成备注", stored["note"]!.GetValue<string>());
        Assert.Equal("2026-10-09T12:00:00.000Z", stored["entered_at_utc"]!.GetValue<string>());
        Assert.Equal("合成导入心得", stored["reflection"]!["text"]!.GetValue<string>());

        async Task<JsonObject> Preview() => (await client.SendAsync("PreviewRunImport", new JsonObject
        { ["source_kind"] = "XLS", ["file_path"] = path, ["time_zone"] = "+08:00" })).Require();
        var duplicate = await Preview(); Assert.Equal("duplicate", duplicate["rows"]![0]!["status"]!.GetValue<string>());
        var duplicateCommit = (await client.SendAsync("CommitRunImport", new JsonObject
        { ["preview_id"] = duplicate["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = true })).Require();
        Assert.Equal(0, duplicateCommit["imported_count"]!.GetValue<int>()); Assert.Equal(1, duplicateCommit["duplicate_count"]!.GetValue<int>());
        Write(path, id, "外来冲突不能覆盖");
        var conflict = await Preview(); Assert.Equal("conflict", conflict["rows"]![0]!["status"]!.GetValue<string>());
        var conflictCommit = (await client.SendAsync("CommitRunImport", new JsonObject
        { ["preview_id"] = conflict["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(2), ["confirm_own_records"] = true })).Require();
        Assert.Equal(0, conflictCommit["imported_count"]!.GetValue<int>()); Assert.Equal(1, conflictCommit["conflict_count"]!.GetValue<int>());
        stored = Assert.Single((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray())!;
        Assert.Equal("本地应保留的合成备注", stored["note"]!.GetValue<string>());
        Assert.Equal(1, stored["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task DamagedRealFileReturnsActionableErrorAndPipeRemainsUsable()
    {
        await using var fixture = ServerFixture.Start(); await using var client = await fixture.ConnectAsync();
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "renamed.xls"); File.WriteAllText(path, "<html>not BIFF8</html>");
        var response = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "XLS", ["file_path"] = path });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode); Assert.Contains("真实", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    private static void Write(string path, string id, string note)
    {
        using var workbook = new HSSFWorkbook(); var sheet = workbook.CreateSheet("本人记录");
        Set(sheet.CreateRow(0), "run_id", "副本", "职业", "开始时间", "结束时间", "结果", "心得", "备注");
        Set(sheet.CreateRow(1), id, "合成导入副本", "骑士", "2026-10-09 20:00", "2026-10-09 20:20", "COMPLETED", "合成导入心得", note);
        using var file = File.Create(path); workbook.Write(file, leaveOpen: true);
    }
    private static void Set(IRow row, params string[] cells) { for (var i = 0; i < cells.Length; i++) row.CreateCell(i).SetCellValue(cells[i]); }
}
