using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Export;

public sealed record CandidateExportResult(string TargetPath, string Sha256Path, string Sha256,
    int ObservationCount, int ReviewCount, long ByteCount, DateTimeOffset CompletedAtUtc);

/// <summary>只向用户本地允许目录导出独立证据及其 SHA256；不覆盖既有文件。</summary>
public sealed class CandidateEvidenceExporter(CandidateObservationRepository repository, IClock clock)
{
    /// <summary>本次导出的默认目录：受 <c>MR_DATA_DIR</c> 控制的数据根目录下的 exports。</summary>
    public static string DefaultDirectory => Path.Combine(DatabasePaths.RootDirectory, "exports");

    /// <summary>默认文件名；时间戳固定使用不变文化的公历，不随系统区域设置改变。</summary>
    /// <param name="now">导出时刻。</param>
    public static string FileNameFor(DateTimeOffset now) => string.Create(
        CultureInfo.InvariantCulture,
        $"candidate-evidence-{now.ToUniversalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");

    public CandidateExportResult Export(string? targetPath = null)
    {
        // 默认路径必须与备份、日志走同一个数据根：直接读 %LOCALAPPDATA% 会让 MR_DATA_DIR
        // 隔离的测试和 verify.ps1 把证据写进真人用户的目录；时间戳走不变文化，否则在
        // 使用非公历的系统区域下会生成一个完全不同的文件名（评审 L-16）。
        var path = ExportPaths.Resolve(
            targetPath ?? Path.Combine(DefaultDirectory, FileNameFor(clock.UtcNow)));
        var sidecar = ExportPaths.Resolve(path + ".sha256");
        ExportPaths.PrepareDestination(path, overwrite: false);
        ExportPaths.PrepareDestination(sidecar, overwrite: false);
        var observationCount = 0;
        var reviewCount = 0;
        var hash = string.Empty;
        var created = false;
        var createdSidecar = false;
        try
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
                // The transaction spans the synchronous enumeration. Only one row projection
                // is materialised at a time, including arbitrarily long append-only history.
                repository.VisitResearchEvidence((payloadOpcodes, observations, reviews, items, history) =>
                {
                    observationCount = observations;
                    reviewCount = reviews;
                    writer.WriteStartObject();
                    writer.WriteString("format", "MentorRecorder.CandidateEvidence");
                    writer.WriteNumber("schema_version", 1);
                    writer.WriteString("exported_at_utc", UtcTimestamp.ToText(clock.UtcNow));
                    writer.WriteString("profile_status", "CANDIDATE");
                    writer.WriteBoolean("contains_raw_payload", payloadOpcodes.Count > 0);
                    writer.WriteStartArray("research_payload_opcodes");
                    foreach (var opcode in payloadOpcodes) writer.WriteStringValue(opcode);
                    writer.WriteEndArray();
                    writer.WriteNumber("observation_count", observations);
                    writer.WriteNumber("review_count", reviews);
                    writer.WriteStartArray("observations");
                    foreach (var item in items)
                    {
                        CandidateWire.Observation(item, includePayload: true).WriteTo(writer);
                        writer.Flush();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("reviews");
                    foreach (var review in history)
                    {
                        CandidateWire.Review(review).WriteTo(writer);
                        writer.Flush();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.Flush();
                });
            }
            using (var file = File.OpenRead(path))
                hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            using (var file = new FileStream(sidecar, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                createdSidecar = true;
                file.Write(Encoding.UTF8.GetBytes(hash + "  " + Path.GetFileName(path) + "\n"));
            }
        }
        catch (Exception ex)
        {
            if (createdSidecar) TryRemove(sidecar);
            if (created) TryRemove(path);
            if (ex is IOException or UnauthorizedAccessException)
                throw new CollectorException(ErrorCodes.ExportFailed, "候选证据导出失败，请检查本地路径和磁盘空间。", inner: ex);
            throw;
        }
        return new CandidateExportResult(path, sidecar, hash, observationCount,
            reviewCount, new FileInfo(path).Length, clock.UtcNow);
    }

    private static void TryRemove(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
