using System.Buffers.Binary;
using System.Diagnostics;
using Machina.Infrastructure;
using MentorRecorder.Collector.Contracts.Texts;

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
    private readonly uint _pid;
    private readonly HashSet<uint> _localIPs;
    private readonly bool _includeLoopback;
    private readonly FirstPacketLimits _limits;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<TCPConnection, IFirstPacketDecoder> _decoderFactory;
    private readonly Action<TCPConnection>? _ownedStreamEnded;
    private readonly Action<TCPConnection, bool>? _directionDamaged;
    private readonly Dictionary<FirstPacketTuple, StreamState> _streams = new();

    /// <summary>
    /// Ends and abandoned directions found under the lock, by either thread, in the order they were
    /// found; reported by the next Pump after release - before it decodes, for what the reader found.
    /// </summary>
    private readonly List<(TCPConnection Connection, StreamReport Kind)> _reports = new();
    private int _bytes, _packets;
    private long _rawPackets, _droppedNoStream, _droppedNoSyn, _expiredStreams, _unconfirmedTuples, _streamResets;
    private long _handshakes, _gameConnections, _gameConnectionsNow;
    private long _damagedGameDirections;
    private readonly HashSet<FirstPacketTuple> _gameSeen = new();
    private bool _stopped, _decoding;
    internal UserText? Failure { get; private set; }
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
        GameConnectionsNow: Interlocked.Read(ref _gameConnectionsNow),
        DamagedGameDirections: Interlocked.Read(ref _damagedGameDirections));

    /// <summary>Distinct game connections remembered before the tally stops growing.</summary>
    private const int MaxTrackedGameConnections = 256;

    /// <summary>
    /// How long a decoding stream must stay absent from the connection table before it counts
    /// as ended. Ending one is what produces DISCONNECTED, so a single reading that does not
    /// list it is not enough (audit 2026-10-03, OA-3).
    /// </summary>
    internal static readonly TimeSpan OwnershipLossConfirmation = TimeSpan.FromSeconds(1);

    /// <summary>Creates a buffer.</summary>
    /// <param name="localIP">Local IPv4 address, as the adapter sees it.</param>
    /// <param name="pid">Game process id whose connections may be decoded.</param>
    /// <param name="decoderFactory">Builds the per-connection decoder once ownership is confirmed.</param>
    /// <param name="limits">Budgets and ages; the documented defaults when null.</param>
    /// <param name="clock">Monotonic reading source; a stopwatch when null.</param>
    /// <param name="ownedStreamEnded">
    /// Called once for each stream that had obtained a decoder and then ended: FIN/RST, the
    /// operating system no longer listing the tuple for the game process for
    /// <see cref="OwnershipLossConfirmation"/>, or a new handshake replacing it. The only
    /// producer of DISCONNECTED on the live path (review finding H-6).
    /// </param>
    /// <param name="directionDamaged">
    /// Called once for each direction of a decoding stream given up for good (see <c>Abandon</c>), with
    /// the connection and true for inbound. Reported in order with the stream ends, after the messages
    /// the stream decoded before it, so the parser can tell whether the connection that lost the
    /// direction was carrying the run (audit 2026-10-03, CS3a-X1).
    /// </param>
    internal FirstPacketBuffer(uint localIP, uint pid, Func<TCPConnection, IFirstPacketDecoder> decoderFactory,
        FirstPacketLimits? limits = null, Func<TimeSpan>? clock = null,
        Action<TCPConnection>? ownedStreamEnded = null, Action<TCPConnection, bool>? directionDamaged = null)
        : this(new[] { localIP }, pid, decoderFactory, limits, clock, ownedStreamEnded, directionDamaged)
    {
    }

    /// <summary>Shares one bounded ingress across the retained paths' local IPv4 addresses.</summary>
    /// <param name="localIPs">Exact selected interface addresses; copied so caller changes cannot redirect ingress.</param>
    /// <param name="pid">Only this process's complete operating-system tuples may obtain decoders.</param>
    /// <param name="decoderFactory">Creates independent protocol state after unique tuple ownership is confirmed.</param>
    /// <param name="limits">Shared raw budgets and aging limits, or the documented defaults.</param>
    /// <param name="clock">Monotonic time source, or a stopwatch.</param>
    /// <param name="ownedStreamEnded">Reports confirmed stream endings outside the buffer lock.</param>
    /// <param name="directionDamaged">Reports a confirmed game direction abandoned after a gap or unsafe sequence.</param>
    /// <param name="includeLoopback">Allows the IPv4 127/8 range when an identified loopback reader is retained.</param>
    internal FirstPacketBuffer(IReadOnlyCollection<uint> localIPs, uint pid,
        Func<TCPConnection, IFirstPacketDecoder> decoderFactory,
        FirstPacketLimits? limits = null, Func<TimeSpan>? clock = null,
        Action<TCPConnection>? ownedStreamEnded = null, Action<TCPConnection, bool>? directionDamaged = null,
        bool includeLoopback = false)
    {
        ArgumentNullException.ThrowIfNull(localIPs);
        _localIPs = new(localIPs); _includeLoopback = includeLoopback;
        _pid = pid; _decoderFactory = decoderFactory;
        _ownedStreamEnded = ownedStreamEnded;
        _directionDamaged = directionDamaged;
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
            if (!FirstPacketFrame.TryRead(data, link, _localIPs, _includeLoopback, out var frame, out var unsafePacket))
            {
                // TCP on this address whose headers cannot be trusted: a fragment, a truncated
                // body, a bad offset. Its tuple cannot be trusted either, so it joins no stream.
                // A stream it belonged to is left with a hole, which the gap rules already
                // handle without ever feeding a decoder across it. Counted, and never a reason
                // to end the capture: the pcap filter admits every program's traffic
                // (audit 2026-10-03, OA-1).
                if (unsafePacket)
                {
                    Interlocked.Increment(ref _rawPackets);
                    Interlocked.Increment(ref _droppedNoStream);
                }
                return;
            }
            // Counted here, after structural validation and before any policy decision: this
            // is "the adapter really is carrying the game's TCP traffic", which is the fact
            // the diagnostics page cannot infer from any other counter.
            Interlocked.Increment(ref _rawPackets);
            // Machina's TCP decoder consumes SYN sequence space but ignores any SYN payload, so
            // TCP Fast Open would start the bundle decoder after a lost prefix. Refused before
            // allocation or decoder access -- for its own stream only (see Refuse).
            var refused = frame.Syn && frame.PayloadLength != 0;
            _streams.TryGetValue(frame.Tuple, out var stream);
            if (stream is null && frame.LocalEndpoints && _streams.TryGetValue(frame.Tuple.Reverse, out stream))
            {
                // A local stream is keyed by its observed origin's wire tuple, not by a guessed
                // game endpoint. Reverse packets share that stream and its two independent states.
                frame = frame with { Tuple = frame.Tuple.Reverse, Inbound = true };
            }
            if (frame.Closed && stream?.Decoder is null) { Remove(frame.Tuple); return; }
            if (frame.Origin && (stream is null || stream.OriginInbound != frame.Inbound || stream.OriginSequence != frame.Sequence))
            {
                if (stream is not null)
                {
                    // A new handshake replaces a live stream; one that was decoding is over.
                    Interlocked.Increment(ref _streamResets);
                    ReportEnded(stream);
                }
                Remove(frame.Tuple);
                // A handshake that cannot be decoded safely opens nothing: the rest of that
                // connection is dropped for want of a stream, and counted as such.
                if (refused) { Interlocked.Increment(ref _droppedNoStream); return; }
                if (_streams.Count == _limits.Tuples && !EvictOldestUnowned(null))
                { Fail(CollectorTexts.CaptureFaultConnectionTableFull()); return; }
                stream = new(frame.Inbound, frame.Sequence, _clock(), frame.LocalEndpoints);
                _streams.Add(frame.Tuple, stream);
                // Every stream here began with an observed handshake, whoever it belongs to.
                // Zero over a long session means no SYN reached us at all, which is a
                // different fault from "the game's own handshake was missed".
                Interlocked.Increment(ref _handshakes);
            }
            if (stream is null) { Interlocked.Increment(ref _droppedNoStream); return; } // Already-open and expired streams cannot resume without a new SYN.
            if (refused) { Refuse(frame.Tuple, stream, frame.Inbound); return; }
            var direction = frame.Inbound ? stream.Inbound : stream.Outbound;
            // Once a close was accepted, only that direction's data at or before it may still
            // arrive: a retransmission filling a hole in front of the FIN, without which the FIN
            // could never be consumed and the end never reported (audit 2026-10-03, OA-5). A
            // direction with a close has its SYN.
            if (stream.Closing && (direction.ClosedAt is not { } closedAt ||
                direction.SynSequence is not { } origin || Rebase(frame, origin).End > closedAt)) return;
            // A direction abandoned after an unfillable gap accepts nothing more: its Machina
            // state is past the hole and would decode the remainder as garbage.
            if (direction.Damaged) { Interlocked.Increment(ref _droppedNoStream); return; }
            if (frame.Syn)
            {
                if (direction.SynSequence is { } initial && initial != frame.Sequence)
                { Refuse(frame.Tuple, stream, frame.Inbound); return; }
                direction.SynSequence ??= frame.Sequence;
            }
            // A SYN/ACK alone does not establish origin; each direction also needs its own SYN.
            // A stream that already decodes keeps its other direction (audit 2026-10-03, OA-5).
            if (direction.SynSequence is null)
            {
                Interlocked.Increment(ref _droppedNoSyn);
                if (stream.Decoder is null) Remove(frame.Tuple);
                else Abandon(stream, frame.Inbound);
                return;
            }
            if (!frame.Syn && !frame.Closed && frame.PayloadLength == 0) return;
            // From here on a sequence is counted from this direction's own SYN, which is zero, and
            // Machina is fed the same numbers (see Rebase): the game's zone connection lasts the
            // whole login, and on the wire numbers its data wraps at a random point, which used to
            // give the direction up and leave every later duty unrecorded (audit 2026-10-20, A1-1).
            // Machina 2.3.1.3 compares unsigned sequences and treats NextSequence == 0 as
            // uninitialized, so a packet ending at zero could remain queued and be returned
            // forever: what would cross zero in the counted numbers -- 4 GiB on one direction, or
            // a segment from before its own SYN -- is still refused, for its own stream only.
            var (sequence, sequenceEnd) = Rebase(frame, direction.SynSequence.Value);
            var expected = direction.Next ?? 1u;
            if (sequenceEnd > uint.MaxValue ||
                AmbiguousDistance(sequence, expected) || AmbiguousDistance((uint)sequenceEnd, expected))
            { Refuse(frame.Tuple, stream, frame.Inbound); return; }
            // Copies seen on several interfaces are removed before any raw budget is charged.
            // Only bounded pending storage is searched, with no second payload cache or unbounded
            // fingerprint history. Keep consumed retransmissions for the decode-time sequence
            // guard: earlier queued progress can make their distance ambiguous before Pump.
            if (HasPendingCopy(stream, frame, sequence, data)) return;
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
                    Fail(CollectorTexts.CaptureFaultRawBufferFull());
                    return;
                }
            }
            if (frame.Closed)
            {
                stream.Closing = true;
                direction.ClosedAt ??= (uint)sequenceEnd;
            }
            var bytes = data.Slice(frame.Offset, frame.Length).ToArray();
            // Neither Machina's IP nor its TCP decoder verifies a checksum, and nothing else reads
            // this copy, so only the sequence field changes.
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((bytes[0] & 15) * 4 + 4), sequence);
            var packet = new Packet(frame with { Sequence = sequence }, bytes, _clock());
            stream.Pending.Add(packet);
            _bytes += packet.Data.Length;
            _packets++;
        }
    }

    /// <summary>
    /// Compares an incoming IPv4 frame with its retained copies, ignoring only the TCP sequence
    /// field that this buffer rebases before storage. Link headers are outside both slices.
    /// </summary>
    private static bool HasPendingCopy(StreamState stream, in FirstPacketFrame frame, uint sequence,
        ReadOnlySpan<byte> data)
    {
        var ipv4 = data.Slice(frame.Offset, frame.Length);
        var sequenceOffset = (ipv4[0] & 15) * 4 + 4;
        foreach (var pending in stream.Pending)
        {
            if (pending.Frame.Inbound != frame.Inbound || pending.Frame.Sequence != sequence ||
                pending.Frame.Length != frame.Length || pending.Frame.Flags != frame.Flags ||
                pending.Frame.PayloadLength != frame.PayloadLength) continue;
            if (ipv4[..sequenceOffset].SequenceEqual(pending.Data.AsSpan(0, sequenceOffset)) &&
                ipv4[(sequenceOffset + 4)..].SequenceEqual(pending.Data.AsSpan(sequenceOffset + 4))) return true;
        }
        return false;
    }

    /// <summary>
    /// Feeds every owned stream and reports the ones that ended and the directions given up.
    ///
    /// The report is made after the lock is released, deliberately: the listener ends up in
    /// the state machine and therefore in a database transaction, and holding the reassembly
    /// lock across that would stall the decode thread behind a busy-retry. Ends and abandoned
    /// directions the reader thread found -- a new handshake replacing a decoding stream, a frame
    /// refused -- are reported here too, so every report follows the messages decoded before it.
    /// What the reader found since the last pump is reported before this pump decodes anything: the
    /// stream or direction it gave up delivers nothing more, and what this pump decodes came off the
    /// wire around or after it. Reported after, a stream replaced on its own four-tuple was told over
    /// only once the new stream on that tuple had started delivering (audit 2026-10-03, V2-3).
    /// The reader keeps running while those reports are made, so the decode starts only under a lock
    /// that finds nothing queued; what the reader found meanwhile is reported first, still outside the
    /// lock (audit 2026-10-03, S33-6).
    /// </summary>
    /// <param name="owned">Connections the operating system currently attributes to the game.</param>
    internal void Pump(IReadOnlyCollection<TCPConnection> owned)
    {
        try
        {
            while (TakeReportsOrPump(owned) is { } found)
            {
                Report(found);
            }
        }
        finally
        {
            Report();
        }
    }

    /// <summary>
    /// Under one lock: takes the reports queued so far, or, when there are none, decodes. Taking and
    /// decoding are one step for the reader thread, so nothing it finds before the decode can be
    /// reported after it.
    /// </summary>
    /// <param name="owned">Connections the operating system currently attributes to the game.</param>
    /// <returns>The reports to make before decoding, in the order they were found; null once decoded.</returns>
    private (TCPConnection Connection, StreamReport Kind)[]? TakeReportsOrPump(IReadOnlyCollection<TCPConnection> owned)
    {
        lock (_gate)
        {
            if (TakeReports() is { } found) return found;
            PumpCore(owned);
            return null;
        }
    }

    /// <summary>Takes the reports queued so far, in the order they were found; null when none are. Under the lock.</summary>
    private (TCPConnection Connection, StreamReport Kind)[]? TakeReports()
    {
        if (_reports.Count == 0) return null;
        var reports = _reports.ToArray();
        _reports.Clear();
        return reports;
    }

    /// <summary>Makes the reports queued so far, in the order they were found, outside the lock.</summary>
    private void Report()
    {
        (TCPConnection Connection, StreamReport Kind)[]? reports;
        lock (_gate)
        {
            reports = TakeReports();
        }

        if (reports is not null) Report(reports);
    }

    /// <summary>Makes <paramref name="reports"/>, in order. Never under the lock: the listeners reach the state machine.</summary>
    private void Report((TCPConnection Connection, StreamReport Kind)[] reports)
    {
        foreach (var (connection, kind) in reports)
        {
            if (kind == StreamReport.Ended)
            {
                _ownedStreamEnded?.Invoke(connection);
            }
            else
            {
                _directionDamaged?.Invoke(connection, kind == StreamReport.InboundDamaged);
            }
        }
    }

    private void PumpCore(IReadOnlyCollection<TCPConnection> owned)
    {
        lock (_gate)
        {
            if (_stopped || Failure is not null) return;
            // Ageing starts with the decode loop, not with the reader: everything captured
            // while Oodle initialized is prefix, and prefix is the one thing that cannot be
            // recovered by waiting.
            _decoding = true;
            var now = _clock();
            var confirmed = owned.Where(c => c.ProcessId == _pid && IsLocal(c.LocalIP))
                .GroupBy(c => new FirstPacketTuple(c.LocalIP, c.LocalPort, c.RemoteIP, c.RemotePort))
                .ToDictionary(group => group.Key, group => group.First());
            // A stream the system lists for the game right now is claimed by this very pump, so the
            // ownership age does not release it first: the first pump comes only after Oodle's
            // initialization, which can outlast that age (audit 2026-10-20, A1-5).
            Expire(confirmed);
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
                var hasDirect = confirmed.TryGetValue(tuple, out var connection);
                TCPConnection? reversedConnection = null;
                var hasReverse = stream.LocalEndpoints && confirmed.TryGetValue(tuple.Reverse, out reversedConnection);
                // Both endpoints owned by the selected process do not identify a single game
                // connection. Likewise, an already-decoding tuple may not silently switch its game
                // endpoint when the OS table changes: a fresh SYN is needed for a replacement.
                if (hasDirect && hasReverse || stream.Connection is not null && (hasDirect || hasReverse) &&
                    stream.ReverseGameDirection != hasReverse)
                {
                    ReportEnded(stream);
                    Remove(tuple);
                    continue;
                }
                if (hasDirect || hasReverse)
                {
                    if (hasReverse) connection = reversedConnection;
                    stream.MissingSince = null;
                    stream.Confirmed = true;
                    stream.ReverseGameDirection = hasReverse;
                    stream.Connection ??= connection;
                    stream.Decoder ??= _decoderFactory(connection!);
                }
                else
                {
                    // Counted once per tuple, not once per tick: the number the user needs is
                    // "how many observed connections were never the game's", which separates
                    // a wrong adapter from a wrong process.
                    if (!stream.Confirmed && !stream.CountedUnconfirmed)
                    { stream.CountedUnconfirmed = true; Interlocked.Increment(ref _unconfirmedTuples); }
                    if (stream.Decoder is null) continue;
                    // An owned, decoding stream the OS no longer lists is a connection that went
                    // away under us -- the fact DISCONNECTED is made of -- once the absence has
                    // lasted (see OwnershipLossConfirmation). Until then it may drain its captured
                    // tail, closing or not: the tuple cannot be reused without a new handshake,
                    // and a new handshake replaces the stream.
                    stream.MissingSince ??= now;
                }
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
                            // Only streams with a decoder get here, so the frame is the game's own
                            // and costs its direction -- reported with its connection, as at Offer
                            // (see Refuse) -- never the capture with every other connection in it
                            // (audit 2026-10-03, CS-8). Abandon drops the frame with the rest of
                            // that direction's queue -- a direction with anything queued is never
                            // already abandoned, since Offer admits nothing to one -- so the scan
                            // starts over.
                            if (AmbiguousDistance(sequence, next) || AmbiguousDistance(end, next))
                            { Abandon(stream, packet.Frame.Inbound); progress = true; break; }
                            if (sequence > next) continue;
                        }
                        stream.Pending.RemoveAt(i);
                        _bytes -= packet.Data.Length; _packets--;
                        try
                        {
                            if (direction.Next is null || end > direction.Next.Value)
                            {
                                stream.Decoder.Feed(packet.Data, packet.Frame.Inbound ^ stream.ReverseGameDirection);
                                direction.Next = end;
                            }
                        }
                        finally { Array.Clear(packet.Data); }
                        if (packet.Frame.Closed)
                        {
                            // FIN or RST on a stream that has a decoder: the game's own
                            // connection is over, not merely quiet.
                            ReportEnded(stream);
                            Remove(tuple);
                            progress = false;
                            break;
                        }
                        progress = true;
                        break;
                    }
                } while (progress);
                if (stream.Decoder is not null && stream.MissingSince is { } since &&
                    now - since >= OwnershipLossConfirmation)
                {
                    ReportEnded(stream);
                    Remove(tuple);
                }
            }
        }
    }

    /// <summary>
    /// Queues one "this connection ended" report, once per stream, for the next Pump to make
    /// after the lock is released. A stream is only worth reporting when it actually decoded
    /// something: the client opens and closes lobby connections constantly before login, and
    /// none of that is evidence about a run.
    /// </summary>
    /// <param name="stream">Stream that is about to be removed.</param>
    private void ReportEnded(StreamState stream)
    {
        if (stream.Reported || stream.Connection is not { } connection) return;
        stream.Reported = true;
        _reports.Add((connection, StreamReport.Ended));
    }

    internal void Tick() { lock (_gate) { if (!_stopped && Failure is null) Expire(); } }
    private bool IsLocal(uint address) => _localIPs.Contains(address) || (_includeLoopback && (address & 0xff) == 127);
    private static bool AmbiguousDistance(uint left, uint right) => Math.Abs((long)left - right) >= 0x80000000L;

    /// <summary>
    /// Where a frame starts and ends counted from its direction's own SYN, which is zero: the
    /// numbers this buffer decides on and Machina is fed. The end takes in SYN and FIN, and is wide
    /// so that crossing zero in these numbers can be seen.
    /// </summary>
    /// <param name="frame">Frame as it was on the wire.</param>
    /// <param name="synSequence">Wire sequence of the SYN that opened the frame's direction.</param>
    private static (uint Start, ulong End) Rebase(in FirstPacketFrame frame, uint synSequence)
    {
        var start = unchecked(frame.Sequence - synSequence);
        return (start, (ulong)start + (uint)frame.PayloadLength + (frame.Syn ? 1u : 0u) +
            ((frame.Flags & 1) != 0 ? 1u : 0u));
    }
    internal void Stop() { lock (_gate) { _stopped = true; Clear(); } }
    internal void Fault(UserText reason) { lock (_gate) Fail(reason); }
    private void Fail(UserText reason) { Failure ??= reason; Clear(); }
    private void Clear() { foreach (var tuple in _streams.Keys.ToArray()) Remove(tuple); }

    /// <summary>
    /// Ages streams once decoding has started. Two rules, deliberately different:
    /// a stream that never obtained a decoder is released whole after the ownership age,
    /// because its prefix is the only thing it holds; a stream that <em>has</em> a decoder
    /// only loses the packets stuck behind an unfillable gap, and only in that direction.
    /// Removing a live decoder over one lost packet would freeze a working session's counters
    /// at their last value with no error anywhere.
    ///
    /// Even with both directions gone the stream is kept, decoding nothing: losing them is not
    /// evidence that the connection ended. It is ended -- and reported -- by FIN/RST or by the
    /// operating system dropping it, like any other; removed silently it stayed counted as a
    /// delivering connection, so a real disconnect later was never reported (audit 2026-10-03,
    /// OA-5).
    /// </summary>
    /// <param name="confirmed">Tuples the pump calling this is about to confirm; never released for their age.</param>
    private void Expire(IReadOnlyDictionary<FirstPacketTuple, TCPConnection>? confirmed = null)
    {
        if (!_decoding) return;
        var now = _clock();
        // Under _gate, this pass only removes dictionary entries. .NET 8 permits
        // Remove during enumeration, so ingress need not allocate a full snapshot
        // for every packet and idle tick. No decoder callbacks run in this pass.
        foreach (var (tuple, stream) in _streams)
        {
            if (stream.Decoder is null)
            {
                if (now - stream.Created >= _limits.Ownership && confirmed?.ContainsKey(tuple) != true &&
                    (!stream.LocalEndpoints || confirmed?.ContainsKey(tuple.Reverse) != true))
                { Interlocked.Increment(ref _expiredStreams); Remove(tuple); }
                continue;
            }

            AbandonStaleDirection(stream, inbound: true, now);
            AbandonStaleDirection(stream, inbound: false, now);
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
        Abandon(stream, inbound);
    }

    /// <summary>
    /// Refuses one frame that cannot be decoded safely, for its own stream and nothing else.
    /// The pcap filter admits every program's TCP on this address, so a refusal that ended the
    /// capture would let any download crossing the 32-bit sequence wrap end a duty in flight
    /// (audit 2026-10-03, OA-1). A stream nobody owns yet is released whole, because its prefix
    /// can no longer be complete; a stream with a decoder gives up only the direction the frame
    /// travelled in, exactly as an unfillable gap does, so the decoder never sees the frame.
    /// </summary>
    /// <param name="tuple">Stream the frame belongs to.</param>
    /// <param name="stream">Its state.</param>
    /// <param name="inbound">Direction the frame travelled in.</param>
    private void Refuse(FirstPacketTuple tuple, StreamState stream, bool inbound)
    {
        if (stream.Decoder is not null) { Abandon(stream, inbound); return; }
        Interlocked.Increment(ref _streamResets);
        Remove(tuple);
    }

    /// <summary>Gives up one direction of a decoding stream for good, and counts it.</summary>
    private void Abandon(StreamState stream, bool inbound)
    {
        var direction = inbound ? stream.Inbound : stream.Outbound;
        if (direction.Damaged) return;

        // Machina's bundle decoder has no framing resynchronisation we can trust mid-stream,
        // so this direction is abandoned rather than fed a hole -- but it is counted, logged
        // and surfaced through silent_reason instead of silently taking the session with it.
        direction.Damaged = true;
        Interlocked.Increment(ref _streamResets);
        Interlocked.Increment(ref _damagedGameDirections);
        // Reported with its connection, like an end and in order with the ends, after the lock is
        // released: which connection lost a direction decides whether a run lost anything
        // (audit 2026-10-03, CS3a-X1).
        if (stream.Connection is { } connection)
        {
            _reports.Add((connection, inbound ^ stream.ReverseGameDirection ? StreamReport.InboundDamaged : StreamReport.OutboundDamaged));
        }

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

    /// <summary>What one queued report says about a decoding stream.</summary>
    private enum StreamReport { Ended, InboundDamaged, OutboundDamaged }

    private sealed class DirectionState
    {
        /// <summary>Wire sequence of this direction's SYN; Next and ClosedAt are counted from it (see Rebase).</summary>
        internal uint? SynSequence, Next;
        internal bool Damaged;

        /// <summary>Sequence end of the FIN/RST accepted in this direction; nothing beyond it is.</summary>
        internal uint? ClosedAt;
    }
    private sealed class StreamState(bool inbound, uint sequence, TimeSpan created, bool localEndpoints)
    {
        internal readonly bool OriginInbound = inbound;
        internal readonly uint OriginSequence = sequence;
        internal readonly TimeSpan Created = created;
        internal readonly bool LocalEndpoints = localEndpoints;
        // True only after the reverse wire tuple is uniquely confirmed for the game PID.
        internal bool ReverseGameDirection;
        internal readonly DirectionState Inbound = new(), Outbound = new();
        internal readonly List<Packet> Pending = new();
        internal IFirstPacketDecoder? Decoder;

        /// <summary>Connection this stream decodes, once ownership was confirmed.</summary>
        internal TCPConnection? Connection;
        internal bool Closing, Confirmed, CountedUnconfirmed;

        /// <summary>True once this stream's end has been reported; reported at most once.</summary>
        internal bool Reported;

        /// <summary>First reading, in the current run of readings, that did not list this stream.</summary>
        internal TimeSpan? MissingSince;
    }
}

internal interface IFirstPacketDecoder { void Feed(byte[] ipv4, bool inbound); }
