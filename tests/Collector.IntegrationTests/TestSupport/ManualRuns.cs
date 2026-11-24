using System.Globalization;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>The 石卫塔 run several pipe tests create by hand, through <c>CreateManualRun</c>.</summary>
internal static class ManualRuns
{
    /// <summary>
    /// Creates a 石卫塔 run matched two minutes before <paramref name="enteredAtUtc"/> and ended 26 minutes after it,
    /// counted toward the goal; returns its id.
    /// </summary>
    /// <param name="client">A connected client.</param>
    /// <param name="contentId">The duty's content id.</param>
    /// <param name="jobId">The job played.</param>
    /// <param name="result">Wire spelling of the result.</param>
    /// <param name="enteredAtUtc">When the duty was entered.</param>
    public static async Task<string> CreateAsync(
        PipeClient client,
        int contentId,
        int jobId = 19,
        string result = "COMPLETED",
        string enteredAtUtc = "2026-09-04T12:00:00.000Z")
    {
        var entered = DateTimeOffset.Parse(enteredAtUtc, CultureInfo.InvariantCulture);
        var response = await client.SendAsync(
            "CreateManualRun",
            new JsonObject
            {
                ["content_id"] = contentId,
                ["job_id"] = jobId,
                ["duty_name"] = "石卫塔",
                ["matched_at_utc"] = UtcTimestamp.ToText(entered.AddMinutes(-2)),
                ["entered_at_utc"] = UtcTimestamp.ToText(entered),
                ["ended_at_utc"] = UtcTimestamp.ToText(entered.AddMinutes(26)),
                ["result"] = result,
                ["contributes_to_goal"] = true,
                ["note"] = "测试补录",
                ["reason"] = "测试补录",
            });

        return response.Require()["run_id"]!.GetValue<string>();
    }
}
