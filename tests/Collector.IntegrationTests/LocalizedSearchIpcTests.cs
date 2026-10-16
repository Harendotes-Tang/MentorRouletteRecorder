using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class LocalizedSearchIpcTests
{
    [Fact]
    public async Task RealPipe_LocalizedSearchKeepsNotesPagingCalendarStatisticsAndExportsInAgreement()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        async Task<string> Create(int day, int jobId, string note)
        {
            var created = (await client.SendAsync("CreateManualRun", new JsonObject
            {
                ["job_id"] = jobId, ["content_id"] = 4,
                ["matched_at_utc"] = $"2026-09-{day:00}T11:58:00.000Z",
                ["entered_at_utc"] = $"2026-09-{day:00}T12:00:00.000Z",
                ["ended_at_utc"] = $"2026-09-{day:00}T12:20:00.000Z",
                ["result"] = "COMPLETED", ["contributes_to_goal"] = true,
                ["note"] = note, ["reason"] = "搜索回归验证",
            })).Require();
            return created["run_id"]!.GetValue<string>();
        }
        var paladin = await Create(1, 19, "unrelated");
        var note = await Create(2, 24, "helped a Paladin");
        _ = await Create(3, 24, "only 骑士");
        _ = await Create(4, 24, "unrelated");
        (await client.SendAsync("SetRunReflection", new JsonObject
        {
            ["run_id"] = note, ["mood"] = "good", ["text"] = "reflection content",
        })).Require();
        var filter = new JsonObject { ["text"] = "Paladin", ["name_text"] = "骑士" };
        ContractSchema.Validate("$defs/RunFilter", filter, "localized name search");

        async Task<JsonObject> Query(JsonObject current, int page = 1, int pageSize = 50) =>
            (await client.SendAsync("QueryRuns", new JsonObject
            {
                ["filter"] = current.DeepClone(), ["page"] = page, ["page_size"] = pageSize,
            })).Require();
        var seen = new HashSet<string>();
        for (var page = 1; page <= 2; page++)
        {
            var found = await Query(filter, page, 1);
            Assert.Equal(2, found["page_info"]!["total"]!.GetValue<int>());
            seen.Add(Assert.Single(found["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        }
        Assert.Equal(new[] { paladin, note }.Order(), seen.Order());
        var legacy = await Query(new JsonObject { ["text"] = "Paladin" });
        Assert.Equal(note, Assert.Single(legacy["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        var nameOnly = await Query(new JsonObject { ["name_text"] = "骑士" });
        Assert.Equal(paladin, Assert.Single(nameOnly["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        var reflectionFilter = filter.DeepClone().AsObject();
        reflectionFilter["with_reflection"] = true;
        Assert.Equal(note, Assert.Single((await Query(reflectionFilter))["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        var jobFilter = filter.DeepClone().AsObject();
        jobFilter["job_id"] = new JsonArray(24);
        Assert.Equal(note, Assert.Single((await Query(jobFilter))["items"]!.AsArray())!["run_id"]!.GetValue<string>());

        var calendar = (await client.SendAsync("QueryRunCalendarDays", new JsonObject
        {
            ["filter"] = filter.DeepClone(), ["month"] = "2026-09",
        })).Require();
        Assert.Equal(new[] { "2026-09-01", "2026-09-02" },
            calendar["days"]!.AsArray().Select(day => day!.GetValue<string>()).Order());
        var stats = (await client.SendAsync("GetResultStats", new JsonObject
        {
            ["filter"] = filter.DeepClone(),
        })).Require();
        Assert.Equal(2, stats["attempt_count"]!.GetValue<int>());
        var target = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "localized-search.json");
        var output = (await client.SendAsync("ExportJson", new JsonObject
        {
            ["target_path"] = target, ["filter"] = filter.DeepClone(),
        })).Require();
        Assert.Equal(2, output["row_count"]!.GetValue<int>());
        Assert.Equal(new[] { paladin, note }.Order(), JsonNode.Parse(File.ReadAllText(target))!
            .AsArray().Select(run => run!["run_id"]!.GetValue<string>()).Order());
    }
}
