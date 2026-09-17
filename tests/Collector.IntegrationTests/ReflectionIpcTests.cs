using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The two 导随心得 messages over a real pipe: the write path, its refusals, its idempotency,
/// the live events it publishes, and the summary the dashboard reads.
///
/// They go through <see cref="ServerFixture"/> rather than calling the service directly because
/// the round trip is what matters: a reflection the storage layer wrote but no serialised Run
/// carries would pass every unit test in the suite.
/// </summary>
public sealed class ReflectionIpcTests
{
    private static string NewId() => Guid.NewGuid().ToString("D");

    private static async Task<string> CreateCompletedRunAsync(PipeClient client, int contentId)
    {
        var response = await client.SendAsync(
            "CreateManualRun",
            new JsonObject
            {
                ["content_id"] = contentId,
                ["job_id"] = 19,
                ["duty_name"] = "石卫塔",
                ["matched_at_utc"] = "2026-09-04T11:58:00.000Z",
                ["entered_at_utc"] = "2026-09-04T12:00:00.000Z",
                ["ended_at_utc"] = "2026-09-04T12:26:00.000Z",
                ["result"] = "COMPLETED",
                ["contributes_to_goal"] = true,
                ["reason"] = "心得测试补录",
            });

        return response.Require()["run_id"]!.GetValue<string>();
    }

    private static Task<IpcResponse> SetAsync(
        PipeClient client, string runId, string mood, string text, string? requestId = null) =>
        client.SendAsync(
            "SetRunReflection",
            new JsonObject { ["run_id"] = runId, ["mood"] = mood, ["text"] = text },
            requestId);

    [Fact]
    public async Task SetRunReflection_WritesTheEntryAndEveryRunPayloadCarriesIt()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var runId = await CreateCompletedRunAsync(client, 900101);

        var written = await SetAsync(client, runId, "good", "  带新人打灯塔，一次过。  ");
        var reflection = written.Require()["reflection"]!.AsObject();

        // Trimmed by the Collector, and the run in the same response already carries it.
        Assert.Equal("带新人打灯塔，一次过。", reflection["text"]!.GetValue<string>());
        Assert.Equal("good", reflection["mood"]!.GetValue<string>());
        Assert.Equal(
            "带新人打灯塔，一次过。",
            written.Require()["run"]!["reflection"]!["text"]!.GetValue<string>());

