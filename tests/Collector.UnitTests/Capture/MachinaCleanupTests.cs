using MentorRecorder.Collector.Capture;

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
        source.Start(new CaptureStartOptions("synthetic", 42, null, null, OodleMode.LibraryTcp, null, null), new NullCaptureObserver());
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
}
