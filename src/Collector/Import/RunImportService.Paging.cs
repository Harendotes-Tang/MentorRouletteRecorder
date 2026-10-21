using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.Import;

public sealed partial class RunImportService
{
    /// <summary>UTF-8 bytes of all frozen preview responses retained by one service.</summary>
    public const long MaxPreviewCacheBytes = 64L * 1024 * 1024;
    private const int MaxPreviewResponseBytes = FrameCodec.MaxFrameBytes * 3 / 4;
    private long _previewCacheBytes;

    /// <summary>The candidates as the Desktop sends them back: UTF-8, CJK unescaped, like the IPC transport.</summary>
    private static readonly JsonSerializerOptions CandidateJsonOptions = new(Wire.JsonOptions)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Returns an independent tree from the original classification snapshot. This does not
    /// reread the source or database and does not extend the session's 30 minute lifetime.
    /// </summary>
    /// <param name="previewId">Identity of a cached preview created with page_size.</param>
    /// <param name="page">One-based page; an empty preview has exactly page 1.</param>
    /// <param name="cancellationToken">Cancellation prevents returning a continued page.</param>
    /// <returns>Complete rows of the requested page, with fixed source metadata and whole-batch summary.</returns>
    public JsonObject PreviewPage(string previewId, int page, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(previewId, "D", out _))
            throw CollectorException.BadRequest(CollectorTexts.ImportCommitIdsInvalid(), "preview_id");
        Preview preview;
        lock (_gate)
        {
            RemoveExpiredPreviews(_clock.UtcNow);
            if (!_previews.TryGetValue(previewId, out preview!))
                throw CollectorException.BadRequest(CollectorTexts.ImportCommitPreviewExpired(), "preview_id");
        }
        if (preview.PageSize is not { } size)
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldUndeclaredValue("preview_id"), "preview_id");
        var pageCount = Math.Max(1, (preview.Candidates.Count + size - 1) / size);
        if (page < 1 || page > pageCount)
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldOutOfRange("page", 1, pageCount), "page");
        var result = SlicePage(preview.FrozenRender!, page, size, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private JsonObject FreezeAndCache(Preview preview, JsonObject rendered, int? requestedPageSize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var json = rendered.ToJsonString(Wire.JsonOptions);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > _maxPreviewCacheBytes || requestedPageSize is null && bytes > MaxPreviewResponseBytes)
            throw CollectorException.BadRequest(CollectorTexts.ImportPreviewTooLarge(), "rows");
        // Every edit or region change sends all candidates back in one request. A batch whose candidates
        // could not travel in one frame would be shown but could never be checked again, so it is refused
        // here, within the same budget as an unpaged preview (audit 2026-10-20 H2-3).
        if (requestedPageSize is not null && CandidateBytes(rendered, cancellationToken) > MaxPreviewResponseBytes)
            throw CollectorException.BadRequest(CollectorTexts.ImportPreviewTooLarge(), "rows");
        var actualPageSize = requestedPageSize is { } requested
            ? BoundedPageSize(rendered, requested, cancellationToken) : (int?)null;
        var result = actualPageSize is { } size ? SlicePage(rendered, 1, size, cancellationToken) : rendered;
        // Only paged previews retain this private tree. The returned page consists of clones;
        // legacy callers own their complete result and cannot mutate a cached classification.
        var frozen = preview with { FrozenRender = actualPageSize is null ? null : rendered, PageSize = actualPageSize, FrozenBytes = bytes };
        lock (_gate)
        {
            // A cancelled preview must not evict useful sessions or occupy cache space.
            var now = _clock.UtcNow;
            cancellationToken.ThrowIfCancellationRequested();
            RemoveExpiredPreviews(now);
            while (_previews.Count >= MaxPreviews || _previewCacheBytes + bytes > _maxPreviewCacheBytes)
                RemovePreview(_previews.Values.OrderBy(value => value.Created).First().Id);
            _previews[preview.Id] = frozen;
            _previewCacheBytes += bytes;
        }
        return result;
    }

    /// <summary>UTF-8 bytes of the JSON array of every row's candidate, as a re-check request carries it.</summary>
    private static long CandidateBytes(JsonObject rendered, CancellationToken cancellationToken)
    {
        var rows = rendered["rows"]!.AsArray();
        long total = 2 + Math.Max(0, rows.Count - 1);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += Encoding.UTF8.GetByteCount(row!["candidate"]?.ToJsonString(CandidateJsonOptions) ?? "null");
        }
        return total;
    }

    private static int BoundedPageSize(JsonObject rendered, int requested, CancellationToken cancellationToken)
    {
        var rows = rendered["rows"]!.AsArray();
        var rowBytes = new long[rows.Count + 1];
        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowBytes[index + 1] = rowBytes[index] + Encoding.UTF8.GetByteCount(rows[index]!.ToJsonString(Wire.JsonOptions));
        }
        for (var size = requested; size >= 1; size--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageCount = Math.Max(1, (rows.Count + size - 1) / size);
            // The last page has the longest page number. Its empty envelope bounds every
            // page's shared metadata; row sizes below include JSON commas without truncation.
            var envelopeBytes = Encoding.UTF8.GetByteCount(PageEnvelope(rendered, pageCount, size, new JsonArray()).ToJsonString(Wire.JsonOptions));
            var fits = true;
            for (var start = 0; start < rows.Count; start += size)
            {
                var end = Math.Min(start + size, rows.Count);
                if (envelopeBytes + rowBytes[end] - rowBytes[start] + end - start - 1 > MaxPreviewResponseBytes)
                {
                    fits = false;
                    break;
                }
            }
            if (fits && envelopeBytes <= MaxPreviewResponseBytes) return size;
        }
        throw CollectorException.BadRequest(CollectorTexts.ImportPreviewTooLarge(), "rows");
    }

    private static JsonObject SlicePage(JsonObject rendered, int page, int size, CancellationToken cancellationToken)
    {
        var allRows = rendered["rows"]!.AsArray();
        var rows = new JsonArray();
        var start = (page - 1) * size;
        for (var index = start; index < Math.Min(start + size, allRows.Count); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(allRows[index]!.DeepClone());
        }
        return PageEnvelope(rendered, page, size, rows);
    }

    private static JsonObject PageEnvelope(JsonObject rendered, int page, int size, JsonArray rows) => new()
    {
        ["preview_id"] = rendered["preview_id"]!.DeepClone(),
        ["source_kind"] = rendered["source_kind"]!.DeepClone(),
        ["source_name"] = rendered["source_name"]?.DeepClone(),
        ["rows"] = rows, ["summary"] = rendered["summary"]!.DeepClone(),
        ["page_info"] = new JsonObject { ["page"] = page, ["page_size"] = size, ["total"] = rendered["summary"]!["total"]!.DeepClone() },
    };

    // Called only while _gate is held; accounting covers legacy and paged frozen JSON alike.
    private void RemoveExpiredPreviews(DateTimeOffset now)
    {
        foreach (var id in _previews.Where(pair => now - pair.Value.Created > PreviewLifetime).Select(pair => pair.Key).ToArray())
            RemovePreview(id);
    }

    private void RemovePreview(string id)
    {
        if (_previews.Remove(id, out var removed)) _previewCacheBytes -= removed.FrozenBytes;
    }
}
