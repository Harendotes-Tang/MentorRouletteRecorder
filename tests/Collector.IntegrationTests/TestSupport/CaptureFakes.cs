using System.Net;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The smallest set of substitutes that lets the shipping IPC surface be driven through a whole
/// capture on a machine with no Npcap and no game.
///
/// Each one returns exactly what the test declared and holds no logic of its own, so a failing
/// assertion is about the Collector rather than the double.
/// </summary>
internal static class CaptureFakes
{
    /// <summary>No game, a fixed adapter, and an explicitly chosen Npcap installation state.</summary>
    /// <param name="npcapInstalled">True supplies a usable driver; false supplies no driver files or registry key.</param>
    public static CaptureServices NoGame(bool npcapInstalled = false) => new()
    {
        Npcap = new NpcapDetector(npcapInstalled
            ? new ReadyNpcapEnvironment()
            : new MissingNpcapEnvironment()),
        Game = new GameProcessLocator(new NoGameProcess(), new FixedGameFiles(null)),
        Adapters = new AdapterEnumerator(new OneAdapter(), new NoGameTcpTable()),
        SourceFactory = static () => throw new InvalidOperationException(
            "The no-game test environment must refuse capture before opening a native source."),
        CollectorVersion = Program.Version,
        EnableFollowTimer = false,
        DetectionTtl = TimeSpan.Zero,
    };

    /// <summary>
    /// No game process, but an install this machine remembers seeing: the shape the shipping
    /// service is in at startup, before the player launches the client.
    /// </summary>
    /// <param name="gameBuild">Build text the remembered install directory holds.</param>
    /// <param name="npcapInstalled">True supplies a usable driver, so capture refusals are about the game.</param>
    /// <param name="executablePath">Remembered path; its install folder identifies the region.</param>
    public static CaptureServices RememberedInstall(string gameBuild, bool npcapInstalled = true,
        string executablePath = RememberedExecutable) =>
        NoGame(npcapInstalled) with
    {
        Game = new GameProcessLocator(new NoGameProcess(), new FixedGameFiles(gameBuild))
            .WithInstallMemory(new RememberedGameInstall(executablePath)),
    };

    /// <summary>Where the fake install sits; a CN path, so the region is read from it.</summary>
    public const string RememberedExecutable = @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe";

    /// <summary>The one adapter's address, which carries the game's connections in <see cref="Ready"/>.</summary>
    public static readonly IPAddress AdapterAddress = IPAddress.Parse("192.168.31.77");

    /// <summary>Capture services that can start: Npcap ready, one game process, one adapter.</summary>
    /// <param name="source">Source the controller should use.</param>
    /// <param name="gameBuild">Build text returned from the fake game directory.</param>
    public static CaptureServices Ready(FakeCaptureSource source, string? gameBuild = null) => new()
    {
        Npcap = new NpcapDetector(new ReadyNpcapEnvironment()),
        Game = new GameProcessLocator(new OneGameProcess(), new FixedGameFiles(gameBuild)),
        Adapters = new AdapterEnumerator(new OneAdapter(), new GameTcpTable()),
        SourceFactory = () => source,
        Profile = new VerifiedProfile(),
        EnableFollowTimer = false,
        DetectionTtl = TimeSpan.Zero,
    };

    /// <summary>
    /// Validation services over <paramref name="capture"/>'s detectors: a 10 ms poll, sources from
    /// <paramref name="sourceFactory"/> and no game connection open before the session.
    /// </summary>
    /// <param name="capture">The capture services whose Npcap, game and adapter detectors the session uses.</param>
    /// <param name="sourceFactory">Creates the session's source.</param>
    public static CaptureValidationServices Validation(CaptureServices capture, Func<ICaptureSource> sourceFactory) => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        Trace = new CaptureTraceServices
        {
            Npcap = capture.Npcap, Game = capture.Game, Adapters = capture.Adapters,
            SourceFactory = sourceFactory, TcpConnectionCounter = (_, _) => 0,
        },
    };

    private sealed class ReadyNpcapEnvironment : INpcapEnvironment
    {
        public string SystemRoot => @"C:\Windows";

        public bool IsElevated => true;

        public bool FileExists(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        public string? FileVersion(string path) => "1.79";

        public bool RegistryKeyExists(string subKey) => subKey == NpcapDetector.RegistryKey;

        public object? RegistryValue(string subKey, string valueName) =>
            valueName == NpcapDetector.WinPcapCompatibleValue ? 1 : 0;
    }

    private sealed class MissingNpcapEnvironment : INpcapEnvironment
    {
        public string SystemRoot => @"C:\Windows";
        public bool IsElevated => false;
        public bool FileExists(string path) => false;
        public string? FileVersion(string path) => null;
        public bool RegistryKeyExists(string subKey) => false;
        public object? RegistryValue(string subKey, string valueName) => null;
    }

    private sealed class NoGameProcess : IGameProcessProvider
    {
        public IReadOnlyList<GameProcessCandidate> ByName(string processName) =>
            Array.Empty<GameProcessCandidate>();
    }

    private sealed class NoGameTcpTable : IProcessTcpTable
    {
        public IReadOnlyList<IPAddress> LocalAddresses(int processId) => Array.Empty<IPAddress>();
    }

    private sealed class OneGameProcess : IGameProcessProvider
    {
        public IReadOnlyList<GameProcessCandidate> ByName(string processName) =>
            processName == GameProcessLocator.Dx11ProcessName
                ? new[]
                {
                    new GameProcessCandidate(
                        4321,
                        processName,
                        DateTimeOffset.UnixEpoch,
                        RememberedExecutable,
                        AccessDenied: false),
                }
                : Array.Empty<GameProcessCandidate>();
    }

    private sealed class FixedGameFiles(string? gameBuild) : IGameFileReader
    {
        public string? ReadText(string path) => gameBuild;
    }

    /// <summary>An install memory that answers from memory, so no test writes into the data directory.</summary>
    private sealed class RememberedGameInstall(string executablePath) : IGameInstallMemory
    {
        public string? Recall() => executablePath;

        public void Remember(string path)
        {
            // Nothing is running in this environment, so nothing is ever remembered.
        }
    }

    private sealed class OneAdapter : IAdapterProvider
    {
        public IReadOnlyList<AdapterInfo> List() => new[]
        {
            new AdapterInfo(
                "{TEST-ADAPTER}",
                "测试网卡",
                "Test adapter",
                IsUp: true,
                IsLoopback: false,
                new[] { AdapterAddress }),
        };
    }

    private sealed class GameTcpTable : IProcessTcpTable
    {
        public IReadOnlyList<IPAddress> LocalAddresses(int processId) =>
            processId == 4321
                ? new[] { AdapterAddress }
                : Array.Empty<IPAddress>();
    }

    private sealed class VerifiedProfile : IProfileStatusProvider
    {
        public ProfileStatusSnapshot Current { get; } = new(
            Domain.ProfileStatus.Verified,
            "test-profile",
            Domain.Region.Cn,
            "2024.06.18.0000.0000",
            DateTimeOffset.UnixEpoch,
            "integration test fixture",
            null);
    }
}
