using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Speech;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Online speech and the integrity check over a real pipe (decision 4). The speech service is a
/// fake transport: nothing here leaves the process.
/// </summary>
public sealed class SpeechIpcTests
{
    private const string Key = "ipc-test-key-7d2e";

    private sealed class Transport
    {
        private readonly Func<SpeechHttpRequest, CancellationToken, Task<SpeechTransportResponse>> _answer;

        public Transport(Func<SpeechHttpRequest, CancellationToken, Task<SpeechTransportResponse>>? answer = null) =>
            _answer = answer ?? ((request, _) => Task.FromResult(Wav(request)));

        public ConcurrentQueue<SpeechHttpRequest> Requests { get; } = new();

        public OnlineSpeechClient Client(string? killSwitch = null) => new(
            (request, token) =>
            {
                Requests.Enqueue(request);
                return _answer(request, token);
            },
            TimeSpan.FromSeconds(5),
            _ => killSwitch);

        public static SpeechTransportResponse Wav(SpeechHttpRequest request) =>
            new(200, request.Uri, "audio/wav", null, new MemoryStream(WaveFile.Silence()));
    }

    private static JsonObject AzureSettings(string? key = Key) => new()
    {
        ["provider"] = "azure",
        ["azure_region"] = "eastasia",
        ["voice"] = "zh-CN-YunxiNeural",
        ["api_key"] = key,
    };

