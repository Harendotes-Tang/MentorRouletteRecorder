using System.Globalization;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.Capture;

/// <summary>研究负载的封闭白名单：只接受已声明、有小长度上界且不混淆的候选假设。</summary>
public static class ResearchPayloadPolicy
{
    /// <summary>
    /// Longest complete research payload the whitelist may keep. Migration 0007 applies
    /// the same 512-byte bound in SQLite; longer payloads are omitted, never truncated.
    /// Eligibility still requires a declared bounded, non-obfuscated candidate hypothesis.
    /// </summary>
    public const int MaxPayloadBytes = 512;
    public const int MaxOpcodes = 32;
    // 只使用任务书引用的本地研究结论中的名称；不推测任何混淆 opcode 数值。
    private static readonly HashSet<string> ObfuscatedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PlayerSpawn", "NpcSpawn", "NpcSpawn2", "ActionEffect01", "ActionEffect02", "ActionEffect04",
        "ActionEffect08", "ActionEffect16", "ActionEffect24", "ActionEffect32", "StatusEffectList",
        "StatusEffectList3", "Examine", "UpdateGearset", "UpdateParty", "ActorControl", "ActorCast",
        "UnknownEffect01", "UnknownEffect16",
    };

    public static bool TryParse(string? text, out ushort opcode)
    {
        opcode = 0;
        return text is { Length: 6 } && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ushort.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out opcode);
    }

    public static string[] Normalize(IReadOnlyList<string> values)
    {
        if (values.Count > MaxOpcodes)
            throw CollectorException.BadRequest("研究白名单最多 32 项。", "payload.research_payload_opcodes");
        var result = new SortedSet<ushort>();
        foreach (var value in values)
        {
            if (!TryParse(value, out var opcode))
                throw CollectorException.BadRequest("白名单条目必须是 0x 加四位十六进制数。", "payload.research_payload_opcodes");
            if (!result.Add(opcode))
                throw CollectorException.BadRequest("研究白名单不能重复。", "payload.research_payload_opcodes");
        }
        return result.Select(opcode => "0x" + opcode.ToString("x4", CultureInfo.InvariantCulture)).ToArray();
    }

    public static bool IsEligible(ProtocolProfile profile, ProfileHypothesis hypothesis) =>
        profile.Status == ProfileCompatibilityStatus.Candidate && !profile.IsObfuscated(hypothesis.Opcode) &&
        !ObfuscatedNames.Contains(string.Concat(hypothesis.Name.Where(char.IsLetterOrDigit))) &&
        (hypothesis.ExpectedLength ?? hypothesis.MaxLength) is >= 0 and <= MaxPayloadBytes;

    /// <summary>调用者已确认验证开关开启后，才能载入候选目录以校验用户增删的白名单。</summary>
    public static string[] ValidateRequested(IReadOnlyList<string> values, IEnumerable<ProtocolProfile> profiles)
    {
        var normalized = Normalize(values);
        var declared = profiles.Where(p => p.Status == ProfileCompatibilityStatus.Candidate)
            .SelectMany(p => p.Hypotheses.Select(h => (Profile: p, Hypothesis: h))).ToArray();
        foreach (var token in normalized)
        {
            TryParse(token, out var opcode);
            var matches = declared.Where(pair => pair.Hypothesis.Opcode == opcode).ToArray();
            if (matches.Length == 0 || matches.Any(pair => !IsEligible(pair.Profile, pair.Hypothesis)))
                throw CollectorException.BadRequest("该 opcode 未被声明为不超过 512 字节的非混淆候选。", "payload.research_payload_opcodes");
        }
        return normalized;
    }
}
