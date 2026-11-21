using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Export;

public sealed record CandidateExportResult(string TargetPath, string Sha256Path, string Sha256,
    int ObservationCount, int ReviewCount, long ByteCount, DateTimeOffset CompletedAtUtc);

/// <summary>只向用户本地允许目录导出独立证据及其 SHA256；不覆盖既有文件。</summary>
/// <param name="databasePath">打开的数据库文件；其所在目录决定未指定路径时的导出目录。</param>
/// <param name="roundReport">软件对这一轮的识别结果；缺省时导出不带这一段。</param>
public sealed class CandidateEvidenceExporter(
    CandidateObservationRepository repository, IClock clock, string databasePath,
    Func<CandidateRoundReport?>? roundReport = null)
{
    /// <summary>未指定路径时的导出目录：打开的数据库所在目录下的 exports，与备份、诊断报告一致。</summary>
    public string DefaultDirectory => Path.Combine(
        Path.GetDirectoryName(databasePath) ?? DatabasePaths.RootDirectory, "exports");

    /// <summary>默认文件名；时间戳固定使用不变文化的公历，不随系统区域设置改变。</summary>
    /// <param name="now">导出时刻。</param>
    public static string FileNameFor(DateTimeOffset now) => string.Create(
        CultureInfo.InvariantCulture,
        $"candidate-evidence-{now.ToUniversalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");

    /// <param name="targetPath">目标文件；缺省时写入 <see cref="DefaultDirectory"/>。</param>
    /// <param name="answers">玩家在卡片上填写的内容；与软件的识别结果一并写入 <c>round_report</c>。</param>
    public CandidateExportResult Export(string? targetPath = null, CandidateRoundAnswers? answers = null) =>
        Export(targetPath, answers, null, null);

    /// <summary>Exports a scoped round when supplied, with its report calculated in the evidence snapshot.</summary>
    internal CandidateExportResult Export(string? targetPath, CandidateRoundAnswers? answers,
        CandidatePlayerRound? round, Func<SqliteTransaction, CandidateRoundReport?>? scopedReport)
    {
        // The maintainer's best-effort report reads its own snapshot.
        var unscopedGuess = round is null ? roundReport?.Invoke() : null;
        // 默认路径跟随打开的数据库，与备份、诊断报告、日志一致：只看数据根目录时，
        // 用一次性数据库启动、却没有设置 MR_DATA_DIR 的测试会把证据写进真人用户的
        // %LOCALAPPDATA%\MentorRecorder\exports。时间戳走不变文化，否则在使用非公历的
        // 系统区域下会生成一个完全不同的文件名（评审 L-16）。
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
                // The repository's read-only snapshot spans the synchronous enumeration, on a
                // connection of its own with no database gate held, so the capture thread keeps
                // writing while this file is written (audit 2026-10-03, OG-7 / CS5-X3). Only one
                // row projection is materialised at a time, including arbitrarily long
                // append-only history.
                repository.VisitResearchEvidence(round, (transaction, payloadOpcodes, observations, reviews, items, history) =>
                {
                    var guessed = scopedReport?.Invoke(transaction) ?? unscopedGuess;
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
                    if (guessed is not null || answers is not null)
                    {
                        // What the software read at the guessed positions and what the player
                        // wrote next to it; the maintainer compares the two.
                        writer.WritePropertyName("round_report");
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["guessed"] = guessed?.Wire(),
                            ["answered"] = answers?.Wire(),
                        }.WriteTo(writer);
                    }
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
                throw new CollectorException(ErrorCodes.ExportFailed, CollectorTexts.ExportCandidateEvidenceFailed(), inner: ex);
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
