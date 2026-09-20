using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Remembering where the client is installed, so the build is known before the game runs.
///
/// Two halves: the file the memory keeps (it must survive nothing but its own contents, and
/// must never throw), and the locator's use of it (it remembers a running client and answers
/// from the memory when none is running, without ever claiming the game is up).
/// </summary>
public sealed class GameInstallMemoryTests : IDisposable
{
    private const string CnExecutable = @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe";
    private const string CnVersionFile = @"D:\SdoA\FFXIV\game\ffxivgame.ver";
    private const string Build = "2026.09.18.0000.0000";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "MentorRecorder.Tests", "install-memory-" + Guid.NewGuid().ToString("N"));

    private string MemoryPath => Path.Combine(_directory, DatabasePaths.GameInstallFileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Debris in the OS temp folder is not worth failing a test over.
        }
    }

    private static FakeGameFileReader VersionFile(string content = Build + "\n") =>
        new FakeGameFileReader().With(CnVersionFile, content);

    // ------------------------------------------------------------------ the file

    [Fact]
    public void File_RemembersAndRecallsTheExecutablePath()
    {
        var memory = new FileGameInstallMemory(MemoryPath);

        memory.Remember(CnExecutable);

        Assert.Equal(CnExecutable, memory.Recall());
        Assert.Equal(CnExecutable, new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Fact]
    public void File_WritesOnlyWhenTheValueChanged()
    {
        var memory = new FileGameInstallMemory(MemoryPath);
        memory.Remember(CnExecutable);
        var first = File.GetLastWriteTimeUtc(MemoryPath);

        // Every status poll while the game runs remembers the same path; rewriting the file
        // each time would be a write per second for the whole session.
        File.SetLastWriteTimeUtc(MemoryPath, first.AddDays(-1));
        memory.Remember(CnExecutable);

        Assert.Equal(first.AddDays(-1), File.GetLastWriteTimeUtc(MemoryPath));
    }

    [Fact]
    public void File_WritesWhenTheInstallMoved()
    {
        const string moved = @"E:\Games\FFXIV\game\ffxiv_dx11.exe";
        var memory = new FileGameInstallMemory(MemoryPath);
        memory.Remember(CnExecutable);

        memory.Remember(moved);

        Assert.Equal(moved, new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Fact]
    public void File_DoesNotRewriteWhatAnotherProcessAlreadyWrote()
    {
        new FileGameInstallMemory(MemoryPath).Remember(CnExecutable);
        var written = File.GetLastWriteTimeUtc(MemoryPath).AddDays(-1);
        File.SetLastWriteTimeUtc(MemoryPath, written);

        // A restart re-observes the same client: the file on disk already says so.
        new FileGameInstallMemory(MemoryPath).Remember(CnExecutable);

        Assert.Equal(written, File.GetLastWriteTimeUtc(MemoryPath));
    }

    [Fact]
    public void File_RecallsNothing_WhenThereIsNoFile() =>
        Assert.Null(new FileGameInstallMemory(MemoryPath).Recall());

    [Fact]
    public void File_RecallsNothing_FromAFileThatIsNotJson()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(MemoryPath, "{ this is not json");

        Assert.Null(new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Fact]
    public void File_RecallsNothing_FromAFileThatIsTooLarge()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            MemoryPath,
            "{\"executable_path\":\"" + CnExecutable.Replace(@"\", @"\\") + "\",\"pad\":\"" +
            new string('x', FileGameInstallMemory.MaxBytes) + "\"}");

        // A file this size is not one this software wrote; it is read as absent rather than
        // parsed, so an unbounded read can never happen.
        Assert.Null(new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Theory]
    [InlineData(@"D:\SdoA\FFXIV\game\notepad.exe")]
    [InlineData(@"D:\SdoA\FFXIV\game\ffxiv_dx11.exe.bat")]
    [InlineData(@"game\ffxiv_dx11.exe")]
    [InlineData(@"\game\ffxiv_dx11.exe")]
    [InlineData("")]
    [InlineData("   ")]
    // Reading the version there would be an outbound connection (docs/privacy-boundary.md section 8).
    [InlineData(@"\\fileserver\games\FFXIV\game\ffxiv_dx11.exe")]
    [InlineData(@"\\?\UNC\fileserver\games\FFXIV\game\ffxiv_dx11.exe")]
    [InlineData(@"\\.\pipe\ffxiv_dx11.exe")]
    [InlineData(@"D:\SdoA\FFXIV\game\notes.txt:ffxiv_dx11.exe")]
    public void File_RecallsNothing_ForAPathThatIsNotAnAbsoluteClientPath(string path)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(
            MemoryPath, "{\"executable_path\":" + System.Text.Json.JsonSerializer.Serialize(path) + "}");

        // The path is fed to a file read, so a value the user (or anything else) could have put
        // in this file must not be able to name something that is not a game client.
        Assert.Null(new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Theory]
    [InlineData(@"game\ffxiv_dx11.exe")]
    [InlineData(@"D:\SdoA\FFXIV\game\notepad.exe")]
    [InlineData(@"\\fileserver\games\FFXIV\game\ffxiv_dx11.exe")]
    public void File_RemembersNothing_ForAPathThatIsNotAnAbsoluteClientPath(string path)
    {
        new FileGameInstallMemory(MemoryPath).Remember(path);

        Assert.False(File.Exists(MemoryPath));
    }

    [Fact]
    public void File_AlsoAcceptsTheLegacyClient()
    {
        const string legacy = @"D:\SdoA\FFXIV\game\FFXIV.exe";
        var memory = new FileGameInstallMemory(MemoryPath);

        memory.Remember(legacy);

        Assert.Equal(legacy, new FileGameInstallMemory(MemoryPath).Recall());
    }

    [Fact]
    public void File_NeverThrows_WhenTheDirectoryCannotBeWritten()
    {
        // The parent is a file, so creating the directory and writing both fail. Remembering
        // where the game is must never be able to take capture down with it.
        Directory.CreateDirectory(_directory);
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var memory = new FileGameInstallMemory(Path.Combine(blocked, DatabasePaths.GameInstallFileName));

        memory.Remember(CnExecutable);

        Assert.Null(memory.Recall());
    }

    [Fact]
    public void File_IsInert_WithoutAPath()
    {
        var memory = FileGameInstallMemory.At(null);

        memory.Remember(CnExecutable);

        Assert.Null(memory.Recall());
    }

    [Fact]
    public void DatabasePaths_PutTheMemoryBesideTheDatabase()
    {
        var database = Path.Combine(_directory, "mentor_recorder.db");

        Assert.Equal(MemoryPath, DatabasePaths.ResolveGameInstallMemory(database));
        Assert.Equal(
            Path.Combine(DatabasePaths.RootDirectory, DatabasePaths.GameInstallFileName),
            DatabasePaths.ResolveGameInstallMemory(null));
        Assert.Equal("game-install.json", DatabasePaths.GameInstallFileName);
    }

    // ------------------------------------------------------------------ the locator

    [Fact]
    public void Locator_IsInertByDefault()
    {
        // A locator a test builds itself must never read or write the real data directory.
        var locator = new GameProcessLocator(
            new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 42, null, CnExecutable),
            VersionFile());

        Assert.True(locator.Locate().Running);
        Assert.False(File.Exists(Path.Combine(DatabasePaths.RootDirectory, DatabasePaths.GameInstallFileName)));
    }

    [Fact]
    public void Locator_RemembersTheInstall_WhenTheGameIsRunning()
    {
        var memory = new RecordingInstallMemory();
        var locator = new GameProcessLocator(
            new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 42, null, CnExecutable),
            VersionFile()).WithInstallMemory(memory);

        locator.Locate();

        Assert.Equal(CnExecutable, memory.Remembered);
    }

    [Fact]
    public void Locator_RemembersNothing_WhenThePathCannotBeRead()
    {
        var memory = new RecordingInstallMemory();
        var locator = new GameProcessLocator(
            new FakeGameProcessProvider().Add(
                GameProcessLocator.Dx11ProcessName, 42, null, path: null, accessDenied: true),
            VersionFile()).WithInstallMemory(memory);

        locator.Locate();

        Assert.Null(memory.Remembered);
    }

    [Fact]
    public void Locator_ReadsTheBuildFromTheRememberedInstall_WhenTheGameIsNotRunning()
    {
        var locator = new GameProcessLocator(new FakeGameProcessProvider(), VersionFile())
            .WithInstallMemory(new RecordingInstallMemory { Stored = CnExecutable });

        var detection = locator.Locate();

        // The build and the region become known, and nothing else does: the game is not up,
        // so no process is claimed and the path is not carried out of here.
        Assert.False(detection.Running);
        Assert.Null(detection.ProcessId);
        Assert.Null(detection.ProcessName);
        Assert.Null(detection.StartedAtUtc);
        Assert.Equal(0, detection.InstanceCount);
        Assert.Null(detection.ExecutablePath);
        Assert.Equal(Build, detection.GameBuild);
        Assert.Equal(Region.Cn, detection.Region);
        Assert.Empty(detection.Warnings);
    }

    [Fact]
    public void Locator_AppliesTheRegionOverride_ToARememberedInstall()
    {
        const string unmarked = @"D:\Games\FF14\game\ffxiv_dx11.exe";
        var files = new FakeGameFileReader().With(@"D:\Games\FF14\game\ffxivgame.ver", Build);
        var locator = new GameProcessLocator(new FakeGameProcessProvider(), files)
            .WithInstallMemory(new RecordingInstallMemory { Stored = unmarked })
            .WithRegionOverride(() => Region.Global);

        var detection = locator.Locate();

        Assert.Equal(Region.Global, detection.Region);
        Assert.Equal(Build, detection.GameBuild);
    }

    [Fact]
    public void Locator_CarriesTheMemoryAndTheOverrideAcrossEachOther()
    {
        var memory = new RecordingInstallMemory { Stored = CnExecutable };
        var files = VersionFile();

        var overrideFirst = new GameProcessLocator(new FakeGameProcessProvider(), files)
            .WithRegionOverride(() => Region.Global)
            .WithInstallMemory(memory);
        var memoryFirst = new GameProcessLocator(new FakeGameProcessProvider(), files)
            .WithInstallMemory(memory)
            .WithRegionOverride(() => Region.Global);

        foreach (var detection in new[] { overrideFirst.Locate(), memoryFirst.Locate() })
        {
            Assert.Equal(Build, detection.GameBuild);
            Assert.Equal(Region.Global, detection.Region);
        }
    }

    [Fact]
    public void Locator_StaysNotRunning_WhenTheRememberedInstallHasNoReadableVersion()
    {
        var locator = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader())
            .WithInstallMemory(new RecordingInstallMemory { Stored = CnExecutable });

        var detection = locator.Locate();

        // The install moved or was uninstalled: the build is unknown again, which is the
        // fail-closed answer, and the memory is not erased over it.
        Assert.False(detection.Running);
        Assert.Null(detection.GameBuild);
        Assert.Equal(Region.Unknown, detection.Region);
        Assert.Empty(detection.Warnings);
    }

    [Fact]
    public void Locator_StaysNotRunning_WhenTheVersionFileIsNotAPlainToken()
    {
        var locator = new GameProcessLocator(new FakeGameProcessProvider(), VersionFile("这不是版本号"))
            .WithInstallMemory(new RecordingInstallMemory { Stored = CnExecutable });

        Assert.Null(locator.Locate().GameBuild);
    }

    [Fact]
    public void Locator_SurvivesAMemoryThatThrows()
    {
        var running = new GameProcessLocator(
                new FakeGameProcessProvider().Add(GameProcessLocator.Dx11ProcessName, 42, null, CnExecutable),
                VersionFile())
            .WithInstallMemory(new ThrowingInstallMemory());
        var stopped = new GameProcessLocator(new FakeGameProcessProvider(), VersionFile())
            .WithInstallMemory(new ThrowingInstallMemory());

        Assert.True(running.Locate().Running);
        Assert.False(stopped.Locate().Running);
        Assert.Null(stopped.Locate().GameBuild);
    }

    private sealed class RecordingInstallMemory : IGameInstallMemory
    {
        public string? Stored { get; init; }

        public string? Remembered { get; private set; }

        public string? Recall() => Stored;

        public void Remember(string executablePath) => Remembered = executablePath;
    }

    private sealed class ThrowingInstallMemory : IGameInstallMemory
    {
        public string? Recall() => throw new InvalidOperationException("the memory is broken");

        public void Remember(string executablePath) =>
            throw new InvalidOperationException("the memory is broken");
    }
}
