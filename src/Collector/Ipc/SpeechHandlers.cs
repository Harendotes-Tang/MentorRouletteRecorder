using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Speech;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The online speech requests and the read-only database check of decision 4. The renderers
/// are public so the contract tests can hold every success shape against the schema, including
/// those a pipe test without a speech service cannot reach.
/// </summary>
public static class SpeechHandlers
{
    /// <summary>Longest <c>text</c> a request may carry before it is even prepared, in UTF-16 units.</summary>
    public const int MaxRawTextLength = 1024;

    /// <summary>Handles <c>GetSpeechSettings</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject GetSettings(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        return SettingsResponse(host.Speech.GetSettings());
    }

    /// <summary>Handles <c>UpdateSpeechSettings</c>. The key goes in and never comes back out.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject UpdateSettings(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        return SettingsResponse(host.Speech.UpdateSettings(ParseUpdate(reader)));
    }

    /// <summary>Reads an <c>UpdateSpeechSettings</c> payload.</summary>
    /// <param name="reader">Request payload.</param>
    public static SpeechSettingsUpdate ParseUpdate(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("provider", "azure_region", "openai_base_url", "openai_model", "voice", "api_key");

        SpeechProvider? provider = null;
        if (reader.Has("provider"))
        {
            var token = reader.String("provider", 32);
            if (!SpeechProviderWire.TryParse(token, out var parsed))
            {
                throw CollectorException.BadRequest(
                    "provider 只能取 " + string.Join(" / ", SpeechProviderWire.AllTokens) + " 之一。", "payload.provider");
            }

            provider = parsed;
        }

        if (reader.IsNull("api_key"))
        {
            throw CollectorException.BadRequest("api_key 必须是字符串：省略表示不变，空字符串表示清除。", "payload.api_key");
        }

        return new SpeechSettingsUpdate
        {
            Provider = provider,
            AzureRegion = reader.String("azure_region", 64),
            AzureRegionSpecified = reader.Has("azure_region"),
            OpenAiBaseUrl = reader.String("openai_base_url", SpeechValidation.MaxBaseUrlLength),
            OpenAiBaseUrlSpecified = reader.Has("openai_base_url"),
            OpenAiModel = reader.String("openai_model", SpeechValidation.MaxNameLength),
            OpenAiModelSpecified = reader.Has("openai_model"),
            Voice = reader.String("voice", SpeechValidation.MaxNameLength),
            VoiceSpecified = reader.Has("voice"),
            ApiKey = reader.String("api_key", SpeechValidation.MaxKeyLength),
        };
    }

    /// <summary>Reads a <c>SynthesizeSpeech</c> payload.</summary>
    /// <param name="reader">Request payload.</param>
    public static SpeechRequest ParseSynthesize(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("text", "rate_percent", "test");
        var raw = reader.String("text", MaxRawTextLength)
            ?? throw CollectorException.BadRequest("缺少必填字段 text。", "payload.text");
        if (!SpeechValidation.TryNormalizeText(raw, out var text))
        {
            throw CollectorException.BadRequest(
                $"text 必须是 1 到 {SpeechValidation.MaxTextLength} 个字符的一句话，不能含控制字符。", "payload.text");
        }

        var rate = reader.Int("rate_percent", SpeechValidation.MinRatePercent, SpeechValidation.MaxRatePercent)
                   ?? SpeechValidation.DefaultRatePercent;
        return new SpeechRequest(text, rate, reader.Bool("test") ?? false);
    }

    /// <summary>Handles <c>SynthesizeSpeech</c>; runs off the connection's read loop.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    public static async Task<JsonObject> SynthesizeAsync(
        CollectorHost host, PayloadReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var request = ParseSynthesize(reader);
        var result = await host.Speech.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        return SynthesisResponse(result);
    }

    /// <summary>
    /// Handles <c>CheckDatabaseIntegrity</c>: read-only, on its own connection, so live capture is
    /// never held up by the scan.
    /// </summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject CheckDatabaseIntegrity(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        var outcome = host.Database.CheckIntegrity();
        return IntegrityResponse(outcome, UtcTimestamp.Truncate(host.Clock.UtcNow));
    }

    /// <summary>The <c>GetSpeechSettings</c> / <c>UpdateSpeechSettings</c> response. Never a key.</summary>
    /// <param name="view">Settings view.</param>
    public static JsonObject SettingsResponse(SpeechSettingsView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var config = view.Config;
        return new JsonObject
        {
            ["provider"] = SpeechProviderWire.Format(config.Provider),
            ["azure_region"] = config.AzureRegion,
            ["openai_base_url"] = config.OpenAiBaseUrl,
            ["openai_model"] = config.OpenAiModel,
            ["voice"] = config.Voice,
            ["has_key"] = view.HasKey,
            ["configured"] = view.Configured,
            ["target_host"] = view.TargetHost,
            ["azure_voices"] = Voices(OnlineSpeechService.AzureVoices),
            ["openai_voices"] = Voices(OnlineSpeechService.OpenAiVoices),
        };
    }

    /// <summary>The <c>SynthesizeSpeech</c> response.</summary>
    /// <param name="result">Synthesis.</param>
    public static JsonObject SynthesisResponse(SpeechSynthesis result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new JsonObject
        {
            ["audio_path"] = result.AudioPath,
            ["from_cache"] = result.FromCache,
            ["provider"] = SpeechProviderWire.Format(result.Provider),
        };
    }

    /// <summary>The <c>CheckDatabaseIntegrity</c> response.</summary>
    /// <param name="outcome">Check result.</param>
    /// <param name="checkedAtUtc">When it ran.</param>
    public static JsonObject IntegrityResponse(IntegrityCheckOutcome outcome, DateTimeOffset checkedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new JsonObject
        {
            ["passed"] = outcome.Passed,
            ["detail"] = outcome.Detail,
            ["checked_at_utc"] = UtcTimestamp.ToText(checkedAtUtc),
        };
    }

    private static JsonArray Voices(IEnumerable<SpeechVoiceOption> voices) => new(voices
        .Select(voice => (JsonNode?)new JsonObject { ["name"] = voice.Name, ["label"] = voice.Label })
        .ToArray());
}
