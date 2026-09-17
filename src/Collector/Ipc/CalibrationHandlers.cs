using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The two calibration requests: confirm the timeline (writes the local profile and, when a
/// session is running, starts recording in it) and discard the evidence of this session.
/// </summary>
public static class CalibrationHandlers
{
    /// <summary>Handles <c>ConfirmCalibration</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Confirm(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("verdicts");
        var verdicts = new Dictionary<string, CalibrationVerdict>(StringComparer.Ordinal);
        foreach (var item in reader.ObjectArray("verdicts", 256))
        {
            item.RejectUnknown("event_id", "verdict", "roulette_name");
            var eventId = item.RequiredString("event_id", 128);
            var verdict = item.RequiredString("verdict", 16);
            if (verdict is not ("CORRECT" or "WRONG" or "RELABEL"))
            {
                throw CollectorException.BadRequest(
                    "verdict 只能是 CORRECT、WRONG 或 RELABEL。", "payload.verdicts");
            }

            var name = item.String("roulette_name", 64);
            if (verdict == "RELABEL" && string.IsNullOrWhiteSpace(name))
            {
                throw CollectorException.BadRequest(
                    "RELABEL 必须带上 roulette_name。", "payload.verdicts");
            }

            verdicts[eventId] = new CalibrationVerdict(verdict, verdict == "RELABEL" ? name : null);
        }

        if (verdicts.Count == 0)
        {
            throw CollectorException.BadRequest("verdicts 不能为空。", "payload.verdicts");
        }

        var pipeline = host.LiveProtocol ?? throw new CollectorException(
            ErrorCodes.CalibrationNotReady, "本进程没有运行协议管线，无法校准。");
        var result = pipeline.ConfirmCalibration(verdicts);
        if (result.BoundInSession)
        {
            host.LiveEvents.PublishCollectorStatus(
                CaptureWire.CaptureStatus(host.Capture.Snapshot()), "已按本机校准的档案开始自动记录。");
        }

        return new JsonObject
        {
            ["profile_id"] = result.ProfileId,
            ["profile_path"] = result.ProfilePath,
            ["bound_in_session"] = result.BoundInSession,
        };
    }

    /// <summary>Handles <c>DiscardCalibration</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload.</param>
    public static JsonObject Discard(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        var pipeline = host.LiveProtocol ?? throw new CollectorException(
            ErrorCodes.CalibrationNotReady, "本进程没有运行协议管线，无法校准。");
        var snapshot = pipeline.DiscardCalibration();
        return new JsonObject
        {
            ["state"] = CalibrationWire.State(snapshot.State),
        };
    }
}
