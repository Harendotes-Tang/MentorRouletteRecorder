using System.Diagnostics;
using Machina.Infrastructure;

namespace MentorRecorder.Collector.Capture;

/// <summary>
/// The two ages a buffered stream lives under. They must stay separate: aging a stream that is
/// waiting for the OS to confirm its tuple on the five-second gap tolerance deletes the whole
/// connection prefix captured while Oodle initializes -- a cold-disk scan of ffxiv_dx11.exe can
/// outlast it -- before the decoder ever exists.
/// </summary>
/// <param name="Bytes">Raw byte budget across every tracked stream.</param>
/// <param name="Packets">Raw packet budget across every tracked stream.</param>
/// <param name="Tuples">Tracked stream budget.</param>
/// <param name="PendingAge">Gap tolerance: how long a packet may wait behind a missing one.</param>
/// <param name="OwnershipAge">How long a stream may wait for ownership confirmation.</param>
internal sealed record FirstPacketLimits(int Bytes = 8 * 1024 * 1024, int Packets = 4096,
    int Tuples = 128, TimeSpan? PendingAge = null, TimeSpan? OwnershipAge = null)
{
    /// <summary>Gap tolerance. Only ever drops the stale packets, never the decoder.</summary>
    public TimeSpan Gap => PendingAge ?? TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a stream with no decoder may keep its prefix while ownership is unconfirmed.
    /// Generous on purpose: losing this prefix costs the whole connection, and the only cost
    /// of waiting is bounded memory that the byte/packet budgets already cap.
    /// </summary>
    public TimeSpan Ownership => OwnershipAge ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Owns all raw ingress, ownership-pending and sequence-gap packets under one shared budget.
/// Prepare only offers packets; Pump runs on the decoder worker after Oodle initialization.
/// No Machina decoder sees an unowned tuple or a continuation without an observed SYN.
///
/// Every path that discards a packet increments a counter here: a silent drop lets a
/// mid-stream attach display as a healthy RUNNING capture with all-zero counters, because
/// nothing reaches the observer to be counted.
/// </summary>
internal sealed class FirstPacketBuffer
{
    private readonly object _gate = new();
    private readonly uint _localIP, _pid;
    private readonly FirstPacketLimits _limits;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TCPConnection, IFirstPacketDecoder> _decoderFactory;
    private readonly Action<TCPConnection>? _ownedStreamEnded;
    private readonly Dictionary<FirstPacketTuple, StreamState> _streams = new();
    private int _bytes, _packets;
    private long _rawPackets, _droppedNoStream, _droppedNoSyn, _expiredStreams, _unconfirmedTuples, _streamResets;
    private long _handshakes, _gameConnections, _gameConnectionsNow;
    private readonly HashSet<FirstPacketTuple> _gameSeen = new();
    private bool _stopped, _decoding;
    internal string? Failure { get; private set; }
    internal (int Bytes, int Packets, int Tuples) Usage { get { lock (_gate) return (_bytes, _packets, _streams.Count); } }

    /// <summary>
    /// What ingress saw and what it threw away.
    ///
    /// Deliberately lock-free. Every writer runs under <c>_gate</c>, but a reader must never
    /// take it: the decode worker calls Machina <em>inside</em> that lock, and Machina reports
    /// a decompression failure through <see cref="System.Diagnostics.Trace"/>, which reaches
    /// the capture source and then the controller. A status read holding this lock could close
    /// that cycle with the decode thread and hang the collector.
    /// </summary>
    internal CaptureIngressCounters Counters => new(
        Interlocked.Read(ref _rawPackets),
        Interlocked.Read(ref _droppedNoStream),
        Interlocked.Read(ref _droppedNoSyn),
        Interlocked.Read(ref _expiredStreams),
        Interlocked.Read(ref _unconfirmedTuples),
        Interlocked.Read(ref _streamResets),
        Handshakes: Interlocked.Read(ref _handshakes),
        GameConnections: Interlocked.Read(ref _gameConnections),
        GameConnectionsNow: Interlocked.Read(ref _gameConnectionsNow));

    /// <summary>Distinct game connections remembered before the tally stops growing.</summary>
    private const int MaxTrackedGameConnections = 256;

    /// <summary>Creates a buffer.</summary>
    /// <param name="localIP">Local IPv4 address, as the adapter sees it.</param>
    /// <param name="pid">Game process id whose connections may be decoded.</param>
    /// <param name="decoderFactory">Builds the per-connection decoder once ownership is confirmed.</param>
    /// <param name="limits">Budgets and ages; the documented defaults when null.</param>
    /// <param name="clock">Monotonic reading source; a stopwatch when null.</param>
    /// <param name="ownedStreamEnded">
    /// Called once for each stream that had obtained a decoder and then ended: FIN/RST, or the
    /// operating system no longer listing the tuple for the game process. The only producer of
    /// DISCONNECTED on the live path (review finding H-6).
    /// </param>
    internal FirstPacketBuffer(uint localIP, uint pid, Func<TCPConnection, IFirstPacketDecoder> decoderFactory,
        FirstPacketLimits? limits = null, Func<TimeSpan>? clock = null,
        Action<TCPConnection>? ownedStreamEnded = null)
    {
        _localIP = localIP; _pid = pid; _decoderFactory = decoderFactory;
        _ownedStreamEnded = ownedStreamEnded;
        _limits = limits ?? new();
        if (_limits.Bytes <= 0 || _limits.Packets <= 0 || _limits.Tuples <= 0 ||
            _limits.Gap <= TimeSpan.Zero || _limits.Ownership <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
    }

    internal void Offer(ReadOnlySpan<byte> data, int link)
    {
        lock (_gate)
        {
            if (_stopped || Failure is not null) return;
            Expire();
            if (!FirstPacketFrame.TryRead(data, link, _localIP, out var frame, out var unsafePacket))
            {
                if (unsafePacket) Fail("不支持或不完整的 TCP 报文使流前缀无法确认。");
                return;
            }
            // Counted here, after structural validation and before any policy decision: this
            // is "the adapter really is carrying the game's TCP traffic", which is the fact
            // the diagnostics page cannot infer from any other counter.
            Interlocked.Increment(ref _rawPackets);
            // Machina's TCP decoder consumes SYN sequence space but ignores any SYN payload.
            // Refuse TCP Fast Open instead of starting the bundle decoder after a lost prefix.
            if (frame.Syn && frame.PayloadLength != 0)
            { Fail("无法安全重放携带数据的 TCP SYN 报文。"); return; }
            // Machina 2.3.1.3 compares unsigned sequences and treats NextSequence == 0
            // as uninitialized. A packet ending at zero can remain queued and be returned
            // forever. Refuse all wrap before allocation or decoder access, including SYN/FIN.
            var sequenceEnd = (ulong)frame.Sequence + (uint)frame.PayloadLength +
                (frame.Syn ? 1u : 0u) + ((frame.Flags & 1) != 0 ? 1u : 0u);
            if (sequenceEnd > uint.MaxValue)
            { Fail("TCP 序号发生回绕，无法安全继续解码。"); return; }
            _streams.TryGetValue(frame.Tuple, out var stream);
            if (frame.Closed && stream?.Decoder is null) { Remove(frame.Tuple); return; }
            if (frame.Origin && (stream is null || stream.OriginInbound != frame.Inbound || stream.OriginSequence != frame.Sequence))
            {
                if (stream is not null) Interlocked.Increment(ref _streamResets); // A new handshake replaces a live stream.
                Remove(frame.Tuple);
                if (_streams.Count == _limits.Tuples && !EvictOldestUnowned(null))
                { Fail("首包连接状态容量已满。"); return; }
                stream = new(frame.Inbound, frame.Sequence, _clock());
                _streams.Add(frame.Tuple, stream);
                // Every stream here began with an observed handshake, whoever it belongs to.
                // Zero over a long session means no SYN reached us at all, which is a
                // different fault from "the game's own handshake was missed".
                Interlocked.Increment(ref _handshakes);
            }
            if (stream is null) { Interlocked.Increment(ref _droppedNoStream); return; } // Already-open and expired streams cannot resume without a new SYN.
            if (stream.Closing) return;
            if (frame.Closed) stream.Closing = true;
            var direction = frame.Inbound ? stream.Inbound : stream.Outbound;
            // A direction abandoned after an unfillable gap accepts nothing more: its Machina
            // state is past the hole and would decode the remainder as garbage.
            if (direction.Damaged) { Interlocked.Increment(ref _droppedNoStream); return; }
            if (frame.Syn)
            {
                if (direction.SynSequence is { } initial && initial != frame.Sequence)
                { Interlocked.Increment(ref _streamResets); Remove(frame.Tuple); return; }
                direction.SynSequence ??= frame.Sequence;
            }
            // A SYN/ACK alone does not establish origin; each direction also needs its own SYN.
            if (direction.SynSequence is null) { Interlocked.Increment(ref _droppedNoSyn); Remove(frame.Tuple); return; }
            if (!frame.Syn && !frame.Closed && frame.PayloadLength == 0) return;
            var expected = direction.Next ?? checked(direction.SynSequence.Value + 1);
            if (AmbiguousDistance(frame.Sequence, expected) || AmbiguousDistance((uint)sequenceEnd, expected))
            { Fail("TCP 序号距离存在歧义，无法安全继续解码。"); return; }
            while (_bytes > _limits.Bytes - frame.Length || _packets == _limits.Packets)
            {
                if (!EvictOldestUnowned(stream))
                {
                    // A single unrelated download can consume the whole pending budget.
                    // Give up its complete prefix as well; only an owned decoder exhausting
                    // the budget can justify ending the capture and all its other streams.
                    if (stream.Decoder is null)
                    {
                        Interlocked.Increment(ref _expiredStreams);
                        Remove(frame.Tuple);
                        return;
                    }
                    Fail("首包原始缓冲容量已满，无法保证流前缀完整。");
                    return;
                }
            }
            var packet = new Packet(frame, data.Slice(frame.Offset, frame.Length).ToArray(), _clock());
            stream.Pending.Add(packet);
            _bytes += packet.Data.Length;
            _packets++;
        }
    }

    /// <summary>
    /// Feeds every owned stream and reports the ones that ended.
    ///
    /// The report is made after the lock is released, deliberately: the listener ends up in
    /// the state machine and therefore in a database transaction, and holding the reassembly
    /// lock across that would stall the decode thread behind a busy-retry.
    /// </summary>
    /// <param name="owned">Connections the operating system currently attributes to the game.</param>
    internal void Pump(IReadOnlyCollection<TCPConnection> owned)
    {
        var ended = new List<TCPConnection>();
        try
        {
            PumpCore(owned, ended);
        }
        finally
        {
            foreach (var connection in ended)
            {
                _ownedStreamEnded?.Invoke(connection);
            }
        }
    }

    private void PumpCore(IReadOnlyCollection<TCPConnection> owned, List<TCPConnection> ended)
    {
        lock (_gate)
        {
            if (_stopped || Failure is not null) return;
            // Ageing starts with the decode loop, not with the reader: everything captured
            // while Oodle initialized is prefix, and prefix is the one thing that cannot be
            // recovered by waiting.
            _decoding = true;
            Expire();
            var confirmed = owned.Where(c => c.ProcessId == _pid && c.LocalIP == _localIP)
                .ToDictionary(c => new FirstPacketTuple(c.LocalIP, c.LocalPort, c.RemoteIP, c.RemotePort), c => c);
            // The operating system's own answer to "has the client reconnected since we
            // started", counted and never reported as an address. A total above the count
            // taken at startup means a fresh login happened while we were listening, so a
            // capture that still decodes nothing lost the handshake rather than missed it.
            Interlocked.Exchange(ref _gameConnectionsNow, confirmed.Count);
            foreach (var tuple in confirmed.Keys)
            {
                if (_gameSeen.Count < MaxTrackedGameConnections && _gameSeen.Add(tuple))
                {
                    Interlocked.Increment(ref _gameConnections);
                }
            }
            foreach (var (tuple, stream) in _streams.ToArray())
            {
                if (!confirmed.TryGetValue(tuple, out var connection) && !(stream.Decoder is not null && stream.Closing))
                {
                    // Counted once per tuple, not once per tick: the number the user needs is
                    // "how many observed connections were never the game's", which separates
                    // a wrong adapter from a wrong process.
                    if (!stream.Confirmed && !stream.CountedUnconfirmed)
                    { stream.CountedUnconfirmed = true; Interlocked.Increment(ref _unconfirmedTuples); }
                    // An owned, decoding stream the OS no longer lists is a connection that
                    // went away under us -- the fact DISCONNECTED is made of.
                    if (stream.Decoder is not null) { ReportEnded(stream, ended); Remove(tuple); }
                    continue;
                }
                stream.Confirmed = true;
                stream.Connection ??= connection;
                // A previously confirmed closing stream may drain the captured tail even if the
                // OS already removed its row. Closing forbids any new continuation or ownership grant.
                stream.Decoder ??= _decoderFactory(connection!);
                // Search in capture order; defer only packets ahead of a gap. Machina still performs
                // actual IP/TCP extraction and overlap handling, without its unsafe 2-second resync.
                bool progress;
                do
                {
                    progress = false;
                    for (var i = 0; i < stream.Pending.Count; i++)
                    {
                        var packet = stream.Pending[i];
                        var direction = packet.Frame.Inbound ? stream.Inbound : stream.Outbound;
                        var sequence = packet.Frame.Sequence;
                        var end = checked(sequence + (uint)packet.Frame.PayloadLength + (packet.Frame.Syn ? 1u : 0u));
                        if (direction.Next is null && !packet.Frame.Syn) continue;
                        if (direction.Next is { } next)
                        {
                            // Recheck at consumption time: expected sequence may have advanced
                            // since Offer. No serial-number/unsigned comparison disagreement is fed.
                            if (AmbiguousDistance(sequence, next) || AmbiguousDistance(end, next))
                            { Fail("TCP 序号距离存在歧义，无法安全继续解码。"); return; }
                            if (sequence > next) continue;
                        }
                        stream.Pending.RemoveAt(i);
                        _bytes -= packet.Data.Length; _packets--;
                        try
                        {
                            if (direction.Next is null || end > direction.Next.Value)
                            {
                                stream.Decoder.Feed(packet.Data, packet.Frame.Inbound);
                                direction.Next = end;
                            }
                        }
                        finally { Array.Clear(packet.Data); }
                        if (packet.Frame.Closed)
                        {
                            // FIN or RST on a stream that has a decoder: the game's own
                            // connection is over, not merely quiet.
                            ReportEnded(stream, ended);
                            Remove(tuple);
                            progress = false;
                            break;
                        }
                        progress = true;
                        break;
                    }
                } while (progress);
            }
        }
    }

    /// <summary>
    /// Queues one "this connection ended" report, once per stream. A stream is only worth
    /// reporting when it actually decoded something: the client opens and closes lobby
    /// connections constantly before login, and none of that is evidence about a run.
    /// </summary>
    /// <param name="stream">Stream that is about to be removed.</param>
    /// <param name="ended">Reports to make once the lock is released.</param>
    private static void ReportEnded(StreamState stream, List<TCPConnection> ended)
    {
        if (stream.Reported || stream.Connection is not { } connection) return;
        stream.Reported = true;
        ended.Add(connection);
    }

    internal void Tick() { lock (_gate) { if (!_stopped && Failure is null) Expire(); } }
    private static bool AmbiguousDistance(uint left, uint right) => Math.Abs((long)left - right) >= 0x80000000L;
    internal void Stop() { lock (_gate) { _stopped = true; Clear(); } }
    internal void Fault(string reason) { lock (_gate) Fail(reason); }
    private void Fail(string reason) { Failure ??= reason; Clear(); }
    private void Clear() { foreach (var tuple in _streams.Keys.ToArray()) Remove(tuple); }

    /// <summary>
    /// Ages streams once decoding has started. Two rules, deliberately different:
    /// a stream that never obtained a decoder is released whole after the ownership age,
    /// because its prefix is the only thing it holds; a stream that <em>has</em> a decoder
    /// only loses the packets stuck behind an unfillable gap, and only in that direction.
    /// Removing a live decoder over one lost packet would freeze a working session's counters
    /// at their last value with no error anywhere.
    /// </summary>
    private void Expire()
    {
        if (!_decoding) return;
        var now = _clock();
        foreach (var (tuple, stream) in _streams.ToArray())
        {
            if (stream.Decoder is null)
            {
                if (now - stream.Created >= _limits.Ownership) { Interlocked.Increment(ref _expiredStreams); Remove(tuple); }
                continue;
            }

            AbandonStaleDirection(stream, inbound: true, now);
            AbandonStaleDirection(stream, inbound: false, now);

            // Both directions gone: nothing can ever come out of this decoder again.
            if (stream.Inbound.Damaged && stream.Outbound.Damaged) Remove(tuple);
        }
    }

    /// <summary>
    /// Drops one direction's packets when the oldest of them has waited out the gap
    /// tolerance. The decoder, and the other direction, survive: a lost inbound packet must
    /// not be able to end outbound decoding, and neither may end the connection.
    /// </summary>
    private void AbandonStaleDirection(StreamState stream, bool inbound, TimeSpan now)
    {
        var direction = inbound ? stream.Inbound : stream.Outbound;
        if (direction.Damaged) return;
        var stale = false;
        foreach (var packet in stream.Pending)
            if (packet.Frame.Inbound == inbound && now - packet.Arrived >= _limits.Gap) { stale = true; break; }
        if (!stale) return;

        // Machina's bundle decoder has no framing resynchronisation we can trust mid-stream,
        // so this direction is abandoned rather than fed a hole -- but it is counted, logged
        // and surfaced through silent_reason instead of silently taking the session with it.
        direction.Damaged = true;
        Interlocked.Increment(ref _streamResets);
        for (var i = stream.Pending.Count - 1; i >= 0; i--)
        {
            if (stream.Pending[i].Frame.Inbound != inbound) continue;
            var packet = stream.Pending[i];
            stream.Pending.RemoveAt(i);
            _bytes -= packet.Data.Length; _packets--;
            Array.Clear(packet.Data);
        }
    }

    /// <summary>
    /// Releases the oldest stream that has not obtained a decoder yet, and reports whether it
    /// found one.
    ///
    /// The budgets cannot be fail-closed. Ownership waits thirty seconds (see
    /// <see cref="FirstPacketLimits.Ownership"/>) and the pcap filter is "this local address",
    /// not "this process", so the buffer holds half a minute of every other program's traffic
    /// as well and one download would be enough to fault a working capture. Giving up the
    /// oldest connection nobody has claimed costs at most that connection's prefix, is counted
    /// as an expired stream, and never touches a stream that is already decoding.
    /// </summary>
    /// <param name="keep">Stream the caller is currently appending to; never evicted.</param>
    private bool EvictOldestUnowned(StreamState? keep)
    {
        FirstPacketTuple? oldest = null;
        var created = TimeSpan.MaxValue;
        foreach (var (tuple, candidate) in _streams)
        {
            if (candidate.Decoder is not null || ReferenceEquals(candidate, keep) ||
                candidate.Created > created)
            {
                continue;
            }

            oldest = tuple;
            created = candidate.Created;
        }

        if (oldest is not { } victim)
        {
            return false;
        }

        Interlocked.Increment(ref _expiredStreams);
        Remove(victim);
        return true;
    }

    private void Remove(FirstPacketTuple tuple)
    {
        if (!_streams.Remove(tuple, out var stream)) return;
        foreach (var p in stream.Pending) { _bytes -= p.Data.Length; _packets--; Array.Clear(p.Data); }
        stream.Pending.Clear();
        stream.Decoder = null;
    }
    private sealed record Packet(FirstPacketFrame Frame, byte[] Data, TimeSpan Arrived);
    private sealed class DirectionState { internal uint? SynSequence, Next; internal bool Damaged; }
    private sealed class StreamState(bool inbound, uint sequence, TimeSpan created)
    {
        internal readonly bool OriginInbound = inbound;
        internal readonly uint OriginSequence = sequence;
        internal readonly TimeSpan Created = created;
        internal readonly DirectionState Inbound = new(), Outbound = new();
        internal readonly List<Packet> Pending = new();
        internal IFirstPacketDecoder? Decoder;

        /// <summary>Connection this stream decodes, once ownership was confirmed.</summary>
        internal TCPConnection? Connection;
        internal bool Closing, Confirmed, CountedUnconfirmed;

        /// <summary>True once this stream's end has been reported; reported at most once.</summary>
        internal bool Reported;
    }
}

internal interface IFirstPacketDecoder { void Feed(byte[] ipv4, bool inbound); }
