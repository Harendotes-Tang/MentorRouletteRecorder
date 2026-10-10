using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class RunImportIpcTests
{
    [Fact]
    public async Task MalformedSourceIsActionableBadRequestAndConnectionRemainsUsable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var response = await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "JSON", ["text"] = "{}",
        });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Contains("解析", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task EditedPreviewOwnConfirmationCommitRetryReflectionAndStatsUseRealPipe()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "SCREENSHOT", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(new JsonObject
            {
                ["duty_name"] = "合成截图副本", ["reflection_text"] = "合成截图心得",
                ["source_recorded_at"] = "2026-10-08 20:30:00", ["job_name"] = "骑士",
            }),
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "import preview");
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var candidate = preview["rows"]![0]!["candidate"]!.DeepClone().AsObject();
        candidate["reflection_text"] = "合成截图心得经核对";
        var edited = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "SCREENSHOT", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(candidate),
        })).Require();
        var payload = new JsonObject
        {
            ["preview_id"] = edited["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1),
            ["confirm_own_records"] = false,
        };
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        payload["confirm_own_records"] = true;
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        var retried = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        Assert.True(retried["replayed"]!.GetValue<bool>());
        ContractSchema.Validate("$defs/Responses/CommitRunImport", retried, "import retry");
        var query = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["with_reflection"] = true },
        })).Require();
        var run = Assert.Single(query["items"]!.AsArray())!;
        ContractSchema.Validate("$defs/Run", run, "imported history");
        Assert.Equal("合成截图心得经核对", run["reflection"]!["text"]!.GetValue<string>());
        Assert.Equal("unknown", run["reflection"]!["mood"]!.GetValue<string>());
        Assert.Equal("COMPLETED", run["result"]!.GetValue<string>());
        Assert.True(run["pending_review"]!.GetValue<bool>());
        Assert.Null(run["entered_at_utc"]);
        Assert.Null(run["ended_at_utc"]);
        Assert.Equal("2026-10-08T12:30:00.000Z", run["import_metadata"]!["source_recorded_at_utc"]!.GetValue<string>());
        Assert.Equal(0, (await client.SendAsync("GetDashboardStats")).Require()["completed_count"]!.GetValue<int>());
        var runId = run["run_id"]!.GetValue<string>();
        var incompleteCorrection = new JsonObject
        {
            ["run_id"] = runId, ["expected_revision"] = 1, ["reason"] = "合成补齐",
            ["changes"] = new JsonObject { ["result"] = "COMPLETED", ["pending_review"] = false },
        };
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("CorrectRun", incompleteCorrection.DeepClone().AsObject())).ErrorCode);
        var changes = incompleteCorrection["changes"]!.AsObject();
        changes["entered_at_utc"] = "2026-10-08T12:00:00.000Z";
        changes["ended_at_utc"] = "2026-10-08T12:20:00.000Z";
        changes["duration_ms"] = null;
        Assert.True((await client.SendAsync("CorrectRun", incompleteCorrection)).Ok);
        Assert.Equal(1, (await client.SendAsync("GetDashboardStats")).Require()["completed_count"]!.GetValue<int>());
        Assert.Equal(2, (await client.SendAsync("GetRunRevisions", new JsonObject { ["run_id"] = runId })).Require()["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task LargeChinesePreviewRefusesWithTypedErrorAndKeepsPipeUsable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var rows = new JsonArray(Enumerable.Range(0, 600).Select(_ => (JsonNode)new JsonObject
        {
            ["duty_name"] = "合成副本", ["reflection_text"] = new string('中', 1000),
        }).ToArray());
        var response = await client.SendAsync("PreviewRunImport", new JsonObject { ["source_kind"] = "SCREENSHOT", ["rows"] = rows });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Contains("分", response.ErrorMessage);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task DeductFromBaselineLowersTheStoredBaselineInTheSameCommit()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("UpdateAchievementBaseline", new JsonObject
        {
            ["goal_count"] = 2000, ["baseline_completed_count"] = 1500,
            ["baseline_effective_at"] = "2026-01-01T00:00:00.000Z", ["reason"] = "安装前已完成 1500 次",
        })).Require();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["time_zone"] = "+08:00", ["source_name"] = "synthetic",
            ["rows"] = new JsonArray(new JsonObject
            {
                ["duty_name"] = "合成表格副本", ["job_name"] = "骑士", ["result"] = "COMPLETED",
                ["entered_at_utc"] = "2026-10-01T12:00:00.000Z", ["ended_at_utc"] = "2026-10-01T12:20:00.000Z",
            }),
        })).Require();
        Assert.False(preview["rows"]![0]!["incomplete"]!.GetValue<bool>());
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = new JsonArray(1),
            ["confirm_own_records"] = true, ["deduct_from_baseline"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        ContractSchema.Validate("$defs/Responses/CommitRunImport", committed, "import deducting from the baseline");
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        Assert.Equal(1, committed["baseline_deducted_count"]!.GetValue<int>());
        Assert.Equal(1499, committed["baseline_completed_count"]!.GetValue<int>());
        var replayed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.True(replayed["replayed"]!.GetValue<bool>());
        Assert.Equal(1, replayed["baseline_deducted_count"]!.GetValue<int>());
        var dashboard = (await client.SendAsync("GetDashboardStats")).Require();
        Assert.Equal(1499, dashboard["baseline_completed_count"]!.GetValue<int>());
        Assert.Equal(1, dashboard["completed_count"]!.GetValue<int>());
        Assert.Equal(1500, dashboard["achievement_progress"]!.GetValue<int>());
    }
}
