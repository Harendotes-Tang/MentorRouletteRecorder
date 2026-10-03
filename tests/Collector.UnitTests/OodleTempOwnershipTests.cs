using System.Diagnostics;
using System.Reflection;
using Machina.FFXIV.Memory;
using Machina.FFXIV.Oodle;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class OodleTempOwnershipTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MentorRecorder.Tests", Guid.NewGuid().ToString("N"));
    private static readonly FieldInfo TempPath = typeof(OodleNative_Ffxiv).GetField("_libraryTempPath", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public OodleTempOwnershipTests() => Directory.CreateDirectory(Path.Combine(_directory, "Machina.FFXIV"));

    [Fact]
    public void EqualLengthExecutableFilesDoNotEstablishOwnership()
    {
        var cleaner = new OodleTempCopyCleaner(_directory);
        var game = Write("game.exe");
        cleaner.Arm(game);
        var unrelated = Write("unrelated.exe");
        var another = Write(Path.Combine("Machina.FFXIV", "another.exe"));
        Assert.Equal(0, cleaner.Sweep());
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(another));
    }

    [Fact]
    public void OnlyTheExactRegisteredNativePathIsRemoved()
    {
        var cleaner = new OodleTempCopyCleaner(_directory);
        cleaner.Arm(Write("game.exe"));
        var native = new OodleNative_Ffxiv(new SigScan());
        var owned = Write(Path.Combine("Machina.FFXIV", "owned.exe"));
        var stranger = Write(Path.Combine("Machina.FFXIV", "stranger.exe"));
        TempPath.SetValue(native, owned);
        cleaner.RememberNative(native);
        Assert.Equal(1, cleaner.Sweep());
        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(stranger));
    }

    [Fact]
    public void InitializationRollbackMayClearTheFieldWithoutLosingTheOwnedPath()
    {
        var cleaner = new OodleTempCopyCleaner(_directory);
        cleaner.Arm(Write("game.exe"));
        var native = new OodleNative_Ffxiv(new SigScan());
        var owned = Write(Path.Combine("Machina.FFXIV", "failed-initialization.exe"));
        Assert.Throws<IOException>(() => cleaner.TrackInitialization(native, () =>
        {
            TempPath.SetValue(native, owned);
            Trace.WriteLine("OodleNative_Ffxiv: simulated initialization failure");
            TempPath.SetValue(native, string.Empty);
            throw new IOException("initialization failed after copy");
        }));
        Assert.Equal(1, cleaner.Sweep());
        Assert.False(File.Exists(owned));
    }

    [Fact]
    public void MissingDependencyFieldOrOutsidePathNeverFallsBackToDirectoryScanning()
    {
        var cleaner = new OodleTempCopyCleaner(_directory);
        cleaner.Arm(Write("game.exe"));
        var native = new OodleNative_Ffxiv(new SigScan());
        var outside = Write("outside.exe");
        TempPath.SetValue(native, outside);
        cleaner.RememberNative(native);
        cleaner.RememberPathField(native, null);
        Assert.Equal(0, cleaner.Sweep());
        Assert.True(File.Exists(outside));
    }

    /// <summary>
    /// Review finding R-14. Emptying the manifest must remove the file on the running path as
    /// well as on the startup sweep: writing an empty array instead would make "no manifest"
    /// and "this software owns nothing" two different observable states.
    /// </summary>
    [Fact]
    public void EmptyingTheManifestRemovesTheFileOnTheRunningPathToo()
    {
        var manifest = Path.Combine(_directory, "oodle-temp.json");
        var cleaner = new OodleTempCopyCleaner(_directory, manifestPath: manifest);
        cleaner.Arm(Write("game.exe"));
        var native = new OodleNative_Ffxiv(new SigScan());
        var owned = Write(Path.Combine("Machina.FFXIV", "owned.exe"));
        TempPath.SetValue(native, owned);
        cleaner.RememberNative(native);

        Assert.True(File.Exists(manifest));
        Assert.Equal(new[] { owned }, OodleTempCopyCleaner.ReadManifest(manifest));

        Assert.Equal(1, cleaner.Sweep());

        Assert.False(File.Exists(owned));
        Assert.False(File.Exists(manifest));
        Assert.Equal(OodleTempManifestReading.Empty, OodleTempCopyCleaner.InspectManifest(manifest));
    }

    private string Write(string name)
    {
        var path = Path.Combine(_directory, name);
        var bytes = new byte[4096]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Audit 2026-10-03 OB-6. Stopping a capture used to delete every unlocked GUID-named copy in
    /// Machina's temp folder, registered or not: a copy another Machina-based program (ACT, for one)
    /// had just written and not yet loaded was deleted under it, and docs/privacy-boundary.md §4.2
    /// says ownership is never guessed from file names. A copy nobody registered is left alone.
    /// </summary>
    [Fact]
    public void StoppingACaptureLeavesACopyThisSoftwareNeverRegistered()
    {
        var folder = Path.Combine(_directory, OodleTempCopyCleaner.MachinaTempFolderName);
        var foreign = Path.Combine(folder, Guid.NewGuid().ToString("D") + ".exe");
        File.WriteAllBytes(foreign, new byte[2048]);
        using var source = new MachinaCaptureSource(cleaner: new OodleTempCopyCleaner(_directory));

        source.Stop();

        Assert.True(File.Exists(foreign), "a copy nobody registered is not this software's to delete");
    }

    /// <summary>
    /// The manifest is what lets a later start remove a copy a killed process could not, which only
    /// works if every copy stays in it until it is gone. Each capture start has its own cleaner, and
    /// a cleaner used to rewrite the shared manifest from its own set alone, erasing the entries of
    /// the one before - typically the copy that could not be deleted because it was still loaded.
    /// Entries are now added and removed, never replaced.
    /// </summary>
    [Fact]
    public void ACleanerNeverErasesAnotherCleanersEntriesFromTheManifest()
    {
        var manifest = Path.Combine(_directory, "oodle-temp.json");
        var earlier = new OodleTempCopyCleaner(_directory, manifestPath: manifest);
        var later = new OodleTempCopyCleaner(_directory, manifestPath: manifest);
        earlier.Arm(Write("game.exe"));
        later.Arm(Path.Combine(_directory, "game.exe"));
        var kept = Write(Path.Combine("Machina.FFXIV", "still-loaded.exe"));
        var removed = Write(Path.Combine("Machina.FFXIV", "released.exe"));

        var first = new OodleNative_Ffxiv(new SigScan());
        TempPath.SetValue(first, kept);
        earlier.RememberNative(first);
        var second = new OodleNative_Ffxiv(new SigScan());
        TempPath.SetValue(second, removed);
        later.RememberNative(second);

        Assert.Equal(new[] { removed, kept }.Order(StringComparer.OrdinalIgnoreCase),
            OodleTempCopyCleaner.ReadManifest(manifest).Order(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(1, later.Sweep());

        Assert.False(File.Exists(removed));
        Assert.Equal(new[] { kept }, OodleTempCopyCleaner.ReadManifest(manifest));
    }
}