    [Fact]
    public async Task SettingsRoundTripAndNoResponseEverCarriesTheKey()
    {
        await using var fixture = ServerFixture.Start(speech: new Transport().Client());
        await using var client = await fixture.ConnectAsync();
        var seen = new List<string>();
        client.EnvelopeReceived += envelope =>
        {
            lock (seen)
            {
                seen.Add(envelope.ToJsonString());
            }
        };

        var initial = (await client.SendAsync("GetSpeechSettings")).Require();
        Assert.Equal("none", initial["provider"]!.GetValue<string>());
        Assert.False(initial["has_key"]!.GetValue<bool>());
        Assert.False(initial["configured"]!.GetValue<bool>());
        Assert.Null(initial["target_host"]);
        Assert.NotEmpty(initial["azure_voices"]!.AsArray());

        var updated = (await client.SendAsync("UpdateSpeechSettings", AzureSettings())).Require();
        Assert.Equal("azure", updated["provider"]!.GetValue<string>());
        Assert.Equal("eastasia", updated["azure_region"]!.GetValue<string>());
        Assert.True(updated["has_key"]!.GetValue<bool>());
        Assert.True(updated["configured"]!.GetValue<bool>());
        Assert.Equal("eastasia" + OnlineSpeechClient.AzureHostSuffix, updated["target_host"]!.GetValue<string>());

        var reread = (await client.SendAsync("GetSpeechSettings")).Require();
        Assert.Equal(updated.ToJsonString(), reread.ToJsonString());

        var moved = (await client.SendAsync("UpdateSpeechSettings", new JsonObject { ["azure_region"] = "westus2" })).Require();
        Assert.False(moved["has_key"]!.GetValue<bool>());

        var cleared = (await client.SendAsync("UpdateSpeechSettings", new JsonObject
        {
            ["provider"] = "none", ["azure_region"] = null, ["voice"] = "", ["api_key"] = "",
        })).Require();
        Assert.Equal("none", cleared["provider"]!.GetValue<string>());
        Assert.Null(cleared["azure_region"]);
        Assert.Null(cleared["voice"]);

        foreach (var text in seen)
        {
            Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
            Assert.DoesNotContain("api_key", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(fixture.Log, line => line.Contains(Key, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "speech-key.bin")));
    }

    [Theory]
    [InlineData("{\"provider\":\"Azure\"}", "payload.provider")]
    [InlineData("{\"provider\":null}", "payload.provider")]
    [InlineData("{\"azure_region\":\"east.asia\"}", "payload.azure_region")]
    [InlineData("{\"azure_region\":7}", "payload.azure_region")]
    [InlineData("{\"openai_base_url\":\"http://speech.example.com/v1\"}", "payload.openai_base_url")]
    [InlineData("{\"openai_base_url\":\"https://u:p@speech.example.com/v1\"}", "payload.openai_base_url")]
    [InlineData("{\"openai_base_url\":\"https://speech.example.com/v1?x=1\"}", "payload.openai_base_url")]
    [InlineData("{\"openai_model\":\"has space\"}", "payload.openai_model")]
    [InlineData("{\"voice\":\"<x>\"}", "payload.voice")]
    [InlineData("{\"api_key\":null}", "payload.api_key")]
    [InlineData("{\"api_key\":\"orphan-key\"}", "payload.api_key")]
    [InlineData("{\"key\":\"x\"}", "payload.key")]
    public async Task ABadUpdateIsRefusedWithItsField(string payload, string field)
    {
        await using var fixture = ServerFixture.Start(speech: new Transport().Client());
        var response = await fixture.CallAsync("UpdateSpeechSettings", JsonNode.Parse(payload)!.AsObject());

        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Equal(field, response.Payload["field"]!.GetValue<string>());
        Assert.Equal("none", (await fixture.CallAsync("GetSpeechSettings")).Require()["provider"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}", "payload.text")]
    [InlineData("{\"text\":\"\"}", "payload.text")]
    [InlineData("{\"text\":\"  \\n \"}", "payload.text")]
    [InlineData("{\"text\":\"bell\\u0007\"}", "payload.text")]
    [InlineData("{\"text\":\"a\",\"rate_percent\":49}", "payload.rate_percent")]
    [InlineData("{\"text\":\"a\",\"rate_percent\":201}", "payload.rate_percent")]
    [InlineData("{\"text\":\"a\",\"rate_percent\":1.5}", "payload.rate_percent")]
    [InlineData("{\"text\":\"a\",\"test\":\"yes\"}", "payload.test")]
    [InlineData("{\"text\":\"a\",\"voice\":\"x\"}", "payload.voice")]
    public async Task ABadSynthesisRequestIsRefusedWithItsField(string payload, string field)
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        var response = await fixture.CallAsync("SynthesizeSpeech", JsonNode.Parse(payload)!.AsObject());

        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Equal(field, response.Payload["field"]!.GetValue<string>());
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ATwoHundredAndOneCharacterSentenceIsRefused()
    {
        await using var fixture = ServerFixture.Start(speech: new Transport().Client());
        var response = await fixture.CallAsync("SynthesizeSpeech", new JsonObject { ["text"] = new string('字', 201) });
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    [Fact]
    public async Task ASentenceIsSpokenIntoTheCacheFolderAndServedFromItTheSecondTime()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("UpdateSpeechSettings", AzureSettings())).Require();

        var request = new JsonObject { ["text"] = "进入副本：天然要害沙都", ["rate_percent"] = 110 };
        var first = (await client.SendAsync("SynthesizeSpeech", request.DeepClone().AsObject())).Require();
        ContractSchema.Validate("$defs/Responses/SynthesizeSpeech", first, "first synthesis");
        Assert.False(first["from_cache"]!.GetValue<bool>());
        Assert.Equal("azure", first["provider"]!.GetValue<string>());

        var path = first["audio_path"]!.GetValue<string>();
        var cacheFolder = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "tts-cache");
        Assert.Equal(cacheFolder, Path.GetDirectoryName(path));
        Assert.True(fixture.Host.Speech.Cache.Contains(path));
        Assert.Equal(WaveFile.Silence(), await File.ReadAllBytesAsync(path));

        var second = (await client.SendAsync("SynthesizeSpeech", request.DeepClone().AsObject())).Require();
        Assert.True(second["from_cache"]!.GetValue<bool>());
        Assert.Equal(path, second["audio_path"]!.GetValue<string>());
        Assert.Single(transport.Requests);

        var sent = transport.Requests.Single();
        Assert.Equal(Key, sent.Header(OnlineSpeechClient.AzureKeyHeader));
        Assert.Contains("rate='+10%'", System.Text.Encoding.UTF8.GetString(sent.Body), StringComparison.Ordinal);

        var test = (await client.SendAsync("SynthesizeSpeech", new JsonObject
        {
            ["text"] = "进入副本：天然要害沙都", ["rate_percent"] = 110, ["test"] = true,
        })).Require();
        Assert.False(test["from_cache"]!.GetValue<bool>());
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task AWaitingSentenceDoesNotHoldUpTheConnection()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport(async (request, _) =>
        {
            await release.Task;
            return Transport.Wav(request);
        });
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("UpdateSpeechSettings", AzureSettings())).Require();

        var pending = client.SendAsync("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" }, timeout: TimeSpan.FromSeconds(20));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (transport.Requests.IsEmpty)
        {
            Assert.True(DateTime.UtcNow < deadline, "the synthesis never reached the transport");
            await Task.Delay(10);
        }

