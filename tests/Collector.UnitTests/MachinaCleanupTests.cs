using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class MachinaCleanupTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void NativeCleanupFailuresAreObservableAndKeepTheSameMonitorForRetry(bool failStop, bool failDispose)
    {
        var native = new NativeMonitor { FailStop = failStop, FailDispose = failDispose };
        var created = 0;
        var source = new MachinaCaptureSource(_ => { created++; return native; });
        source.Start(new CaptureStartOptions("synthetic", 42, null, null, OodleMode.LibraryTcp, null, null), new Observer());
        try
        {
            Assert.ThrowsAny<Exception>(source.Stop);
            Assert.True(source.IsRunning);
            Assert.ThrowsAny<Exception>(source.Dispose);
            Assert.True(source.IsRunning);
            Assert.Equal(1, created);
        }
        finally { native.FailStop = native.FailDispose = false; source.Dispose(); }
        Assert.False(source.IsRunning);
        Assert.True(native.Released);
        Assert.True(native.StopCalls >= (failStop ? 2 : 1));
        Assert.True(native.DisposeCalls >= 1);
    }

    /// <summary>
    /// A registered temp copy must survive this process's death, since being killed is the
    /// ordinary way it ends. The manifest lets the next Collector finish the deletion
    /// (review finding H-2).
    /// </summary>
    [Fact]
    public void RegisteredTempCopiesArePersistedAndReclaimedByTheNextStart()
    {
        using var root = new TempRoot();
        var manifest = Path.Combine(root.Path, "oodle-temp.json");
        var machinaTemp = Path.Combine(root.Path, "temp", OodleTempCopyCleaner.MachinaTempFolderName);
        Directory.CreateDirectory(machinaTemp);
        var copy = Path.Combine(machinaTemp, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(copy, new byte[2048]);

        var cleaner = new OodleTempCopyCleaner(Path.Combine(root.Path, "temp"), manifestPath: manifest);
        cleaner.Arm(@"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
        cleaner.RememberPathField(new PathHolder(copy), typeof(PathHolder).GetField("Path"));

        // Written the moment the path is known, not at shutdown: shutdown is the step that
        // cannot be relied on.
        Assert.Equal(new[] { copy }, OodleTempCopyCleaner.ReadManifest(manifest));

        var reading = OodleTempCopyCleaner.InspectManifest(manifest);
        Assert.Equal(1, reading.Present);
        Assert.Equal(2048, reading.Bytes);
        Assert.Equal(0, reading.Missing);

        // A new process with no memory of the first: the manifest is its only evidence, and
        // it is sufficient.
        Assert.Equal(1, OodleTempCopyCleaner.SweepManifest(manifest, Path.Combine(root.Path, "temp")));
        Assert.False(File.Exists(copy));
        Assert.Empty(OodleTempCopyCleaner.ReadManifest(manifest));
        Assert.Equal(OodleTempManifestReading.Empty, OodleTempCopyCleaner.InspectManifest(manifest));
    }

    /// <summary>
    /// A manifest entry outside Machina's own temp subdirectory is never deleted, whatever the
    /// manifest says. The manifest establishes what this software created; it does not license
    /// deleting an arbitrary path (docs/privacy-boundary.md section 4.2).
    /// </summary>
    [Fact]
    public void SweepRefusesAnyRegisteredPathOutsideMachinasOwnTempFolder()
    {
        using var root = new TempRoot();
        var manifest = Path.Combine(root.Path, "oodle-temp.json");
        var elsewhere = Path.Combine(root.Path, "important.exe");
        File.WriteAllBytes(elsewhere, new byte[16]);
        File.WriteAllText(
            manifest,
            "{\"version\":1,\"paths\":[" +
            System.Text.Json.JsonSerializer.Serialize(elsewhere) + "]}");

        Assert.Equal(0, OodleTempCopyCleaner.SweepManifest(manifest, Path.Combine(root.Path, "temp")));
        Assert.True(File.Exists(elsewhere));
    }

    /// <summary>A missing or malformed manifest reads as nothing, and deletes nothing.</summary>
    [Fact]
    public void AnUnreadableManifestIsSilentlyEmpty()
    {
        using var root = new TempRoot();
        var missing = Path.Combine(root.Path, "absent.json");
        Assert.Empty(OodleTempCopyCleaner.ReadManifest(missing));
        Assert.Equal(0, OodleTempCopyCleaner.SweepManifest(missing));
        Assert.Equal(OodleTempManifestReading.Empty, OodleTempCopyCleaner.InspectManifest(missing));

        var garbage = Path.Combine(root.Path, "garbage.json");
        File.WriteAllText(garbage, "not json at all");
        Assert.Empty(OodleTempCopyCleaner.ReadManifest(garbage));
        Assert.Equal(0, OodleTempCopyCleaner.SweepManifest(garbage));
    }

    /// <summary>Field holder used to hand the cleaner one exact path, as Machina's native object does.</summary>
    private sealed class PathHolder(string path)
    {
        public string Path = path;
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MentorRecorder.OodleManifest", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class NativeMonitor : IMachinaMonitor
    {
        public bool FailStop, FailDispose, Released;
        public bool FailStart = false;
        public int StopCalls, DisposeCalls;
        public void Start() { if (FailStart) throw new IOException("start fault"); }
        public void Stop() { StopCalls++; if (FailStop) throw new IOException("native stop fault"); }
        public void Dispose() { DisposeCalls++; if (FailDispose) throw new IOException("native dispose fault"); Released = true; }
        public void DetachCallbacks() { }
    }

    private sealed class Observer : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) { }
        public void OnDecodeError() { }
        public void OnFault(string reason, Exception? error) { }
    }
}
