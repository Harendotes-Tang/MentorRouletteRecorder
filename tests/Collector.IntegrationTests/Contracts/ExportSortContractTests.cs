using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class ExportSortContractTests
{
    /// <summary>
    /// contracts/CHANGELOG.md 2026-11-03: ExportRequest.sort is the RunSort QueryRuns takes. The
    /// schema and the Collector agree on what passes and what is refused, and an accepted sort
    /// orders the file: ascending history_date puts the earliest run on the first data row.
    /// </summary>
    [Theory]
    [InlineData("history_date", "asc", true)]
    [InlineData("history_date", "desc", true)]
    [InlineData("history_date", "sideways", false)]
    [InlineData("run_id", "asc", false)]
    public async Task ExportSort_MatchesSchemaAndRealPipe(string field, string direction, bool accepted)
    {
        var request = new JsonObject
        {
            ["target_path"] = "placeholder",
            ["filter"] = new JsonObject(),
            ["sort"] = new JsonObject { ["field"] = field, ["direction"] = direction },
        };
        Assert.Equal(accepted, ContractSchema.Evaluate("$defs/ExportRequest", request).IsValid);

        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var earliest = await CreateRunAsync(client, "COMPLETED", 5, 19, "2026-09-01T01:00:00.000Z");
        var latest = await CreateRunAsync(client, "COMPLETED", 5, 19, "2026-09-03T01:00:00.000Z");
        var target = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, $"sorted-{field}-{direction}.json");
        request["target_path"] = target;

        var response = await client.SendAsync("ExportJson", request);
        Assert.Equal(accepted, response.Ok);
        if (!accepted)
        {
            Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
            return;
        }

        ContractSchema.Validate("$defs/Responses/ExportJson", response.Require(), "sorted export");
        var ids = JsonNode.Parse(File.ReadAllText(target))!.AsArray()
            .Select(item => item!["run_id"]!.GetValue<string>()).ToArray();
        Assert.Equal(direction == "asc" ? new[] { earliest, latest } : new[] { latest, earliest }, ids);
    }

    private static async Task<string> CreateRunAsync(
        PipeClient client, string result, int contentId, int jobId,
        string enteredAtUtc = "2026-09-04T12:00:00.000Z")
    {
        var entered = DateTimeOffset.Parse(enteredAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var response = await client.SendAsync(
            "CreateManualRun",
            new JsonObject
            {
                ["content_id"] = contentId,
                ["job_id"] = jobId,
                ["duty_name"] = "石卫塔",
                ["matched_at_utc"] = Stamp(entered.AddMinutes(-2)),
                ["entered_at_utc"] = Stamp(entered),
                ["ended_at_utc"] = Stamp(entered.AddMinutes(26)),
                ["result"] = result,
                ["contributes_to_goal"] = true,
                ["note"] = "契约测试补录",
                ["reason"] = "契约测试补录",
            });

        return response.Require()["run_id"]!.GetValue<string>();
    }

}
