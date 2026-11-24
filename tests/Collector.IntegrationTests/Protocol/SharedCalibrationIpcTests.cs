using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The five shared-calibration requests and the setting over a real pipe on a machine with no
/// game: ordinary answers are outcomes matching the contract, only a share code that cannot be
/// given fails, and a request of the wrong shape is refused before anything decodes it. Each
/// answer's and refusal's sentence also travels as a catalogue reference that renders back to the
/// Chinese (contracts/collector-texts.md §2.1).
/// </summary>
public sealed class SharedCalibrationIpcTests
{
    private static (string Code, string Sha) Vector()
    {
        var path = Path.Combine(ContractSchema.RepositoryRoot, "tests", "Fixtures", "shared-calibration", "vectors.json");
        var valid = JsonNode.Parse(File.ReadAllText(path))!["valid"]![0]!;
        return (valid["code"]!.GetValue<string>(), valid["code_sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task WithNothingBeingCalibratedEveryRequestAnswersWithAnOutcome()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        Assert.Equal("NOT_NEEDED",
            ContractSchema.Answered(await client.SendAsync("CheckSharedCalibration"), "CheckSharedCalibration")["outcome"]!.GetValue<string>());
        Assert.Equal("NOTHING_TO_ACCEPT",
            ContractSchema.Answered(await client.SendAsync("AcceptSharedQueueInference"), "AcceptSharedQueueInference")["outcome"]!.GetValue<string>());
        var rejected = ContractSchema.Answered(await client.SendAsync("RejectSharedCalibration"), "RejectSharedCalibration");
        Assert.Null(rejected["withdrawn_profile_id"]);
        Assert.Equal(0, rejected["dropped_candidates"]!.GetValue<int>());

        var share = await client.SendAsync("GetCalibrationShareCode");
        CollectorTextCatalogue.AssertRefusal(share, "sharing.share_code.no_profile");
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

        var garbage = ContractSchema.Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" }), "ImportCalibrationCode");
        Assert.Equal("MALFORMED", garbage["outcome"]!.GetValue<string>());
        Assert.StartsWith("E_SHARE_CODE_", garbage["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(garbage["code_sha256"]);
        Assert.Matches("[\\u4e00-\\u9fff]", garbage["message"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(garbage["message"], garbage["message_ref"]);
        Assert.Equal("sharing.import.not_imported", CollectorTextCatalogue.Key(garbage["message_ref"]));
        Assert.Equal("sharing.code.characters", CollectorTextCatalogue.Key(garbage["message_ref"]!["args"]!["reason"]));

        var overlong = ContractSchema.Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = ShareCode.Prefix + new string('A', ShareCode.MaxCodeLength) }),
            "ImportCalibrationCode");
        Assert.Equal("MALFORMED", overlong["outcome"]!.GetValue<string>());
        Assert.Equal("E_SHARE_CODE_TOO_LONG", overlong["reason"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(overlong["message"], overlong["message_ref"]);
        Assert.Equal("sharing.import.code_too_long", CollectorTextCatalogue.Key(overlong["message_ref"]));
        Assert.Equal(ShareCode.MaxCodeLength, overlong["message_ref"]!["args"]!["max"]!.GetValue<int>());

        var notCalibrating = ContractSchema.Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "  " + code + "\n" }), "ImportCalibrationCode");
        Assert.Equal("NOT_APPLICABLE", notCalibrating["outcome"]!.GetValue<string>());
        Assert.Equal("NOT_CALIBRATING", notCalibrating["reason"]!.GetValue<string>());
        Assert.Equal(sha, notCalibrating["code_sha256"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(notCalibrating["message"], notCalibrating["message_ref"]);
        Assert.Equal("sharing.import.not_calibrating", CollectorTextCatalogue.Key(notCalibrating["message_ref"]));

        var missing = await client.SendAsync("ImportCalibrationCode", new JsonObject());
        Assert.Equal(ErrorCodes.BadRequest, missing.ErrorCode);
        CollectorTextCatalogue.AssertRefusal(missing, "ipc.field.missing");
        Assert.Equal("code", missing.Payload["message_ref"]!["args"]!["field"]!.GetValue<string>());

        foreach (var payload in new[]
                 {
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

        var afterwards = ContractSchema.Answered(
            await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" }), "ImportCalibrationCode");
        Assert.Equal("MALFORMED", afterwards["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSharedCalibrationSettingIsOnByDefaultAndTurnsOffThroughTheContract()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        Assert.True(ContractSchema.Answered(await client.SendAsync("GetCaptureSettings"), "GetCaptureSettings")["shared_calibration_enabled"]!.GetValue<bool>());

        var off = ContractSchema.Answered(
            await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["shared_calibration_enabled"] = false }), "UpdateCaptureSettings");

        Assert.False(off["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.True(off["auto_calibration_enabled"]!.GetValue<bool>());
        Assert.False(ContractSchema.Answered(await client.SendAsync("GetCaptureSettings"), "GetCaptureSettings")["shared_calibration_enabled"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.BadRequest,
            (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["shared_calibration_enabled"] = "no" })).ErrorCode);
    }
}
