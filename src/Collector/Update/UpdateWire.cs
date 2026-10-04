using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// Renders the update check onto the wire, as <c>$defs/UpdateStatus</c> declares it.
///
/// The optional fields are omitted rather than sent as null, because a fresh install has never
/// checked and "absent" says that more honestly than a null version string. <c>release_url</c> is
/// always present: it is a constant of this build, and a Desktop that reads it unconditionally must
/// not have to guess it. <c>installer_url</c> goes with <c>latest_version</c>: it names that version's
/// installer, for the user's browser to open. <c>download</c> is always present too, <c>IDLE</c> when
/// nothing was asked, so a Desktop can tell a Collector that knows the download from one that does not.
/// </summary>
public static class UpdateWire
{
    /// <summary>Builds a <c>$defs/UpdateStatus</c> object.</summary>
    /// <param name="snapshot">State to render.</param>
    /// <param name="download">The download as it stands.</param>
    public static JsonObject Status(UpdateCheckSnapshot snapshot, UpdateDownloadSnapshot download)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(download);

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

        if (snapshot.InstallerUrl is { } installer)
        {
            status["installer_url"] = installer;
        }

        if (snapshot.LastCheckedAtUtc is { } checkedAt)
        {
            status["last_checked_at_utc"] = UtcTimestamp.ToText(checkedAt);
        }

        if (snapshot.LastOutcome is { } outcome)
        {
            status["last_outcome"] = outcome;
        }

        status["download"] = Download(download);
        return status;
    }

    /// <summary>
    /// Builds a <c>$defs/UpdateDownload</c> object: the state, and each other field only when the snapshot has it
    /// (<see cref="UpdateDownloadSnapshot"/> gives every state exactly its own).
    /// </summary>
    /// <param name="download">State to render.</param>
    public static JsonObject Download(UpdateDownloadSnapshot download)
    {
        ArgumentNullException.ThrowIfNull(download);

        var rendered = new JsonObject { ["state"] = EnumWire<UpdateDownloadState>.Format(download.State) };
        if (download.Version is { } version)
        {
            rendered["version"] = version;
        }

        if (download.ReceivedBytes is { } received)
        {
            rendered["received_bytes"] = received;
        }

        if (download.TotalBytes is { } total)
        {
            rendered["total_bytes"] = total;
        }

        if (download.FilePath is { } path)
        {
            rendered["file_path"] = path;
        }

        if (download.Sha256 is { } sha256)
        {
            rendered["sha256"] = sha256;
        }

        if (download.Failure is { } failure)
        {
            rendered["failure"] = EnumWire<UpdateDownloadFailure>.Format(failure);
            rendered["message"] = download.Message;
        }

        return rendered;
    }
}
