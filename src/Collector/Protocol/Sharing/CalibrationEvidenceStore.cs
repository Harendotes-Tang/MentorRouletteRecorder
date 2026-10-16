using System.Text;
using System.Text.Json;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// 有界公开摘要留存。空 root 只保存在内存供隔离测试；指定 root 时原子替换单个文件，
/// 跨重启保留随机别名及重试身份。此处不保存原始会话 ID、连接标识或流量。
/// </summary>
public sealed class CalibrationEvidenceReceiptStore
{
    internal const int MaxDocuments = 32;
    private const int MaxStoreBytes = MaxDocuments * CalibrationEvidenceDocument.MaxBytes;
    private readonly string? _path;
    private readonly List<CalibrationEvidenceDocument> _documents = new();
    private bool _unreadable;

    /// <summary>只读取现有摘要，不建目录、不访问网络。无法读取时禁止随后覆盖旧摘要。</summary>
    public CalibrationEvidenceReceiptStore(string? root = null)
    {
        if (root is null) return;
        _path = Path.Combine(Path.GetFullPath(root), "receipts.json");
        try
        {
            if (!File.Exists(_path)) return;
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxStoreBytes) return;
            var stored = JsonSerializer.Deserialize<List<CalibrationEvidenceDocument>>(file, CalibrationEvidenceDocument.Json);
            if (stored is { Count: <= MaxDocuments } && stored.All(document => document is not null && document.Valid()))
                _documents.AddRange(stored);
        }
        catch (JsonException) { /* A corrupt file proves nothing; no session is invented. */ }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { _unreadable = true; }
    }

    internal CalibrationEvidenceDocument? Find(CalibrationEvidenceTarget target) =>
        _documents.LastOrDefault(document => document.Target == target);

    internal bool Append(CalibrationEvidenceTarget target, CalibrationEvidenceSession session)
    {
        var previous = Find(target);
        if (previous?.Sessions.Any(existing => existing.SessionAlias == session.SessionAlias) == true) return true;
        var sessions = (previous?.Sessions ?? Array.Empty<CalibrationEvidenceSession>()).Append(session).TakeLast(8).ToArray();
        var document = new CalibrationEvidenceDocument("MentorRecorder.CalibrationEvidence", 1, "CODE_VERIFICATION",
            Guid.NewGuid().ToString("D"), new(Program.Version, CalibrationEvidenceDocument.RulesVersion), target,
            sessions, Array.Empty<object>());
        if (!document.Valid() || _unreadable) return false;
        var next = _documents.Where(existing => existing.Target != target).Append(document).TakeLast(MaxDocuments).ToList();
        if (_path is not null)
        {
            var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(next, CalibrationEvidenceDocument.Json);
                if (bytes.Length > MaxStoreBytes) return false;
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { return false; }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        _documents.Clear();
        _documents.AddRange(next);
        return true;
    }
}
