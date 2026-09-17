using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Speech;

/// <summary>
/// One <c>UpdateSpeechSettings</c> request. Every member is optional; the string fields carry a
/// separate "was it named at all" flag because null (or "") is a value the user can ask for - clear
/// the field - and not the same thing as leaving it alone. <see cref="ApiKey"/> is write-only:
/// null leaves the stored key alone, "" clears it.
/// </summary>
public sealed record SpeechSettingsUpdate
{
    /// <summary>Service to use; null leaves it alone.</summary>
    public SpeechProvider? Provider { get; init; }

    /// <summary>Azure region as typed; null or empty clears it.</summary>
    public string? AzureRegion { get; init; }

    /// <summary>True when the request named <c>azure_region</c>.</summary>
    public bool AzureRegionSpecified { get; init; }

    /// <summary>OpenAI-compatible base address as typed; null or empty clears it.</summary>
    public string? OpenAiBaseUrl { get; init; }

    /// <summary>True when the request named <c>openai_base_url</c>.</summary>
    public bool OpenAiBaseUrlSpecified { get; init; }

    /// <summary>OpenAI-compatible model; null or empty clears it.</summary>
    public string? OpenAiModel { get; init; }

    /// <summary>True when the request named <c>openai_model</c>.</summary>
    public bool OpenAiModelSpecified { get; init; }

    /// <summary>Voice; null or empty clears it.</summary>
    public string? Voice { get; init; }

    /// <summary>True when the request named <c>voice</c>.</summary>
    public bool VoiceSpecified { get; init; }

    /// <summary>New key; null leaves it alone, "" clears it.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Printing without the key.</summary>
    /// <param name="builder">Target.</param>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Provider = ").Append(Provider)
            .Append(", AzureRegionSpecified = ").Append(AzureRegionSpecified)
            .Append(", OpenAiBaseUrlSpecified = ").Append(OpenAiBaseUrlSpecified)
            .Append(", OpenAiModelSpecified = ").Append(OpenAiModelSpecified)
            .Append(", VoiceSpecified = ").Append(VoiceSpecified)
            .Append(", ApiKey = ").Append(ApiKey is null ? "unchanged" : ApiKey.Length == 0 ? "clear" : "set");
        return true;
    }
}

/// <summary>
/// Reads and writes <see cref="OnlineSpeechConfig"/> against <c>application_settings</c>, beside the
/// capture settings. Nothing here is a credential: the key lives in <see cref="SpeechKeyStore"/>.
/// </summary>
public static class SpeechSettingsStore
{
    /// <summary>Setting holding the service token.</summary>
    public const string ProviderSetting = "speech.provider";

    /// <summary>Setting holding the Azure region.</summary>
    public const string AzureRegionSetting = "speech.azure_region";

    /// <summary>Setting holding the OpenAI-compatible base address.</summary>
    public const string OpenAiBaseUrlSetting = "speech.openai_base_url";

    /// <summary>Setting holding the OpenAI-compatible model.</summary>
    public const string OpenAiModelSetting = "speech.openai_model";

    /// <summary>Setting holding the voice.</summary>
    public const string VoiceSetting = "speech.voice";

    /// <summary>Reads the settings. Anything missing, unreadable or invalid reads as unset.</summary>
    /// <param name="settings">Settings repository.</param>
    /// <param name="transaction">Enclosing settings transaction, or null.</param>
    public static OnlineSpeechConfig Read(SettingsRepository settings, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var provider = SpeechProviderWire.TryParse(ReadString(settings, ProviderSetting, transaction), out var parsed)
            ? parsed
            : SpeechProvider.None;
        var region = ReadString(settings, AzureRegionSetting, transaction);
        var baseUrl = ReadString(settings, OpenAiBaseUrlSetting, transaction);
        var model = ReadString(settings, OpenAiModelSetting, transaction);
        var voice = ReadString(settings, VoiceSetting, transaction);
        return new OnlineSpeechConfig(
            provider,
            SpeechValidation.IsRegion(region) ? region : null,
            SpeechValidation.TryNormalizeBaseUrl(baseUrl, out var url, out _) ? url : null,
            SpeechValidation.IsModel(model) ? model : null,
            SpeechValidation.IsVoice(SpeechProvider.OpenAiCompatible, voice) ? voice : null);
    }

