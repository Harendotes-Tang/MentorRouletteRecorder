using System.Text;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Shared profiles on disk: <c>&lt;root&gt;/&lt;cn|global&gt;/&lt;region&gt;.&lt;build&gt;.shared.json</c>.
/// Production root is
/// <see cref="Profiles.ProfileCatalog.SharedRootPath"/>. One file per region and build; writing again
/// replaces it atomically, and nothing but a verified <see cref="SharedProfileBuildResult"/> is written.
/// </summary>
public static class SharedProfileFiles
{
    /// <summary>Path of the shared profile for a region and build under <paramref name="root"/>.</summary>
    /// <param name="root">Shared profile directory.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    public static string PathFor(string root, Region region, string gameBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return Path.Combine(
            Path.GetFullPath(root), LocalProfileWriter.RegionDirectory(region), SharedProfileBuilder.ProfileIdFor(region, gameBuild) + ".json");
    }

    /// <summary>Writes a built shared profile atomically and returns its path.</summary>
    /// <param name="built">A <see cref="SharedProfileBuildStatus.Built"/> result.</param>
    /// <param name="root">Shared profile directory.</param>
    public static string Write(SharedProfileBuildResult built, string root)
    {
        ArgumentNullException.ThrowIfNull(built);
        if (built is not { Status: SharedProfileBuildStatus.Built, Json: { } json, Profile: { } profile })
        {
            throw new InvalidOperationException("only a built shared profile can be written");
        }

        var path = PathFor(root, profile.Region, profile.GameBuild);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        // Not *.json, so a catalogue scanning the directory mid-write never reads half a file.
        var temporary = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return path;
    }

    /// <summary>Removes the shared profile for a region and build; false when there was none or it could not be removed.</summary>
    /// <param name="root">Shared profile directory.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build.</param>
    public static bool Delete(string root, Region region, string gameBuild)
    {
        var path = PathFor(root, region, gameBuild);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
