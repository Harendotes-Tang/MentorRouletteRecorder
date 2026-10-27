using System.Globalization;
using Machina.FFXIV.Memory;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>构造测试用 Oodle 签名档案；每次写入独立字典，不共享可变测试状态。</summary>
internal static class OodleSignatureTestFiles
{
    /// <summary>
    /// 将合成签名写入指定目录和文件名；保留调用方提供的构建号、可执行文件身份及 RVA。
    /// stamp 为 false 时保留全零哈希，以便测试加载器拒绝未正确签名的档案。
    /// </summary>
    public static string Write(
        string directory,
        string id,
        string gameBuild,
        string exeSha256,
        long exeSize,
        int resolvedRva = 0x10,
        bool stamp = true)
    {
        Directory.CreateDirectory(directory);
        var document = new Dictionary<string, object>
        {
            ["schema_version"] = OodleSignatureProfile.SupportedSchemaVersion,
            ["region"] = "CN",
            ["game_build"] = gameBuild,
            ["exe_sha256"] = exeSha256,
            ["exe_size"] = exeSize,
            ["generated_at_utc"] = "2026-09-05T00:00:00Z",
            ["source"] = OodleSignatureProfile.FinderSource,
            ["status"] = OodleSignatureProfile.CandidateStatus,
            ["signatures"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                type => (object)(type is SignatureType.OodleMalloc or SignatureType.OodleFree
                    ? "48 8b ff 15"
                    : "48 8b 00 e8")),
            ["resolved_rvas"] = Enum.GetValues<SignatureType>().ToDictionary(
                type => type.ToString(),
                _ => (object)resolvedRva.ToString("X8", CultureInfo.InvariantCulture)),
            ["profile_sha256"] = new string('0', 64),
        };

        // 仅复用规范化哈希与 UTF-8 写盘；Oodle schema 不包含协议档案的 profile_id。
        return ProfileTestFiles.Write(directory, id, document, stamp, rewriteId: false);
    }
}
