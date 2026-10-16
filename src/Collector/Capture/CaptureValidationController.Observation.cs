using System.Text.Json.Nodes;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Capture;

public sealed partial class CaptureValidationController
{
    private Session? _observationSession;
    private string? _observationEvidenceId;
    private string? _observationSessionAlias;

    /// <summary>
    /// 只导出本控制器最近已结束并成功封存的元数据会话；调用方不能指定磁盘路径。
    /// 同一会话预览复用随机身份，摘要不含本地路径、流量哈希、连接身份或精确时刻。
    /// </summary>
    public JsonObject GetTemplateObservation()
    {
        Session session;
        string evidenceId, alias, path, digest;
        lock (_gate)
        {
            if (_disposed) return Unavailable("CONTROLLER_DISPOSED");
            if (_session is not { } current) return Unavailable("NO_OBSERVATION");
            if (!current.Finalized) return Unavailable("OBSERVATION_ACTIVE");
            if (current.ErrorCode is not null || current.WriteFailed) return Unavailable("OBSERVATION_FAILED");
            if (!current.RecordingStarted || current.Path is null || current.Digest is null)
                return Unavailable("NO_OBSERVATION");
            session = current;
            path = current.Path;
            digest = current.Digest;
            if (!ReferenceEquals(_observationSession, current))
            {
                _observationSession = current;
                _observationEvidenceId = Guid.NewGuid().ToString("D");
                _observationSessionAlias = Guid.NewGuid().ToString("D");
            }
            evidenceId = _observationEvidenceId!;
            alias = _observationSessionAlias!;
        }

        // Disk work must not hold the lifecycle gate. A replacement session invalidates a late response.
        var result = TemplateObservationExporter.Export(path, digest, evidenceId, alias);
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_session, session)) return Unavailable("OBSERVATION_CHANGED");
            return CalibrationEvidenceHandlers.Response(result, "TEMPLATE_OBSERVATION");
        }
    }

    private static JsonObject Unavailable(string reason) => CalibrationEvidenceHandlers.Response(
        CalibrationEvidenceResult.Unavailable(reason), "TEMPLATE_OBSERVATION");
}
