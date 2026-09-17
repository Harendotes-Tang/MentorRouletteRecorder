using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The capture settings the Collector owns, exactly as <c>$defs/CaptureSettings</c> declares
/// them.
///
/// Every field here has a consumer: a switch the user can change with nothing on the other end
/// is a worse failure than a missing feature (spec gaps P1-3, P1-5).
/// </summary>
/// <param name="FollowGame">Start capture as soon as the game process appears.</param>
/// <param name="Autostart">Legacy name of <paramref name="FollowGame"/>, still honoured.</param>
/// <param name="AdapterId">Remembered adapter; null means automatic selection.</param>
/// <param name="LogRetentionDays">Days of diagnostic log history kept.</param>
/// <param name="AllowWithoutProfile">Allow counters-only capture with no verified profile.</param>
/// <param name="RegionOverride">Forced service region, or null for path-based detection.</param>
/// <param name="SharedCalibrationEnabled">
/// Whether a build with no profile fetches other players' shared calibrations. On by default.
/// Turning it off cancels a download in flight; it never removes a shared profile that already
/// passed local verification.
/// </param>
public sealed record CaptureSettingsSnapshot(
    bool FollowGame,
    bool Autostart,
    string? AdapterId,
    int LogRetentionDays,
    bool AllowWithoutProfile,
    Region? RegionOverride,
    bool CandidateValidationEnabled = false,
    IReadOnlyList<string>? ResearchPayloadOpcodes = null,
    bool AutoCalibrationEnabled = true,
    bool SharedCalibrationEnabled = true);

/// <summary>
/// Reads and writes <see cref="CaptureSettingsSnapshot"/> against <c>application_settings</c>.
///
/// The one place where the capture keys are read as a consistent set and rendered onto the
/// wire, so the Desktop settings page and the Collector cannot drift apart over what a setting
/// is called or what its bounds are.
/// </summary>
public static class CaptureSettingsStore
{
    public const string CandidateValidationSetting = "capture.candidate_validation_enabled";
    public const string ResearchPayloadOpcodesSetting = "capture.research_payload_opcodes";
    /// <summary>Setting holding the diagnostic log retention, in days.</summary>
    public const string LogRetentionDaysSetting = "diagnostics.log_retention_days";

    /// <summary>Setting holding an explicit service region, used when the path cannot say.</summary>
    public const string RegionOverrideSetting = "capture.region_override";

    /// <summary>
    /// Whether a build with no profile is calibrated automatically from the shipped template.
    /// On by default: the alternative is silence on every patch day.
    /// </summary>
    public const string AutoCalibrationSetting = "capture.auto_calibration_enabled";

    /// <summary>
    /// Whether a build with no profile fetches other players' shared calibrations
    /// (docs/privacy-boundary.md §8.2). On by default.
    /// </summary>
    public const string SharedCalibrationSetting = "capture.shared_calibration_enabled";

    /// <summary>Reads the current settings. Missing or unreadable values fall back to defaults.</summary>
    /// <param name="settings">Settings repository, or null when there is no database.</param>
    /// <param name="transaction">Enclosing settings transaction, or null for ordinary reads.</param>
    public static CaptureSettingsSnapshot Read(SettingsRepository? settings, SqliteTransaction? transaction = null)
    {
        var followGame = ReadBool(settings, CaptureController.FollowGameSetting, transaction);
        var autostart = ReadBool(settings, CaptureController.AutostartSetting, transaction);

        return new CaptureSettingsSnapshot(
            // The legacy key was seeded "false" for everyone, so only an explicit true there
            // carries any intent; an explicit follow_game always wins.
            FollowGame: followGame ?? (autostart is true || CaptureController.FollowGameDefault),
            Autostart: autostart ?? false,
            AdapterId: ReadString(settings, CaptureController.AdapterSetting, transaction),
            LogRetentionDays: Clamp(ReadInt(settings, LogRetentionDaysSetting, transaction)),
            AllowWithoutProfile: ReadBool(settings, CaptureController.AllowWithoutProfileSetting, transaction) ?? false,
            RegionOverride: ParseRegionOverride(ReadString(settings, RegionOverrideSetting, transaction)),
            CandidateValidationEnabled: ReadBool(settings, CandidateValidationSetting, transaction) ?? false,
            ResearchPayloadOpcodes: ReadResearchOpcodes(settings, transaction),
            AutoCalibrationEnabled: ReadBool(settings, AutoCalibrationSetting, transaction) ?? true,
            SharedCalibrationEnabled: ReadBool(settings, SharedCalibrationSetting, transaction) ?? true);
    }

