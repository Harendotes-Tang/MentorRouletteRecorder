using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Locally calibrated profiles on disk, beside the writer that makes them:
/// <c>&lt;root&gt;/&lt;cn|global&gt;/&lt;region&gt;.&lt;build&gt;.local.json</c>. Production root is
/// <see cref="Profiles.ProfileCatalog.LocalRootPath"/>.
///
/// <see cref="LocalProfileWriter"/> puts a profile there; this puts one away again. A profile
/// the machine's own traffic disproved is retired rather than deleted: it is the record of what
/// the software believed while it was recording, and a player who asks why an evening's records
/// are marked for review deserves to be able to see it. Retiring only takes the file out of the
/// catalogue's reach, which scans for <c>*.json</c> and nothing else.
/// </summary>
public static class LocalProfileFiles
{
    /// <summary>What a retired profile's name ends in; deliberately not <c>.json</c>.</summary>
    public const string RetiredSuffix = ".contradicted";

    /// <summary>Path of the local profile for a region and build under <paramref name="root"/>.</summary>
    /// <param name="root">Local profile directory.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    public static string PathFor(string root, Region region, string gameBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(
            Path.GetFullPath(root),
            LocalProfileWriter.RegionDirectory(region),
            LocalProfileWriter.ProfileIdFor(region, gameBuild) + ".json");
    }

    /// <summary>
    /// Moves the local profile of a region and build aside, replacing an older retirement of the
    /// same build. False when there was nothing to retire or the file system refused; never
    /// throws, because the one caller is a capture thread withdrawing a profile it must stop
    /// recording with whatever the disk answers.
    /// </summary>
    /// <param name="root">Local profile directory.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    public static bool Retire(string root, Region region, string gameBuild)
    {
        try
        {
            var path = PathFor(root, region, gameBuild);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Move(path, path + RetiredSuffix, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }
}
