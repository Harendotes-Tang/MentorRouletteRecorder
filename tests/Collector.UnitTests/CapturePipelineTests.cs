using System.Reflection;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Diagnostics;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The bounded hand-off between capture and parser, and the rate estimate built on its
/// counters. These are the properties that decide whether a slow parser degrades the capture
/// or destroys it.
/// </summary>
public sealed class CapturePipelineTests
{
    [Fact]
    public void MachinaMonitor_UsesPreparedFirstPacketTransport_ForStatefulOodleStartup()
    {
        using var source = new MachinaCaptureSource(RotatingFileLogger.Disabled);
        var configure = typeof(MachinaCaptureSource).GetMethod(
            "Configure", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(configure);

        using var monitor = Assert.IsType<FirstPacketMonitor>(configure!.Invoke(
            source,
            new object[]
            {
                new CaptureStartOptions(
                    "test", 1, System.Net.IPAddress.Parse("192.0.2.10"), null, OodleMode.FfxivTcp, null, null),
            }));

        Assert.IsAssignableFrom<IMachinaMonitor>(monitor);
    }

    [Theory]
    [InlineData("DEBUG-MACHINA: FFXIVBundleDecoder: Oodle Decompression failure.", 1, 0)]
    [InlineData("DEBUG-MACHINA: FFXIVBundleDecoder: Oodle Decompression error: invalid state", 1, 0)]
    [InlineData("DEBUG-MACHINA: FFXIVBundleDecoder: Decompression error: invalid deflate stream", 1, 0)]
    [InlineData("FFXIVBundleDecode() - Resetting stream. Message Null:True, Buffer Size:0", 0, 0)]
    [InlineData(@"DEBUG-MACHINA: OodleNative_Ffxiv: ffxiv_dx11 executable at path D:\missing\game\ffxiv_dx11.exe does not exist.", 0, 1)]
    public void MachinaMonitor_ReportsUpstreamFailures_EvenWithoutAMessageCallback(
        string trace, int decodeErrors, int faults)
    {
        using var source = new MachinaCaptureSource(RotatingFileLogger.Disabled);
        var observer = new CountingCaptureObserver();
        var observerField = typeof(MachinaCaptureSource).GetField(
            "_observer", BindingFlags.Instance | BindingFlags.NonPublic);
        var onTrace = typeof(MachinaCaptureSource).GetMethod(
            "OnMonitorTrace", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observerField);
        Assert.NotNull(onTrace);
        observerField!.SetValue(source, observer);

        onTrace!.Invoke(source, new object[] { trace });

        Assert.Equal(decodeErrors, observer.DecodeErrors);
        Assert.Equal(faults, observer.Faults);
        Assert.Equal(0, observer.Messages);
    }

    /// <summary>
    /// Machina trace lines are sampled, not written one disk record each.
    ///
    /// Oodle's TCP decompressor is stateful, so one lost segment makes every later compressed
    /// bundle on that direction fail. Writing each failure synchronously on the decode thread,
    /// under the process-wide trace gate, rotates the day's diagnostics away within the hour --
    /// the very log the failure has to be diagnosed from (review finding M-6).
    /// </summary>
    [Fact]
    public void MachinaTraceLinesAreSampledRatherThanWrittenOnceEachForever()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "MentorRecorder.TraceSampling", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var logger = new RotatingFileLogger(
                directory, new TestClock(new DateTimeOffset(2026, 9, 8, 7, 0, 0, TimeSpan.Zero)));
            using var source = new MachinaCaptureSource(logger);
            var onTrace = typeof(MachinaCaptureSource).GetMethod(
                "OnMonitorTrace", BindingFlags.Instance | BindingFlags.NonPublic)!;

            for (var i = 0; i < 500; i++)
            {
                onTrace.Invoke(
                    source, new object[] { "DEBUG-MACHINA: FFXIVBundleDecoder: Oodle Decompression failure." });
            }

            var written = File.ReadAllLines(logger.CurrentPath)
                .Count(line => line.Contains("monitor_trace\"", StringComparison.Ordinal));

            Assert.Equal(MachinaCaptureSource.MonitorTraceSamples, written);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void DeliversEveryMessageInOrder_WhenTheSinkKeepsUp()
    {
        var sink = new RecordingSink();
        using (var queue = new DecodedMessageQueue(sink, DecodedMessageQueue.MinCapacity))
        {
            for (var i = 0; i < 500; i++)
            {
                queue.Offer(Message((ushort)i));
            }

            queue.Complete(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(500, sink.Opcodes.Count);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => (ushort)i), sink.Opcodes);
    }

    [Fact]
    public void DropsTheOldestAndCountsIt_WhenTheSinkCannotKeepUp()
    {
        const int offered = 100_000;
        var sink = new BlockingSink();
        long dropped;
        long delivered;

        using (var queue = new DecodedMessageQueue(sink, DecodedMessageQueue.MinCapacity))
        {
            for (var i = 0; i < offered; i++)
            {
                // Offering must stay non-blocking no matter how far behind the sink is: a
                // blocked capture callback makes the driver drop the packets instead, and
                // those drops cannot be counted.
                queue.Offer(Message((ushort)(i % 1000), payloadBytes: 256));
            }

            // The queue can never hold more than its capacity, whatever was offered. With a
            // 256-byte payload that caps the queue's own memory at a couple of hundred
            // kilobytes, no matter that 100k messages went past it.
            Assert.InRange(queue.Depth, 0, queue.Capacity);

            sink.Release();
            queue.Complete(TimeSpan.FromSeconds(20));
            delivered = queue.DeliveredCount;
            dropped = queue.DroppedCount;
        }

        Assert.True(dropped > 0, "a 100k burst past a blocked sink must have dropped something");

        // Nothing is invented and nothing vanishes: everything offered was either handed on
        // or counted as a drop.
        Assert.Equal(offered, delivered + dropped);
        Assert.InRange(delivered, 1, offered);
    }

    [Fact]
    public void ClampsTheConfiguredCapacityIntoTheDocumentedRange()
    {
        using var tiny = new DecodedMessageQueue(new RecordingSink(), 1);
        using var huge = new DecodedMessageQueue(new RecordingSink(), int.MaxValue);

        Assert.Equal(DecodedMessageQueue.MinCapacity, tiny.Capacity);
        Assert.Equal(DecodedMessageQueue.MaxCapacity, huge.Capacity);
    }

    [Fact]
    public void ASinkThatThrowsIsCountedAndTheQueueKeepsGoing()
    {
        var sink = new ThrowingSink(failOn: 2);
        using var queue = new DecodedMessageQueue(sink, DecodedMessageQueue.MinCapacity);

        for (ushort i = 0; i < 5; i++)
        {
            queue.Offer(Message(i));
        }

        queue.Complete(TimeSpan.FromSeconds(10));

        // The contract says a sink must not throw. One that does is a bug to fix, never a
        // reason to stop parsing everything that follows it.
        Assert.Equal(1, queue.SinkErrorCount);
        Assert.Equal(4, queue.DeliveredCount);
        Assert.Equal(5, sink.SeenCount);
    }

    [Fact]
    public void ReportsPressure_WhenTheQueueIsMostlyFull()
    {
        var sink = new BlockingSink();
        using var queue = new DecodedMessageQueue(sink, DecodedMessageQueue.MinCapacity);
        Assert.False(queue.IsUnderPressure);

        for (var i = 0; i < DecodedMessageQueue.MinCapacity; i++)
        {
            queue.Offer(Message(1));
        }

        Assert.True(queue.IsUnderPressure);
        sink.Release();
    }

    [Fact]
    public void CountingSinkCountsPerOpcodeAndNeverInterprets()
    {
        var sink = new CountingSink();
        sink.Accept(Message(10));
        sink.Accept(Message(10));
        sink.Accept(Message(11));

        Assert.Equal(3, sink.AcceptedCount);
        Assert.Equal(2, sink.CountFor(10));
        Assert.Equal(1, sink.CountFor(11));
        Assert.Equal(2, sink.DistinctOpcodeCount);

        // Counting is not parsing: nothing here may ever look like a successful parse.
        Assert.Equal(0, sink.ParseOkCount);
        Assert.Equal(0, sink.ParseFailCount);
        Assert.Equal(0, sink.IgnoredCount);
        Assert.Null(sink.LastValidEventAtUtc);
    }

    [Fact]
    public void CountingSinkStopsTrackingNewOpcodes_OnceTheTableIsFull()
    {
        var sink = new CountingSink();
        for (var opcode = 0; opcode <= CountingSink.MaxTrackedOpcodes; opcode++)
        {
            sink.Accept(Message((ushort)opcode));
        }

        Assert.Equal(CountingSink.MaxTrackedOpcodes, sink.DistinctOpcodeCount);
        Assert.Equal(1, sink.UntrackedCount);
    }

    [Fact]
    public void RateEstimateConvergesOnASteadyStream()
    {
        var estimator = new ExponentialRateEstimator();
        estimator.Observe(0, TimeSpan.Zero);

        double rate = 0;
        for (var second = 1; second <= 40; second++)
        {
            rate = estimator.Observe(second * 100, TimeSpan.FromSeconds(second));
        }

        Assert.InRange(rate, 99, 101);
    }

    [Fact]
    public void RateEstimateDecaysWhenTheStreamStops()
    {
        var estimator = new ExponentialRateEstimator();
        estimator.Observe(0, TimeSpan.Zero);
        for (var second = 1; second <= 20; second++)
        {
            estimator.Observe(second * 100, TimeSpan.FromSeconds(second));
        }

        double rate = 0;
        for (var second = 21; second <= 60; second++)
        {
            rate = estimator.Observe(2000, TimeSpan.FromSeconds(second));
        }

        Assert.InRange(rate, 0, 1);
    }

    [Fact]
    public void RateEstimateIgnoresSamplesTakenTooCloseTogether()
    {
        var estimator = new ExponentialRateEstimator();
        estimator.Observe(0, TimeSpan.Zero);

        Assert.Equal(0, estimator.Observe(1_000_000, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void RateEstimateReseedsRatherThanGoingNegative()
    {
        var estimator = new ExponentialRateEstimator();
        estimator.Observe(0, TimeSpan.Zero);
        estimator.Observe(1000, TimeSpan.FromSeconds(1));

        // A counter that went backwards means a new session started underneath the estimator.
        Assert.Equal(0, estimator.Observe(0, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void RateEstimateRefusesAnImpossibleSmoothingFactor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialRateEstimator(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialRateEstimator(1.5));
    }

    private static DecodedMessage Message(ushort opcode, int payloadBytes = 0) => new(
        "session",
        MessageDirection.Inbound,
        DateTimeOffset.UnixEpoch,
        TimeSpan.Zero,
        0,
        FfxivFraming.SegmentTypeIpc,
        opcode,
        new byte[payloadBytes],
        "abcdef0123456789");

    [Fact]
    public async Task ConcurrentOverflowCountersBalanceAfterEveryAcceptedMessageIsAccountedFor()
    {
        using var queue = new DecodedMessageQueue(new CountingSink(), 512);
        var message = Message(1);
        const int writers = 4, each = 20000;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < each; i++) queue.Offer(message);
        })));
        Assert.True(queue.Complete(TimeSpan.FromSeconds(10)));
        Assert.Equal(writers * each, queue.DeliveredCount + queue.LostCount);
        Assert.Equal(0, queue.Depth);
    }

    /// <summary>
    /// The backlog a timed-out shutdown gives up on is counted apart from an overflow drop.
    ///
    /// They mean opposite things. An overflow drop is a hole in a session that is still
    /// running and the run in flight can no longer be classified; an abandoned backlog is the
    /// tail of a session that is already over. Reporting the second as the first marks an
    /// ordinary, correctly finished duty as EVENT_SEQUENCE_GAP (review finding H-7).
    /// </summary>
    [Fact]
    public async Task DrainTimeoutCountsTheAbandonedBacklogSeparatelyFromOverflowDrops()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reported = 0L;
        var queue = new DecodedMessageQueue(
            new SignallingSink(entered, release), 512, onDropped: count => Interlocked.Add(ref reported, count));
        try
        {
            queue.Offer(Message(1));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < 100; i++) queue.Offer(Message(2));
            Assert.False(queue.Complete(TimeSpan.FromMilliseconds(20)));
            Assert.Equal(100, queue.AbandonedCount);
            Assert.Equal(0, queue.DroppedCount);
            Assert.Equal(100, queue.LostCount);
            Assert.Equal(0, Interlocked.Read(ref reported));
            Assert.Equal(0, queue.Depth);
            Assert.False(queue.Completion.IsCompleted);
        }
        finally { release.Set(); }
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Dispose();
        Assert.Equal(1, queue.DeliveredCount);
    }

    /// <summary>
    /// An overflow drop tells its owner at the moment it happens, with how many were lost.
    /// Reporting only at teardown leaves the state machine unaware of a hole while the run it
    /// belongs to is still open (review finding H-7).
    /// </summary>
    [Fact]
    public void OverflowDropsAreReportedImmediatelyRatherThanAtTeardown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reported = 0L;
        using var queue = new DecodedMessageQueue(
            new SignallingSink(entered, release),
            DecodedMessageQueue.MinCapacity,
            onDropped: count => Interlocked.Add(ref reported, count));
        try
        {
            queue.Offer(Message(1));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < DecodedMessageQueue.MinCapacity + 40; i++) queue.Offer(Message(2));

            // Still running, nothing torn down, and the owner already knows.
            Assert.True(Interlocked.Read(ref reported) > 0);
            Assert.Equal(queue.DroppedCount, Interlocked.Read(ref reported));
        }
        finally { release.Set(); }
    }

    private sealed class SignallingSink(ManualResetEventSlim entered, ManualResetEventSlim release) : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message) { entered.Set(); release.Wait(); }
    }

    private sealed class CountingCaptureObserver : ICaptureSourceObserver
    {
        public int DecodeErrors { get; private set; }

        public int Faults { get; private set; }

        public int Messages { get; private set; }

        public void OnMessage(DecodedMessage message) => Messages++;

        public void OnDecodeError() => DecodeErrors++;

        public void OnFault(string reason, Exception? error) => Faults++;
    }

    private sealed class RecordingSink : IDecodedMessageSink
    {
        public List<ushort> Opcodes { get; } = new();

        public void Accept(DecodedMessage message) => Opcodes.Add(message.Opcode);
    }

    /// <summary>Blocks the parser thread until released, so the queue is guaranteed to fill.</summary>
    private sealed class BlockingSink : IDecodedMessageSink
    {
        private readonly ManualResetEventSlim _gate = new(false);

        public void Accept(DecodedMessage message) => _gate.Wait(TimeSpan.FromSeconds(30));

        public void Release() => _gate.Set();
    }

    private sealed class ThrowingSink : IDecodedMessageSink
    {
        private readonly int _failOn;
        private int _seen;

        public ThrowingSink(int failOn) => _failOn = failOn;

        public int SeenCount => _seen;

        public void Accept(DecodedMessage message)
        {
            _seen++;
            if (_seen == _failOn)
            {
                throw new InvalidOperationException("sink failure");
            }
        }
    }
}
