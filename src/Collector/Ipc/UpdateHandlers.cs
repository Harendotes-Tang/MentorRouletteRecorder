using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// 立即检查更新 (<c>CheckUpdateNow</c>): the one update-check request a user can make by hand
/// (docs/privacy-boundary.md §8.4). Answered off the connection's read loop, because it waits on the
/// same single <c>GET</c> the daily check sends; the setting and the kill switch are honoured exactly
/// as for that check, and the answer is the cached status the next <c>GetStatus</c> would report too.
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
        return CheckNowResponse(outcome, host.Updates.Snapshot());
    }

    /// <summary>The <c>CheckUpdateNow</c> response: the outcome token and the status as it now stands.</summary>
    /// <param name="outcome">What the request came to.</param>
    /// <param name="snapshot">The update check after it.</param>
    public static JsonObject CheckNowResponse(UpdateCheckRequestOutcome outcome, UpdateCheckSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonObject
        {
            ["outcome"] = EnumWire<UpdateCheckRequestOutcome>.Format(outcome),
            ["update"] = UpdateWire.Status(snapshot),
        };
    }
}
