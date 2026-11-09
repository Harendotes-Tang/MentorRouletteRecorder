using System.Security.Cryptography;

namespace MentorRecorder.Collector.Replay;

/// <summary>读取离线回放样本并校验同名 SHA-256 文件；格式和业务语义仍由各加载器验证。</summary>
internal static class VerifiedFixtureFile
{
    /// <summary>返回已通过哈希校验的原始字节及小写摘要；缺少校验文件或摘要不符时拒绝读取结果。</summary>
    /// <param name="path">样本路径；为空时抛出参数异常，文件读取错误直接传给调用者。</param>
    /// <param name="label">加载器原有错误消息前缀，用于区分两种回放格式。</param>
    internal static (byte[] Bytes, string Sha256) Read(string path, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sidecarPath = fullPath + ".sha256";
        if (!File.Exists(sidecarPath))
        {
            throw new InvalidDataException(label + " SHA-256 sidecar is missing: " + sidecarPath);
        }

        var expectedHash = File.ReadAllText(sidecarPath).Trim().ToLowerInvariant();
        if (expectedHash.Length != 64 || !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(label + " SHA-256 mismatch");
        }

        return (bytes, actualHash);
    }
}
