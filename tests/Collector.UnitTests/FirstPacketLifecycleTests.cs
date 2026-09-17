using System.Collections.Concurrent;
using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

public sealed class FirstPacketLifecycleTests
{
    private static CaptureStartOptions Options => new("synthetic", 42, IPAddress.Parse("192.0.2.10"),
        null, OodleMode.LibraryTcp, null, null);

    [Fact]
    public void PrepareDrainsBeforeOodleAndStartReplaysTheRealBundleExactlyOnce()
    {
        using var delivered = new ManualResetEventSlim();
        using var readAll = new ManualResetEventSlim();
        var reader = new Reader();
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 100, 2));
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 101, 24, FirstPacketTests.Bundle(0x1234)));
        reader.OnEmpty = readAll.Set;
        var count = 0;
        using var monitor = new FirstPacketMonitor(Options, (_, _, _, _) =>
            { Interlocked.Increment(ref count); delivered.Set(); }, _ => { }, reader,
            () => new[] { FirstPacketTests.Owned() }, () =>
            {
                Assert.True(readAll.Wait(TimeSpan.FromSeconds(2)));
                Assert.Equal(0, Volatile.Read(ref count));
                Assert.True(reader.Reads > 0);
            });
        monitor.Prepare();
        Assert.Equal(0, Volatile.Read(ref count));
        monitor.Start();
        Assert.True(delivered.Wait(TimeSpan.FromSeconds(2)));
        monitor.Stop();
        Assert.Equal(1, count);
        Assert.False(reader.Disposed);
        monitor.Dispose();
        Assert.True(reader.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceRollsBackPreparedReaderWhenOpenOrOodleInitializationFails(bool openFailure)
    {
        var reader = new Reader { FailOpen = openFailure };
        FirstPacketMonitor? monitor = null;
        using var source = new MachinaCaptureSource(options => monitor = new FirstPacketMonitor(options,
            (_, _, _, _) => { }, _ => { }, reader, () => Array.Empty<Machina.Infrastructure.TCPConnection>(),
            () => throw new IOException("synthetic Oodle initialization failure")));
        Assert.ThrowsAny<Exception>(() => source.Start(Options, new Observer()));
        Assert.False(source.IsRunning);
        Assert.True(reader.Disposed);
        var reads = reader.Reads;
        Assert.True(reader.OpenCalled);
        if (!openFailure) Assert.True(reads > 0);
        Assert.NotNull(monitor);
    }

    [Fact]
    public void StopTimeoutRetainsNativeResourcesAndBlocksRestartUntilReaderJoins()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new Reader();
        reader.OnEmpty = () => { if (reader.Reads > 1) { entered.Set(); release.Wait(); } };
        var monitor = new FirstPacketMonitor(Options, (_, _, _, _) => { }, _ => { }, reader,
            () => Array.Empty<Machina.Infrastructure.TCPConnection>(), () => { }, TimeSpan.FromMilliseconds(100));
        var source = new MachinaCaptureSource(_ => monitor);
        try
        {
            source.Start(Options, new Observer());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Throws<TimeoutException>(source.Stop);
            Assert.True(source.IsRunning);
            Assert.False(reader.Disposed);
            Assert.ThrowsAny<Exception>(() => source.Start(Options, new Observer()));
        }
        finally { release.Set(); source.Dispose(); }
        Assert.False(source.IsRunning);
        Assert.True(reader.Disposed);
    }

    [Fact]
    public void StopTimeoutAlsoRetainsResourcesWhileDecoderWorkerIsActive()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new Reader();
        var monitor = new FirstPacketMonitor(Options, (_, _, _, _) => { }, _ => { }, reader,
            () => { entered.Set(); release.Wait(); return Array.Empty<Machina.Infrastructure.TCPConnection>(); },
            () => { }, TimeSpan.FromMilliseconds(100));
        try
        {
            monitor.Prepare(); monitor.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.Throws<TimeoutException>(monitor.Dispose);
            Assert.False(reader.Disposed);
        }
        finally { release.Set(); monitor.Dispose(); }
        Assert.True(reader.Disposed);
    }

    [Fact]
    public void ReaderFailureStopsFutureDecodingAndReportsOneSanitizedFault()
    {
        using var failed = new ManualResetEventSlim();
        var reader = new Reader();
        var faults = new ConcurrentQueue<string>();
        using var monitor = new FirstPacketMonitor(Options, (_, _, _, _) => { }, reason =>
            { faults.Enqueue(reason); failed.Set(); }, reader,
            () => Array.Empty<Machina.Infrastructure.TCPConnection>(), () => { });
        monitor.Prepare(); monitor.Start();
        reader.FailRead = true;
        Assert.True(failed.Wait(TimeSpan.FromSeconds(2)));
        monitor.Stop();
        var fault = Assert.Single(faults);
        Assert.DoesNotContain("192.0.2.10", fault);
        Assert.DoesNotContain("deadbeef", fault);
    }

    /// <summary>
    /// Review finding R-2. The CN client keeps three decoded connections open at once -- lobby,
    /// zone and chat -- and the chat server drops and reconnects on its own schedule. Only the
    /// end of the last connection still delivering may be reported: treating any connection
    /// that ever delivered a message as a loss turns an ordinary cleared duty into a permanent
    /// DISCONNECTED record while the zone connection is undisturbed.
    /// </summary>
    [Fact]
    public void OnlyTheEndOfTheLastDeliveringConnectionIsReportedAsALostConnection()
    {
        var reader = new Reader();
        var closures = 0;
        var delivered = 0;
        using var monitor = new FirstPacketMonitor(
            Options,
            (_, _, _, _) => Interlocked.Increment(ref delivered),
            _ => { },
            reader,
            () => new[] { FirstPacketTests.Owned(), FirstPacketTests.Owned(41001) },
            () => { },
            connectionClosed: () => Interlocked.Increment(ref closures));

        // Two owned connections, each decoding something.
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 100, 2));
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 101, 24, FirstPacketTests.Bundle(0x1111)));
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 200, 2, port: 41001));
        reader.Packets.Enqueue(
            FirstPacketTests.Packet(false, 201, 24, FirstPacketTests.Bundle(0x2222), port: 41001));

        monitor.Prepare();
        monitor.Start();
        WaitFor(() => Volatile.Read(ref delivered) >= 2, "both connections deliver");

        // The chat connection goes away. The zone connection is still carrying the run, so
        // nothing about the run has been lost.
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 277, 1, port: 41001));
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 177, 24, FirstPacketTests.Bundle(0x3333)));
        WaitFor(() => Volatile.Read(ref delivered) >= 3, "the surviving connection keeps decoding");
        Assert.Equal(0, Volatile.Read(ref closures));

        // The last one ends: the game has stopped talking.
        reader.Packets.Enqueue(FirstPacketTests.Packet(false, 253, 1));
        WaitFor(() => Volatile.Read(ref closures) == 1, "the last connection ending is reported");

        monitor.Stop();
        Assert.Equal(1, Volatile.Read(ref closures));
    }

    private static void WaitFor(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.True(condition(), because);
    }

    private sealed class Reader : INpcapPacketReader
    {
        internal readonly ConcurrentQueue<byte[]> Packets = new();
        internal bool FailOpen, OpenCalled, Disposed;
        internal volatile bool FailRead;
        internal int Reads;
        internal Action? OnEmpty;
        public void Open() { OpenCalled = true; if (FailOpen) throw new IOException("open failure"); }
        public bool Read(NpcapPacketReceiver receive)
        {
            Interlocked.Increment(ref Reads);
            if (FailRead) throw new IOException("192.0.2.10 deadbeef");
            if (Packets.TryDequeue(out var packet)) { receive(packet, 101); return true; }
            OnEmpty?.Invoke(); return false;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class Observer : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) { }
        public void OnDecodeError() { }
        public void OnFault(string reason, Exception? error) { }
    }
}
