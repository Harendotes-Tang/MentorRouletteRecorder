using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>What building a shared profile came to.</summary>
public enum SharedProfileBuildStatus
{
    /// <summary>A profile the ordinary loader accepts as VERIFIED.</summary>
    Built,

    /// <summary>
    /// The code was made against another template, region or template hash. It is not wrong, it
    /// is not for this version of the software on this machine; not an error, not a rejection.
    /// </summary>
    NotApplicable,

    /// <summary>
    /// A queue-inferred code that is otherwise buildable, waiting for the player to accept the
    /// inference once, exactly as a local calibration of that kind does (plan §4.2, last item).
    /// </summary>
    ConsentRequired,

    /// <summary>The code cannot describe a profile of this template.</summary>
    Invalid,
}

/// <summary>A shared profile document, or why there is none.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="ProfileId">Profile id, <c>&lt;region&gt;.&lt;build&gt;.shared</c>, when built.</param>
/// <param name="Json">Profile text to write, when built.</param>
/// <param name="ProfileSha256">Canonical hash stamped into it, when built.</param>
/// <param name="Profile">The loader's reading of that text, when built.</param>
/// <param name="CodeSha256">Identity of the code, when it decoded.</param>
/// <param name="Reason">Short non-sensitive reason when not built.</param>
public sealed record SharedProfileBuildResult(
    SharedProfileBuildStatus Status,
    string? ProfileId = null,
    string? Json = null,
    string? ProfileSha256 = null,
    ProtocolProfile? Profile = null,
    string? CodeSha256 = null,
    string? Reason = null);

/// <summary>A share code taken from a local profile, or why there is none.</summary>
/// <param name="Code">The code text.</param>
/// <param name="CodeSha256">Its identity.</param>
/// <param name="Payload">What it says.</param>
/// <param name="Reason">Short non-sensitive reason when there is no code.</param>
public sealed record SharedCodeExport(string? Code, string? CodeSha256, ShareCodePayload? Payload, string? Reason);

/// <summary>
/// Share code + the receiver's template -> a VERIFIED profile document, and a locally calibrated
/// profile + its template -> a share code.
///
/// Both directions go through <see cref="CalibratedShape"/>, the transform the draft writes local
/// profiles with, so a rebuilt profile differs from the sharer's only in where it says it came
/// from: <c>profile_id</c>, <c>generated_at</c>, <c>provenance</c> and therefore the hash. The
/// document is refused unless <see cref="ProfileLoader"/> accepts it as VERIFIED. Pure: nothing
/// here reads or writes a file.
/// </summary>
public static class SharedProfileBuilder
{
    /// <summary>Suffix that marks a shared profile id.</summary>
    public const string ProfileIdSuffix = ".shared";

    /// <summary>Profile id for a region and build, e.g. <c>cn.2026.09.01.0000.0000.shared</c>.</summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    public static string ProfileIdFor(Region region, string gameBuild) =>
        LocalProfileWriter.ProfileIdFor(region, gameBuild, ProfileIdSuffix);

    /// <summary>Decodes a code and builds its profile.</summary>
    /// <param name="code">Code text.</param>
    /// <param name="template">This machine's template for the region.</param>
    /// <param name="verifiedAtUtc">When local verification passed; becomes <c>generated_at</c>.</param>
    /// <param name="verifiedCounts">Structural matches per evidence key (<c>messages.NAME.opcode</c>).</param>
    /// <param name="queueInferenceAcceptedAtUtc">When the player accepted queue inference; required for QUEUE_REQUEST.</param>
    public static SharedProfileBuildResult Build(
        string? code,
        CalibrationTemplate template,
        DateTimeOffset verifiedAtUtc,
        IReadOnlyDictionary<string, int> verifiedCounts,
        DateTimeOffset? queueInferenceAcceptedAtUtc = null)
    {
        var decoded = ShareCode.Decode(code);
        return decoded.Rejection is { } rejection
            ? new SharedProfileBuildResult(SharedProfileBuildStatus.Invalid, Reason: rejection.Code + " " + rejection.Detail)
            : Build(decoded.Payload!, template, verifiedAtUtc, verifiedCounts, queueInferenceAcceptedAtUtc);
    }

