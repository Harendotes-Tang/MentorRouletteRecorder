using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class CalibrationEvidenceIpcTests
{
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
