using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Storage.Repositories;

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
    public async Task RealPipe_DeletedOnlyPagesCountsExportAndRestoreUseTheSameFilter()
    {
        await using var f = ServerFixture.Start();
        await using var client = await f.ConnectAsync();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
            ids.Add((await client.SendAsync("CreateManualRun", Manual())).Require()["run_id"]!.GetValue<string>());
        (await client.SendAsync("BatchMutateRuns", Batch("soft_delete", (ids[0], 1), (ids[1], 1)))).Require();
        var deleted = new JsonObject { ["deleted_only"] = true };
        ContractSchema.Validate("$defs/RunFilter", deleted, "deleted-only filter");
        var found = new HashSet<string>();
        for (var page = 1; page <= 2; page++)
        {
            var listed = (await client.SendAsync("QueryRuns", new JsonObject
            {
                ["filter"] = deleted.DeepClone(), ["page"] = page, ["page_size"] = 1,
            })).Require();
            ContractSchema.Validate("$defs/Responses/QueryRuns", listed, "deleted-only page");
            Assert.Equal(2, listed["page_info"]!["total"]!.GetValue<int>());
            var item = Assert.Single(listed["items"]!.AsArray())!;
            Assert.True(item["soft_deleted"]!.GetValue<bool>());
            found.Add(item["run_id"]!.GetValue<string>());
        }
        Assert.Equal(2, found.Count);
        Assert.Contains(ids[0], found); Assert.Contains(ids[1], found);
        async Task<JsonObject> Query(JsonObject filter) => (await client.SendAsync("QueryRuns",
            new JsonObject { ["filter"] = filter, ["page"] = 1, ["page_size"] = 10 })).Require();
        Assert.Equal(1, (await Query(new JsonObject()))["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal(3, (await Query(new JsonObject { ["include_deleted"] = true }))["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal(2, (await Query(new JsonObject { ["include_deleted"] = true, ["deleted_only"] = true }))["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal(0, (await Query(new JsonObject { ["deleted_only"] = true, ["text"] = "no-such-note" }))["page_info"]!["total"]!.GetValue<int>());
        var stats = (await client.SendAsync("GetResultStats", new JsonObject
        {
            ["filter"] = new JsonObject { ["deleted_only"] = true, ["include_deleted"] = true },
        })).Require();
        Assert.Equal(1, stats["attempt_count"]!.GetValue<int>());
        var target = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "deleted.json");
        var output = (await client.SendAsync("ExportJson", new JsonObject { ["target_path"] = target, ["filter"] = deleted.DeepClone() })).Require();
        Assert.Equal(2, output["row_count"]!.GetValue<int>());
        Assert.All(JsonNode.Parse(File.ReadAllText(target))!.AsArray(), item => Assert.True(item!["soft_deleted"]!.GetValue<bool>()));
        (await client.SendAsync("BatchMutateRuns", Batch("restore", (ids[0], 2)))).Require();
        var remaining = await Query(deleted.DeepClone().AsObject());
        Assert.Equal(1, remaining["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal(ids[1], Assert.Single(remaining["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        Assert.Equal(2, (await Query(new JsonObject()))["page_info"]!["total"]!.GetValue<int>());
    }
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
    public async Task RealPipe_NewBatchInvalidatesOnceAndReplayRetainsTheSameResponseFields()
    {
        await using var f = ServerFixture.Start();
        await using var client = await f.ConnectAsync();
        var runId = (await client.SendAsync("CreateManualRun", Manual())).Require()["run_id"]!.GetValue<string>();
        var requestId = Guid.NewGuid().ToString("D");
        var before = f.Host.LiveEvents.ReplayCount;
        using var events = f.Host.LiveEvents.Subscribe(Guid.NewGuid().ToString("D"));
        for (var i = 0; i < before; i++)
            Assert.NotNull(await events.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None));

        var first = (await client.SendAsync("BatchMutateRuns", Batch("soft_delete", (runId, 1)), requestId)).Require();

        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", first, "new typed batch IPC");
        Assert.False(first["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal("soft_delete", first["action"]!.GetValue<string>());
        Assert.Equal(1, first["changed_count"]!.GetValue<int>());
        Assert.Equal(runId, Assert.Single(first["run_ids"]!.AsArray())!.GetValue<string>());
        Assert.Equal(before + 1, f.Host.LiveEvents.ReplayCount);
        var invalidated = Assert.IsType<JsonObject>(await events.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal("stats_invalidated", invalidated["kind"]!.GetValue<string>());

        var replay = (await client.SendAsync("BatchMutateRuns", Batch("soft_delete", (runId, 1)), requestId)).Require();
        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", replay, "typed batch replay IPC");
        Assert.True(replay["idempotent_replay"]!.GetValue<bool>());
        var expectedReplay = first.DeepClone().AsObject();
        expectedReplay["idempotent_replay"] = true;
        Assert.True(JsonNode.DeepEquals(expectedReplay, replay));
        Assert.Equal(before + 1, f.Host.LiveEvents.ReplayCount);
        Assert.Equal(2, f.Host.Runs.Get(runId)!.Revision);
    }

    [Fact]
    public async Task RealPipe_LegacyReceiptReplaysWithoutInvalidationAndChangedRequestConflicts()
    {
        const string firstId = "11111111-1111-1111-1111-111111111111";
        const string secondId = "22222222-2222-2222-2222-222222222222";
        const string legacy = """
            {"batch_fingerprint":"18d3f4fe675be4e85aa847595ff95a17f07f4a7a7bde5a6f101d13c2caf7edcb","outcome":{"changed_count":2,"run_ids":["11111111-1111-1111-1111-111111111111","22222222-2222-2222-2222-222222222222"],"action":"soft_delete","idempotent_replay":false}}
            """;
        var requestId = Guid.NewGuid().ToString("D");
        await using var f = ServerFixture.Start(host => host.Database.RunInTransaction(tx =>
            new IdempotencyRepository(host.Database, host.Clock).Store(requestId, "BatchMutateRuns", legacy, tx)));
        await using var client = await f.ConnectAsync();
        var payload = Batch("soft_delete", (secondId, 1), (firstId, 1));
        payload["reason"] = "legacy reason";
        var before = f.Host.LiveEvents.ReplayCount;

        var replay = (await client.SendAsync("BatchMutateRuns", payload, requestId)).Require();

        ContractSchema.Validate("$defs/Responses/BatchMutateRuns", replay, "legacy batch replay IPC");
        Assert.True(replay["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal(2, replay["changed_count"]!.GetValue<int>());
        Assert.Equal(new[] { firstId, secondId }, replay["run_ids"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(before, f.Host.LiveEvents.ReplayCount);

        var changed = payload.DeepClone().AsObject();
        changed["reason"] = "changed reason";
        var conflict = await client.SendAsync("BatchMutateRuns", changed, requestId);
        Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(before, f.Host.LiveEvents.ReplayCount);
        Assert.Equal(legacy, new IdempotencyRepository(f.Host.Database, f.Host.Clock).TryGetResponse(requestId));
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
