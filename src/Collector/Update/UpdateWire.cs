using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// Renders the update check onto the wire, as <c>$defs/UpdateStatus</c> declares it.
///
/// The optional fields are omitted rather than sent as null, because a fresh install has never
/// checked and "absent" says that more honestly than a null version string. <c>release_url</c> is
/// always present: it is a constant of this build, and a Desktop that reads it unconditionally must
/// not have to guess it.
/// </summary>
public static class UpdateWire
{
    /// <summary>Builds a <c>$defs/UpdateStatus</c> object.</summary>
    /// <param name="snapshot">State to render.</param>
    public static JsonObject Status(UpdateCheckSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var status = new JsonObject
        {
            ["enabled"] = snapshot.Enabled,
            ["update_available"] = snapshot.UpdateAvailable,
            ["release_url"] = snapshot.ReleaseUrl,
        };

        if (snapshot.LatestVersion is { } latest)
        {
            status["latest_version"] = latest;
        }

        if (snapshot.LastCheckedAtUtc is { } checkedAt)
        {
            status["last_checked_at_utc"] = UtcTimestamp.ToText(checkedAt);
        }

        if (snapshot.LastOutcome is { } outcome)
        {
            status["last_outcome"] = outcome;
        }

        return status;
    }
}
