using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.Protocol.Calibration;

/// <summary>
/// How trustworthy one capture session's silence is.
///
/// A shared calibration may only be contradicted by the absence of a message, and absence proves
/// something only when the capture could have seen the message: it did not attach midway through
/// a connection it cannot decode, the adapter lost nothing, and nothing else made the session go
/// quiet. Only the capture controller knows these facts; it hands them to the pipeline, which
/// records them here per session (wiring is phase B). They are never persisted: evidence carried
/// over from an earlier run of the Collector has no health and can therefore only support a pass.
/// </summary>
/// <param name="CaptureSessionId">Session the reading describes.</param>
/// <param name="SilentReason">Why the capture was silent, as the controller classified it.</param>
/// <param name="PreexistingConnections">Game connections already open when capture started; null when unknown.</param>
/// <param name="AdapterDropped">Packets the capture driver reported as lost.</param>
public sealed record CaptureSessionHealth(
    string CaptureSessionId,
    CaptureSilentReason SilentReason,
    int? PreexistingConnections,
    long AdapterDropped)
{
    /// <summary>
    /// True when an absence in this session means something: no silent reason, no connection that
    /// predates the capture (unknown counts as present), and no packet lost at the adapter.
    /// </summary>
    public bool IsHealthy =>
        SilentReason == CaptureSilentReason.None && PreexistingConnections == 0 && AdapterDropped == 0;

    /// <summary>
    /// Combines two readings of one session into the worse of them. A silent reason, once seen,
    /// stays (the controller clears its own when traffic resumes, but the evidence already lost
    /// is not recovered); pre-existing connections and adapter drops keep their largest value.
    /// </summary>
    /// <param name="later">A later reading of the same session.</param>
    public CaptureSessionHealth Merge(CaptureSessionHealth later)
    {
        ArgumentNullException.ThrowIfNull(later);
        if (!string.Equals(CaptureSessionId, later.CaptureSessionId, StringComparison.Ordinal))
        {
            throw new ArgumentException("only readings of the same capture session can be merged", nameof(later));
        }

        int? preexisting = (PreexistingConnections, later.PreexistingConnections) switch
        {
            (null, var right) => right,
            (var left, null) => left,
            ({ } left, { } right) => Math.Max(left, right),
        };
        return new CaptureSessionHealth(
            CaptureSessionId,
            SilentReason != CaptureSilentReason.None ? SilentReason : later.SilentReason,
            preexisting,
            Math.Max(AdapterDropped, later.AdapterDropped));
    }
}
