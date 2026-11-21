using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Ipc;

/// <summary>Bounded IPC pages and upload chunks for an exact checked history set.</summary>
internal static class HistorySelectionHandlers
{
    /// <summary>Creates, pages or releases a temporary selection handle.</summary>
    public static JsonObject Query(CollectorHost host, PayloadReader reader, CancellationToken cancellationToken)
    {
        reader.RejectUnknown("filter", "selection_id", "page", "release");
        var selections = host.HistorySelections;
        if (reader.Has("release"))
        {
            reader.RejectUnknown("selection_id", "release");
            if (reader.Bool("release") is not true)
                throw CollectorException.BadRequest(CollectorTexts.IpcFieldOnlyValues("release", "true"), "payload.release");
            var id = reader.RequiredString("selection_id", 64);
            selections.Release(IsDigest(id) ? id : FreezeId(id));
            return new JsonObject { ["released"] = true };
        }

        var page = reader.Has("page") ? reader.RequiredInt("page", 1) : 1;
        if (reader.Has("selection_id"))
        {
            reader.RejectUnknown("selection_id", "page");
            var id = FreezeId(reader.RequiredString("selection_id", 64));
            return Page(id, selections.ReadPage(id, page));
        }

        if (page != 1)
            throw CollectorException.BadRequest(CollectorTexts.HistorySelectionFirstPageRequired(), "payload.page");
        var result = selections.Freeze(RequestParsers.Filter(reader.Object("filter")), cancellationToken);
        return Page(result.Id, result.Page);
    }

    /// <summary>Appends or replays an ordered, bounded chunk and seals the set on its last chunk.</summary>
    public static JsonObject Stage(CollectorHost host, PayloadReader reader)
    {
        reader.RejectUnknown("selection_id", "chunk_index", "runs", "last");
        var id = ReadStagedId(reader) ??
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing("selection_id"), "payload.selection_id");
        var index = reader.RequiredInt("chunk_index", 0);
        var runs = RequestParsers.BatchRunTargets(reader, Paging.MaxPageSize);
        var last = reader.Bool("last") ??
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing("last"), "payload.last");
        var staged = host.HistorySelections.Stage(id, index, runs, last);
        return new JsonObject
        {
            ["selection_id"] = id,
            ["next_chunk_index"] = staged.NextIndex,
            ["sealed"] = staged.Sealed,
        };
    }

    /// <summary>Reads a content identity, preserving omission for legacy array requests.</summary>
    public static string? ReadStagedId(PayloadReader reader)
    {
        if (!reader.Has("selection_id")) return null;
        var id = reader.RequiredString("selection_id", 64);
        if (!IsDigest(id))
            throw CollectorException.BadRequest(CollectorTexts.HistorySelectionDigestInvalid(), "payload.selection_id");
        return id;
    }

    /// <summary>Reads a staged identity for a target request and rejects dual specification.</summary>
    public static string? ReadTargetId(PayloadReader reader)
    {
        var id = ReadStagedId(reader);
        if (id is not null && reader.Has("runs"))
            throw CollectorException.BadRequest(CollectorTexts.HistorySelectionExclusiveTarget(), "payload.selection_id");
        return id;
    }

    private static bool IsDigest(string id) => id.Length == 64 &&
        id.All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string FreezeId(string id) => Guid.TryParseExact(id, "D", out var parsed)
        ? parsed.ToString("D")
        : throw CollectorException.BadRequest(CollectorTexts.IpcFieldNotUuid("selection_id"), "payload.selection_id");

    private static JsonObject Page(string id, Page<HistorySelectionRow> page) => new()
    {
        ["selection_id"] = id,
        ["items"] = new JsonArray(page.Items.Select(row => (JsonNode?)new JsonObject
        {
            ["run_id"] = row.RunId,
            ["revision"] = row.Revision,
            ["soft_deleted"] = row.SoftDeleted,
            ["source"] = EnumWire<RunSource>.Format(row.Source),
            ["region"] = EnumWire<Region>.Format(row.Region),
            ["capture_session_id"] = row.CaptureSessionId,
        }).ToArray()),
        ["page_info"] = Wire.PageInfo(page.PageNumber, page.PageSize, page.Total),
    };
}
