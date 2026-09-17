using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Pipeline;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The five shared-calibration requests: the share code of the local calibration in force,
/// 立即检查, 导入校准码, accepting queue inference once, and 不用共享的，我自己校准. Normal answers are
/// outcome tokens; only a share code that cannot be given fails the request. The renderers are
/// public so the contract tests can hold every success shape against the schema.
/// </summary>
public static class SharedCalibrationHandlers
{
    /// <summary>Longest <c>code</c> a request may carry at all; a longer one is a malformed request, not a malformed code.</summary>
    public const int MaxCodeRequestLength = 65536;

    /// <summary>Handles <c>GetCalibrationShareCode</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject GetShareCode(CollectorHost host, PayloadReader reader)
    {
        var result = EmptyRequest(host, reader).GetCalibrationShareCode();
        if (result.ShareCode is { } code)
        {
            return ShareCodeResponse(code);
        }

        throw new CollectorException(
            ErrorCodes.ShareCodeUnavailable,
            result.Message ?? "当前档案无法生成校准码。",
            new Dictionary<string, object?>
            {
                ["reason"] = EnumWire<SharedShareCodeRefusal>.Format(result.Refusal ?? SharedShareCodeRefusal.NotShareable),
            });
    }

    /// <summary>Handles <c>CheckSharedCalibration</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Check(CollectorHost host, PayloadReader reader) =>
        OutcomeResponse(EnumWire<SharedCheckOutcome>.Format(EmptyRequest(host, reader).CheckSharedCalibrationNow()));

    /// <summary>Handles <c>ImportCalibrationCode</c>. The length is checked before anything decodes the text.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Import(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("code");
        var code = reader.String("code", MaxCodeRequestLength)
            ?? throw CollectorException.BadRequest("缺少必填字段 code。", "payload.code");
        var pipeline = RequirePipeline(host);
        var result = code.AsSpan().Trim().Length > ShareCode.MaxCodeLength
            ? new SharedImportResult(
                SharedImportOutcome.Malformed,
                ShareCodeRejection.TooLong,
                $"这不是一份能识别的校准码：内容太长，校准码最多 {ShareCode.MaxCodeLength} 个字符。")
            : pipeline.ImportCalibrationCode(code);
        return ImportResponse(result);
    }

    /// <summary>Handles <c>AcceptSharedQueueInference</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject AcceptQueueInference(CollectorHost host, PayloadReader reader) =>
        OutcomeResponse(EnumWire<SharedConsentOutcome>.Format(EmptyRequest(host, reader).AcceptSharedQueueInference()));

    /// <summary>Handles <c>RejectSharedCalibration</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Reject(CollectorHost host, PayloadReader reader) =>
        RejectResponse(EmptyRequest(host, reader).RejectSharedCalibration());

    /// <summary>The <c>GetCalibrationShareCode</c> response.</summary>
    /// <param name="code">The share code.</param>
    public static JsonObject ShareCodeResponse(SharedShareCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new JsonObject
        {
            ["code"] = code.Code,
            ["code_sha256"] = code.CodeSha256,
            ["issue_url"] = code.IssueUrl,
            ["code_in_url"] = code.CodeInUrl,
        };
    }

    /// <summary>The <c>ImportCalibrationCode</c> response.</summary>
    /// <param name="result">What importing came to.</param>
    public static JsonObject ImportResponse(SharedImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new JsonObject
        {
            ["outcome"] = EnumWire<SharedImportOutcome>.Format(result.Outcome),
            ["reason"] = result.Reason,
            ["message"] = result.Message,
            ["code_sha256"] = result.CodeSha256,
        };
    }

    /// <summary>The <c>RejectSharedCalibration</c> response.</summary>
    /// <param name="result">What the refusal did.</param>
    public static JsonObject RejectResponse(SharedRejectResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new JsonObject
        {
            ["withdrawn_profile_id"] = result.WithdrawnProfileId,
            ["dropped_candidates"] = result.DroppedCandidates,
        };
    }

    /// <summary>The <c>CheckSharedCalibration</c> and <c>AcceptSharedQueueInference</c> responses.</summary>
    /// <param name="token">Outcome token.</param>
    public static JsonObject OutcomeResponse(string token) => new() { ["outcome"] = token };

    private static LiveProtocolPipeline EmptyRequest(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        return RequirePipeline(host);
    }

    private static LiveProtocolPipeline RequirePipeline(CollectorHost host) =>
        host.LiveProtocol ?? throw new CollectorException(ErrorCodes.CalibrationNotReady, "本进程没有运行协议管线，无法校准。");
}
