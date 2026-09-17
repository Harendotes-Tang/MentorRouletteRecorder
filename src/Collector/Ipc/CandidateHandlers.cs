using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Ipc;

/// <summary>候选账本 IPC。核对只更新独立审阅历史，不触发正式记录或统计通知。</summary>
public static class CandidateHandlers
{
    public static JsonObject Query(CollectorHost host, PayloadReader reader)
    {
        reader.RejectUnknown("session_id", "from_utc", "to_utc", "page", "page_size");
        var session = reader.String("session_id", 36);
        if (session is not null) reader.RequiredUuid("session_id");
        var from = reader.Timestamp("from_utc");
        var to = reader.Timestamp("to_utc");
        if (from > to) throw CollectorException.BadRequest("开始时间不能晚于结束时间。", "payload.from_utc");
        var page = reader.Has("page") ? reader.RequiredInt("page", 1, 1_000_000) : 1;
        var size = reader.Has("page_size") ? reader.RequiredInt("page_size", 1, 200) : 50;
        var result = host.Candidates.Query(session, from, to, page, size);
        return new JsonObject
        {
            ["items"] = new JsonArray(result.Items.Select(item => (JsonNode)CandidateWire.Observation(item)).ToArray()),
            ["page_info"] = Wire.PageInfo(result.PageNumber, result.PageSize, result.Total),
        };
    }

    public static JsonObject Review(CollectorHost host, string requestId, PayloadReader reader)
    {
        reader.RejectUnknown("observation_id", "verdict", "note");
        var result = host.Candidates.Review(requestId, reader.RequiredUuid("observation_id"),
            reader.RequiredString("verdict", 7), reader.String("note", 2000));
        return new JsonObject
        {
            ["observation_id"] = result.ObservationId,
            ["review_verdict"] = result.ReviewVerdict,
            ["reviewed_at_utc"] = UtcTimestamp.ToText(result.ReviewedAtUtc),
        };
    }

    public static JsonObject Export(CollectorHost host, PayloadReader reader)
    {
        reader.RejectUnknown("target_path");
        var result = host.CandidateExporter.Export(reader.String("target_path", 32767));
        return new JsonObject
        {
            ["target_path"] = result.TargetPath,
            ["sha256_path"] = result.Sha256Path,
            ["sha256"] = result.Sha256,
            ["observation_count"] = result.ObservationCount,
            ["review_count"] = result.ReviewCount,
            ["byte_count"] = result.ByteCount,
            ["completed_at_utc"] = UtcTimestamp.ToText(result.CompletedAtUtc),
        };
    }
}

/// <summary>候选查询和证据共用的 JSON 字段投影。</summary>
public static class CandidateWire
{
    public static JsonObject Observation(CandidateObservationEntry entry, bool includePayload = false)
    {
        var o = entry.Observation;
        var result = new JsonObject
        {
            ["observation_id"] = o.ObservationId, ["capture_session_id"] = o.CaptureSessionId,
            ["profile_id"] = o.ProfileId, ["hypothesis_name"] = o.HypothesisName,
            ["group_name"] = o.Group, ["direction"] = o.Direction, ["opcode"] = o.Opcode,
            ["payload_length"] = o.Length, ["payload_hash12"] = o.PayloadHash12,
            ["connection_tag"] = o.ConnectionTag, ["observed_at_utc"] = UtcTimestamp.ToText(o.ObservedAtUtc),
            ["t_ms"] = o.TMs, ["first_observed_at_utc"] = UtcTimestamp.ToTextOrNull(o.FirstObservedAtUtc),
            ["last_observed_at_utc"] = UtcTimestamp.ToTextOrNull(o.LastObservedAtUtc),
            ["first_t_ms"] = o.FirstTMs, ["last_t_ms"] = o.LastTMs, ["occurrences"] = o.Occurrences,
            ["review_verdict"] = entry.ReviewVerdict, ["review_note"] = entry.ReviewNote,
            ["reviewed_at_utc"] = UtcTimestamp.ToTextOrNull(entry.ReviewedAtUtc),
        };
        if (includePayload) result["payload_hex"] = o.PayloadHex;
        return result;
    }

    public static JsonObject Review(CandidateReviewEntry r) => new()
    {
        ["review_id"] = r.ReviewId, ["observation_id"] = r.ObservationId,
        ["request_id"] = r.RequestId, ["verdict"] = r.Verdict, ["note"] = r.Note,
        ["reviewed_at_utc"] = UtcTimestamp.ToText(r.ReviewedAtUtc),
    };
}
