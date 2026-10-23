using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>分页边界和大响应通过真实命名管道返回，不能截断记录或破坏后续请求。</summary>
public sealed class IpcPaginationRegressionTests
{
    [Theory]
    [InlineData("GetDungeonStats")]
    [InlineData("GetJobStats")]
    public async Task APageBeyondInt32OffsetRangeReturnsAnEmptyPage(string messageType)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("CreateManualRun", new JsonObject
        {
            ["reason"] = "分页回归", ["result"] = "COMPLETED", ["content_id"] = 1, ["job_id"] = 19,
            ["entered_at_utc"] = "2026-10-10T00:00:00.000Z", ["ended_at_utc"] = "2026-10-10T00:01:00.000Z",
        })).Require();

        var first = (await client.SendAsync(messageType, Page(1, 200))).Require();
        Assert.Single(first["items"]!.AsArray());
        var last = (await client.SendAsync(messageType, Page(int.MaxValue, 200))).Require();
        Assert.Empty(last["items"]!.AsArray());
        Assert.Equal(int.MaxValue, last["page_info"]!["page"]!.GetValue<int>());
        Assert.Equal(200, last["page_info"]!["page_size"]!.GetValue<int>());
        Assert.Equal(1, last["page_info"]!["total"]!.GetValue<int>());
        Assert.Empty((await client.SendAsync(messageType, Page(2, 200))).Require()["items"]!.AsArray());
    }

    [Fact]
    public async Task TwoHundredLongChineseImportsAreReturnedWithoutLossOrPaginationChanges()
    {
        await using var fixture = ServerFixture.Start(host => ImportRows(host, '记'));
        await using var client = await fixture.ConnectAsync();
        var result = (await client.SendAsync("QueryRuns", Page(1, 200))).Require();
        Assert.Equal(200, result["items"]!.AsArray().Count);
        Assert.Equal(200, result["page_info"]!["page_size"]!.GetValue<int>());
        Assert.Equal(200, result["page_info"]!["total"]!.GetValue<int>());
        AssertCompleteRows(result, '记');
        var next = (await client.SendAsync("QueryRuns", Page(2, 200))).Require();
        Assert.Empty(next["items"]!.AsArray());

        var wireBytes = IpcEnvelope.ToBytes(IpcEnvelope.Success(Guid.NewGuid().ToString("D"), "QueryRuns", result.DeepClone().AsObject()));
        Assert.True(wireBytes.Length < FrameCodec.MaxFrameBytes);
        Assert.Contains(new string('记', 50), Encoding.UTF8.GetString(wireBytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStillOversizedEscapedPageReturnsACorrelatedErrorAndSmallerPagesRemainComplete()
    {
        // U+0001 is legal stored text but JSON must escape it; raw Unicode alone cannot
        // make every allowed 200-row page fit the unchanged 4 MiB frame limit.
        await using var fixture = ServerFixture.Start(host => ImportRows(host, '\u0001'));
        await using var client = await fixture.ConnectAsync();
        var refused = await client.SendAsync("QueryRuns", Page(1, 200));
        Assert.False(refused.Ok);
        Assert.Equal("QueryRuns", refused.MessageType);
        Assert.Equal(ErrorCodes.Internal, refused.ErrorCode);
        Assert.Contains("4 MiB", refused.ErrorMessage, StringComparison.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 4; page++)
        {
            var result = (await client.SendAsync("QueryRuns", Page(page, 50))).Require();
            Assert.Equal(50, result["items"]!.AsArray().Count);
            Assert.Equal(page, result["page_info"]!["page"]!.GetValue<int>());
            Assert.Equal(50, result["page_info"]!["page_size"]!.GetValue<int>());
            Assert.Equal(200, result["page_info"]!["total"]!.GetValue<int>());
            AssertCompleteRows(result, '\u0001');
            foreach (var row in result["items"]!.AsArray())
                Assert.True(seen.Add(row!["run_id"]!.GetValue<string>()));
        }
        Assert.Equal(200, seen.Count);
        Assert.Empty((await client.SendAsync("QueryRuns", Page(5, 50))).Require()["items"]!.AsArray());
    }

    [Fact]
    public void UnicodeTransportKeepsJsonEscapesAndFileSerializerBehavior()
    {
        const string value = "中文<>&\"\\\u0001😀";
        var envelope = IpcEnvelope.Success(Guid.NewGuid().ToString("D"), "QueryRuns", new JsonObject { ["text"] = value });
        var bytes = IpcEnvelope.ToBytes(envelope);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("中文", json, StringComparison.Ordinal);
        Assert.DoesNotContain("<", json, StringComparison.Ordinal);
        Assert.DoesNotContain(">", json, StringComparison.Ordinal);
        Assert.DoesNotContain("&", json, StringComparison.Ordinal);
        Assert.Equal(value, JsonNode.Parse(bytes)!["payload"]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("中文", envelope.ToJsonString(Wire.JsonOptions), StringComparison.Ordinal);
    }

    private static JsonObject Page(int page, int size) => new() { ["page"] = page, ["page_size"] = size };

    private static void AssertCompleteRows(JsonObject response, char text)
    {
        Assert.All(response["items"]!.AsArray(), row =>
        {
            Assert.Equal(new string(text, 1000), row!["note"]!.GetValue<string>());
            Assert.Equal(new string(text, 2000), row["reflection"]!["text"]!.GetValue<string>());
            Assert.Equal(new string(text, 500), row["import_metadata"]!["source_name"]!.GetValue<string>());
        });
    }

    private static void ImportRows(CollectorHost host, char text)
    {
        for (var batch = 0; batch < 8; batch++)
        {
            var rows = new JsonArray(Enumerable.Range(0, 25).Select(_ => (JsonNode)new JsonObject
            {
                ["run_id"] = Guid.NewGuid().ToString("D"), ["duty_name"] = "导入副本",
                ["entered_at_utc"] = "2026-10-10T00:00:00.000Z", ["ended_at_utc"] = "2026-10-10T00:01:00.000Z",
                ["result"] = "COMPLETED", ["note"] = new string(text, 1000),
                ["reflection_text"] = new string(text, 2000), ["reflection_mood"] = "good",
            }).ToArray());
            var preview = host.Imports.PreviewSource("ROWS", null, null, rows, null, null, new string(text, 500), CancellationToken.None);
            var committed = host.Imports.Commit(preview["preview_id"]!.GetValue<string>(), Enumerable.Range(1, 25).ToArray(),
                true, Guid.NewGuid().ToString("D"), false, CancellationToken.None);
            Assert.Equal(25, committed["imported_count"]!.GetValue<int>());
        }
    }
}