    /// <summary>Builds the profile a decoded payload describes.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="template">This machine's template for the region.</param>
    /// <param name="verifiedAtUtc">When local verification passed; becomes <c>generated_at</c>.</param>
    /// <param name="verifiedCounts">Structural matches per evidence key (<c>messages.NAME.opcode</c>).</param>
    /// <param name="queueInferenceAcceptedAtUtc">When the player accepted queue inference; required for QUEUE_REQUEST.</param>
    public static SharedProfileBuildResult Build(
        ShareCodePayload payload,
        CalibrationTemplate template,
        DateTimeOffset verifiedAtUtc,
        IReadOnlyDictionary<string, int> verifiedCounts,
        DateTimeOffset? queueInferenceAcceptedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(verifiedCounts);
        if (ShareCode.Check(payload) is { } rejection)
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.Invalid, Reason: rejection.Code + " " + rejection.Detail);
        }

        var codeSha = ShareCode.Sha256(payload);
        if (!IsApplicable(payload, template))
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.NotApplicable, CodeSha256: codeSha,
                Reason: "the code was made against another template or region");
        }

        if (ToValues(payload, template) is not { } values)
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.Invalid, CodeSha256: codeSha,
                Reason: "the selector values do not match the template's selectors");
        }

        var shape = CalibratedShape.Messages(template, values);
        if (shape.Error is { } error)
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.Invalid, CodeSha256: codeSha, Reason: error);
        }

        if (payload.MatchSource == CalibrationMatchSource.QueueRequest && queueInferenceAcceptedAtUtc is null)
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.ConsentRequired, CodeSha256: codeSha,
                Reason: "queue inference needs the player's consent");
        }

        var profileId = ProfileIdFor(template.Region, payload.GameBuild);
        var document = Document(payload, template, shape.Messages, codeSha, profileId, verifiedAtUtc, verifiedCounts,
            queueInferenceAcceptedAtUtc);
        var json = CalibratedProfileDocument.Stamp(document);
        var path = Path.Combine(ProfileCatalog.SharedDirectoryName, LocalProfileWriter.RegionDirectory(template.Region),
            profileId + ".json");
        var report = CalibratedProfileDocument.Validate(path, json);
        if (report.Profile is not { Status: ProfileCompatibilityStatus.Verified } profile)
        {
            return new SharedProfileBuildResult(SharedProfileBuildStatus.Invalid, CodeSha256: codeSha,
                Reason: string.Join("; ", report.Errors.Select(issue => issue.Code + " " + issue.Message)));
        }

        return new SharedProfileBuildResult(SharedProfileBuildStatus.Built, profileId, json,
            document["profile_sha256"]!.GetValue<string>(), profile, codeSha);
    }

    /// <summary>
    /// The share code of a profile this machine calibrated. Only a VERIFIED <c>.local</c> profile
    /// that is exactly a calibrated shape of <paramref name="template"/> has one: a shipped or
    /// shared profile is not passed on, and a hand-edited one does not survive the rebuild check.
    /// </summary>
    /// <param name="profile">Loaded local profile.</param>
    /// <param name="template">Template it was calibrated under.</param>
    public static SharedCodeExport ToShareCode(ProtocolProfile profile, CalibrationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(template);
        if (!profile.ProfileId.EndsWith(LocalProfileWriter.ProfileIdSuffix, StringComparison.Ordinal) ||
            profile.Status != ProfileCompatibilityStatus.Verified || profile.Calibration is not null)
        {
            return Refused("only a verified profile calibrated on this machine can be shared");
        }

        if (profile.Region != template.Region || profile.MentorRouletteId != template.MentorRouletteId)
        {
            return Refused("the profile does not belong to this template");
        }

        if (CalibratedShape.Read(template, profile.Messages) is not { } values)
        {
            return Refused("the profile's messages are not a calibrated shape of this template");
        }

        if ((int)profile.MatchWindow.TotalSeconds != CalibratedShape.MatchWindowSeconds(template, values.MatchSource))
        {
            return Refused("the profile's match window is not the one calibration writes");
        }

        var payload = PayloadOf(profile, template, values);
        return ShareCode.Check(payload) is { } rejection
            ? Refused(rejection.Code + " " + rejection.Detail)
            : new SharedCodeExport(ShareCode.Encode(payload), ShareCode.Sha256(payload), payload, null);
    }

    /// <summary>
    /// The payload a calibrated profile of this template - local or shared - was built from, or null
    /// when it is not exactly such a shape. Unlike <see cref="ToShareCode"/> this does not refuse a
    /// shared profile: it only recovers the code identity of a profile already in use, so that after a
    /// restart the profile can keep being verified and its contradictions recorded against that code.
    /// </summary>
    /// <param name="profile">Loaded calibrated profile.</param>
    /// <param name="template">Template in force.</param>
    internal static ShareCodePayload? RecoverPayload(ProtocolProfile profile, CalibrationTemplate template)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(template);
        if (profile.Region != template.Region || profile.MentorRouletteId != template.MentorRouletteId ||
            CalibratedShape.Read(template, profile.Messages) is not { } values ||
            (int)profile.MatchWindow.TotalSeconds != CalibratedShape.MatchWindowSeconds(template, values.MatchSource))
        {
            return null;
        }

        var payload = PayloadOf(profile, template, values);
        return ShareCode.Check(payload) is null ? payload : null;
    }

    private static ShareCodePayload PayloadOf(ProtocolProfile profile, CalibrationTemplate template, CalibratedValues values)
    {
        var pop = values.Pop;
        return new ShareCodePayload(
            template.Region,
            profile.GameBuild,
            template.Source.ProfileId,
            template.Source.ProfileSha256,
            values.MatchSource,
            new ShareCodePop(pop.Opcode, pop.Length, pop.RouletteOffset, pop.Selectors?.Select(reading => reading.Value).ToArray()),
            values.ZoneOpcode,
            values.TerritoryOpcode,
            values.JobOpcode);
    }

    private static SharedCodeExport Refused(string reason) => new(null, null, null, reason);

    /// <summary>True when a code was made against exactly this template: same region, profile id and hash.</summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="template">This machine's template.</param>
    internal static bool IsApplicable(ShareCodePayload payload, CalibrationTemplate template) =>
        payload.Region == template.Region &&
        string.Equals(payload.TemplateProfileId, template.Source.ProfileId, StringComparison.Ordinal) &&
        string.Equals(payload.TemplateSha256, template.Source.ProfileSha256, StringComparison.Ordinal);

    /// <summary>
    /// The learned values a payload carries, with selector values named by the template's
    /// learnable selectors; null when their count does not match the template.
    /// </summary>
    /// <param name="payload">Decoded payload.</param>
    /// <param name="template">This machine's template.</param>
    internal static CalibratedValues? ToValues(ShareCodePayload payload, CalibrationTemplate template)
    {
        IReadOnlyList<CalibrationSelectorReading>? readings = null;
        if (payload.Pop.SelectorValues is { } selectorValues)
        {
            var learnable = CalibratedShape.LearnableSelectors(template);
            if (learnable.Count != selectorValues.Count)
            {
                return null;
            }

            readings = learnable.Select((field, index) => new CalibrationSelectorReading(field.Name, selectorValues[index]))
                .ToArray();
        }

        return new CalibratedValues(
            new CalibratedPop(payload.MatchSource, payload.Pop.Opcode, payload.Pop.Length, payload.Pop.RouletteOffset, readings),
            payload.ZoneOpcode,
            payload.TerritoryOpcode,
            payload.JobOpcode);
    }

    private static JsonObject Document(
        ShareCodePayload payload,
        CalibrationTemplate template,
        IReadOnlyList<ProfileMessage> messages,
        string codeSha,
        string profileId,
        DateTimeOffset verifiedAtUtc,
        IReadOnlyDictionary<string, int> verifiedCounts,
        DateTimeOffset? queueInferenceAcceptedAtUtc)
    {
        var recorded = CalibratedProfileDocument.Timestamp(verifiedAtUtc);
        var sha12 = codeSha[..12];
        var evidence = new JsonArray();
        foreach (var message in messages)
        {
            var key = "messages." + message.Name + ".opcode";
            var samples = verifiedCounts.GetValueOrDefault(key);
            evidence.Add(CalibratedProfileDocument.Evidence(key, "OBSERVED_LOCAL_TRAFFIC", recorded, samples,
                $"共享校准码 {sha12} 声明了 {message.Name} 的 opcode（长度 {message.ExpectedLength}、字段偏移继承自随包模板 " +
                $"{template.Source.ProfileId}），在本机客户端 {payload.GameBuild} 的被动流量中按结构核实 {samples} 次；" +
                "时间线由分享者核对，未在本机重复。"));
        }

        if (payload.MatchSource == CalibrationMatchSource.QueueRequest && queueInferenceAcceptedAtUtc is { } accepted)
        {
            evidence.Add(CalibratedProfileDocument.Evidence("messages.CONTENT_FINDER_POP.opcode", "USER_CONFIRMED",
                CalibratedProfileDocument.Timestamp(accepted), 1,
                "用户接受了「按排本申请推断匹配」的记录方式：本档案不声称观察到了服务器的匹配报文。"));
        }

        var window = CalibratedShape.MatchWindowSeconds(template, payload.MatchSource);
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["profile_id"] = profileId,
            ["region"] = EnumWire<Region>.Format(template.Region),
            ["game_build"] = payload.GameBuild,
            ["generated_at"] = recorded,
            ["mentor_roulette_id"] = template.MentorRouletteId,
            ["compatibility_status"] = "VERIFIED",
            ["match_window_seconds"] = window,
            ["messages"] = CalibratedProfileDocument.Messages(messages),
            ["fixtures"] = new JsonArray(),
            ["provenance"] = new JsonObject
            {
                ["summary"] =
                    $"共享校准生成的档案。报文结构（长度、字段偏移、数据字段约束、指导者轮盘编号 {template.MentorRouletteId}、" +
                    $"匹配窗口 {window} 秒）继承自随包档案 {template.Source.ProfileId}；" +
                    $"opcode 来自其他玩家分享的共享校准码 {sha12}，{MatchNote(payload.MatchSource)}；" +
                    $"本机在客户端 {payload.GameBuild} 的被动流量中按结构逐条核实后启用，详见各条证据。" +
                    "时间线由分享者在其本机核对，未在本机重复。本档案不含 calibration 段，不能作为下一版本的模板，也不会再次分享；" +
                    "同版本的随包档案出现时自动让位。",
                ["evidence"] = evidence,
            },
            ["profile_sha256"] = new string('0', 64),
        };
    }

    private static string MatchNote(CalibrationMatchSource source) => source switch
    {
        CalibrationMatchSource.Announcement =>
            "「匹配成功」在这一版是与排本回执不同的一条报文，其 opcode 与长度由分享者观察得出",
        CalibrationMatchSource.MarkerOffset =>
            "「匹配成功」在这一版是与排本回执不同的一条报文，其 opcode、长度与轮盘编号所在字节位置由分享者观察得出",
        CalibrationMatchSource.QueueRequest =>
            "分享者没能认出服务器发出的「匹配成功」报文，因此本档案改用玩家自己发出的排本请求作为起点，" +
            "再由随后进入的已知副本确认；这是推断而不是观察，档案里的 CONTENT_FINDER_POP 方向为 CLIENT_TO_SERVER 即为标记",
        _ => "匹配状态的选择器取值由分享者观察得出",
    };
}
