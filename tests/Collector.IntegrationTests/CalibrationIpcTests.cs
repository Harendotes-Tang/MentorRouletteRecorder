using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// <c>DiscardCalibration</c> over a real pipe on a machine with no game, where nothing is being
/// calibrated and no profile is in force: the point here is the shape of the request, not what
/// it does. What it does with a local profile in force is pinned without a pipe by
/// <c>LocalProfileRetirementTests</c>.
///
/// The new field is optional, so the Desktop that never sends it and the Desktop that sends
/// <c>false</c> must be indistinguishable; anything that is not a boolean, and anything the
/// contract does not declare, is refused before the pipeline is touched at all.
/// </summary>
public sealed class CalibrationIpcTests
{
    [Fact]
    public async Task DiscardAcceptsTheRetirementFlagOnlyAsAnOptionalBoolean()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        foreach (var payload in new[]
                 {
                     new JsonObject(),
                     new JsonObject { ["retire_local_profile"] = false },
                     new JsonObject { ["retire_local_profile"] = true },
                 })
        {
            var answered = await client.SendAsync("DiscardCalibration", payload);
            Assert.True(answered.Ok, answered.ErrorMessage);
            ContractSchema.Validate(
                "$defs/Responses/DiscardCalibration", answered.Payload, "discard response");
            Assert.Equal("IDLE", answered.Payload["state"]!.GetValue<string>());
        }

        foreach (var payload in new[]
                 {
                     new JsonObject { ["retire_local_profile"] = "true" },
                     new JsonObject { ["retire_local_profile"] = 1 },
                     new JsonObject { ["retire_local_profile"] = new JsonArray() },
                     new JsonObject { ["retire_profile"] = true },
                     new JsonObject { ["retire_local_profile"] = true, ["extra"] = 1 },
                 })
        {
            Assert.Equal(
                ErrorCodes.BadRequest, (await client.SendAsync("DiscardCalibration", payload)).ErrorCode);
        }
    }

    [Fact]
    public async Task DiscardAcceptsTheRollbackFlagOnlyAsAnOptionalBooleanAndNeverBesideARetirement()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        // Nothing is being calibrated and nothing was retired here, so the request is refused on
        // its merits rather than on its shape; what this pins is that the field is read at all.
        foreach (var payload in new[]
                 {
                     new JsonObject { ["restore_local_profile"] = false },
                     new JsonObject { ["restore_local_profile"] = true },
                     new JsonObject { ["retire_local_profile"] = false, ["restore_local_profile"] = false },
                 })
        {
            var answered = await client.SendAsync("DiscardCalibration", payload);
            Assert.NotEqual(ErrorCodes.BadRequest, answered.ErrorCode);
        }

        // Retiring the profile in force and putting the retired one back are opposite requests;
        // asking for both at once has no meaning the Collector could honour, in either order.
        var both = await client.SendAsync(
            "DiscardCalibration",
            new JsonObject { ["retire_local_profile"] = true, ["restore_local_profile"] = true });
        Assert.Equal(ErrorCodes.BadRequest, both.ErrorCode);
        Assert.Matches("[\\u4e00-\\u9fff]", both.ErrorMessage);

        foreach (var payload in new[]
                 {
                     new JsonObject { ["restore_local_profile"] = "true" },
                     new JsonObject { ["restore_local_profile"] = 1 },
                 })
        {
            Assert.Equal(
                ErrorCodes.BadRequest, (await client.SendAsync("DiscardCalibration", payload)).ErrorCode);
        }
    }

    /// <summary>
    /// The field the desktop keys 恢复上一份本机校准 off. Optional, so a Collector that does not
    /// send it is read as "no rollback"; here nothing was ever retired, so it is false.
    /// </summary>
    [Fact]
    public async Task CaptureStatusReportsWhetherARollbackIsAvailable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/Responses/GetCaptureStatus", status, "capture status");
        Assert.False(status["calibration"]!["retired_local_profile_available"]!.GetValue<bool>());
    }
}
