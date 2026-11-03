using System.Collections.Concurrent;
using System.Net;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class MultiPathReaderTests
{
    [Fact]
    public void OpensEachDeviceOnceAndPollsEveryDeviceWithItsOwnLinkType()
    {
        var readers = new Dictionary<string, Reader> { ["selected"] = new(101), ["virtual"] = new(1),
            [CapturePathPlan.LoopbackDeviceId] = new(0) };
        foreach (var reader in readers.Values) reader.Packets.Enqueue(new byte[] { 1 });
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => readers[binding.AdapterId]);
        Assert.Equal(CapturePathStatus.Unmeasured, multiplexed.PathStatus);
        multiplexed.Open();
        var links = new List<int>();
        Assert.True(multiplexed.Read((_, link) => links.Add(link)));
        Assert.Equal(new[] { 101, 1, 0 }, links);
        Assert.False(multiplexed.Read((_, _) => Assert.Fail("No extra native packet exists.")));
        Assert.All(readers.Values, reader => { Assert.Equal(1, reader.Opens); Assert.Equal(2, reader.Reads); });
        Assert.Equal(new CapturePathStatus(3, 3, 0, false, true), multiplexed.PathStatus);
    }

    [Fact]
    public void OptionalFailureClosesItsPartialOpenAndReportsPartialCoverage()
    {
        var selected = new Reader(101);
        var optional = new Reader(1) { FailOpen = true };
        var loopback = new Reader(0);
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => binding.AdapterId switch
        { "selected" => selected, "virtual" => optional, _ => loopback });

        multiplexed.Open();
        Assert.Equal(1, optional.Disposals);
        Assert.Equal(new CapturePathStatus(3, 2, 1, false, true), multiplexed.PathStatus);
        multiplexed.Read((_, _) => { });
        Assert.Equal(0, optional.Reads);
        Assert.Equal(1, selected.Reads);
        Assert.Equal(1, loopback.Reads);
    }

    [Fact]
    public void RequiredFailureRefusesCaptureAndDoesNotOpenFallbacks()
    {
        var selected = new Reader(101) { FailOpen = true };
        var created = new List<string>();
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding =>
        { created.Add(binding.AdapterId); return selected; });

        Assert.Throws<IOException>(multiplexed.Open);
        Assert.Equal(new[] { "selected" }, created);
        Assert.Equal(1, selected.Disposals);
        Assert.Equal(new CapturePathStatus(3, 0, 1, false, false), multiplexed.PathStatus);
    }

    [Fact]
    public void FailedOptionalCleanupRefusesStartupAndRetainsEveryResourceForRetry()
    {
        var selected = new Reader(101);
        var optional = new Reader(1) { FailOpen = true, FailDisposeCount = 2 };
        var loopback = new Reader(0);
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => binding.AdapterId switch
        { "selected" => selected, "virtual" => optional, _ => loopback });
        Assert.Throws<AggregateException>(multiplexed.Open);
        Assert.Equal(0, selected.Disposals);
        Assert.Equal(0, loopback.Opens);
        Assert.Throws<AggregateException>(multiplexed.Dispose);
        Assert.Equal(1, selected.Disposals);
        Assert.Equal(2, optional.Disposals);
        multiplexed.Dispose();
        Assert.Equal(3, optional.Disposals);
        Assert.Equal(1, selected.Disposals); // Successfully closed resources are not closed again.
    }

    [Fact]
    public void ACloseFailureDoesNotPreventOtherPathsClosingAndOnlyTheRetainedPathIsRetried()
    {
        var readers = new[] { new Reader(101) { FailDisposeCount = 1 }, new Reader(1), new Reader(0) };
        var index = 0;
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), _ => readers[index++]);
        multiplexed.Open();
        Assert.Throws<AggregateException>(multiplexed.Dispose);
        Assert.All(readers, reader => Assert.Equal(1, reader.Disposals));
        multiplexed.Dispose();
        Assert.Equal(new[] { 2, 1, 1 }, readers.Select(reader => reader.Disposals));
        multiplexed.Dispose();
        Assert.Equal(new[] { 2, 1, 1 }, readers.Select(reader => reader.Disposals));
    }

    [Fact]
    public void EveryPathOpensAndDrainsBeforeTheSingleOodleInitializer()
    {
        var readers = new[] { new Reader(101), new Reader(1), new Reader(0) };
        var index = 0;
        var initializeCalls = 0;
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), _ => readers[index++]);
        using var monitor = new FirstPacketMonitor(Options(), (_, _, _, _) => { }, _ => { }, multiplexed,
            () => Array.Empty<Machina.Infrastructure.TCPConnection>(), () =>
            {
                Interlocked.Increment(ref initializeCalls);
                Assert.All(readers, reader => { Assert.Equal(1, reader.Opens); Assert.True(reader.Reads > 0); });
            });

        monitor.Prepare();
        Assert.Equal(0, initializeCalls);
        monitor.Start();
        Assert.Equal(1, initializeCalls);
        Assert.Equal(new CapturePathStatus(3, 3, 0, false, true), monitor.PathStatus);
        monitor.Stop();
        Assert.All(readers, reader => Assert.Equal(0, reader.Disposals));
        monitor.Dispose();
        Assert.All(readers, reader => Assert.Equal(1, reader.Disposals));
    }

    [Fact]
    public void StopTimeoutRetainsEveryNativePathUntilTheOneReaderWorkerCanJoin()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var readers = new[] { new Reader(101), new Reader(1), new Reader(0) };
        var index = 0;
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), _ => readers[index++]);
        var monitor = new FirstPacketMonitor(Options(), (_, _, _, _) => { }, _ => { }, multiplexed,
            () => Array.Empty<Machina.Infrastructure.TCPConnection>(), () => { }, TimeSpan.FromMilliseconds(100));
        try
        {
            monitor.Prepare(); monitor.Start();
            readers[1].OnRead = () => { entered.Set(); release.Wait(); };
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Throws<TimeoutException>(monitor.Dispose);
            Assert.All(readers, reader => Assert.Equal(0, reader.Disposals));
        }
        finally { release.Set(); monitor.Dispose(); }
        Assert.All(readers, reader => Assert.Equal(1, reader.Disposals));
    }

    /// <summary>
    /// Audit 2026-11-03, A1-2. An optional path that fails while reading -- a tunnel switched off,
    /// an adapter unplugged -- is closed and left out; the selected path keeps the capture alive.
    /// </summary>
    [Fact]
    public void AnOptionalPathThatFailsWhileReadingIsClosedAndTheOthersKeepReading()
    {
        var selected = new Reader(101);
        var optional = new Reader(1) { FailRead = true };
        var loopback = new Reader(0);
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => binding.AdapterId switch
        { "selected" => selected, "virtual" => optional, _ => loopback });
        multiplexed.Open();
        var links = new List<int>();

        selected.Packets.Enqueue(new byte[] { 1 });
        Assert.True(multiplexed.Read((_, link) => links.Add(link)));
        Assert.Equal(1, optional.Disposals);
        Assert.Equal(new CapturePathStatus(3, 2, 1, false, true), multiplexed.PathStatus);

        selected.Packets.Enqueue(new byte[] { 2 });
        Assert.True(multiplexed.Read((_, link) => links.Add(link)));
        Assert.Equal(1, optional.Reads);
        Assert.Equal(new[] { 101, 101 }, links);
    }

    [Fact]
    public void TheSelectedPathFailingWhileReadingStillEndsTheCapture()
    {
        var selected = new Reader(101) { FailRead = true };
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(),
            binding => binding.AdapterId == "selected" ? selected : new Reader(1));
        multiplexed.Open();

        Assert.Throws<IOException>(() => multiplexed.Read((_, _) => { }));
        Assert.Equal(0, selected.Disposals);
    }

    [Fact]
    public void AFailureHandlingAnOptionalPathsPacketIsNotBlamedOnThatPath()
    {
        var optional = new Reader(1);
        optional.Packets.Enqueue(new byte[] { 1 });
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(),
            binding => binding.AdapterId == "virtual" ? optional : new Reader(101));
        multiplexed.Open();

        Assert.Throws<InvalidOperationException>(() =>
            multiplexed.Read((_, _) => throw new InvalidOperationException("synthetic reassembly failure")));
        Assert.Equal(0, optional.Disposals);
        Assert.Equal(new CapturePathStatus(3, 3, 0, false, true), multiplexed.PathStatus);
    }

    [Fact]
    public void LostPacketsAreKeptPerPathAndOutliveAPathClosedWhileReading()
    {
        var selected = new Reader(101) { Dropped = 5 };
        var optional = new Reader(1) { Dropped = 7, FailRead = true };
        var loopback = new Reader(0) { Dropped = 11 };
        using var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => binding.AdapterId switch
        { "selected" => selected, "virtual" => optional, _ => loopback });
        multiplexed.Open();
        multiplexed.Read((_, _) => { });
        Assert.Equal(1, optional.Disposals);

        Assert.Equal(5, multiplexed.DroppedPacketsOn(binding => binding.AdapterId == "selected"));
        Assert.Equal(7, multiplexed.DroppedPacketsOn(binding => binding.AdapterId == "virtual"));
        Assert.Equal(0, multiplexed.DroppedPacketsOn(_ => false));
        Assert.Equal(23, multiplexed.DroppedPacketsOn(_ => true));
    }

    /// <summary>
    /// Audit 2026-11-03, A1-2. Loss on a path that never carried one of the game's connections --
    /// the loopback full of other programs' traffic, an idle tunnel -- says nothing about the game's
    /// stream, so it neither degrades the capture nor spoils its calibration evidence.
    /// </summary>
    [Fact]
    public void OnlyPathsThatCarriedAGameConnectionReportTheirLossAsTheCapturesLoss()
    {
        var readers = new Dictionary<string, Reader> { ["selected"] = new(101) { Dropped = 5 },
            ["virtual"] = new(1) { Dropped = 7 }, [CapturePathPlan.LoopbackDeviceId] = new(0) { Dropped = 11 } };
        var game = new Machina.Infrastructure.TCPConnection
        {
            LocalIP = FirstPacketTests.Address("192.0.2.10"), RemoteIP = FirstPacketTests.Address("203.0.113.5"),
            LocalPort = 50000, RemotePort = 55006, ProcessId = 42,
        };
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => readers[binding.AdapterId]);
        using var monitor = new FirstPacketMonitor(Options(), (_, _, _, _) => { }, _ => { }, multiplexed,
            () => new[] { game }, () => { });

        monitor.Prepare();
        monitor.Start();

        // Waits on the figure itself: the counters are read lock-free (re-review 2026-11-03, RV1-7).
        Assert.True(SpinWait.SpinUntil(() => monitor.Ingress.AdapterDropped == 5, TimeSpan.FromSeconds(5)),
            monitor.Ingress.ToString());
        Assert.Equal(1, monitor.Ingress.GameConnections);
        monitor.Stop();
    }

    /// <summary>
    /// Re-review 2026-11-03, RV1-1. A path that is not the selected one but carries the game -- the
    /// game logged in over a split-tunnel adapter or a local proxy after capture started at the title
    /// screen -- is as essential as the selected one: closed after a read failure, it would leave the
    /// game unreadable with nothing to say so. Its failure ends the capture, which restarts with every
    /// path open.
    /// </summary>
    [Fact]
    public void AnOptionalPathThatCarriesTheGameEndsTheCaptureWhenItFailsWhileReading()
    {
        var readers = new Dictionary<string, Reader> { ["selected"] = new(101), ["virtual"] = new(1),
            [CapturePathPlan.LoopbackDeviceId] = new(0) };
        var game = new Machina.Infrastructure.TCPConnection
        {
            LocalIP = FirstPacketTests.Address("10.0.0.1"), RemoteIP = FirstPacketTests.Address("203.0.113.5"),
            LocalPort = 50000, RemotePort = 55006, ProcessId = 42,
        };
        using var faulted = new ManualResetEventSlim();
        var multiplexed = new MultiplexedNpcapPacketReader(Options(), binding => readers[binding.AdapterId]);
        using var monitor = new FirstPacketMonitor(Options(), (_, _, _, _) => { }, _ => faulted.Set(), multiplexed,
            () => new[] { game }, () => { });

        monitor.Prepare();
        monitor.Start();
        Assert.True(SpinWait.SpinUntil(() => monitor.Ingress.GameConnections == 1, TimeSpan.FromSeconds(5)));
        readers["virtual"].FailRead = true;

        Assert.True(faulted.Wait(TimeSpan.FromSeconds(5)), "a failed path carrying the game was closed silently");
        Assert.Equal(0, readers["virtual"].Disposals);
        Assert.Equal(new CapturePathStatus(3, 3, 0, false, true), monitor.PathStatus);
    }

    [Fact]
    public void DuplicateNativeIdentitiesAreRefusedBeforeAnyOpen()
    {
        var options = Options();
        var duplicate = options.PathPlan!.Bindings[0];
        options = options with { PathPlan = options.PathPlan with { Bindings = new[] { duplicate, duplicate } } };
        var creates = 0;
        using var multiplexed = new MultiplexedNpcapPacketReader(options, _ => { creates++; return new Reader(101); });
        Assert.Throws<InvalidOperationException>(multiplexed.Open);
        Assert.Equal(0, creates);
    }

    private static CaptureStartOptions Options() => new("paths", 42, IPAddress.Parse("192.0.2.10"),
        "selected", OodleMode.LibraryTcp, null, null)
    {
        PathPlan = new(new[] {
            new CaptureInterfaceBinding("selected", new[] { IPAddress.Parse("192.0.2.10") }, false),
            new CaptureInterfaceBinding("virtual", new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2") }, false),
            new CaptureInterfaceBinding(CapturePathPlan.LoopbackDeviceId, new[] { IPAddress.Loopback }, true) }, true, false),
    };

    private sealed class Reader(int linkType) : INpcapPacketReader
    {
        public readonly ConcurrentQueue<byte[]> Packets = new();
        public int Opens, Reads, Disposals;
        public bool FailOpen;
        public volatile bool FailRead;
        public int FailDisposeCount;
        public long Dropped;
        public Action? OnRead;
        public long DroppedPackets => Volatile.Read(ref Dropped);
        public void Open() { Opens++; if (FailOpen) throw new IOException("Synthetic partial open failure."); }
        public bool Read(NpcapPacketReceiver receive)
        {
            Interlocked.Increment(ref Reads);
            Volatile.Read(ref OnRead)?.Invoke();
            if (FailRead) throw new IOException("Synthetic adapter removed while reading.");
            if (!Packets.TryDequeue(out var bytes)) return false;
            receive(bytes, linkType);
            return true;
        }
        public void Dispose()
        {
            Disposals++;
            if (Disposals <= FailDisposeCount) throw new IOException("Synthetic close failure.");
        }
    }
}
