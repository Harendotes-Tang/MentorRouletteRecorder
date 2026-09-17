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

    [Fact]
    public void SweepingMachinasFolderRemovesCopiesThisProcessNeverRegistered()
    {
        // A copy left by a faulted capture start is registered nowhere: Machina writes it, the
        // install throws, and the cleaner never sees the path. The sweep must remove such
        // orphans; each is tens of megabytes and one more accumulates per retry.
        var temp = Path.Combine(Path.GetTempPath(), "MentorRecorder.Sweep", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(temp, OodleTempCopyCleaner.MachinaTempFolderName);
        Directory.CreateDirectory(folder);
        var orphan = Path.Combine(folder, Guid.NewGuid().ToString("D") + ".exe");
        var other = Path.Combine(folder, "notes.txt");
        File.WriteAllBytes(orphan, new byte[2048]);
        File.WriteAllText(other, "not a copy");
        try
        {
            var before = OodleTempCopyCleaner.InspectOrphans(temp);
            Assert.Equal(1, before.Removed);
            Assert.Equal(2048, before.Bytes);

            var swept = OodleTempCopyCleaner.SweepOrphans(temp);

            Assert.Equal(1, swept.Removed);
            Assert.Equal(2048, swept.Bytes);
            Assert.Equal(0, swept.Locked);
            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(other), "only the executable copies are ours to delete");
            Assert.Equal(0, OodleTempCopyCleaner.SweepOrphans(temp).Removed);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void ACopyAnotherProcessHoldsOpenIsCountedNotForced()
    {
        var temp = Path.Combine(Path.GetTempPath(), "MentorRecorder.Sweep", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(temp, OodleTempCopyCleaner.MachinaTempFolderName);
        Directory.CreateDirectory(folder);
        var held = Path.Combine(folder, Guid.NewGuid().ToString("D") + ".exe");
        File.WriteAllBytes(held, new byte[16]);
        try
        {
            using var handle = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);

            var swept = OodleTempCopyCleaner.SweepOrphans(temp);

            Assert.Equal(0, swept.Removed);
            Assert.Equal(1, swept.Locked);
            Assert.True(File.Exists(held));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SweepingATemporaryFolderThatDoesNotExistIsSilent()
    {
        var swept = OodleTempCopyCleaner.SweepOrphans(
            Path.Combine(Path.GetTempPath(), "MentorRecorder.Sweep", Guid.NewGuid().ToString("N")));

        Assert.Equal(OodleTempSweepReading.Empty, swept);
    }
}
