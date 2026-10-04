using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// The update requests a user can make by hand (docs/privacy-boundary.md §8.4):
/// <list type="bullet">
/// <item>立即检查更新 (<c>CheckUpdateNow</c>), answered off the connection's read loop, because it waits on the same
/// single <c>GET</c> the daily check sends; the setting and the kill switch are honoured exactly as for that check.</item>
/// <item>下载并安装 (<c>StartUpdateDownload</c>) and its 取消 (<c>CancelUpdateDownload</c>), answered at once on the
/// read loop: the download runs on its own background task and is followed through <c>GetStatus</c>.</item>
/// </list>
/// Every answer carries the cached status the next <c>GetStatus</c> would report too, the download included.
/// </summary>
public static class UpdateHandlers
{
    /// <summary>Handles <c>CheckUpdateNow</c>.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload; must be empty.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    public static async Task<JsonObject> CheckNowAsync(CollectorHost host, PayloadReader reader, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        var outcome = await host.Updates.CheckNowIfAllowedAsync(cancellationToken).ConfigureAwait(false);
        return CheckNowResponse(outcome, host.Updates.Snapshot(), host.UpdateDownloads.Snapshot());
    }

    /// <summary>The <c>CheckUpdateNow</c> response: the outcome token and the status as it now stands.</summary>
    /// <param name="outcome">What the request came to.</param>
    /// <param name="snapshot">The update check after it.</param>
    /// <param name="download">The download as it stands.</param>
    public static JsonObject CheckNowResponse(
        UpdateCheckRequestOutcome outcome, UpdateCheckSnapshot snapshot, UpdateDownloadSnapshot download)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonObject
        {
            ["outcome"] = EnumWire<UpdateCheckRequestOutcome>.Format(outcome),
            ["update"] = UpdateWire.Status(snapshot, download),
        };
    }

    /// <summary>
    /// Handles <c>StartUpdateDownload</c>: starts the download of <c>latest_version</c>, or says why there is nothing
    /// to start, and answers with the status as it now stands. Never waits for the download.
    /// </summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload: <c>reinstall</c>, optional.</param>
    public static JsonObject StartDownload(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RejectUnknown("reinstall");
        var download = host.UpdateDownloads.Start(reader.Bool("reinstall") ?? false);
        return Response(host, download);
    }

    /// <summary>Handles <c>CancelUpdateDownload</c>: stops a running download; changes nothing otherwise.</summary>
    /// <param name="host">Collector host.</param>
    /// <param name="reader">Request payload; must be empty.</param>
    public static JsonObject CancelDownload(CollectorHost host, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reader);
        reader.RequireEmpty();
        return Response(host, host.UpdateDownloads.Cancel());
    }

    private static JsonObject Response(CollectorHost host, UpdateDownloadSnapshot download) =>
        new() { ["update"] = UpdateWire.Status(host.Updates.Snapshot(), download) };
}
