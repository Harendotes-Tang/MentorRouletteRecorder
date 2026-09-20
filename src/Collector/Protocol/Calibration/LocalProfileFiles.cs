using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// Locally calibrated profiles on disk, beside the writer that makes them:
/// <c>&lt;root&gt;/&lt;cn|global&gt;/&lt;region&gt;.&lt;build&gt;.local.json</c>. Production root is
/// <see cref="Profiles.ProfileCatalog.LocalRootPath"/>.
///
/// <see cref="LocalProfileWriter"/> puts a profile there; this puts one away again. A profile
/// that goes out of use is retired rather than deleted: it is the record of what the software
/// believed while it was recording, and a player who asks why an evening's records are marked
/// for review - or who retired a profile and wants it back - deserves to be able to see it.
/// Retiring only takes the file out of the catalogue's reach, which scans for <c>*.json</c> and
/// nothing else.
///
/// The suffix says which of the two put it away, because the two mean opposite things to a
/// player reading their own profile directory: the traffic disproved this profile, or you asked
/// the software to stop using it.
/// </summary>
public static class LocalProfileFiles
{
    /// <summary>What a profile the machine's own traffic disproved ends in; deliberately not <c>.json</c>.</summary>
    public const string RetiredSuffix = ".contradicted";

    /// <summary>What a profile the player retired through 重新校准 ends in.</summary>
    public const string RetiredByRequestSuffix = ".retired";

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
    /// same build under the same suffix. False when there was nothing to retire or the file
    /// system refused; never throws, because the callers are a capture thread withdrawing a
    /// profile it must stop recording with whatever the disk answers, and a player waiting on a
    /// request that must go through either way.
    /// </summary>
    /// <param name="root">Local profile directory.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    /// <param name="suffix">Which retirement this is: <see cref="RetiredSuffix"/> or <see cref="RetiredByRequestSuffix"/>.</param>
    public static bool Retire(string root, Region region, string gameBuild, string suffix = RetiredSuffix)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
            var path = PathFor(root, region, gameBuild);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Move(path, path + suffix, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return false;
        }
    }
}
