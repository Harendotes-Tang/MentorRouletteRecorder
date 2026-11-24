using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// A trace sink on a fixed clock (2026-01-01 UTC) whose monotonic time steps 10 ms per read, the header written
/// first, and a fake source wired into the sink the way the runner wires the live one. Import with
/// <c>using static</c>.
/// </summary>
internal static class TraceSinkTestKit
{
    internal static CaptureTraceSink NewSink(TextWriter writer, int maxLines = 1000, CaptureIdentityProbe? probe = null)
    {
        var elapsed = TimeSpan.Zero;
        return new CaptureTraceSink(
            writer,
            new TestClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            () => elapsed += TimeSpan.FromMilliseconds(10),
            maxLines,
            probe);
    }

    /// <summary>A CN header: Npcap 1.79, build 2026.01.01.0000.0000, live (not synthetic).</summary>
    internal static CaptureTraceHeader Header() => new(
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        "1.79",
        "2026.01.01.0000.0000",
        Region.Cn,
        "0123456789ab",
        "0.1.0",
        OodleMode.FfxivTcp,
        Synthetic: false);

    /// <summary>Starts a fake source whose messages land in the sink through the real observer path.</summary>
    internal static FakeCaptureSource Started(CaptureTraceSink sink, CaptureStartOptions options)
    {
        var source = new FakeCaptureSource();
        source.Start(options, new SinkObserver(sink));
        return source;
    }

    /// <summary>Forwards a fake source's callbacks into the trace sink, as the runner does.</summary>
    private sealed class SinkObserver(CaptureTraceSink sink) : ICaptureSourceObserver
    {
        public void OnMessage(DecodedMessage message) => sink.Accept(message);

        public void OnDecodeError() => sink.CountDecodeError();

        public void OnFault(string reason, Exception? error)
        {
        }
    }
}
