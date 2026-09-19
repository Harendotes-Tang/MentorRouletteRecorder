using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Events;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>What a write produced.</summary>
/// <param name="Path">Full path of the profile file.</param>
/// <param name="ProfileId">Its profile id (the file stem).</param>
/// <param name="Sha256">The canonical hash stamped into it.</param>
public sealed record LocalProfileWriteResult(string Path, string ProfileId, string Sha256);

/// <summary>
/// Turns a confirmed <see cref="CalibrationDraft"/> into a VERIFIED profile file under the data
/// directory's <c>protocol-profiles</c> tree.
///
/// Structure comes from the template, opcodes from the draft, evidence from the observation
/// counts and the user's confirmation. The document is validated by <see cref="ProfileLoader"/>
/// before a byte reaches disk, so a file that exists is a file the catalogue will accept. The
/// generated profile carries no <c>calibration</c>, <c>hypotheses</c> or <c>fixtures</c>: it can
/// never become the template for the next build (review F-8).
/// </summary>
public static class LocalProfileWriter
{
    /// <summary>Suffix that marks a locally calibrated profile id.</summary>
    public const string ProfileIdSuffix = ".local";

    /// <summary>Profile id for a region and build, e.g. <c>cn.2026.09.01.0000.0000.local</c>.</summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build as read from disk.</param>
    public static string ProfileIdFor(Region region, string gameBuild) => ProfileIdFor(region, gameBuild, ProfileIdSuffix);

    /// <summary>
    /// Profile id for a region, a build and an origin suffix: lowercase, file-safe, and within the
    /// schema's 64 characters with the suffix always kept.
    /// </summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build as read from disk.</param>
    /// <param name="suffix">Origin suffix such as <c>.local</c> or <c>.shared</c>.</param>
    internal static string ProfileIdFor(Region region, string gameBuild, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        var builder = new StringBuilder(RegionDirectory(region)).Append('.');
        foreach (var c in gameBuild.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-');
        }

        var id = builder.Append(suffix).ToString();
        return id.Length <= 64 ? id : id[..(64 - suffix.Length)] + suffix;
    }

    /// <summary>Directory under the local root that a region's profiles live in.</summary>
    /// <param name="region">CN or GLOBAL.</param>
    public static string RegionDirectory(Region region) => region switch
    {
        Region.Cn => "cn",
        Region.Global => "global",
        _ => throw new ArgumentException("a local profile needs a CN or GLOBAL region", nameof(region)),
    };

