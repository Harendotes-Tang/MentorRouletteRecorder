using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Retiring a local profile the machine's own traffic disproved. The file is never deleted -
/// it is the record of what the software believed while it recorded - and the catalogue must
/// stop reading it.
/// </summary>
public sealed class LocalProfileFilesTests : IDisposable
{
    private const string Build = "2026.09.01.0000.0000";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"), "protocol-profiles");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string WriteProfileFile(string contents = "{}")
    {
        var path = LocalProfileFiles.PathFor(_root, Region.Cn, Build);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    /// <summary>The file moves aside under a name the catalogue's <c>*.json</c> scan cannot see.</summary>
    [Fact]
    public void RetiringRenamesTheProfileBesideItself()
    {
        var path = WriteProfileFile();

        Assert.True(LocalProfileFiles.Retire(_root, Region.Cn, Build));

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + LocalProfileFiles.RetiredSuffix));
        Assert.NotEqual(".json", Path.GetExtension(path + LocalProfileFiles.RetiredSuffix));
    }

    /// <summary>A retired profile is invisible to the merged catalogue, so nothing selects it again.</summary>
    [Fact]
    public void ARetiredProfileIsNoLongerLoaded()
    {
        var written = LocalProfileWriter.Write(
            CalibrationTrafficCases.Derive(CalibrationTrafficCases.Announcement),
            CalibrationObserverTests.Template(), Build, new DateTimeOffset(2026, 9, 9, 12, 30, 0, TimeSpan.Zero), _root);
        Assert.NotEmpty(ProfileCatalog.LoadMerged(null, _root, null).Entries);

        Assert.True(LocalProfileFiles.Retire(_root, Region.Cn, Build));

        Assert.Empty(ProfileCatalog.LoadMerged(null, _root, null).Entries);
        Assert.True(File.Exists(written.Path + LocalProfileFiles.RetiredSuffix));
    }

    /// <summary>Retiring twice replaces the older retirement instead of failing on it.</summary>
    [Fact]
    public void RetiringAgainOverwritesTheOlderRetirement()
    {
        WriteProfileFile("first");
        Assert.True(LocalProfileFiles.Retire(_root, Region.Cn, Build));
        WriteProfileFile("second");

        Assert.True(LocalProfileFiles.Retire(_root, Region.Cn, Build));

        Assert.Equal("second", File.ReadAllText(
            LocalProfileFiles.PathFor(_root, Region.Cn, Build) + LocalProfileFiles.RetiredSuffix));
    }

    /// <summary>Nothing to retire is not a failure: the withdrawal must go through either way.</summary>
    [Fact]
    public void RetiringWhatIsNotThereIsANoOp()
    {
        Assert.False(LocalProfileFiles.Retire(_root, Region.Cn, Build));
        Assert.False(Directory.Exists(_root));
    }

    /// <summary>
    /// Every refusal the file system can answer with - a locked file, a root that is not a path,
    /// a region no local profile can be written for - is an answer, never an exception on the
    /// capture thread.
    /// </summary>
    [Fact]
    public void RetiringNeverThrows()
    {
        var path = WriteProfileFile();
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.False(LocalProfileFiles.Retire(_root, Region.Cn, Build));
        Assert.False(LocalProfileFiles.Retire(_root, Region.Unknown, Build));
        Assert.False(LocalProfileFiles.Retire(_root, Region.Cn, "   "));
        Assert.False(LocalProfileFiles.Retire("   ", Region.Cn, Build));
    }
}
