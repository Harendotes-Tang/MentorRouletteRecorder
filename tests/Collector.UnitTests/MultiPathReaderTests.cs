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
        public int FailDisposeCount;
        public Action? OnRead;
        public void Open() { Opens++; if (FailOpen) throw new IOException("Synthetic partial open failure."); }
        public bool Read(NpcapPacketReceiver receive)
        {
            Interlocked.Increment(ref Reads);
            Volatile.Read(ref OnRead)?.Invoke();
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
