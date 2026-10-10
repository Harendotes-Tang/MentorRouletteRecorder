using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class HistoryBatchIpcTests
{
    private static JsonObject Manual() => new()
    {
        ["result"] = "COMPLETED", ["entered_at_utc"] = "2026-09-01T00:00:00.000Z",
        ["ended_at_utc"] = "2026-09-01T00:30:00.000Z", ["reason"] = "批量IPC验证", ["note"] = "permanent-body-secret",
    };
    private static JsonObject Batch(string action, params (string Id, int Revision)[] runs) => new()
    {
        ["action"] = action, ["reason"] = "批量IPC验证",
        ["runs"] = new JsonArray(runs.Select(run => (JsonNode)new JsonObject { ["run_id"] = run.Id, ["expected_revision"] = run.Revision }).ToArray()),
    };

    [Fact]
    public async Task RealPipe_StaleBatchDeletesNothingThenPurgeRefusesOldBodyReplay()
    {
        await using var f = ServerFixture.Start(); await using var client = await f.ConnectAsync();
        var creationId = Guid.NewGuid().ToString("D");
        var first = (await client.SendAsync("CreateManualRun", Manual(), creationId)).Require()["run_id"]!.GetValue<string>();
        var second = (await client.SendAsync("CreateManualRun", Manual())).Require()["run_id"]!.GetValue<string>();
        var refused = await client.SendAsync("BatchMutateRuns", Batch("soft_delete", (first, 1), (second, 99)));
        Assert.Equal(ErrorCodes.RevisionConflict, refused.ErrorCode);
        Assert.False(f.Host.Runs.Get(first)!.SoftDeleted); Assert.False(f.Host.Runs.Get(second)!.SoftDeleted);
        var deleted = (await client.SendAsync("BatchMutateRuns", Batch("soft_delete", (first, 1), (second, 1)))).Require();
        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", deleted, "batch delete IPC");
        Assert.Equal(2, deleted["changed_count"]!.GetValue<int>());
        var purge = (await client.SendAsync("BatchMutateRuns", Batch("purge", (first, 2)))).Require();
        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", purge, "controlled purge IPC");
        Assert.Null(f.Host.Runs.Get(first)); Assert.NotNull(f.Host.Runs.Get(second));
        Assert.Equal(ErrorCodes.IdempotencyConflict, (await client.SendAsync("CreateManualRun", Manual(), creationId)).ErrorCode);
        var pending = (await client.SendAsync("GetPendingImageCleanup")).Require();
        ContractSchema.Validate("$defs/Responses/GetPendingImageCleanup", pending, "cleanup pending IPC");
        Assert.Contains(first, pending["run_ids"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(1, (await client.SendAsync("AcknowledgeImageCleanup", new JsonObject { ["run_ids"] = new JsonArray(first) })).Require()["acknowledged_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task RealPipe_ExactIdExportNeverSilentlyDropsMissingSelection()
    {
        await using var f = ServerFixture.Start(); await using var client = await f.ConnectAsync();
        var first = (await client.SendAsync("CreateManualRun", Manual())).Require()["run_id"]!.GetValue<string>();
        var second = (await client.SendAsync("CreateManualRun", Manual())).Require()["run_id"]!.GetValue<string>();
        var target = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "selected.json");
        var output = (await client.SendAsync("ExportJson", new JsonObject
        {
            ["target_path"] = target, ["filter"] = new JsonObject { ["run_id"] = new JsonArray(first) },
        })).Require();
        Assert.Equal(1, output["row_count"]!.GetValue<int>());
        var rows = JsonNode.Parse(File.ReadAllText(target))!.AsArray();
        Assert.Equal(first, Assert.Single(rows)!["run_id"]!.GetValue<string>());
        Assert.DoesNotContain(rows, node => node!["run_id"]!.GetValue<string>() == second);
        var missing = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "missing.json");
        var refused = await client.SendAsync("ExportJson", new JsonObject
        {
            ["target_path"] = missing, ["filter"] = new JsonObject { ["run_id"] = new JsonArray(first, Guid.NewGuid().ToString("D")) },
        });
        Assert.Equal(ErrorCodes.NotFound, refused.ErrorCode); Assert.False(File.Exists(missing));
    }
}
