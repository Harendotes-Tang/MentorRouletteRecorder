using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The online speech, integrity-check and last-event-kind shapes, taken straight from the
/// renderers and held against contracts/ipc-v1.schema.json. Every token the Collector can write
/// is checked against the enums the contract declares, and the request side is checked both ways.
/// </summary>
public sealed class SpeechContractTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 16, 21, 38, 4, TimeSpan.Zero);

    private static string[] Declared(params string[] pointer)
    {
        JsonNode? node = ContractSchema.Root;
        foreach (var segment in pointer)
        {
            node = node![segment];
        }

        return node!.AsArray().OfType<JsonNode>().Select(item => item.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
    }

    public static TheoryData<string> SettingsCases => new() { "off", "azure", "azure-no-key", "openai", "openai-loopback", "half" };

    [Theory]
    [MemberData(nameof(SettingsCases))]
    public void EverySettingsShapeMatchesTheContract(string which)
    {
        var view = which switch
        {
            "off" => new SpeechSettingsView(OnlineSpeechConfig.Off, false, null),
            "azure" => new SpeechSettingsView(
                new OnlineSpeechConfig(SpeechProvider.Azure, "eastasia", null, null, "zh-CN-XiaoxiaoNeural"), true,
                "eastasia" + OnlineSpeechClient.AzureHostSuffix),
            "azure-no-key" => new SpeechSettingsView(
                new OnlineSpeechConfig(SpeechProvider.Azure, "eastasia", "https://speech.example.com/v1", "tts-1", "zh-CN-YunxiNeural"),
                false, "eastasia" + OnlineSpeechClient.AzureHostSuffix),
            "openai" => new SpeechSettingsView(
                new OnlineSpeechConfig(SpeechProvider.OpenAiCompatible, null, "https://speech.example.com/v1", "gpt-4o-mini-tts", "vendor/model:speaker"),
                true, "speech.example.com"),
            "openai-loopback" => new SpeechSettingsView(
                new OnlineSpeechConfig(SpeechProvider.OpenAiCompatible, null, "http://[::1]:8000", "tts-1", "alloy"),
                true, "[::1]"),
            _ => new SpeechSettingsView(new OnlineSpeechConfig(SpeechProvider.OpenAiCompatible, null, null, null, "alloy"), false, null),
        };

        var wire = SpeechHandlers.SettingsResponse(view);
        ContractSchema.Validate("$defs/Responses/GetSpeechSettings", wire, which);
        ContractSchema.Validate("$defs/Responses/UpdateSpeechSettings", wire, which);
        Assert.Equal(view.Configured, wire["configured"]!.GetValue<bool>());
        Assert.False(wire.ContainsKey("api_key"));
    }

    [Fact]
    public void EverySynthesisAndIntegrityShapeMatchesTheContract()
    {
        foreach (var provider in new[] { SpeechProvider.Azure, SpeechProvider.OpenAiCompatible })
        {
            foreach (var fromCache in new[] { true, false })
            {
                ContractSchema.Validate(
                    "$defs/Responses/SynthesizeSpeech",
                    SpeechHandlers.SynthesisResponse(new SpeechSynthesis(@"C:\data\tts-cache\" + new string('a', 64) + ".wav", fromCache, provider)),
                    provider + " " + fromCache);
            }
        }

        foreach (var outcome in new[]
                 {
                     new IntegrityCheckOutcome(true, "ok"),
                     new IntegrityCheckOutcome(false, "*** in database main ***"),
                     new IntegrityCheckOutcome(false, new string('x', SqliteDatabase.MaxIntegrityDetailLength)),
                 })
        {
            ContractSchema.Validate("$defs/Responses/CheckDatabaseIntegrity", SpeechHandlers.IntegrityResponse(outcome, At), outcome.Detail);
        }

        var tooLong = SpeechHandlers.IntegrityResponse(new IntegrityCheckOutcome(false, new string('x', 201)), At);
        Assert.False(ContractSchema.Evaluate("$defs/Responses/CheckDatabaseIntegrity", tooLong).IsValid);
    }

    [Fact]
    public void TheContractDeclaresEveryTokenTheCollectorWrites()
    {
        Assert.Equal(SpeechProviderWire.AllTokens.Order(StringComparer.Ordinal), Declared("$defs", "SpeechProvider", "enum"));
        Assert.Equal(
            SpeechProviderWire.AllTokens.Where(token => token != "none").Order(StringComparer.Ordinal),
            Declared("$defs", "Responses", "SynthesizeSpeech", "properties", "provider", "enum"));

        var kinds = ContractSchema.Root["$defs"]!["CaptureStatus"]!["properties"]!["last_valid_event_kind"]!["enum"]!
            .AsArray().Select(node => node?.GetValue<string>()).ToArray();
        // The predicate overload is required: on a string?[] the .NET 8 SDK compiler cannot
        // choose between xunit 2.9's span and enumerable Contains (CS8631); the 9.x SDK can.
        Assert.Contains(kinds, kind => kind is null);
        Assert.Equal(
            ProfileMessageParser.EventKinds.Order(StringComparer.Ordinal),
            kinds.OfType<string>().Order(StringComparer.Ordinal));

        var codes = Declared("$defs", "ErrorPayload", "properties", "code", "enum");
        foreach (var outcome in Enum.GetValues<SpeechOutcome>().Where(outcome => outcome != SpeechOutcome.Ok))
        {
            var code = OnlineSpeechService.ErrorCodeFor(outcome);
            Assert.StartsWith("ERR_SPEECH_", code, StringComparison.Ordinal);
            Assert.Contains(code, codes);
        }

        Assert.Equal(7, codes.Count(code => code.StartsWith("ERR_SPEECH_", StringComparison.Ordinal)));

        var errorTable = File.ReadAllText(Path.Combine(ContractSchema.RepositoryRoot, "contracts", "error-codes.md"));
        Assert.All(codes, code => Assert.Contains("`" + code + "`", errorTable, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCaptureStatusStaysValidWithAndWithoutTheEventKind()
    {
        var status = new JsonObject
        {
            ["state"] = "RUNNING",
            ["npcap_installed"] = true,
            ["ffxiv_running"] = true,
            ["profile_status"] = "VERIFIED",
            ["last_valid_event_at_utc"] = "2026-09-16T21:38:04.000Z",
        };
        ContractSchema.Validate("$defs/CaptureStatus", status, "a Collector before last_valid_event_kind");

        foreach (var kind in ProfileMessageParser.EventKinds)
        {
            status["last_valid_event_kind"] = kind;
            ContractSchema.Validate("$defs/CaptureStatus", status, kind);
        }

        status["last_valid_event_kind"] = null;
        ContractSchema.Validate("$defs/CaptureStatus", status, "no event yet");

        status["last_valid_event_kind"] = "CAPTURE_STOPPED";
        Assert.False(ContractSchema.Evaluate("$defs/CaptureStatus", status).IsValid);
    }

    public static TheoryData<string, string, bool> RequestCases => new()
    {
        { "GetSpeechSettings", "{}", true },
        { "GetSpeechSettings", "{\"provider\":\"azure\"}", false },
        { "CheckDatabaseIntegrity", "{}", true },
        { "CheckDatabaseIntegrity", "{\"quick\":true}", false },
        { "UpdateSpeechSettings", "{}", true },
        { "UpdateSpeechSettings", "{\"api_key\":\"\"}", true },
        { "UpdateSpeechSettings", "{\"azure_region\":null,\"voice\":null,\"openai_model\":null,\"openai_base_url\":null}", true },
        { "UpdateSpeechSettings", "{\"api_key\":null}", false },
        { "UpdateSpeechSettings", "{\"provider\":\"Azure\"}", false },
        { "UpdateSpeechSettings", "{\"provider\":null}", false },
        { "UpdateSpeechSettings", "{\"has_key\":true}", false },
        { "SynthesizeSpeech", "{\"text\":\"匹配成功\"}", true },
        { "SynthesizeSpeech", "{\"text\":\"匹配成功\",\"rate_percent\":50,\"test\":true}", true },
        { "SynthesizeSpeech", "{}", false },
        { "SynthesizeSpeech", "{\"text\":\"\"}", false },
        { "SynthesizeSpeech", "{\"text\":\"a\",\"rate_percent\":201}", false },
        { "SynthesizeSpeech", "{\"text\":\"a\",\"rate_percent\":49}", false },
        { "SynthesizeSpeech", "{\"text\":\"a\",\"voice\":\"alloy\"}", false },
    };

    [Theory]
    [MemberData(nameof(RequestCases))]
    public void TheRequestSchemaAcceptsWhatTheCollectorAcceptsAndRefusesTheRest(string messageType, string payload, bool valid)
    {
        var envelope = new JsonObject
        {
            ["protocol_version"] = 1,
            ["request_id"] = "11111111-2222-4333-8444-555555555555",
            ["message_type"] = messageType,
            ["payload"] = JsonNode.Parse(payload),
        };

        Assert.Equal(valid, ContractSchema.Evaluate("$defs/RequestEnvelope", envelope).IsValid);
    }

    [Fact]
    public void AnOverlongSentenceIsRefusedByTheSchemaToo()
    {
        var envelope = new JsonObject
        {
            ["protocol_version"] = 1,
            ["request_id"] = "11111111-2222-4333-8444-555555555555",
            ["message_type"] = "SynthesizeSpeech",
            ["payload"] = new JsonObject { ["text"] = new string('字', 201) },
        };
        Assert.False(ContractSchema.Evaluate("$defs/RequestEnvelope", envelope).IsValid);

        envelope["payload"] = new JsonObject { ["text"] = new string('字', 200) };
        Assert.True(ContractSchema.Evaluate("$defs/RequestEnvelope", envelope).IsValid);
    }
}
