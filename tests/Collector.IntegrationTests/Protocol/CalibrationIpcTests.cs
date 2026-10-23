using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Capture;

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

    // ------------------------------------------------------- calibration evidence ----

    [Fact]
    public async Task CompletedMetadataSessionExportsWhitelistOverRealPipeWithoutCreatingRuns()
    {
        var source = new FakeCaptureSource();
        var capture = CaptureFakes.Ready(source, "2026.09.01.0000.0000");
        var validation = new CaptureValidationServices
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            Trace = new CaptureTraceServices
            {
                Npcap = capture.Npcap, Game = capture.Game, Adapters = capture.Adapters,
                SourceFactory = () => source, TcpConnectionCounter = (_, _) => 0,
            },
        };
        await using var fixture = CaptureServerFixture.Start(capture, validation: validation);
        await using var client = await fixture.ConnectAsync();
        Assert.True((await client.SendAsync("StartCaptureValidation",
            new JsonObject { ["adapter_id"] = "{TEST-ADAPTER}" })).Ok);
        await AwaitValidationState(client, "RECORDING");
        Assert.True((await client.SendAsync("AddCaptureValidationMarker", new JsonObject { ["marker"] = "queued" })).Ok);
        source.PushOpcode(0x1234, 12);
        Assert.False((await client.SendAsync("GetTemplateObservation")).Require()["available"]!.GetValue<bool>());
        Assert.True((await client.SendAsync("StopCaptureValidation")).Ok);
        await AwaitValidationState(client, "COMPLETED");
        var reply = (await client.SendAsync("GetTemplateObservation")).Require();
        ContractSchema.Validate("$defs/Responses/GetTemplateObservation", reply, "completed observation");
        Assert.True(reply["available"]!.GetValue<bool>(), reply.ToJsonString());
        var text = reply["evidence_json"]!.GetValue<string>();
        var evidence = JsonNode.Parse(text)!;
        Assert.Equal("TEMPLATE_OBSERVATION", evidence["kind"]!.GetValue<string>());
        Assert.Null(evidence["target"]!["template_sha256"]);
        Assert.Equal("SYNTHETIC_SOURCE", evidence["sessions"]![0]!["health"]!["silent_reason"]!.GetValue<string>());
        Assert.DoesNotContain(fixture.DataDirectory, text);
        Assert.DoesNotContain("payload", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(text, (await client.SendAsync("GetTemplateObservation")).Require()["evidence_json"]!.GetValue<string>());
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    private static async Task AwaitValidationState(PipeClient client, string state)
    {
        for (var i = 0; i < 250; i++)
        {
            if ((await client.SendAsync("GetCaptureValidationStatus")).Require()["state"]!.GetValue<string>() == state) return;
            await Task.Delay(20);
        }
        Assert.Fail("validation state did not reach " + state);
    }

    [Fact]
    public async Task EvidenceRequestsHaveExplicitUnavailableResultsAndRejectUnknownInput()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        foreach (var type in new[] { "GetCalibrationEvidence", "GetTemplateObservation" })
        {
            var response = (await client.SendAsync(type)).Require();
            ContractSchema.Validate("$defs/Responses/" + type, response, type);
            Assert.False(response["available"]!.GetValue<bool>());
            Assert.Null(response["evidence_json"]);
            Assert.Null(response["issue_url"]);
            Assert.False(response["evidence_in_url"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(response["reason"]!.GetValue<string>()));
            Assert.Equal(ErrorCodes.BadRequest,
                (await client.SendAsync(type, new JsonObject { ["extra"] = true })).ErrorCode);
        }
        foreach (var hash in new[] { "", new string('A', 64), new string('a', 63), new string('g', 64) })
            Assert.Equal(ErrorCodes.BadRequest,
                (await client.SendAsync("GetCalibrationEvidence", new JsonObject { ["code_sha256"] = hash })).ErrorCode);
    }
}