        // Answered while the sentence is still waiting on the speech service.
        var version = await client.SendAsync("GetVersion", timeout: TimeSpan.FromSeconds(3));
        Assert.True(version.Ok);
        Assert.True((await client.SendAsync("GetCaptureStatus", timeout: TimeSpan.FromSeconds(3))).Ok);
        Assert.False(pending.IsCompleted);

        release.SetResult();
        var spoken = (await pending).Require();
        Assert.False(spoken["from_cache"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ASentenceStillWaitingWhenTheClientLeavesDoesNotBreakTheServer()
    {
        var transport = new Transport(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Transport.Wav(request);
        });
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        (await fixture.CallAsync("UpdateSpeechSettings", AzureSettings())).Require();

        await using (var client = await fixture.ConnectAsync())
        {
            _ = client.SendAsync("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (transport.Requests.IsEmpty)
            {
                Assert.True(DateTime.UtcNow < deadline, "the synthesis never reached the transport");
                await Task.Delay(10);
            }
        }

        Assert.True((await fixture.CallAsync("GetVersion")).Ok);
        Assert.False(fixture.Serving.IsCompleted);
    }

    [Fact]
    public async Task TheKillSwitchRefusesBeforeAnythingIsSent()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(speech: transport.Client(killSwitch: "1"));
        (await fixture.CallAsync("UpdateSpeechSettings", AzureSettings())).Require();

        var refused = await fixture.CallAsync("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功", ["test"] = true });
        Assert.Equal(ErrorCodes.SpeechDisabled, refused.ErrorCode);
        ContractSchema.Validate("$defs/ErrorPayload", refused.Payload, "disabled refusal");
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task AServiceRefusalIsAnErrorCodeAndNothingOfWhatTheServiceSaid()
    {
        var transport = new Transport((request, _) => Task.FromResult(new SpeechTransportResponse(
            401, request.Uri, "application/json", null,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"error\":\"bad key " + Key + "\"}")))));
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        (await fixture.CallAsync("UpdateSpeechSettings", AzureSettings())).Require();

        var refused = await fixture.CallAsync("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" });
        Assert.Equal(ErrorCodes.SpeechAuth, refused.ErrorCode);
        Assert.Equal(401, refused.Payload["details"]!["http_status"]!.GetValue<int>());
        Assert.DoesNotContain(Key, refused.Payload.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("bad key", refused.Payload.ToJsonString(), StringComparison.Ordinal);
        ContractSchema.Validate("$defs/ErrorPayload", refused.Payload, "auth refusal");
    }

    [Fact]
    public async Task TheDiagnosticsReportStatesTheSecondRequestClassWithoutTheKeyOrTheAddress()
    {
        var transport = new Transport();
        await using var fixture = ServerFixture.Start(speech: transport.Client());
        await using var client = await fixture.ConnectAsync();
        (await client.SendAsync("UpdateSpeechSettings", AzureSettings())).Require();
        (await client.SendAsync("SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" })).Require();

        var target = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "speech-diag.json");
        var report = (await client.SendAsync("ExportDiagnosticsReport", new JsonObject { ["target_path"] = target })).Require();
        var text = await File.ReadAllTextAsync(report["target_path"]!.GetValue<string>());
        var online = JsonNode.Parse(text)!["boundary"]!["outbound"]!["online_speech"]!;

        Assert.Equal("azure", online["provider"]!.GetValue<string>());
        Assert.False(online["kill_switch"]!.GetValue<bool>());
        Assert.Equal("OK", online["last_outcome"]!.GetValue<string>());
        Assert.NotNull(online["last_request_utc"]);
        Assert.DoesNotContain(Key, text, StringComparison.Ordinal);
        Assert.DoesNotContain("eastasia", text, StringComparison.Ordinal);
        Assert.DoesNotContain(OnlineSpeechClient.AzureHostSuffix, text, StringComparison.Ordinal);
        Assert.DoesNotContain("匹配成功", text, StringComparison.Ordinal);
        Assert.DoesNotContain("YunxiNeural", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHealthyDatabasePassesTheIntegrityCheck()
    {
        await using var fixture = ServerFixture.Start();
        var response = await fixture.CallAsync("CheckDatabaseIntegrity");
        var payload = response.Require();

        ContractSchema.Validate("$defs/Responses/CheckDatabaseIntegrity", payload, "integrity check");
        Assert.True(payload["passed"]!.GetValue<bool>());
        Assert.Equal("ok", payload["detail"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.BadRequest, (await fixture.CallAsync(
            "CheckDatabaseIntegrity", new JsonObject { ["quick"] = true })).ErrorCode);
    }
}
