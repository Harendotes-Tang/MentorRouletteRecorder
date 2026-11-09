namespace MentorRecorder.Collector.Capture;

/// <summary>一次健康判断所需的值快照；不持有采集源、控制器、时钟或锁。</summary>
internal readonly record struct CaptureHealthReading(long IpcMessagesDecoded, long DecodeErrors, TimeSpan Elapsed);

/// <summary>根据已采样计数判断采集沉默原因；不探测系统、不记录日志、不触发恢复。</summary>
internal static class CaptureHealthPolicy
{
    internal const int MidstreamRejectionThreshold = 20;
    internal static readonly TimeSpan LivenessGrace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Reads one verdict out of the counters. Pure, so the table of cases is testable without
    /// a controller, a clock or a source.
    /// </summary>
    /// <param name="run">Counters of the capture in flight.</param>
    /// <param name="ingress">What the adapter delivered and what was discarded.</param>
    /// <param name="preexisting">Connections the game already held at start, when known.</param>
    /// <param name="gameConnectionSeen">The game held a connection on some interface, or the table could not tell.</param>
    internal static CaptureSilentReason Classify(
        CaptureHealthReading run, CaptureIngressCounters ingress, int? preexisting, bool gameConnectionSeen)
    {
        // One decoded IPC segment proves the compressed stream is readable. Control segments
        // do not: they stay readable exactly when Oodle state is missing.
        if (run.IpcMessagesDecoded > 0)
        {
            return CaptureSilentReason.None;
        }

        // Two forms of hard evidence, neither of which needs to wait out the grace period.
        // A sustained rejection run on a capture that started with connections is the
        // original midstream signature; every frame being dropped for want of a handshake is
        // the same fact observed one layer lower, where nothing reaches the observer at all.
        if (preexisting > 0 && run.DecodeErrors >= MidstreamRejectionThreshold)
        {
            return CaptureSilentReason.Midstream;
        }

        // Every frame the card delivered was discarded before reassembly could use it. At
        // the wire a mid-connection attach shows up as DroppedNoStream, not DroppedNoSyn:
        // a continuation whose handshake happened before we were listening has no tracked
        // stream at all, and only a connection that showed one SYN and not the other lands
        // in DroppedNoSyn. Both mean the same thing -- no handshake was observed -- so both
        // count, and the sample has to be big enough that ordinary background chatter on the
        // same local address cannot produce this verdict on its own. And the game has to hold
        // a connection at all: before login every frame is another program's, and those are
        // dropped for want of a handshake too (audit 2026-10-03, OA-6).
        if (ingress.AllDroppedBeforeDecode && ingress.RawPackets >= MidstreamRejectionThreshold &&
            (ingress.GameConnectionsNow > 0 || preexisting > 0))
        {
            return CaptureSilentReason.Midstream;
        }

        if (run.Elapsed < LivenessGrace)
        {
            return CaptureSilentReason.None;
        }

        if (preexisting > 0)
        {
            return CaptureSilentReason.Midstream;
        }

        // The game has held no connection on any interface since this capture started: the
        // title screen, an opening movie. Silence then says nothing about where its traffic goes,
        // and other programs' frames say nothing about ownership: no verdict, no advice, no
        // recovery episode, the same rule as OA-6 above (audit 2026-11-03, A1-5).
        if (!gameConnectionSeen && ingress.GameConnections == 0)
        {
            return CaptureSilentReason.None;
        }

        // Nothing at all on this card for a whole minute while the game is running: the
        // traffic is somewhere else, which is what a 加速器 or a VPN does.
        if (ingress.RawPackets == 0)
        {
            return CaptureSilentReason.NoPacketsOnAdapter;
        }

        // Traffic arrives, but no tuple was ever confirmed as belonging to the game.
        return ingress.UnconfirmedTuples > 0
            ? CaptureSilentReason.NoStreamOwnership
            : CaptureSilentReason.None;
    }

}
