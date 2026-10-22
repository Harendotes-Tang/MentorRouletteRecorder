using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class DutyCategoryIpcTests
{
    [Fact]
    public async Task RealPipeHistoryFilterStatsCalendarEditsAndExportSeparateDisplayFromRawFacts()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var source = new JsonArray(new JsonObject
        {
            ["region"] = "CN", ["content_id"] = 92, ["duty_category"] = "大型任务", ["job_id"] = 19,
            ["result"] = "COMPLETED", ["source_recorded_at"] = "2026-09-04",
        });
        async Task<JsonObject> Preview() => (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = source.DeepClone(),
        })).Require();
        var preview = await Preview();
        var committed = (await client.SendAsync("CommitRunImport", new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1),
            ["confirm_own_records"] = true,
        })).Require();
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        var runId = committed["run_ids"]![0]!.GetValue<string>();
        var filter = new JsonObject { ["duty_category"] = new JsonArray("团队任务") };
        var query = (await client.SendAsync("QueryRuns", new JsonObject { ["filter"] = filter.DeepClone() })).Require();
        ContractSchema.Validate("$defs/Responses/QueryRuns", query, "projected alliance history");
        Assert.Equal(1, query["page_info"]!["total"]!.GetValue<int>());
        var displayed = Assert.Single(query["items"]!.AsArray())!;
        Assert.Equal("团队任务", displayed["duty_category"]!.GetValue<string>());
        var raw = fixture.Host.Runs.Get(runId)!;
        Assert.Equal("大型任务", raw.DutyCategory);
        Assert.Equal("大型任务", Wire.Run(raw)["duty_category"]!.GetValue<string>());
        Assert.Empty((await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["duty_category"] = new JsonArray("大型任务") },
        })).Require()["items"]!.AsArray());
        var calendar = (await client.SendAsync("QueryRunCalendarDays", new JsonObject
        {
            ["month"] = "2026-09", ["filter"] = filter.DeepClone(),
        })).Require();
        Assert.Equal("2026-09-04", Assert.Single(calendar["days"]!.AsArray())!.GetValue<string>());
        var dungeon = (await client.SendAsync("GetDungeonStats", new JsonObject { ["filter"] = filter.DeepClone() })).Require();
        Assert.Equal("团队任务", Assert.Single(dungeon["items"]!.AsArray())!["duty_category"]!.GetValue<string>());
        var dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject { ["filter"] = filter.DeepClone() })).Require();
        Assert.Equal(1, dashboard["attempt_count"]!.GetValue<int>());
        var corrected = (await client.SendAsync("CorrectRun", new JsonObject
        {
            ["run_id"] = runId, ["expected_revision"] = 1, ["reason"] = "synthetic note-only edit",
            ["changes"] = new JsonObject { ["note"] = "note without category write" },
        })).Require();
        Assert.Equal("团队任务", corrected["run"]!["duty_category"]!.GetValue<string>());
        Assert.Equal("大型任务", fixture.Host.Runs.Get(runId)!.DutyCategory);
        var revisions = (await client.SendAsync("GetRunRevisions", new JsonObject { ["run_id"] = runId })).Require();
        var importedRevision = revisions["items"]!.AsArray().Single(node => node!["revision"]!.GetValue<int>() == 1)!;
        Assert.Contains(importedRevision["changes"]!.AsArray(), change =>
            change!["field"]!.GetValue<string>() == "duty_category" && change["new_value"]!.GetValue<string>() == "大型任务");
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "category-raw-export.json");
        (await client.SendAsync("ExportJson", new JsonObject { ["target_path"] = path, ["filter"] = filter.DeepClone() })).Require();
        Assert.Equal("大型任务", JsonNode.Parse(File.ReadAllText(path))![0]!["duty_category"]!.GetValue<string>());
        Assert.Equal("duplicate", (await Preview())["rows"]![0]!["status"]!.GetValue<string>());
    }
}
