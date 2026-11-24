using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// <c>SetDisplayTimeZone</c> over a real pipe: the zone is put in force and the calendar of days with
/// records follows it, null goes back to the computer's zone, and an id this computer does not know is
/// refused with its catalogue reference while the zone in force stays. The zone is process-wide, so the
/// class runs alone and leaves the computer's zone in force.
/// </summary>
[Collection(DisplayTimeZoneCollection.Name)]
public sealed class DisplayTimeZoneIpcTests
{
    [Fact]
    public async Task TheCalendarFollowsTheZoneTheDesktopSends()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        try
        {
            (await client.SendAsync("CreateManualRun", new JsonObject
            {
                ["result"] = "COMPLETED", ["contributes_to_goal"] = true,
                ["content_id"] = 5, ["job_id"] = 19, ["reason"] = "显示时区验证",
                ["matched_at_utc"] = "2026-09-15T16:29:00.000Z",
                ["entered_at_utc"] = "2026-09-15T16:30:00.000Z",
                ["ended_at_utc"] = "2026-09-15T16:50:00.000Z",
            })).Require();

            var tokyo = await SetAsync(client, "Asia/Tokyo");
            Assert.Equal("Asia/Tokyo", tokyo["time_zone"]!.GetValue<string>());
            Assert.Equal(540, tokyo["utc_offset_minutes"]!.GetValue<int>());
            Assert.Equal(new[] { "2026-09-16" }, await DaysAsync(client));

            var newYork = await SetAsync(client, "America/New_York");
            Assert.Equal("America/New_York", newYork["time_zone"]!.GetValue<string>());
            Assert.Equal(
                (int)TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
                newYork["utc_offset_minutes"]!.GetValue<int>());
            Assert.Equal(new[] { "2026-09-15" }, await DaysAsync(client));

            // Unknown: refused with its reference, and New York stays in force.
            var refused = await client.SendAsync("SetDisplayTimeZone", new JsonObject { ["time_zone"] = "Mars/Olympus_Mons" });
            CollectorTextCatalogue.AssertRefusal(refused, "ipc.request.time_zone_unknown");
            Assert.Equal(ErrorCodes.BadRequest, refused.ErrorCode);
            Assert.Equal("time_zone", refused.Payload["field"]!.GetValue<string>());
            Assert.Equal("America/New_York", DisplayTimeZone.Id);
            Assert.Equal(new[] { "2026-09-15" }, await DaysAsync(client));

            // The field is required and nothing else is accepted.
            Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("SetDisplayTimeZone", new JsonObject())).ErrorCode);
            Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("SetDisplayTimeZone", new JsonObject
            {
                ["time_zone"] = null, ["persist"] = true,
            })).ErrorCode);

            // null: the computer's zone again.
            var system = await SetAsync(client, null);
            Assert.Null(system["time_zone"]);
            Assert.Equal((int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes,
                system["utc_offset_minutes"]!.GetValue<int>());
            Assert.Null(DisplayTimeZone.Id);
            var localDay = new DateTimeOffset(2026, 9, 15, 16, 30, 0, TimeSpan.Zero).ToLocalTime()
                .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(new[] { localDay }, await DaysAsync(client));
        }
        finally
        {
            DisplayTimeZone.Set(null);
        }
    }

    private static async Task<JsonObject> SetAsync(PipeClient client, string? zone) =>
        ContractSchema.Answered(
            await client.SendAsync("SetDisplayTimeZone", new JsonObject { ["time_zone"] = zone }), "SetDisplayTimeZone response");

    private static async Task<string[]> DaysAsync(PipeClient client)
    {
        var payload = (await client.SendAsync("QueryRunCalendarDays", new JsonObject { ["month"] = "2026-09" })).Require();
        return payload["days"]!.AsArray().Select(day => day!.GetValue<string>()).ToArray();
    }
}
