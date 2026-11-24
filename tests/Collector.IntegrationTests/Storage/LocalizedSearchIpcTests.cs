using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class LocalizedSearchIpcTests
{
    [Theory]
    [InlineData("枪刃", 37, "绝枪战士")]
    [InlineData("画家", 42, "绘灵法师")]
    [InlineData("暗骑", 32, "暗黑骑士")]
    public async Task ChineseAliasImportAndHistorySearchUseTheSameCatalogOverRealPipe(string alias, int jobId, string fullName)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var preview = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "PASTE",
            ["text"] = "副本\t职业\t开始时间\t结束时间\t结果\n样例迷宫挑战 A\t" + alias
                + "\t2026-09-01T12:00:00Z\t2026-09-01T12:20:00Z\t通关",
        })).Require();
        ContractSchema.Validate("$defs/Responses/PreviewRunImport", preview, "Chinese alias preview");
        var first = Assert.Single(preview["rows"]!.AsArray())!;
        Assert.Equal(jobId, first["run"]!["job_id"]!.GetValue<int>());
        var reviewed = (await client.SendAsync("PreviewRunImport", new JsonObject
        {
            ["source_kind"] = "PASTE", ["rows"] = new JsonArray(first["candidate"]!.DeepClone()),
        })).Require();
        var row = Assert.Single(reviewed["rows"]!.AsArray())!;
        Assert.True(row["can_import"]!.GetValue<bool>());
        var committed = (await client.SendAsync("CommitRunImport", new JsonObject
        {
            ["preview_id"] = reviewed["preview_id"]!.DeepClone(),
            ["row_numbers"] = new JsonArray(row["row_number"]!.GetValue<int>()), ["confirm_own_records"] = true,
        })).Require();
        Assert.Equal(1, committed["imported_count"]!.GetValue<int>());
        var imported = Assert.Single(committed["run_ids"]!.AsArray())!.GetValue<string>();

        var noteMatch = await CreateRunAsync(client, 2, 19, 4, "只在备注提到" + alias);
        _ = await CreateRunAsync(client, 3, 19, 4, "只在备注提到" + fullName);
        var filter = new JsonObject { ["text"] = alias };
        await AssertFilterAgreesEverywhereAsync(
            client, fixture, filter, [imported, noteMatch], ["2026-09-01", "2026-09-02"], "chinese-alias-search.json");

        async Task<JsonObject> Query(JsonObject current) =>
            (await client.SendAsync("QueryRuns", new JsonObject { ["filter"] = current.DeepClone() })).Require();
        var restricted = filter.DeepClone().AsObject(); restricted["job_id"] = new JsonArray(jobId);
        var saved = Assert.Single((await Query(restricted))["items"]!.AsArray())!;
        Assert.Equal(imported, saved["run_id"]!.GetValue<string>());
        Assert.Equal(alias, saved["job_name"]!.GetValue<string>());
        Assert.Contains((await Query(new JsonObject { ["text"] = fullName }))["items"]!.AsArray(),
            run => run!["run_id"]!.GetValue<string>() == imported);
        var literal = await Query(new JsonObject { ["text"] = "备注提到" + alias });
        Assert.Equal(noteMatch, Assert.Single(literal["items"]!.AsArray())!["run_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RealPipe_LocalizedSearchKeepsNotesPagingCalendarStatisticsAndExportsInAgreement()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var paladin = await CreateRunAsync(client, 1, 19, 4, "unrelated");
        var note = await CreateRunAsync(client, 2, 24, 4, "helped a Paladin");
        _ = await CreateRunAsync(client, 3, 24, 4, "only 骑士");
        _ = await CreateRunAsync(client, 4, 24, 4, "unrelated");
        (await client.SendAsync("SetRunReflection", new JsonObject
        {
            ["run_id"] = note, ["mood"] = "good", ["text"] = "reflection content",
        })).Require();
        var filter = new JsonObject { ["text"] = "Paladin", ["name_text"] = "骑士" };
        ContractSchema.Validate("$defs/RunFilter", filter, "localized name search");
        await AssertFilterAgreesEverywhereAsync(
            client, fixture, filter, [paladin, note], ["2026-09-01", "2026-09-02"], "localized-search.json");

        async Task<JsonObject> Query(JsonObject current) =>
            (await client.SendAsync("QueryRuns", new JsonObject { ["filter"] = current.DeepClone() })).Require();
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
    }

    // 骑士 is inside 暗黑骑士 and 龙骑士, and 万魔殿 边境之狱4 inside its Savage mode's name:
    // exact names find only the runs they name, in every query that takes the filter.
    [Fact]
    public async Task RealPipe_ExactNamesFindOnlyTheNamedRunsEverywhereTheFilterApplies()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var paladin = await CreateRunAsync(client, 1, 19, 4);
        _ = await CreateRunAsync(client, 2, 32, 4);
        _ = await CreateRunAsync(client, 3, 22, 4);
        var note = await CreateRunAsync(client, 4, 24, 4, "helped a Paladin");
        var normal = await CreateRunAsync(client, 5, 24, 800);
        _ = await CreateRunAsync(client, 6, 24, 801);
        var filter = new JsonObject
        {
            ["text"] = "Paladin", ["name_exact"] = new JsonArray("骑士", "万魔殿 边境之狱4"),
        };
        ContractSchema.Validate("$defs/RunFilter", filter, "exact name search");
        await AssertFilterAgreesEverywhereAsync(
            client, fixture, filter, [paladin, note, normal], ["2026-09-01", "2026-09-04", "2026-09-05"], "exact-name-search.json");

        var empty = await client.SendAsync("QueryRuns", new JsonObject
        {
            ["filter"] = new JsonObject { ["name_exact"] = new JsonArray("骑士", "") },
        });
        Assert.Equal(ErrorCodes.BadRequest, empty.ErrorCode);
    }

    /// <summary>A completed CN run on 2026-09-<paramref name="day"/>: matched 11:58, entered 12:00, ended 12:20.</summary>
    private static async Task<string> CreateRunAsync(PipeClient client, int day, int jobId, int contentId, string note = "unrelated")
    {
        var created = (await client.SendAsync("CreateManualRun", new JsonObject
        {
            ["region"] = "CN", ["job_id"] = jobId, ["content_id"] = contentId,
            ["matched_at_utc"] = $"2026-09-{day:00}T11:58:00.000Z",
            ["entered_at_utc"] = $"2026-09-{day:00}T12:00:00.000Z",
            ["ended_at_utc"] = $"2026-09-{day:00}T12:20:00.000Z",
            ["result"] = "COMPLETED", ["contributes_to_goal"] = true,
            ["note"] = note, ["reason"] = "搜索回归验证",
        })).Require();
        return created["run_id"]!.GetValue<string>();
    }

    /// <summary>
    /// Every query that takes the filter finds exactly <paramref name="runs"/>: QueryRuns page by page, the calendar
    /// of 2026-09 (<paramref name="days"/>, in order), the result statistics and a JSON export.
    /// </summary>
    private static async Task AssertFilterAgreesEverywhereAsync(
        PipeClient client, ServerFixture fixture, JsonObject filter, string[] runs, string[] days, string exportName)
    {
        var expected = runs.Order().ToArray();
        var seen = new List<string>();
        for (var page = 1; page <= runs.Length; page++)
        {
            var found = (await client.SendAsync("QueryRuns", new JsonObject
            {
                ["filter"] = filter.DeepClone(), ["page"] = page, ["page_size"] = 1,
            })).Require();
            Assert.Equal(runs.Length, found["page_info"]!["total"]!.GetValue<int>());
            seen.Add(Assert.Single(found["items"]!.AsArray())!["run_id"]!.GetValue<string>());
        }
        Assert.Equal(expected, seen.Order());

        var calendar = (await client.SendAsync("QueryRunCalendarDays", new JsonObject
        {
            ["filter"] = filter.DeepClone(), ["month"] = "2026-09",
        })).Require();
        Assert.Equal(days, calendar["days"]!.AsArray().Select(day => day!.GetValue<string>()).Order());
        var stats = (await client.SendAsync("GetResultStats", new JsonObject
        {
            ["filter"] = filter.DeepClone(),
        })).Require();
        Assert.Equal(runs.Length, stats["attempt_count"]!.GetValue<int>());

        var target = Path.Combine(fixture.DataDirectory, exportName);
        var output = (await client.SendAsync("ExportJson", new JsonObject
        {
            ["target_path"] = target, ["filter"] = filter.DeepClone(),
        })).Require();
        Assert.Equal(runs.Length, output["row_count"]!.GetValue<int>());
        Assert.Equal(expected, JsonNode.Parse(File.ReadAllText(target))!
            .AsArray().Select(run => run!["run_id"]!.GetValue<string>()).Order());
    }
}
