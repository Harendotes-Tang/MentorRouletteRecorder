using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A validation controller on a declared machine: the CN client with build 2026.01, healthy Npcap, one "wifi"
/// adapter at 192.168.1.2, a game holding no connection yet, polled every 10 ms. Import with <c>using static</c>.
/// </summary>
internal static class CaptureValidationTestKit
{
    /// <summary>The CN client the validation finds: running at <paramref name="pid"/>, identity readable.</summary>
    internal static GameProcessDetection Game(int pid = 42) => new(true, pid, "ffxiv_dx11", DateTimeOffset.UnixEpoch,
        Region.Cn, "2026.01", 1, @"D:\SdoA\game\ffxiv_dx11.exe", Array.Empty<UserText>());

    /// <param name="game">What the validation finds; <see cref="Game"/> when null.</param>
    /// <param name="source">The source every session gets; a fresh one per session when null.</param>
    /// <param name="tcp">The game's connection count; none when null.</param>
    /// <param name="adapters">The adapters; one up "wifi" adapter at 192.168.1.2 when null.</param>
    /// <param name="writer">Opens the trace file; the real writer when null.</param>
    internal static CaptureValidationServices Services(Func<GameProcessDetection>? game = null,
        ICaptureSource? source = null, Func<int, IPAddress?, int?>? tcp = null, FakeAdapterProvider? adapters = null,
        Func<string, TextWriter>? writer = null) => new()
    {
        LocateGame = game ?? (() => Game()),
        PollInterval = TimeSpan.FromMilliseconds(10),
        Trace = new CaptureTraceServices
        {
            Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
            Adapters = new AdapterEnumerator(
                adapters ?? new FakeAdapterProvider().Add("wifi", "WiFi", addresses: "192.168.1.2"),
                new FakeProcessTcpTable()),
            SourceFactory = () => source ?? new FakeCaptureSource(),
            TcpConnectionCounter = tcp ?? ((_, _) => 0),
            TraceWriterFactory = writer,
        },
    };

    /// <summary>Waits up to 5 s for a snapshot field to read <paramref name="value"/>; fails showing the snapshot.</summary>
    internal static void Until(CaptureValidationController controller, string field, string value) =>
        Assert.True(
            SpinWait.SpinUntil(() => controller.Snapshot()[field]!.GetValue<string>() == value, 5000),
            controller.Snapshot().ToJsonString());

    /// <summary>Waits up to 5 s for a counter in the snapshot to reach <paramref name="value"/>.</summary>
    internal static void Until(CaptureValidationController controller, string field, long value) =>
        Assert.True(
            SpinWait.SpinUntil(() => controller.Snapshot()[field]?.GetValue<long>() == value, 5000),
            controller.Snapshot().ToJsonString());
}
