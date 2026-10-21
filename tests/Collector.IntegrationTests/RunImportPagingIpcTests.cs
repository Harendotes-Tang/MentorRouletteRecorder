using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class RunImportPagingIpcTests
{
    private static JsonArray Rows(int count) => new(Enumerable.Range(1, count).Select(index => (JsonNode)new JsonObject
    {
        ["duty_name"] = "分页管道合成副本 " + index, ["job_name"] = "战士",
        ["result"] = "COMPLETED", ["source_recorded_at"] = "2022-09-28", ["reflection_text"] = "管道合成心得",
    }).ToArray());

    [Fact]
    public async Task All2090RowsTravelThroughRealPagesAndCommitWithOneIdempotentReceipt()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "XLSX", ["rows"] = Rows(2090), ["page_size"] = 100,
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "paged import");
        var rowNumbers = new JsonArray();
        for (var page = 1; rowNumbers.Count < 2090; page++)
        {
            var result = page == 1 ? preview : (await client.SendAsync("PreviewRunImport", new JsonObject
            {
                ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = page,
            })).Require();
            ContractSchema.Validate("$defs/Responses/PreviewRunImport", result, "continued import");
            Assert.Equal(page, result["page_info"]!["page"]!.GetValue<int>());
            foreach (var row in result["rows"]!.AsArray()) rowNumbers.Add(row!["row_number"]!.DeepClone());
        }
        Assert.Equal(Enumerable.Range(1, 2090), rowNumbers.Select(node => node!.GetValue<int>()));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["row_numbers"] = rowNumbers,
            ["confirm_own_records"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        var committed = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(2090, committed["imported_count"]!.GetValue<int>());
        var replay = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.True(replay["replayed"]!.GetValue<bool>());
        Assert.Equal(2090, (await client.SendAsync("QueryRuns")).Require()["page_info"]!["total"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("source_kind", "ROWS")]
    [InlineData("file_path", "ignored.xlsx")]
    [InlineData("text", "ignored")]
    [InlineData("time_zone", "+08:00")]
    [InlineData("source_name", "ignored")]
    [InlineData("page_size", 1)]
    [InlineData("column_mapping", null)]
    [InlineData("rows", null)]
    public async Task ContinuationCannotMixAnySourceOrEditingField(string field, object? value)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = Rows(2), ["page_size"] = 1,
        })).Require();
        var payload = new JsonObject { ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2 };
        payload[field] = value is int number ? JsonValue.Create(number) : JsonValue.Create(value as string);
        var invalid = await client.SendAsync("PreviewRunImport", payload);
        Assert.Equal(ErrorCodes.BadRequest, invalid.ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        Assert.True((await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2,
        })).Ok);
    }

    [Theory]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":null}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":0}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":101}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page_size\":1.5}")]
    [InlineData("{\"source_kind\":\"ROWS\",\"rows\":[],\"page\":1}")]
    [InlineData("{\"preview_id\":\"00000000-0000-0000-0000-000000000001\",\"page\":null}")]
    [InlineData("{\"preview_id\":\"00000000-0000-0000-0000-000000000001\",\"page\":0}")]
    public async Task InvalidPagingFieldsRemainTypedAndKeepConnectionUsable(string json)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("PreviewRunImport", JsonNode.Parse(json)!.AsObject())).ErrorCode);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task FailedCommitAcrossPagesRollsBackEverythingThenSameRequestCanRetry()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "ROWS", ["rows"] = Rows(102), ["page_size"] = 100,
        })).Require();
        (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(), ["page"] = 2,
        })).Require();
        using (var trigger = fixture.Host.Database.CreateCommand())
        {
            trigger.CommandText = "CREATE TRIGGER fail_last_page BEFORE INSERT ON mentor_runs WHEN NEW.duty_name='分页管道合成副本 102' BEGIN SELECT RAISE(ABORT,'synthetic paging failure'); END;";
            trigger.ExecuteNonQuery();
        }
        var payload = new JsonObject
        {
            ["preview_id"] = preview["preview_id"]!.DeepClone(),
            ["row_numbers"] = new JsonArray(Enumerable.Range(1, 102).Select(number => (JsonNode)JsonValue.Create(number)!).ToArray()),
            ["confirm_own_records"] = true,
        };
        var requestId = Guid.NewGuid().ToString("D");
        Assert.False((await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Ok);
        foreach (var table in new[] { "mentor_runs", "run_import_metadata", "run_revisions", "run_reflections", "run_import_batches" })
        {
            using var count = fixture.Host.Database.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, count.ExecuteScalar());
        }
        using (var drop = fixture.Host.Database.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_last_page;";
            drop.ExecuteNonQuery();
        }
        var retried = (await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require();
        Assert.Equal(102, retried["imported_count"]!.GetValue<int>());
        Assert.False(retried["replayed"]!.GetValue<bool>());
        Assert.True((await client.SendAsync("CommitRunImport", payload.DeepClone().AsObject(), requestId)).Require()["replayed"]!.GetValue<bool>());
    }
}
