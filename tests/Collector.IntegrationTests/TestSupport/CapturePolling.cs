using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Waiting over the pipe for a capture, its run or a validation session to reach a state: each wait asks the
/// Collector again until the answer shows it, and gives up with a <see cref="TimeoutException"/> naming what never
/// came.
/// </summary>
internal static class CapturePolling
{
    private static readonly TimeSpan RunPatience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RunInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ValidationPatience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ValidationInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>The first capture status that satisfies <paramref name="until"/>; 10 s at most.</summary>
    /// <param name="fixture">Server under test.</param>
    /// <param name="until">What the status must show.</param>
    public static Task<JsonObject> WaitForCaptureStatusAsync(this ServerFixture fixture, Func<JsonObject, bool> until) =>
        AskUntilAsync(
            async () =>
            {
                var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
                return until(status) ? status : null;
            },
            RunPatience,
            RunInterval,
            "the capture status never reached the expected value");

    /// <summary>The current run once it is in <paramref name="state"/>; 10 s at most.</summary>
    /// <param name="fixture">Server under test.</param>
    /// <param name="state">Wire spelling of the expected run state.</param>
    public static Task<JsonObject> WaitForCurrentRunAsync(this ServerFixture fixture, string state) =>
        AskUntilAsync(
            async () =>
            {
                var current = (await fixture.CallAsync("GetCurrentRun")).Require();
                return current["state"]!.GetValue<string>() == state ? current : null;
            },
            RunPatience,
            RunInterval,
            "the current run never reached " + state);

    /// <summary>
    /// Waits until the stored row carries a terminal result.
    ///
    /// <c>GetCurrentRun</c> normalises a terminal state to IDLE as soon as it is asked, so a
    /// player who finished a duty and then idled in a city is not reported as still in one
    /// (review finding L-9). The durable answer to what an attempt ended as is the stored row,
    /// which is also what <c>run_finished</c> is computed from.
    /// </summary>
    /// <param name="fixture">Server under test.</param>
    /// <param name="result">Wire spelling of the expected result.</param>
    public static Task<JsonObject> WaitForStoredResultAsync(this ServerFixture fixture, string result) =>
        AskUntilAsync(
            async () =>
            {
                var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
                return rows.Count > 0 && rows[0]!["result"]!.GetValue<string>() == result ? rows[0]!.AsObject() : null;
            },
            RunPatience,
            RunInterval,
            "no stored run ever reached " + result);

    /// <summary>The capture-validation status once it is in <paramref name="state"/>; 5 s at most.</summary>
    /// <param name="client">A connected client.</param>
    /// <param name="state">Wire spelling of the expected validation state.</param>
    public static Task<JsonObject> WaitForValidationStateAsync(this PipeClient client, string state) =>
        AskUntilAsync(
            async () =>
            {
                var status = (await client.SendAsync("GetCaptureValidationStatus")).Require();
                return status["state"]!.GetValue<string>() == state ? status : null;
            },
            ValidationPatience,
            ValidationInterval,
            "validation state did not reach " + state);

    private static async Task<JsonObject> AskUntilAsync(
        Func<Task<JsonObject?>> ask, TimeSpan patience, TimeSpan interval, string failure)
    {
        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await ask() is { } answer)
            {
                return answer;
            }

            await Task.Delay(interval);
        }

        throw new TimeoutException(failure);
    }
}
