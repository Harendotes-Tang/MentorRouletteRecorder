using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Ipc;

/// <summary>公开证据的只读 IPC：严守固定表单与完整 JSON，预览不会上传或打开任何窗口。</summary>
public static class CalibrationEvidenceHandlers
{
    /// <summary>读取指定校准码或唯一当前候选的已完成健康摘要；无可用证据时返回原因。</summary>
    public static JsonObject Get(CollectorHost host, PayloadReader reader)
    {
        reader.RejectUnknown("code_sha256");
        var code = reader.String("code_sha256", 64);
        if (code is not null && !CalibrationEvidenceDocument.Hash(code))
            throw CollectorException.BadRequest(CollectorTexts.CalibrationEvidenceHashInvalid(), "code_sha256");
        return Response(host.LiveProtocol?.GetCalibrationEvidence(code) ??
            CalibrationEvidenceResult.Unavailable("PIPELINE_UNAVAILABLE"));
    }

    /// <summary>统一成功/等待响应。过长地址仅省略预填，完整摘要仍由 evidence_json 提供。</summary>
    public static JsonObject Response(CalibrationEvidenceResult result, string kind = "CODE_VERIFICATION")
    {
        var form = kind == "TEMPLATE_OBSERVATION" ? "template-observation.yml" : "evidence-calibration.yml";
        var root = "https://github.com/" + SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository +
            "/issues/new?template=" + form;
        var prefilled = result.Available && result.EvidenceJson is { } json
            ? root + "&evidence=" + Uri.EscapeDataString(json) : null;
        var inUrl = prefilled is { Length: <= SharedCalibrationIssueLink.MaxUrlLength };
        return new JsonObject
        {
            ["available"] = result.Available,
            ["kind"] = kind,
            ["evidence_json"] = result.Available ? result.EvidenceJson : null,
            ["issue_url"] = result.Available ? (inUrl ? prefilled : root) : null,
            ["evidence_in_url"] = inUrl,
            ["reason"] = result.Reason,
        };
    }
}
