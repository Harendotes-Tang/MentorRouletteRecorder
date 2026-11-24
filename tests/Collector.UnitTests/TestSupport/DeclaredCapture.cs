using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A capture controller on a declared machine: healthy Npcap, a verified profile, the CN client's build file
/// readable, detection never cached, no follow timer, sessions kept in a test database.
/// </summary>
internal static class DeclaredCapture
{
    /// <summary>The CN client's executable.</summary>
    internal const string CnExecutable = @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe";

    /// <summary>The build file beside <see cref="CnExecutable"/>.</summary>
    internal const string CnVersionFile = @"D:\SdoA\FFXIV\game\ffxivgame.ver";

    /// <summary>The build <see cref="CnVersionFile"/> holds.</summary>
    internal const string CnBuild = "2024.06.18.0000.0000";

    /// <param name="files">The game's files; only <see cref="CnVersionFile"/> when null.</param>
    internal static CaptureServices Services(
        TestDatabase database,
        SettingsRepository settings,
        IGameProcessProvider processes,
        IAdapterProvider adapters,
        IProcessTcpTable tcp,
        Func<ICaptureSource> sourceFactory,
        IGameFileReader? files = null) => new()
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(processes, files ?? new FakeGameFileReader().With(CnVersionFile, CnBuild)),
        Adapters = new AdapterEnumerator(adapters, tcp),
        SourceFactory = sourceFactory,
        Profile = new FakeProfileStatusProvider(ProfileStatus.Verified),
        Clock = database.Clock,
        Settings = settings,
        Sessions = new CaptureSessionRepository(database.Database),
        Database = database.Database,
        CollectorVersion = "0.1.0",
        DetectionTtl = TimeSpan.Zero,
        EnableFollowTimer = false,
    };

    /// <summary>The first snapshot <paramref name="predicate"/> accepts, read every 10 ms for up to 10 s.</summary>
    internal static CaptureDiagnosticsSnapshot WaitFor(
        CaptureController controller, Func<CaptureDiagnosticsSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = controller.Snapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("the capture controller never reached the expected state");
    }
}
