using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Import;

namespace MentorRecorder.Collector.Ipc;

/// <summary>Strict IPC boundary for personal record preview and durable merge commit.</summary>
public static class RunImportHandlers
{
    public static JsonObject Preview(CollectorHost host, JsonObject payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new PayloadReader(payload);
        reader.RejectUnknown("source_kind", "file_path", "text", "rows", "column_mapping", "time_zone", "source_name");
        JsonArray? rows = null;
        if (reader.Has("rows"))
        {
            if (payload["rows"] is not JsonArray array || array.Count > RunImportService.MaxRows || array.Any(node => node is not JsonObject))
                throw CollectorException.BadRequest("rows 必须是最多 5000 行的候选对象数组。", "rows");
            rows = (JsonArray)array.DeepClone();
        }
        Dictionary<string, string>? mapping = null;
        if (reader.Object("column_mapping") is { } mapReader)
        {
            if (mapReader.Names.Count() > 200) throw CollectorException.BadRequest("列映射超过 200 列。", "column_mapping");
            mapping = mapReader.Names.ToDictionary(name => name, name => mapReader.RequiredString(name, 100), StringComparer.Ordinal);
        }
        return host.Imports.PreviewSource(reader.RequiredString("source_kind", 32),
            reader.String("file_path", 32000), reader.String("text", FrameCodec.MaxFrameBytes / 2), rows, mapping,
            reader.String("time_zone", 100), reader.String("source_name", 500), cancellationToken);
    }

    public static JsonObject Commit(CollectorHost host, string requestId, JsonObject payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new PayloadReader(payload);
        reader.RejectUnknown("preview_id", "row_numbers", "confirm_own_records", "deduct_from_baseline");
        var response = host.Imports.Commit(reader.RequiredUuid("preview_id"),
            reader.IntArray("row_numbers", RunImportService.MaxRows, minimum: 1),
            reader.Bool("confirm_own_records") == true, requestId,
            reader.Bool("deduct_from_baseline") == true, cancellationToken);
        if (response["replayed"]?.GetValue<bool>() != true && response["imported_count"]?.GetValue<int>() > 0)
            host.LiveEvents.PublishStatsInvalidated("records_imported");
        return response;
    }
}
