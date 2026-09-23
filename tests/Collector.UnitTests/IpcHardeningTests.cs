using System.Diagnostics;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Load-bearing invariants of the IPC layer: the order live events are handed out in, the
/// memory a receive buffer keeps, the size of the strings a client may push through an array
/// field, how long a connection may stay silent, and how many full-database scans one client
/// can start at once.
/// </summary>
public sealed class IpcHardeningTests
{
    /// <summary>
    /// Two publishers on two threads must not hand a subscriber sequence numbers out of order.
    ///
    /// The contract defines a gap in <c>sequence</c> as "you fell behind, re-query". A number
    /// that goes <em>backwards</em> is not a gap and has no defined meaning, so the counter
    /// must be incremented under the lock that fans the event out (review finding M1).
    /// </summary>
    [Fact]
    public async Task ConcurrentPublishersHandOutSequenceNumbersInOrder()
    {
        const int perPublisher = 100;
        var bus = new LiveEventBus(SystemClock.Instance);
        using var subscription = bus.Subscribe(Guid.NewGuid().ToString("D"));

        using var start = new Barrier(2);
        var publishers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < perPublisher; i++)
            {
                bus.Publish(LiveEventKind.StatsInvalidated, payload => payload["message"] = "x");
            }
        })).ToArray();

        await Task.WhenAll(publishers);

        var previous = 0L;
        for (var i = 0; i < 2 * perPublisher; i++)
        {
            var received = await subscription.ReadAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(received);
            var sequence = received!["sequence"]!.GetValue<long>();
            Assert.True(
                sequence > previous,
                $"sequence {sequence} arrived after {previous}; the fan-out is not ordered");
            previous = sequence;
        }
    }

    /// <summary>
    /// A single oversized burst must not leave a per-connection buffer holding megabytes for
    /// the rest of the connection's life (review finding L3).
    /// </summary>
    [Fact]
    public void TheReceiveBufferGivesItsMemoryBackWhenItRunsDry()
    {
        var buffer = new FrameBuffer();
        var body = new byte[512 * 1024];
        buffer.Append(FrameCodec.Encode(body));

        Assert.True(buffer.Capacity > FrameBuffer.InitialCapacity);
        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var taken));
        Assert.Equal(body.Length, taken.Length);
        Assert.Equal(0, buffer.Buffered);
        Assert.Equal(FrameBuffer.InitialCapacity, buffer.Capacity);
    }

    /// <summary>
    /// The buffer only shrinks when nothing is left in it: a partially consumed stream must
    /// keep its bytes.
    /// </summary>
    [Fact]
    public void APartiallyConsumedBufferKeepsWhatIsLeft()
    {
        var buffer = new FrameBuffer();
        buffer.Append(FrameCodec.Encode(new byte[256 * 1024]));
        buffer.Append(FrameCodec.Encode(new byte[8]));

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out _));
        Assert.Equal(FrameCodec.PrefixBytes + 8, buffer.Buffered);
        Assert.True(buffer.Capacity > FrameBuffer.InitialCapacity);

        Assert.Equal(FrameStatus.Ok, buffer.TryTake(out var second));
        Assert.Equal(8, second.Length);
        Assert.Equal(FrameBuffer.InitialCapacity, buffer.Capacity);
    }

    /// <summary>
    /// An array field caps how many elements it takes; it must cap how long each one is too,
    /// or sixteen elements of a megabyte each are a legal request (review finding L4).
    /// </summary>
    [Fact]
    public void AnArrayElementLongerThanTheCapIsRefused()
    {
        var payload = new JsonObject
        {
            ["event_types"] = new JsonArray(new string('x', 200)),
        };

        var error = Assert.Throws<CollectorException>(
            () => new PayloadReader(payload).StringArray("event_types", 16, maxLength: 64));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("payload.event_types", error.Field);
    }

    [Fact]
    public void AnArrayElementInsideTheCapIsAccepted()
    {
        var payload = new JsonObject { ["event_types"] = new JsonArray("RunStarted", "Heartbeat") };

        var values = new PayloadReader(payload).StringArray("event_types", 16, maxLength: 64);

        Assert.Equal(new[] { "RunStarted", "Heartbeat" }, values);
    }

    /// <summary>
    /// The two ends must agree on which answers take a while.
    ///
    /// Every message type in this set is answered off the connection's read loop because it
    /// waits on something slower than a query, so every one of them also needs a deadline on
    /// the Desktop longer than a status poll's. The two drifted apart once already:
    /// <c>CheckDatabaseIntegrity</c> was deferred here but left on the ordinary 8 s deadline
    /// there, so a large database reported a timeout while the scan was still running and about
    /// to succeed (2026-09-21 review finding 2).
    ///
    /// The Desktop half of this pair is
    /// <c>LifecycleTests::everyAnswerTheCollectorDefersGetsAnExtendedDeadline</c>. A fourth
    /// entry here has to be given a deadline there as well, and this test is what says so.
    /// </summary>
    [Fact]
    public void TheMessageTypesAnsweredLaterAreExactlyTheThreeTheDesktopWaitsLongerFor()
    {
        Assert.Equal(
            new[] { "CheckDatabaseIntegrity", "CheckUpdateNow", "SynthesizeSpeech" },
            MessageDispatcher.AsynchronousMessageTypes.OrderBy(type => type, StringComparer.Ordinal));
    }

    /// <summary>
    /// A client that connects and then says nothing used to hold one of the eight pipe
    /// instances for as long as it lived, and a full pipe is deliberately treated as
    /// backpressure rather than a fault, so eight of them locked the real Desktop out in
    /// silence (2026-09-21 review finding 3).
    /// </summary>
    [Fact]
    public async Task AConnectionThatSaysNothingIsClosedWhenTheDeadlinePasses()
    {
        using var peer = new SilentStream();
        var stream = new IdleTimeoutStream(peer, TimeSpan.FromMilliseconds(400));
        var buffer = new byte[64];

        var waited = Stopwatch.StartNew();
        await Assert.ThrowsAsync<IOException>(
            async () => await stream.ReadAsync(buffer, CancellationToken.None));

        // Closed because the deadline passed, not before it: a connection with budget left is
        // never dropped.
        Assert.True(
            waited.Elapsed >= TimeSpan.FromMilliseconds(300),
            $"the read gave up after {waited.ElapsedMilliseconds} ms, before its deadline");
    }

    /// <summary>
    /// A live-events subscriber legitimately sends nothing at all once it is subscribed: the
    /// traffic on its connection is the heartbeat this process writes to it. Dropping such a
    /// connection would disconnect a healthy Desktop, so the deadline measures silence in both
    /// directions and a write refreshes it exactly like an incoming request does.
    /// </summary>
    [Fact]
    public async Task ASubscriberThatOnlyListensIsKeptForAsLongAsTheHeartbeatsFlow()
    {
        using var peer = new SilentStream();
        var stream = new IdleTimeoutStream(peer, TimeSpan.FromMilliseconds(600));
        var buffer = new byte[64];
        var reading = stream.ReadAsync(buffer, CancellationToken.None).AsTask();

        // Six heartbeats over 1.5 s -- well past the deadline -- while the client sends nothing.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await stream.WriteAsync(new byte[] { 0x01 }, CancellationToken.None);
        }

        Assert.False(reading.IsCompleted);

        // Once the heartbeats stop, and only then, the connection is let go.
        await Assert.ThrowsAsync<IOException>(() => reading);
    }

    /// <summary>
    /// The shipped deadline has to outlast every silence a client is entitled to, or it
    /// disconnects healthy ones: a subscriber may ask for a heartbeat as slow as 60 s, and the
    /// Desktop waits 120 s for an export before giving up on the request itself.
    /// </summary>
    [Fact]
    public void TheShippedIdleDeadlineOutlastsEverySilenceAClientIsEntitledTo()
    {
        Assert.True(PipeServer.DefaultIdleTimeout >= TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task ABlockedResponseWriteExpiresWithoutAConcurrentRead()
    {
        using var peer = new BlockedWriteStream();
        var stream = new IdleTimeoutStream(peer, TimeSpan.FromMilliseconds(150));
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(async () =>
            await stream.WriteAsync(new byte[] { 1 }, safety.Token));
        Assert.False(safety.IsCancellationRequested);
        Assert.Equal(1, peer.WriteAttempts);
        Assert.True(peer.WriteCancelled);
    }

    [Fact]
    public async Task CancellingABlockedWriteKeepsTheCallersCancellation()
    {
        using var peer = new BlockedWriteStream();
        var stream = new IdleTimeoutStream(peer, TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var writing = stream.WriteAsync(new byte[] { 1 }, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.True(peer.WriteCancelled);
    }

    /// <summary>
    /// <c>CheckDatabaseIntegrity</c> reads every page of the database on a connection of its
    /// own. Nothing bounded how many of those ran at once -- neither the dispatcher nor the
    /// connection's deferred list -- while the other costly deferred message has had a queue
    /// and a refusal of its own since it shipped (2026-09-21 review finding 14).
    /// </summary>
    [Fact]
    public async Task ASecondFullDatabaseScanIsRefusedWhileTheFirstIsStillRunning()
    {
        using var database = new TestDatabase();
        using var host = CollectorHost.Open(
            Path.Combine(Path.GetDirectoryName(database.Path)!, "integrity.db"),
            database.Clock,
            capture: FakeCapture());
        var dispatcher = new MessageDispatcher(host);

        // Holding the slot is what a scan in flight does. Taking it here needs no database big
        // enough to scan slowly, which keeps the test deterministic.
        Assert.True(dispatcher.TryBeginIntegrityCheck());

        var refusal = await Assert.ThrowsAsync<CollectorException>(
            () => dispatcher.DispatchAsync(IntegrityRequest(), CancellationToken.None));
        Assert.Equal(ErrorCodes.DbBusy, refusal.Code);
        Assert.True(refusal.Retryable);
        Assert.Contains("校验", refusal.Message, StringComparison.Ordinal);

        // Refused rather than queued, so the one that finishes frees the slot for the next ask.
        dispatcher.EndIntegrityCheck();

        var payload = await dispatcher.DispatchAsync(IntegrityRequest(), CancellationToken.None);
        Assert.True(payload["passed"]!.GetValue<bool>());

        // And the scan gives the slot back itself: the next request is not refused for ever.
        Assert.True(dispatcher.TryBeginIntegrityCheck());
        dispatcher.EndIntegrityCheck();
    }

    private static IpcRequest IntegrityRequest() =>
        new(Guid.NewGuid().ToString("D"), "CheckDatabaseIntegrity", new JsonObject());

    private static CaptureServices FakeCapture() => new()
    {
        Npcap = new NpcapDetector(new FakeNpcapEnvironment()),
        Game = new GameProcessLocator(new FakeGameProcessProvider(), new FakeGameFileReader()),
        Adapters = new AdapterEnumerator(new FakeAdapterProvider(), new FakeProcessTcpTable()),
        EnableFollowTimer = false,
    };

    /// <summary>A peer whose full receive buffer keeps a response write pending.</summary>
    private sealed class BlockedWriteStream : MemoryStream
    {
        public int WriteAttempts { get; private set; }
        public bool WriteCancelled { get; private set; }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteAttempts++;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                WriteCancelled = cancellationToken.IsCancellationRequested;
            }
        }
    }

    /// <summary>A peer that connects and then never sends a byte.</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
