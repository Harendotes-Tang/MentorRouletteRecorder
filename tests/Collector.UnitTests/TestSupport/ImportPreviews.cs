using System.Text.Json.Nodes;
using MentorRecorder.Collector.Import;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Reading and committing the previews a <see cref="RunImportService"/> returns.</summary>
internal static class ImportPreviews
{
    /// <summary>One row of a preview.</summary>
    /// <param name="preview">The preview.</param>
    /// <param name="index">Position of the row among the preview's rows.</param>
    public static JsonObject Row(JsonObject preview, int index = 0) => preview["rows"]![index]!.AsObject();

    /// <summary>The status of one row of a preview, such as <c>new</c> or <c>duplicate</c>.</summary>
    /// <param name="preview">The preview.</param>
    /// <param name="index">Position of the row among the preview's rows.</param>
    public static string Status(JsonObject preview, int index = 0) => Row(preview, index)["status"]!.GetValue<string>();

    /// <summary>Commits every row of a preview as the user's own records.</summary>
    /// <param name="service">The service that made the preview.</param>
    /// <param name="preview">The preview.</param>
    /// <param name="requestId">The commit's request id; a fresh one when null.</param>
    public static JsonObject CommitAll(RunImportService service, JsonObject preview, string? requestId = null) =>
        service.Commit(preview["preview_id"]!.GetValue<string>(),
            preview["rows"]!.AsArray().Select(row => row!["row_number"]!.GetValue<int>()).ToArray(),
            true, requestId ?? Guid.NewGuid().ToString("D"));
}
