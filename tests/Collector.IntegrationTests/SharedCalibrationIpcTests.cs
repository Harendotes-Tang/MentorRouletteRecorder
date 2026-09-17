using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The five shared-calibration requests and the setting over a real pipe on a machine with no
/// game: ordinary answers are outcomes matching the contract, only a share code that cannot be
/// given fails, and a request of the wrong shape is refused before anything decodes it.
/// </summary>
public sealed class SharedCalibrationIpcTests
{
    private static (string Code, string Sha) Vector()
    {
        var path = Path.Combine(ContractSchema.RepositoryRoot, "tests", "Fixtures", "shared-calibration", "vectors.json");
        var valid = JsonNode.Parse(File.ReadAllText(path))!["valid"]![0]!;
        return (valid["code"]!.GetValue<string>(), valid["code_sha256"]!.GetValue<string>());
    }

    private static JsonObject Answered(IpcResponse response, string messageType)
    {
        Assert.True(response.Ok, messageType + ": " + response.ErrorMessage);
        ContractSchema.Validate("$defs/Responses/" + messageType, response.Payload, messageType + " response payload");
        return response.Payload;
    }

    [Fact]
    public async Task WithNothingBeingCalibratedEveryRequestAnswersWithAnOutcome()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        Assert.Equal("NOT_NEEDED",
            Answered(await client.SendAsync("CheckSharedCalibration"), "CheckSharedCalibration")["outcome"]!.GetValue<string>());
        Assert.Equal("NOTHING_TO_ACCEPT",
            Answered(await client.SendAsync("AcceptSharedQueueInference"), "AcceptSharedQueueInference")["outcome"]!.GetValue<string>());
        var rejected = Answered(await client.SendAsync("RejectSharedCalibration"), "RejectSharedCalibration");
        Assert.Null(rejected["withdrawn_profile_id"]);
        Assert.Equal(0, rejected["dropped_candidates"]!.GetValue<int>());

        var share = await client.SendAsync("GetCalibrationShareCode");
        Assert.False(share.Ok);
        Assert.Equal(ErrorCodes.ShareCodeUnavailable, share.ErrorCode);
        Assert.Equal("NO_PROFILE", share.Payload["details"]!["reason"]!.GetValue<string>());
        Assert.Matches("[\\u4e00-\\u9fff]", share.ErrorMessage);
        ContractSchema.Validate("$defs/ErrorPayload", share.Payload, "share code refusal");

        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/Responses/GetCaptureStatus", status, "capture status");
        var shared = status["calibration"]!["shared"]!;
        Assert.Equal("NONE", shared["phase"]!.GetValue<string>());
        Assert.False(shared["user_rejected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ImportAnswersAnyPastedTextWithAnOutcomeAndRefusesOnlyARequestOfTheWrongShape()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var (code, sha) = Vector();

        var garbage = Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" }), "ImportCalibrationCode");
        Assert.Equal("MALFORMED", garbage["outcome"]!.GetValue<string>());
        Assert.StartsWith("E_SHARE_CODE_", garbage["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(garbage["code_sha256"]);
        Assert.Matches("[\\u4e00-\\u9fff]", garbage["message"]!.GetValue<string>());

        var overlong = Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = ShareCode.Prefix + new string('A', ShareCode.MaxCodeLength) }),
            "ImportCalibrationCode");
        Assert.Equal("MALFORMED", overlong["outcome"]!.GetValue<string>());
        Assert.Equal("E_SHARE_CODE_TOO_LONG", overlong["reason"]!.GetValue<string>());

        var notCalibrating = Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "  " + code + "\n" }), "ImportCalibrationCode");
        Assert.Equal("NOT_APPLICABLE", notCalibrating["outcome"]!.GetValue<string>());
        Assert.Equal("NOT_CALIBRATING", notCalibrating["reason"]!.GetValue<string>());
        Assert.Equal(sha, notCalibrating["code_sha256"]!.GetValue<string>());

        foreach (var payload in new[]
                 {
                     new JsonObject(),
                     new JsonObject { ["code"] = 5 },
                     new JsonObject { ["code"] = new string('A', SharedCalibrationHandlers.MaxCodeRequestLength + 1) },
                     new JsonObject { ["code"] = code, ["extra"] = true },
                 })
        {
            Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("ImportCalibrationCode", payload)).ErrorCode);
        }

        Assert.Equal(ErrorCodes.BadRequest,
            (await client.SendAsync("RejectSharedCalibration", new JsonObject { ["extra"] = 1 })).ErrorCode);
    }

    [Fact]
    public async Task ARequestWhoseTextIsNotWellFormedUnicodeIsRefusedWithoutEndingTheConnection()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        const string envelope = "{\"protocol_version\":1,\"request_id\":\"00000000-0000-4000-8000-0000000000a1\"," +
            "\"message_type\":\"ImportCalibrationCode\",\"payload\":{\"code\":\"MRC1.x\"}}";

        // Lone surrogate escapes in the pasted code, in a payload key and in an envelope key.
        // Sent as raw frames, because a client serialiser never writes them.
        foreach (var (anchor, replacement) in new[]
                 {
                     ("\"MRC1.x\"", "\"\\ud800\""),
                     ("{\"code\"", "{\"\\ud800\":1,\"code\""),
                     ("{\"protocol_version\"", "{\"\\ud800\":1,\"protocol_version\""),
                 })
        {
            await client.SendRawAsync(System.Text.Encoding.UTF8.GetBytes(envelope.Replace(anchor, replacement, StringComparison.Ordinal)));
        }

        var afterwards = Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" }), "ImportCalibrationCode");
        Assert.Equal("MALFORMED", afterwards["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSharedCalibrationSettingIsOnByDefaultAndTurnsOffThroughTheContract()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        Assert.True(Answered(await client.SendAsync("GetCaptureSettings"), "GetCaptureSettings")["shared_calibration_enabled"]!.GetValue<bool>());

        var off = Answered(
            await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["shared_calibration_enabled"] = false }), "UpdateCaptureSettings");

        Assert.False(off["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.True(off["auto_calibration_enabled"]!.GetValue<bool>());
        Assert.False(Answered(await client.SendAsync("GetCaptureSettings"), "GetCaptureSettings")["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.BadRequest,
            (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["shared_calibration_enabled"] = "no" })).ErrorCode);
    }
}