    /// <summary>
    /// Validates an update against the settings in force and returns what they would become. Throws
    /// <c>ERR_BAD_REQUEST</c> naming the field; nothing is written.
    /// </summary>
    /// <param name="current">Settings in force.</param>
    /// <param name="update">Requested changes.</param>
    public static OnlineSpeechConfig Merge(OnlineSpeechConfig current, SpeechSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);
        var next = current with { Provider = update.Provider ?? current.Provider };

        if (update.AzureRegionSpecified)
        {
            string? region = null;
            if (!string.IsNullOrWhiteSpace(update.AzureRegion) &&
                !SpeechValidation.TryNormalizeRegion(update.AzureRegion, out region))
            {
                throw CollectorException.BadRequest(
                    "Azure 区域只能是 2 到 32 个小写字母或数字，例如 eastasia。", "payload.azure_region");
            }

            next = next with { AzureRegion = region };
        }

        if (update.OpenAiBaseUrlSpecified)
        {
            string? url = null;
            if (!string.IsNullOrWhiteSpace(update.OpenAiBaseUrl))
            {
                if (!SpeechValidation.TryNormalizeBaseUrl(update.OpenAiBaseUrl, out var normalized, out _))
                {
                    throw CollectorException.BadRequest(
                        "接口地址必须以 https:// 开头（本机地址可用 http://），不能带用户名、密码、问号参数或 # 片段。",
                        "payload.openai_base_url");
                }

                url = normalized;
            }

            next = next with { OpenAiBaseUrl = url };
        }

        if (update.OpenAiModelSpecified)
        {
            var model = update.OpenAiModel?.Trim();
            if (!string.IsNullOrEmpty(model) && !SpeechValidation.IsModel(model))
            {
                throw CollectorException.BadRequest(
                    "模型名只能包含字母、数字和 . _ : / -，最多 128 个字符。", "payload.openai_model");
            }

            next = next with { OpenAiModel = string.IsNullOrEmpty(model) ? null : model };
        }

        if (update.VoiceSpecified)
        {
            var voice = update.Voice?.Trim();
            if (!string.IsNullOrEmpty(voice) && !SpeechValidation.IsVoice(SpeechProvider.OpenAiCompatible, voice))
            {
                throw CollectorException.BadRequest(
                    "音色名只能包含字母、数字和 . _ : / -，最多 128 个字符。", "payload.voice");
            }

            next = next with { Voice = string.IsNullOrEmpty(voice) ? null : voice };
        }

        if (next.Provider == SpeechProvider.Azure && next.Voice is { } azureVoice &&
            !SpeechValidation.IsVoice(SpeechProvider.Azure, azureVoice) &&
            (update.VoiceSpecified || update.Provider is not null))
        {
            throw CollectorException.BadRequest(
                "Azure 音色名只能包含字母、数字和连字符，长度 3 到 64，例如 zh-CN-XiaoxiaoNeural。", "payload.voice");
        }

        return next;
    }

    /// <summary>Writes every field of <paramref name="config"/> in one transaction.</summary>
    /// <param name="settings">Settings repository.</param>
    /// <param name="config">Settings to store, already validated.</param>
    public static OnlineSpeechConfig Write(SettingsRepository settings, OnlineSpeechConfig config)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(config);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProviderSetting] = Json(SpeechProviderWire.Format(config.Provider)),
            [AzureRegionSetting] = Json(config.AzureRegion),
            [OpenAiBaseUrlSetting] = Json(config.OpenAiBaseUrl),
            [OpenAiModelSetting] = Json(config.OpenAiModel),
            [VoiceSetting] = Json(config.Voice),
        };
        return settings.SetSettings(values, transaction => Read(settings, transaction));
    }

    private static string Json(string? value) => value is null ? "null" : JsonValue.Create(value).ToJsonString();

    private static string? ReadString(SettingsRepository settings, string key, SqliteTransaction? transaction)
    {
        try
        {
            var raw = settings.GetSetting(key, transaction);
            return raw is not null && JsonNode.Parse(raw) is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