        var queried = await client.SendAsync("QueryRuns", new JsonObject());
        var item = queried.Require()["items"]!.AsArray().Single()!.AsObject();
        Assert.Equal("good", item["reflection"]!["mood"]!.GetValue<string>());
    }

    [Fact]
    public async Task QueryRuns_AlwaysCarriesTheProperty_AndFiltersOnIt()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var withOne = await CreateCompletedRunAsync(client, 900102);
        _ = await CreateCompletedRunAsync(client, 900103);
        Assert.True((await SetAsync(client, withOne, "ok", "还行。")).Ok);

        var all = await client.SendAsync("QueryRuns", new JsonObject());
        Assert.All(
            all.Require()["items"]!.AsArray(),
            node => Assert.True(node!.AsObject().ContainsKey("reflection")));
        Assert.Contains(
            all.Require()["items"]!.AsArray(),
            node => node!["reflection"] is null);

        var filtered = await client.SendAsync(
            "QueryRuns",
            new JsonObject { ["filter"] = new JsonObject { ["with_reflection"] = true } });
        Assert.Equal(
            withOne,
            filtered.Require()["items"]!.AsArray().Single()!["run_id"]!.GetValue<string>());

        // The same filter object also reaches the statistics queries, which splice the
        // fragment into a differently shaped SELECT.
        var stats = await client.SendAsync(
            "GetDashboardStats",
            new JsonObject { ["filter"] = new JsonObject { ["with_reflection"] = true } });
        Assert.Equal(1, stats.Require()["attempt_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task SetRunReflection_EmptyTextClearsTheEntry()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var runId = await CreateCompletedRunAsync(client, 900104);
        Assert.True((await SetAsync(client, runId, "bad", "糟心。")).Ok);

        var cleared = await SetAsync(client, runId, "bad", "   ");

        Assert.True(cleared.Ok);
        Assert.Null(cleared.Require()["reflection"]);
        Assert.Null(cleared.Require()["run"]!["reflection"]);
    }

    [Fact]
    public async Task SetRunReflection_RefusesAnUnknownRunAndAnUndeclaredMood()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var runId = await CreateCompletedRunAsync(client, 900105);

        Assert.Equal(
            ErrorCodes.NotFound,
            (await SetAsync(client, NewId(), "good", "不存在的记录。")).ErrorCode);
        Assert.Equal(
            ErrorCodes.BadRequest,
            (await SetAsync(client, runId, "great", "没有这个心情。")).ErrorCode);
        Assert.Equal(
            ErrorCodes.BadRequest,
            (await SetAsync(client, runId, "good", new string('心', 2001))).ErrorCode);
    }

    [Fact]
    public async Task SetRunReflection_AcceptsASoftDeletedRunAndIsIdempotent()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var runId = await CreateCompletedRunAsync(client, 900106);

        Assert.True((await client.SendAsync(
            "SoftDeleteRun",
            new JsonObject
            {
                ["run_id"] = runId,
                ["expected_revision"] = 1,
                ["reason"] = "重复记录",
            })).Ok);

        var requestId = NewId();
        var first = await SetAsync(client, runId, "bad", "删掉了，但当时确实糟心。", requestId);
        var replay = await SetAsync(client, runId, "bad", "删掉了，但当时确实糟心。", requestId);

        Assert.True(first.Ok);
        Assert.Equal(
            first.Require()["reflection"]!["updated_at_utc"]!.GetValue<string>(),
            replay.Require()["reflection"]!["updated_at_utc"]!.GetValue<string>());

        // The replayed run comes out of the idempotency table rather than the database, so this
        // also covers a reflection round-tripping through that snapshot.
        Assert.Equal(
            "删掉了，但当时确实糟心。",
            replay.Require()["run"]!["reflection"]!["text"]!.GetValue<string>());

        // The same id with a different body is a client bug, not a replay.
        Assert.Equal(
            ErrorCodes.IdempotencyConflict,
            (await SetAsync(client, runId, "good", "换了内容。", requestId)).ErrorCode);
    }

    [Fact]
    public async Task SetRunReflection_PublishesRunUpdatedAndStatsInvalidated()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var events = new List<JsonObject>();
        client.EnvelopeReceived += envelope =>
        {
            if (envelope["message_type"]!.GetValue<string>() != "Event")
            {
                return;
            }

            lock (events)
            {
                events.Add(envelope["payload"]!.DeepClone().AsObject());
            }
        };

        Assert.True((await client.SendAsync(
            "SubscribeLiveEvents", new JsonObject { ["heartbeat_interval_ms"] = 5000 })).Ok);

        var runId = await CreateCompletedRunAsync(client, 900107);
        Assert.True((await SetAsync(client, runId, "good", "发事件用的心得。")).Ok);

        var stats = await WaitForAsync(events, payload =>
            payload["kind"]!.GetValue<string>() == "stats_invalidated" &&
            payload["message"]?.GetValue<string>() == "reflection_changed");
        Assert.NotNull(stats);

        var updated = await WaitForAsync(events, payload =>
            payload["kind"]!.GetValue<string>() == "run_updated" &&
            payload["run"]?["reflection"]?["text"]?.GetValue<string>() == "发事件用的心得。");
        Assert.NotNull(updated);
    }

    [Fact]
    public async Task GetReflectionSummary_CountsPendingRunsAndNamesTheNextOne()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var written = await CreateCompletedRunAsync(client, 900108);
        var pending = await CreateCompletedRunAsync(client, 900109);
        Assert.True((await SetAsync(client, written, "ok", "写过了。")).Ok);

        var summary = (await client.SendAsync(
            "GetReflectionSummary", new JsonObject { ["recent_limit"] = 3 })).Require();

        Assert.Equal(1, summary["reflection_count"]!.GetValue<int>());
        Assert.Equal(1, summary["pending_completed_count"]!.GetValue<int>());

        var recent = summary["recent"]!.AsArray().Single()!.AsObject();
        Assert.Equal(written, recent["run"]!["run_id"]!.GetValue<string>());
        Assert.Equal("写过了。", recent["reflection"]!["text"]!.GetValue<string>());

        // The 补录心得 target is the only COMPLETED run nobody has written about yet.
        Assert.Equal(pending, summary["next_pending"]!["run_id"]!.GetValue<string>());
        Assert.Null(summary["next_pending"]!["reflection"]);
    }

    [Fact]
    public async Task GetReflectionSummary_IsEmptyOnAFreshDatabase()
    {
        await using var fixture = ServerFixture.Start();

        var summary = (await fixture.CallAsync("GetReflectionSummary")).Require();

        Assert.Equal(0, summary["reflection_count"]!.GetValue<int>());
        Assert.Equal(0, summary["pending_completed_count"]!.GetValue<int>());
        Assert.Empty(summary["recent"]!.AsArray());
        Assert.Null(summary["next_pending"]);
    }

    private static async Task<JsonObject?> WaitForAsync(
        List<JsonObject> events, Func<JsonObject, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (events)
            {
                var match = events.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(25);
        }

        return null;
    }
}