    /// <summary>
    /// Builds, validates and atomically writes the profile. Throws when the draft is not ready or
    /// the document would be refused; nothing is written in that case.
    /// </summary>
    /// <param name="draft">A draft in the Ready state.</param>
    /// <param name="template">Template the draft was derived from.</param>
    /// <param name="gameBuild">Client build the profile is for.</param>
    /// <param name="confirmedAtUtc">When the user confirmed the timeline.</param>
    /// <param name="localRoot">The <c>protocol-profiles</c> directory under the data root.</param>
    public static LocalProfileWriteResult Write(
        CalibrationDraft draft,
        CalibrationTemplate template,
        string gameBuild,
        DateTimeOffset confirmedAtUtc,
        string localRoot)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameBuild);
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        if (draft.Status != CalibrationDraftStatus.Ready || draft.Messages.Count == 0)
        {
            throw new InvalidOperationException("only a ready calibration draft can be written as a profile");
        }

        var region = template.Region;
        var profileId = ProfileIdFor(region, gameBuild);
        var directory = Path.Combine(Path.GetFullPath(localRoot), RegionDirectory(region));
        var path = Path.Combine(directory, profileId + ".json");
        var document = Build(draft, template, gameBuild, confirmedAtUtc, profileId);

        var json = CalibratedProfileDocument.Stamp(document);
        var report = CalibratedProfileDocument.Validate(path, json);
        if (report.Profile is null || report.Profile.Status != ProfileCompatibilityStatus.Verified)
        {
            var reasons = string.Join("; ", report.Errors.Select(issue => issue.Code + " " + issue.Message));
            throw new InvalidOperationException("the calibrated profile was refused before writing: " + reasons);
        }

        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, profileId + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new LocalProfileWriteResult(path, profileId, document["profile_sha256"]!.GetValue<string>());
    }

    private static JsonObject Build(
        CalibrationDraft draft, CalibrationTemplate template, string gameBuild, DateTimeOffset confirmedAtUtc, string profileId)
    {
        var recorded = CalibratedProfileDocument.Timestamp(confirmedAtUtc);
        var evidence = new JsonArray();
        foreach (var message in draft.Messages)
        {
            var key = "messages." + message.Name + ".opcode";
            var samples = draft.SampleCounts.GetValueOrDefault(key);
            evidence.Add(CalibratedProfileDocument.Evidence(key, "OBSERVED_LOCAL_TRAFFIC", recorded, samples,
                Observed(message, draft, gameBuild, samples) + Learned(message) + Rationale(message)));
        }

        if (draft.TimedAnnouncement is { } announced)
        {
            evidence.Add(CalibratedProfileDocument.Evidence(
                "messages." + CalibratedShape.AnnouncedName + ".opcode", "USER_CONFIRMED", recorded,
                announced.Samples.Count,
                "用户逐条确认了时间线上「匹配弹窗（按出现时机认出）」各行与自己看到的弹窗时刻一致。"));
        }

        if (draft.MatchSource == CalibrationMatchSource.QueueRequest)
        {
            evidence.Add(CalibratedProfileDocument.Evidence("messages.CONTENT_FINDER_POP.opcode", "USER_CONFIRMED", recorded,
                draft.Events.Count(item => item.RequiresConfirmation),
                "用户接受了「按排本申请推断匹配」的记录方式：本档案不声称观察到了服务器的匹配报文。"));
        }

        foreach (var name in new[] { "CONTENT_FINDER_POP", "ZONE_INITIALIZATION" })
        {
            if (draft.Messages.Any(message => message.Name == name))
            {
                evidence.Add(CalibratedProfileDocument.Evidence("messages." + name + ".opcode", "USER_CONFIRMED", recorded,
                    draft.Events.Count(item => item.RequiresConfirmation),
                    "用户在软件里逐条核对了校准时间线（排本、匹配弹窗、进入副本、离开副本）并全部确认无误。"));
            }
        }

        var messages = CalibratedProfileDocument.Messages(draft.Messages);

        var roulettes = string.Join("、", draft.ConfirmedRouletteIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        // A profile that reads the server's announcement measures the accept timer and the
        // loading screen, which is a couple of minutes at most. One that stands the queue
        // request in for it measures the queue, and a mentor roulette queued as a damage dealer
        // can stand for tens of minutes, so the window becomes the format's ceiling.
        var window = CalibratedShape.MatchWindowSeconds(template, draft.MatchSource);
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["profile_id"] = profileId,
            ["region"] = EnumWire<Region>.Format(template.Region),
            ["game_build"] = gameBuild,
            ["generated_at"] = recorded,
            ["mentor_roulette_id"] = template.MentorRouletteId,
            ["compatibility_status"] = "VERIFIED",
            ["match_window_seconds"] = window,
            ["messages"] = messages,
            ["fixtures"] = new JsonArray(),
            ["provenance"] = new JsonObject
            {
                ["summary"] =
                    $"本机校准生成的档案。报文结构（长度、字段偏移、数据字段约束、指导者轮盘编号 {template.MentorRouletteId}、" +
                    $"匹配窗口 {window} 秒）继承自随包档案 {draft.TemplateProfileId}；" +
                    $"opcode 由本机在客户端 {gameBuild} 的被动流量中按结构与时序重新识别" +
                    (draft.FinderRequestOpcode is { } request ? $"（排本请求 C2S 0x{request:x4} 与其回执配对）" : string.Empty) +
                    $"，涉及轮盘编号 {roulettes}；" +
                    MatchNote(draft) +
                    "，详见各条证据。用户核对时间线后启用。本档案不含 calibration 段，不能作为下一版本的模板；" +
                    "同版本的随包档案出现时自动让位。",
                ["evidence"] = evidence,
            },
            ["profile_sha256"] = new string('0', 64),
        };
    }

    /// <summary>
    /// What this machine saw of one message. The announcement is described differently because
    /// nothing about it is inherited: it declares no field, borrows no offset from the template,
    /// and was recognised by nothing but when it arrived.
    /// </summary>
    /// <param name="message">Message as the draft resolved it.</param>
    /// <param name="draft">Draft being written.</param>
    /// <param name="gameBuild">Client build the profile is for.</param>
    /// <param name="samples">Observation count behind it.</param>
    private static string Observed(ProfileMessage message, CalibrationDraft draft, string gameBuild, int samples) =>
        message.Name == CalibratedShape.AnnouncedName
            ? $"本机校准：在客户端 {gameBuild} 的被动流量里，按出现时机认出这条服务器报文" +
              $"（方向 SERVER_TO_CLIENT、长度 {message.ExpectedLength}，不声明任何字段）；" +
              $"有 {samples} 次进本由它先行。"
            : $"本机校准：在客户端 {gameBuild} 的被动流量里，按模板 {draft.TemplateProfileId} 的 {message.Name} 结构" +
              $"（长度 {message.ExpectedLength}、字段偏移继承自模板）找到唯一满足条件的 opcode，样本 {samples} 次。";

    /// <summary>How this profile knows a match happened, in one clause of the summary sentence.</summary>
    /// <param name="draft">Draft being written.</param>
    private static string MatchNote(CalibrationDraft draft) => draft.MatchSource switch
    {
        CalibrationMatchSource.Announcement =>
            "「匹配成功」在这一版是与排本回执不同的一条报文，其 opcode 与长度均来自本机观察",
        CalibrationMatchSource.MarkerOffset =>
            "「匹配成功」在这一版是与排本回执不同的一条报文，其 opcode、长度与轮盘编号所在字节位置均来自本机观察",
        CalibrationMatchSource.QueueRequest =>
            "这一版没能认出服务器发出的「匹配成功」报文，因此本档案改用玩家自己发出的排本请求作为起点，" +
            "再由随后进入的已知副本确认；这是推断而不是观察，档案里的 CONTENT_FINDER_POP 方向为 CLIENT_TO_SERVER 即为标记" +
            (draft.TimedAnnouncement is null
                ? string.Empty
                : "。另外本机按出现时机认出了一条只在排本期间、且每次进本之前都会出现的服务器报文，" +
                  "记为 MATCH_ANNOUNCED：它只提供「匹配成功」的时刻，所排的轮盘仍然由排本请求确定"),
        _ => "匹配状态的选择器取值来自本机观察",
    };

    /// <summary>Names the constraints that came from this machine rather than from the template.</summary>
    /// <param name="message">Message as the draft resolved it.</param>
    private static string Learned(ProfileMessage message)
    {
        var states = message.Fields
            .Where(field => field.Role == ProfileFieldRole.Selector && field.Constraints.In is { Count: > 0 })
            .Select(field => field.Name + " = " +
                string.Join("、", field.Constraints.In!.Select(value => value.ToString(CultureInfo.InvariantCulture))))
            .ToArray();
        return states.Length == 0
            ? string.Empty
            : "状态取值（" + string.Join("；", states) + "）来自本机观察，不是继承：" +
              "只有在同轮盘的已配对请求之后、回执窗口与实际加载之外到达，并在模板匹配窗口内由唯一已知副本进本佐证的取值才被当作匹配成功，" +
              "并且它从未作为申请回执出现过。";
    }

    /// <summary>
    /// Why this message was accepted. The pop has two rationales because it has two shapes on
    /// the wire: a build may send the match as another state of the queue reply, or as a message
    /// of its own. Which one this profile records is visible in whether it kept a selector.
    /// </summary>
    /// <param name="message">Message as the draft resolved it.</param>
    private static string Rationale(ProfileMessage message) => message.Name switch
    {
        "CONTENT_FINDER_POP" when message.Direction == PacketDirection.ClientToServer =>
            "判据：这一版没有认出服务器发出的「匹配成功」报文。本条记录的是客户端发出的排本请求本身" +
            "（长度与轮盘编号偏移来自模板的 calibration 段，opcode 由请求/回执配对确定）；" +
            "「这次排本确实匹配上了」由随后一小时内进入的、副本表能认出的副本来确认，" +
            "期间若有其他副本进入则不采信。玩家申请后取消、随后自行进入副本时可能误记一次，" +
            "这是本路径已知且唯一的代价。",
        "CONTENT_FINDER_POP" when !message.Fields.Any(field => field.Role == ProfileFieldRole.Selector) =>
            "判据：这一版把「匹配成功」发在与排本回执不同的报文上，因此按独立报文识别。候选须在同一连接上晚于同轮盘的已配对排本请求、" +
            "在回执窗口与实际加载之外到达，并且在模板匹配窗口内先于每一次已知副本进本；同时该报文种类从不出现在任何换区簇内部" +
            "（属于加载的报文会随每次换区一起出现，宣布匹配的报文不会）。若仍并列，再要求它出现过而其后没有任何进本；" +
            "仍然并列则不启用。",
        "CONTENT_FINDER_POP" => "判据：同一连接上客户端排本请求后在模板回执窗口内回传同一轮盘编号；候选来自同一 opcode 配对，晚于同轮盘请求，在回执窗口与实际加载之外，并由模板匹配窗口内唯一已知副本进本佐证；状态取值从未出现在回执里，歧义不启用。",
        "MATCH_ANNOUNCED" =>
            "判据：这一版的匹配通知在任何字节位置都不带轮盘编号，按数值找不到，因此只按出现时机认定。" +
            "候选须满足：只在本连接有未结束的排本申请时出现（或其后一小段时间内确实进了已知副本），" +
            "从不在无人排本、也无副本加载时出现；不属于任何换区簇；" +
            "在每一次「自己申请后进入已知副本」之前的模板匹配窗口内都至少出现一次，且晚于对应申请；" +
            "这样的进本至少两次、涉及至少两个不同轮盘；每次排本窗口内出现不超过 8 次。" +
            "若仍有多个候选，取各次进本最小提前量最大的那个；并列则不启用。" +
            "本条只提供匹配时刻，不提供轮盘编号。",
        "ZONE_INITIALIZATION" => "判据：长度等于模板长度，在至少三个换区簇里各恰好出现一次，且所选同一链的进本、出本簇各恰好一次，簇外从不出现。",
        "ZONE_TERRITORY" => "判据：进本簇里恰好出现一次，且区域编号命中副本表。",
        "PLAYER_JOB" => "判据：进本、出本两个换区簇都出现且取值稳定、满足职业编号约束，全部换区簇（含进本、出本）至少半数出现，没有任何换区簇与之矛盾。",
        _ => string.Empty,
    };
}
