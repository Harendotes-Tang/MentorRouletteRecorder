using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>白名单公开摘要或准确的不可用原因；读取本身没有上传、剪贴板或浏览器副作用。</summary>
public sealed record CalibrationEvidenceResult(bool Available, string? EvidenceJson, string Reason)
{
    /// <summary>证据不存在或尚未满足健康、完整观察条件。</summary>
    public static CalibrationEvidenceResult Unavailable(string reason) => new(false, null, reason);
}

internal sealed record CalibrationEvidenceProducer(string AppVersion, string RulesVersion);
internal sealed record CalibrationEvidenceTarget(string Region, string GameBuild, string TemplateProfileId,
    string TemplateSha256, string CodeSha256, int QuarantineEpoch);
internal sealed record CalibrationEvidenceHealth(string SilentReason, int? PreexistingConnections, long AdapterDropped,
    long DecodedMessagesDropped, long DamagedDirections, bool ObservationOverflow);
internal sealed record CalibrationEvidenceCheck(string Field, string Verdict);
internal sealed record CalibrationEvidenceSession(string SessionAlias, bool Complete, CalibrationEvidenceHealth Health,
    IReadOnlyList<CalibrationEvidenceCheck> Checks);

/// <summary>只声明 canonical v1 允许的字段；内部诊断对象和原始流量永远不参与序列化。</summary>
internal sealed record CalibrationEvidenceDocument(string Format, int SchemaVersion, string Kind, string EvidenceId,
    CalibrationEvidenceProducer Producer, CalibrationEvidenceTarget Target, IReadOnlyList<CalibrationEvidenceSession> Sessions,
    IReadOnlyList<object> Observations)
{
    internal const int MaxBytes = 48 * 1024;
    internal const string RulesVersion = "calibration-evidence-v1";
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
    };

    internal string Serialize() => JsonSerializer.Serialize(this, Json);

    // Disk contents are untrusted. Bound every collection and reject unknown fields before reusing a receipt.
    internal bool Valid() => Format == "MentorRecorder.CalibrationEvidence" && SchemaVersion == 1 &&
        Kind == "CODE_VERIFICATION" && Uuid(EvidenceId) && Producer is not null && Target is not null &&
        Producer.RulesVersion == RulesVersion && Producer.AppVersion is { Length: <= 64 } version &&
        Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.+-]+)?\z", RegexOptions.CultureInvariant) &&
        Target.Region is "CN" or "GLOBAL" &&
        Target.GameBuild is { Length: <= 24 } build && Regex.IsMatch(build, @"\A[0-9]{4}\.[0-9]{2}\.[0-9]{2}\.[0-9]{4}\.[0-9]{4}\z", RegexOptions.CultureInvariant) &&
        Target.TemplateProfileId is { Length: > 0 and <= 128 } profile &&
        Regex.IsMatch(profile, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant) &&
        Hash(Target.TemplateSha256) && Hash(Target.CodeSha256) && Target.QuarantineEpoch >= 0 &&
        Sessions is { Count: > 0 and <= 8 } && Sessions.All(session => session is not null &&
            Uuid(session.SessionAlias) && session.Complete && session.Health is { SilentReason: "NONE", PreexistingConnections: 0,
                AdapterDropped: 0, DecodedMessagesDropped: 0, DamagedDirections: 0, ObservationOverflow: false } &&
            session.Checks is { Count: > 0 and <= 4 } && session.Checks.All(check => check is not null &&
                check.Field is "zone" or "territory" or "job" or "match" && check.Verdict is "PASS" or "CONTRADICT" or "WAIT") &&
            session.Checks.Select(check => check.Field).Distinct(StringComparer.Ordinal).Count() == session.Checks.Count) &&
        Sessions.Select(session => session.SessionAlias).Distinct(StringComparer.Ordinal).Count() == Sessions.Count &&
        Observations is { Count: 0 } && System.Text.Encoding.UTF8.GetByteCount(Serialize()) <= MaxBytes;

    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Uuid(string? value) => Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;
}
