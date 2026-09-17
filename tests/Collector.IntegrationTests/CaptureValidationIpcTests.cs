using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class CaptureValidationIpcTests
{
    [Fact]
    public async Task SyntheticRecordingUsesRealPipeStrictSchemaAndNeverCreatesRuns()
    {
        var source = new FakeCaptureSource();
        var capture = CaptureFakes.Ready(source, "2026.01");
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
        var responses = new List<JsonObject>();
        client.EnvelopeReceived += envelope => { lock (responses) responses.Add(envelope.DeepClone().AsObject()); };
        Assert.Equal("IDLE", (await client.SendAsync("GetCaptureValidationStatus")).Require()["state"]!.GetValue<string>());
        var start = await client.SendAsync("StartCaptureValidation", new JsonObject { ["adapter_id"] = "{TEST-ADAPTER}" });
        Assert.True(start.Ok, start.ErrorMessage);
        Assert.Equal("WAITING", start.Require()["state"]!.GetValue<string>());
        await AwaitState(client, "RECORDING");
        source.PushOpcode(0x1234, 12);
        source.PushRaw(new byte[] { 1 });
        foreach (var marker in new[] { "queued", "pop", "entered", "victory", "left" })
            Assert.True((await client.SendAsync("AddCaptureValidationMarker", new JsonObject { ["marker"] = marker })).Ok);
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("StartCapture")).ErrorCode);
        Assert.Equal(ErrorCodes.BadRequest, (await client.SendAsync("StartCaptureValidation")).ErrorCode);
        var stop = await client.SendAsync("StopCaptureValidation");
        Assert.Equal("STOPPING", stop.Require()["state"]!.GetValue<string>());
        var final = await AwaitState(client, "COMPLETED");
        Assert.Equal(1, final["message_count"]!.GetValue<long>());
        Assert.Equal(1, final["decode_error_count"]!.GetValue<long>());
        Assert.Equal(5, final["marker_count"]!.GetValue<long>());
        var path = final["trace_path"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(Path.GetDirectoryName(fixture.Host.Database.Path)!, "traces") + Path.DirectorySeparatorChar, path);
        Assert.Equal(CaptureTraceDigest.Compute(path), final["sha256"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(File.ReadLines(path).First())!["synthetic"]!.GetValue<bool>());
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        Assert.True((await client.SendAsync("StopCaptureValidation")).Ok);
        JsonObject[] envelopes;
        lock (responses) envelopes = responses.ToArray();
        foreach (var envelope in envelopes)
        {
            ContractSchema.Validate(string.Empty, envelope, "validation pipe envelope");
            var type = envelope["message_type"]!.GetValue<string>();
            if (envelope["ok"]!.GetValue<bool>() && type.Contains("Validation", StringComparison.Ordinal))
                ContractSchema.Validate("$defs/Responses/" + type, envelope["payload"], type);
        }
    }

    /// <summary>
    /// The FAILED terminal status, validated against the same contract as the success path.
    ///
    /// Every other test here ends in COMPLETED, which exercises only the <c>true</c> branch of
    /// <c>CaptureValidationStatus</c>'s if/then/else (WAITING/RECORDING/STOPPING implies
    /// <c>active: true</c>, anything else <c>active: false</c>) and never produces the FAILED
    /// member of the state and reason enums. A source that cannot be created reaches it: no
    /// trace file, no hash, an error code, and still a legal CaptureValidationStatus.
    /// </summary>
    [Fact]
    public async Task AFailedSessionIsReportedAsFailedAndStillMatchesTheContract()
    {
        var capture = CaptureFakes.Ready(new FakeCaptureSource(), "2026.01");
        var validation = new CaptureValidationServices
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            Trace = new CaptureTraceServices
            {
                Npcap = capture.Npcap, Game = capture.Game, Adapters = capture.Adapters,
                // Npcap is present and the game is running, so the session gets as far as
                // opening a source, which is what fails.
                SourceFactory = () => throw new InvalidOperationException("无法打开所选网卡（测试）。"),
                TcpConnectionCounter = (_, _) => 0,
            },
        };
        await using var fixture = CaptureServerFixture.Start(capture, validation: validation);
        await using var client = await fixture.ConnectAsync();
        var responses = new List<JsonObject>();
        client.EnvelopeReceived += envelope => { lock (responses) responses.Add(envelope.DeepClone().AsObject()); };

        var start = await client.SendAsync(
            "StartCaptureValidation", new JsonObject { ["adapter_id"] = "{TEST-ADAPTER}" });
        Assert.True(start.Ok, start.ErrorMessage);

        var final = await AwaitState(client, "FAILED");
        Assert.Equal("FAILED", final["reason"]!.GetValue<string>());
        Assert.False(final["active"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.Internal, final["error_code"]!.GetValue<string>());
        // Nothing was recorded, so there is no file to cite and no hash of one.
        Assert.Null(final["trace_path"]);
        Assert.Null(final["sha256"]);
        Assert.Null(final["sha256_path"]);
        // A failed session must not have created a run either.
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());

        JsonObject[] envelopes;
        lock (responses) envelopes = responses.ToArray();
        Assert.NotEmpty(envelopes);
        foreach (var envelope in envelopes)
        {
            ContractSchema.Validate(string.Empty, envelope, "validation pipe envelope");
            var type = envelope["message_type"]!.GetValue<string>();
            if (envelope["ok"]!.GetValue<bool>() && type.Contains("Validation", StringComparison.Ordinal))
                ContractSchema.Validate("$defs/Responses/" + type, envelope["payload"], type);
        }
        Assert.Contains(envelopes, envelope =>
            envelope["message_type"]!.GetValue<string>() == "GetCaptureValidationStatus"
            && envelope["ok"]!.GetValue<bool>()
            && envelope["payload"]!["state"]!.GetValue<string>() == "FAILED");
    }

    [Theory]
    [InlineData("StartCaptureValidation", "{\"adapter_id\":\"\"}")]
    [InlineData("StartCaptureValidation", "{\"adapter_id\":\" \"}")]
    [InlineData("StartCaptureValidation", "{\"process_id\":42}")]
    [InlineData("StartCaptureValidation", "{\"output_path\":\"x\"}")]
    [InlineData("GetCaptureValidationStatus", "{\"extra\":true}")]
    [InlineData("StopCaptureValidation", "{\"extra\":true}")]
    [InlineData("AddCaptureValidationMarker", "{}")]
    [InlineData("AddCaptureValidationMarker", "{\"marker\":null}")]
    [InlineData("AddCaptureValidationMarker", "{\"marker\":\"QUEUED\"}")]
    [InlineData("AddCaptureValidationMarker", "{\"marker\":\"queued\",\"extra\":true}")]
    public async Task InvalidPayloadIsRejectedByPipeAndSchema(string messageType, string json)
    {
        await using var fixture = CaptureServerFixture.Start();
        var payload = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(ErrorCodes.BadRequest, (await fixture.CallAsync(messageType, payload)).ErrorCode);
        var request = new JsonObject
        {
            ["protocol_version"] = 1, ["request_id"] = Guid.NewGuid().ToString("D"),
            ["message_type"] = messageType, ["payload"] = payload.DeepClone(),
        };
        Assert.False(ContractSchema.Evaluate("$defs/RequestEnvelope", request).IsValid);
    }

    [Fact]
    public async Task MissingNpcapIsExplicitAndCreatesNoTrace()
    {
        await using var fixture = CaptureServerFixture.Start();
        Assert.Equal(ErrorCodes.NpcapMissing, (await fixture.CallAsync("StartCaptureValidation")).ErrorCode);
        Assert.Null((await fixture.CallAsync("GetCaptureValidationStatus")).Require()["trace_path"]);
    }

    private static async Task<JsonObject> AwaitState(PipeClient client, string state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var status = (await client.SendAsync("GetCaptureValidationStatus")).Require();
            if (status["state"]!.GetValue<string>() == state) return status;
            await Task.Delay(10, timeout.Token);
        }
    }
}