    /// <summary>
    /// Applies the fields a request actually carried and returns the complete settings
    /// afterwards. A field the caller did not name is left exactly as it was: "unset means
    /// default" would silently undo the other half of a two-page settings screen. All writes
    /// and the returned snapshot share one transaction, so a later field failure cannot persist
    /// a candidate-mode change on its own.
    /// </summary>
    /// <param name="settings">Settings repository.</param>
    /// <param name="update">Fields to write; null members are "not specified".</param>
    public static CaptureSettingsSnapshot Apply(
        SettingsRepository settings, CaptureSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(update);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        if (update.ResearchPayloadOpcodes is { } requested)
        {
            var normalized = ResearchPayloadPolicy.Normalize(requested);
            if (normalized.Length > 0)
            {
                if (!(update.CandidateValidationEnabled ?? Read(settings).CandidateValidationEnabled))
                    throw CollectorException.BadRequest("请先开启候选档案验证，再设置研究白名单。", "payload.research_payload_opcodes");
                normalized = ResearchPayloadPolicy.ValidateRequested(normalized,
                    ProfileCatalog.LoadDefault(allowCandidate: true).Entries
                        .Where(entry => entry.Profile is not null).Select(entry => entry.Profile!));
            }
            values[ResearchPayloadOpcodesSetting] = JsonSerializer.Serialize(normalized);
        }

        if (update.CandidateValidationEnabled is { } candidate)
            values[CandidateValidationSetting] = candidate ? "true" : "false";

        if (update.AutoCalibrationEnabled is { } autoCalibration)
        {
            values[AutoCalibrationSetting] = autoCalibration ? "true" : "false";
        }

        if (update.SharedCalibrationEnabled is { } sharedCalibration)
        {
            values[SharedCalibrationSetting] = sharedCalibration ? "true" : "false";
        }

        if (update.FollowGame is { } followGame)
        {
            values[CaptureController.FollowGameSetting] = followGame ? "true" : "false";
        }

        if (update.Autostart is { } autostart)
        {
            values[CaptureController.AutostartSetting] = autostart ? "true" : "false";
        }

        if (update.AdapterSpecified)
        {
            values[CaptureController.AdapterSetting] =
                update.AdapterId is null
                    ? "null"
                    : JsonValue.Create(update.AdapterId)!.ToJsonString();
        }

        if (update.LogRetentionDays is { } days)
        {
            values[LogRetentionDaysSetting] =
                Clamp(days).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (update.AllowWithoutProfile is { } allow)
        {
            values[CaptureController.AllowWithoutProfileSetting] = allow ? "true" : "false";
        }

        if (update.RegionOverrideSpecified)
        {
            values[RegionOverrideSetting] =
                update.RegionOverride is { } region
                    ? JsonValue.Create(EnumWire<Region>.Format(region))!.ToJsonString()
                    : "null";
        }

        return settings.SetSettings(values, transaction => Read(settings, transaction));
    }

    /// <summary>Builds a <c>$defs/CaptureSettings</c> object.</summary>
    /// <param name="snapshot">Settings to render.</param>
    public static JsonObject Wire(CaptureSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new JsonObject
        {
            ["follow_game"] = snapshot.FollowGame,
            ["candidate_validation_enabled"] = snapshot.CandidateValidationEnabled,
            ["auto_calibration_enabled"] = snapshot.AutoCalibrationEnabled,
            ["shared_calibration_enabled"] = snapshot.SharedCalibrationEnabled,
            ["research_payload_opcodes"] = new JsonArray((snapshot.ResearchPayloadOpcodes ?? Array.Empty<string>())
                .Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
            ["autostart"] = snapshot.Autostart,
            ["adapter_id"] = snapshot.AdapterId,
            ["log_retention_days"] = snapshot.LogRetentionDays,
            ["allow_without_profile"] = snapshot.AllowWithoutProfile,
            ["region_override"] = snapshot.RegionOverride is { } region
                ? EnumWire<Region>.Format(region)
                : null,
        };
    }

    /// <summary>Clamps a retention value to the range the contract declares.</summary>
    /// <param name="days">Requested days, or null for the default.</param>
    public static int Clamp(int? days) => days is null
        ? RotatingFileLogger.DefaultRetentionDays
        : Math.Clamp(
            days.Value, RotatingFileLogger.MinRetentionDays, RotatingFileLogger.MaxRetentionDays);

    /// <summary>Reads the region override alone, for the game locator.</summary>
    /// <param name="settings">Settings repository, or null.</param>
    public static Region? ReadRegionOverride(SettingsRepository? settings) =>
        ParseRegionOverride(ReadString(settings, RegionOverrideSetting));

    private static Region? ParseRegionOverride(string? text) => text switch
    {
        "CN" => Region.Cn,
        "GLOBAL" => Region.Global,
        _ => null,
    };

    private static JsonNode? ReadSetting(SettingsRepository? settings, string key, SqliteTransaction? transaction = null)
    {
        if (settings is null)
        {
            return null;
        }

        try
        {
            var raw = settings.GetSetting(key, transaction);
            return raw is null ? null : JsonNode.Parse(raw);
        }
        catch (Exception ex) when (ex is JsonException or CollectorException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadResearchOpcodes(SettingsRepository? settings, SqliteTransaction? transaction)
    {
        if (ReadSetting(settings, ResearchPayloadOpcodesSetting, transaction) is not JsonArray array || array.Count == 0)
            return Array.Empty<string>();
        try
        {
            var values = array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var text)
                ? text : string.Empty).ToArray();
            return ResearchPayloadPolicy.Normalize(values);
        }
        catch (CollectorException) { return Array.Empty<string>(); }
    }

    private static string? ReadString(SettingsRepository? settings, string key, SqliteTransaction? transaction = null) =>
        ReadSetting(settings, key, transaction) is JsonValue value &&
        value.TryGetValue<string>(out var text) &&
        !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static bool? ReadBool(SettingsRepository? settings, string key, SqliteTransaction? transaction = null) =>
        ReadSetting(settings, key, transaction) is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : null;

    private static int? ReadInt(SettingsRepository? settings, string key, SqliteTransaction? transaction = null) =>
        ReadSetting(settings, key, transaction) is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;
}

/// <summary>
/// One <c>UpdateCaptureSettings</c> request. Every member is optional; the two nullable
/// fields carry a separate "was it named at all" flag, because for them null is a value the
/// user can legitimately ask for (clear the adapter, clear the region override) and not the
/// same thing as leaving them alone.
/// </summary>
public sealed record CaptureSettingsUpdate
{
    /// <summary>显式提供的研究白名单；null 表示未提供，空数组立即停止保存后续负载。</summary>
    public IReadOnlyList<string>? ResearchPayloadOpcodes { get; init; }
    /// <summary>显式允许候选元数据观测；不改变正式解析的信任级别。</summary>
    public bool? CandidateValidationEnabled { get; init; }

    /// <summary>Whether builds without a profile are calibrated automatically; null leaves it alone.</summary>
    public bool? AutoCalibrationEnabled { get; init; }

    /// <summary>Whether shared calibrations are fetched for builds with no profile; null leaves it alone.</summary>
    public bool? SharedCalibrationEnabled { get; init; }
    /// <summary>Start capture as soon as the game appears.</summary>
    public bool? FollowGame { get; init; }

    /// <summary>Legacy alias of <see cref="FollowGame"/>.</summary>
    public bool? Autostart { get; init; }

    /// <summary>Adapter to remember; null clears it.</summary>
    public string? AdapterId { get; init; }

    /// <summary>True when the request named <c>adapter_id</c> at all.</summary>
    public bool AdapterSpecified { get; init; }

    /// <summary>Days of diagnostic log history to keep.</summary>
    public int? LogRetentionDays { get; init; }

    /// <summary>Allow counters-only capture with no verified profile.</summary>
    public bool? AllowWithoutProfile { get; init; }

    /// <summary>Forced service region; null returns to path-based detection.</summary>
    public Region? RegionOverride { get; init; }

    /// <summary>True when the request named <c>region_override</c> at all.</summary>
    public bool RegionOverrideSpecified { get; init; }
}
