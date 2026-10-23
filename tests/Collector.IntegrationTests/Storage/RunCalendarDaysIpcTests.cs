using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class RunCalendarDaysIpcTests
{
    [Fact]
    public async Task RealPipeFullSetIgnoresDateBoundsAndPreservesOtherFilters()
    {
        await using var fixture = ServerFixture.Start(host => host.Database.RunInTransaction(tx =>
        {
            var imports = new RunImportRepository(host.Database);
            for (var i = 0; i < 207; i++)
            {
                var day = i < 205 ? "2026-09-01" : i == 205 ? "2026-09-30" : "2026-09-15";
                var run = new MentorRun
                {
                    RunId = Guid.NewGuid().ToString("D"), Revision = 1, Source = RunSource.Import,
                    CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow,
                    JobId = i == 206 ? 19 : 24, DutyCategory = "RAID", Note = "calendar-test",
                    Result = RunResult.Completed, SoftDeleted = i == 206,
                };
                host.Runs.Insert(run, tx);
                imports.Insert(run.RunId, new RunImportMetadata("SCREENSHOT", "fixture", day,
                    null, run.CreatedAtUtc, Guid.NewGuid().ToString("N"), true), tx);
            }
        }));
        await using var client = await fixture.ConnectAsync();
        var filter = new JsonObject
        {
            ["job_id"] = new JsonArray(24), ["source"] = new JsonArray("IMPORT"),
            ["duty_category"] = new JsonArray("RAID"), ["text"] = "calendar-test",
            ["date_field"] = "history_date", ["from_utc"] = "2026-09-02T00:00:00.000Z",
            ["to_utc"] = "2026-09-03T00:00:00.000Z", ["history_from_day"] = "2026-09-02",
            ["history_to_day"] = "2026-09-03",
        };
        async Task<string[]> Query(JsonObject f)
        {
            var payload = (await client.SendAsync("QueryRunCalendarDays",
                new JsonObject { ["month"] = "2026-09", ["filter"] = f.DeepClone() })).Require();
            ContractSchema.Validate("$defs/Responses/QueryRunCalendarDays", payload, "calendar response");
            Assert.Equal("2026-09", payload["month"]!.GetValue<string>());
            return payload["days"]!.AsArray().Select(d => d!.GetValue<string>()).ToArray();
        }
        Assert.Equal(new[] { "2026-09-01", "2026-09-30" }, await Query(filter));
        filter["job_id"] = new JsonArray(19);
        Assert.Empty(await Query(filter));
        filter["deleted_only"] = true;
        Assert.Equal(new[] { "2026-09-15" }, await Query(filter));
        filter["include_deleted"] = true;
        Assert.Equal(new[] { "2026-09-15" }, await Query(filter));
        filter["text"] = "no-such-record";
        Assert.Empty(await Query(filter));
    }

    [Theory]
    [InlineData("{\"month\":\"2026-13\"}")]
    [InlineData("{\"month\":\"2026-09\",\"page\":1}")]
    [InlineData("{\"month\":\"2026-09\",\"filter\":{\"unexpected\":true}}")]
    [InlineData("{\"month\":42}")]
    [InlineData("{}")]
    public async Task RealPipeRejectsMalformedMonthAndUnknownFields(string json)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var response = await client.SendAsync("QueryRunCalendarDays", JsonNode.Parse(json)!.AsObject());
        Assert.False(response.Ok);
        Assert.Equal("ERR_BAD_REQUEST", response.ErrorCode);
    }
}
