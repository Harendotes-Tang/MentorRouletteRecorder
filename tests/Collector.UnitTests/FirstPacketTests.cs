using System.Buffers.Binary;
using System.Net;
using Machina.Infrastructure;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class FirstPacketTests
{
    internal static readonly uint Local = Address("192.0.2.10"), Remote = Address("198.51.100.20");
    internal static uint Address(string value) => BinaryPrimitives.ReadUInt32LittleEndian(IPAddress.Parse(value).GetAddressBytes());
    internal static TCPConnection Owned(ushort localPort = 41000, uint pid = 42) => new()
    { LocalIP = Local, LocalPort = localPort, RemoteIP = Remote, RemotePort = 55000, ProcessId = pid };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(101)]
    [InlineData(108)]
    [InlineData(228)]
    public void PacketsBeforeOwnershipUseRealMachinaDecodersExactlyOnce(int link)
    {
        var messages = new List<(byte[] Bytes, bool Inbound)>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, inbound) => messages.Add((bytes.ToArray(), inbound))));
        var bundle = Bundle(0x1234);
        buffer.Offer(Packet(false, 100, 2, link: link), link);
        buffer.Offer(Packet(true, 500, 18, link: link), link);
        // Arrives out of sequence before the TCP ownership table catches up.
        buffer.Offer(Packet(true, 521, 24, bundle[20..], link), link);
        var prefix = Packet(true, 501, 24, bundle[..20], link);
        buffer.Offer(prefix, link);
        Array.Fill(prefix, (byte)0xee); // Native callback storage is reused by the next read.
        buffer.Pump(Array.Empty<TCPConnection>());
        Assert.Empty(messages);
        buffer.Pump(new[] { Owned() });
        Assert.Single(messages);
        Assert.True(messages[0].Inbound);
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(messages[0].Bytes.AsSpan(18)));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, messages[0].Bytes[32..]);
        // Retransmission cannot duplicate an IPC event.
        buffer.Offer(Packet(true, 501, 24, bundle, link), link);
        buffer.Pump(new[] { Owned() });
        Assert.Single(messages);
        Assert.Equal(0, buffer.Usage.Bytes);
        Assert.Equal(0, buffer.Usage.Packets);
    }

    [Fact]
    public void EachTupleAndDirectionHasIndependentMachinaState()
    {
        var messages = new List<(ushort Port, bool Inbound, ushort Opcode)>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (connection, _, bytes, inbound) => messages.Add((connection.LocalPort, inbound,
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))));
        foreach (var port in new ushort[] { 41000, 41001 })
        {
            buffer.Offer(Packet(false, 100, 2, port: port), 101);
            buffer.Offer(Packet(true, 900, 18, port: port), 101);
            buffer.Offer(Packet(true, 901, 24, Bundle(port), port: port), 101);
            buffer.Offer(Packet(false, 101, 24, Bundle((ushort)(port + 10)), port: port), 101);
        }
        buffer.Pump(new[] { Owned(), Owned(41001) });
        Assert.Equal(4, messages.Count);
        Assert.Contains(((ushort)41000, true, (ushort)41000), messages);
        Assert.Contains(((ushort)41000, false, (ushort)41010), messages);
        Assert.Contains(((ushort)41001, true, (ushort)41001), messages);
        Assert.Contains(((ushort)41001, false, (ushort)41011), messages);
    }

    [Fact]
    public void PidAndFullTupleMustMatchBeforeAnySemanticDecoderExists()
    {
        var created = 0;
        var buffer = new FirstPacketBuffer(Local, 42, _ => { created++; return new Sink(); });
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(false, 101, 24, Bundle(1)), 101);
        var wrongRemote = Owned(); wrongRemote.RemotePort++;
        var wrongIP = Owned(); wrongIP.LocalIP++;
        buffer.Pump(new[] { Owned(pid: 43), Owned(41001), wrongRemote, wrongIP });
        Assert.Equal(0, created);
        buffer.Stop();
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Fact]
    public void MissingOriginOrOppositeDirectionSynNeverDecodesContinuationButIsCounted()
    {
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink);
        buffer.Offer(Packet(true, 500, 18), 101);
        buffer.Offer(Packet(true, 501, 24, Bundle(1)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Empty(sink.Packets);
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(true, 501, 24, Bundle(1)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Empty(sink.Packets);

        // Refusing to decode is correct; refusing in silence makes a whole session read as a
        // healthy RUNNING capture with every counter at zero. Each refusal is counted.
        var counters = buffer.Counters;
        Assert.Equal(4, counters.RawPackets);
        Assert.Equal(2, counters.DroppedNoStream);
        Assert.Equal(1, counters.DroppedNoSyn);
        Assert.Equal(3, counters.DroppedBeforeDecode);

        // Not the all-dropped shape: the outbound SYN was accepted and opened a stream.
        // "Every frame was discarded" is the evidence the controller calls a mid-connection
        // attach on without waiting out its grace period, so one accepted handshake must be
        // enough to withhold that verdict.
        Assert.False(counters.AllDroppedBeforeDecode);
    }

    [Fact]
    public void ContinuationOnlyTrafficIsCountedAsDroppedForWantOfAStream()
    {
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink);

        // Exactly what a mid-stream attach looks like at the wire: data on a connection whose
        // handshake happened before this software was listening.
        for (var i = 0; i < 5; i++)
        {
            buffer.Offer(Packet(true, 501 + (uint)(i * 76), 24, Bundle(1)), 101);
        }

        buffer.Pump(new[] { Owned() });

        Assert.Empty(sink.Packets);
        Assert.Equal(5, buffer.Counters.RawPackets);
        Assert.Equal(5, buffer.Counters.DroppedNoStream);
        Assert.Equal(0, buffer.Counters.DroppedNoSyn);
        Assert.Null(buffer.Failure);

        // The shape the controller reads as MIDSTREAM: frames arrived, not one of them
        // survived reassembly, and no observer callback ever fired to say so. Note the
        // reason is DroppedNoStream -- at the wire a mid-connection attach has no tracked
        // stream at all, so a rule that looked only at DroppedNoSyn would never fire.
        Assert.True(buffer.Counters.AllDroppedBeforeDecode);
    }

    [Theory]
    [InlineData(40, 10, 10)]
    [InlineData(10000, 1, 10)]
    [InlineData(10000, 10, 1)]
    public void AFullBudgetReleasesUnclaimedStreamsWholeAndNeverDecodesAPartialPrefix(
        int bytes, int packets, int tuples)
    {
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink, new(bytes, packets, tuples));
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(false, 200, 2, port: 41001), 101);

        // Whichever budget binds, the answer is the same: the oldest unclaimed connection is
        // released whole -- prefix and all -- and the capture carries on. Faulting would be a
        // global answer to a local problem, and with the thirty-second ownership wait the
        // budget holds half a minute of every other program's traffic, so an ordinary download
        // would end a working session (docs/capture-diagnostics.md section 5.5).
        Assert.Null(buffer.Failure);
        Assert.Equal(1, buffer.Counters.ExpiredStreams);

        // The invariant the budget exists for is untouched: a released stream is gone, not
        // truncated, so nothing can resume it and no decoder is ever fed a partial prefix.
        buffer.Offer(Packet(false, 101, 24, Bundle(1)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Empty(sink.Packets);
    }

    [Fact]
    public void AFullBudgetOfLiveDecodersStillFailsClosed()
    {
        // Nothing left to give up: every tracked stream is already decoding, so accepting one
        // more would mean either truncating a live stream or growing without a bound. Both are
        // worse than stopping, and Failure surfaces the stop as a fault rather than as silence.
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink, new(Tuples: 1));
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Single(sink.Packets); // The SYN reached a real decoder: this stream is live.

        buffer.Offer(Packet(false, 200, 2, port: 41001), 101);

        Assert.NotNull(buffer.Failure);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Fact]
    public void OneUnownedDownloadCannotEvictAnAlreadyDecodingGameStream()
    {
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink, new(Packets: 4));
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Offer(Packet(false, 100, 2, port: 41001), 101);
        for (uint i = 0; i < 10; i++)
        {
            buffer.Offer(Packet(false, 101 + i * 100, 24, new byte[100], port: 41001), 101);
            buffer.Pump(new[] { Owned() });
        }
        Assert.Null(buffer.Failure);
        Assert.Equal(1, buffer.Counters.ExpiredStreams);
        buffer.Offer(Packet(false, 101, 24, Bundle(42)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Equal(2, sink.Packets.Count);
        Assert.Equal(1, buffer.Usage.Tuples);
    }

    [Fact]
    public void ThePrefixOutlivesOodleInitializationInsteadOfBeingAgedOutBeforeItCanDecode()
    {
        // Machina's default Oodle mode copies and signature-scans ffxiv_dx11.exe, which can
        // take well over the gap tolerance on a cold disk. The prefix captured while that
        // runs is the only chance this connection has, so it must not be aged out with the
        // same five seconds that tolerate a lost packet.
        var time = TimeSpan.Zero;
        var messages = new List<ushort>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, _) => messages.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)))), clock: () => time);
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(false, 101, 24, Bundle(7)), 101);

        time = TimeSpan.FromSeconds(8);
        buffer.Tick(); // The reader thread keeps ticking while the decode loop does not exist.

        buffer.Pump(new[] { Owned() });

        Assert.Equal(new ushort[] { 7 }, messages);
        Assert.Equal(0, buffer.Counters.ExpiredStreams);
    }

    [Fact]
    public void AHandshakeIsCountedForEveryConnectionAndTheGameSConnectionsAreCountedApart()
    {
        // The two numbers that separate "capture started too late" from "the client
        // reconnected and its handshake was lost". Without them both situations arrive as a
        // report with zero decoded messages, and only one of them is answered by asking the
        // player to log in again.
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink);

        buffer.Offer(Packet(false, 100, 2), 101);                    // the game's own SYN
        buffer.Offer(Packet(false, 300, 2, port: 41001), 101);       // somebody else's
        buffer.Pump(new[] { Owned() });

        Assert.Equal(2, buffer.Counters.Handshakes);
        Assert.Equal(1, buffer.Counters.GameConnections);
        Assert.Equal(1, buffer.Counters.GameConnectionsNow);
        Assert.Equal(1, buffer.Counters.UnconfirmedTuples);

        // A second connection on the same process is a reconnection: the running total grows
        // even though only one connection is live at a time.
        buffer.Pump(new[] { Owned(41002) });

        Assert.Equal(2, buffer.Counters.GameConnections);
        Assert.Equal(1, buffer.Counters.GameConnectionsNow);
        Assert.True(buffer.Counters.ReconnectedUnreadable(1));
        Assert.False(buffer.Counters.ReconnectedUnreadable(2));
    }

    [Fact]
    public void AStreamThatIsNeverConfirmedIsCountedAndReleasedOnTheOwnershipAge()
    {
        var time = TimeSpan.Zero;
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink, clock: () => time);
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Pump(Array.Empty<TCPConnection>());

        // Counted once per tuple, so "packets arrive but none of them are the game's" is a
        // number rather than an inference from silence.
        Assert.Equal(1, buffer.Counters.UnconfirmedTuples);
        Assert.Equal(0, buffer.Counters.ExpiredStreams);

        time = TimeSpan.FromSeconds(31);
        buffer.Pump(Array.Empty<TCPConnection>());

        Assert.Equal(1, buffer.Counters.ExpiredStreams);
        Assert.Equal((0, 0, 0), buffer.Usage);
        Assert.Empty(sink.Packets);
    }

    [Fact]
    public void AnUnfillableGapAbandonsOneDirectionAndKeepsTheConnectionDecoding()
    {
        // One lost packet must not delete the stream and its decoder: that freezes every
        // counter of a live connection with no error anywhere. Only the direction with the
        // hole is given up, and it is counted.
        var time = TimeSpan.Zero;
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, inbound) => messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            clock: () => time);
        buffer.Offer(Packet(false, 100, 2), 101);  // outbound SYN
        buffer.Offer(Packet(true, 500, 18), 101);  // inbound SYN/ACK
        buffer.Pump(new[] { Owned() });

        // An outbound segment arrives after one that never will: it can only be deferred.
        buffer.Offer(Packet(false, 101 + 76, 24, Bundle(3)), 101);
        time = TimeSpan.FromSeconds(5);
        buffer.Pump(new[] { Owned() });

        Assert.Empty(messages);
        Assert.Equal(1, buffer.Counters.StreamResets);

        // The other direction -- the one that carries everything this software records --
        // still decodes on the same connection and the same decoder.
        buffer.Offer(Packet(true, 501, 24, Bundle(9)), 101);
        buffer.Pump(new[] { Owned() });

        Assert.Equal(new[] { (true, (ushort)9) }, messages);
        Assert.Equal(1, buffer.Counters.StreamResets);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void ClosedAndReusedTuplesDiscardOldState(byte closeFlag)
    {
        var messages = new List<ushort>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, _) => messages.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)))));
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(false, 101, 24, Bundle(1)[..20]), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Offer(Packet(false, 121, closeFlag), 101);
        buffer.Offer(Packet(false, 121, 24, Bundle(1)[20..]), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Empty(messages);
        buffer.Offer(Packet(false, 1000, 2), 101);
        buffer.Offer(Packet(false, 1001, 24, Bundle(2)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Equal(new ushort[] { 2 }, messages);
    }

    [Fact]
    public void ChangedSynSequenceAndLostOwnershipDiscardDecoderState()
    {
        var created = 0;
        var buffer = new FirstPacketBuffer(Local, 42, _ => { created++; return new Sink(); });
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Offer(Packet(false, 200, 2), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Equal(2, created);
        buffer.Pump(Array.Empty<TCPConnection>());
        buffer.Offer(Packet(false, 201, 24, Bundle(1)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Equal(2, created);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Fact]
    public void SynPayloadIsRejectedInsteadOfLosingTcpFastOpenPrefix()
    {
        var buffer = new FirstPacketBuffer(Local, 42, _ => new Sink());
        buffer.Offer(Packet(false, 100, 2, Bundle(1)), 101);
        Assert.NotNull(buffer.Failure);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    public void ConfirmedTailAndCloseInOnePumpPreserveTheLastCompleteIpc(byte flags, bool finCarriesData)
    {
        var messages = new List<byte[]>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, _) => messages.Add(bytes.ToArray())));
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Pump(new[] { Owned() });
        var bundle = Bundle(9);
        if (!finCarriesData) buffer.Offer(Packet(false, 101, 24, bundle), 101);
        buffer.Offer(Packet(false, finCarriesData ? 101u : 101u + (uint)bundle.Length,
            flags, finCarriesData ? bundle : null), 101);
        // The OS row may disappear before the next pump; existing ownership authorizes only
        // the already-captured tail ending at the close marker, never a reused tuple.
        buffer.Pump(Array.Empty<TCPConnection>());
        Assert.Single(messages);
        Assert.Equal((0, 0, 0), buffer.Usage);
        buffer.Offer(Packet(false, 101u + (uint)bundle.Length, 24, Bundle(10)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Single(messages);
    }

    [Theory]
    [InlineData(uint.MaxValue - 76, 0)] // Data ends at zero: upstream can emit it indefinitely.
    [InlineData(uint.MaxValue - 50, 0)] // Data crosses zero.
    [InlineData(uint.MaxValue - 76, 20)] // Overlap retransmission ends at zero.
    [InlineData(uint.MaxValue - 50, 20)] // Overlap retransmission crosses zero.
    public void SequenceWrapIsRejectedBeforeTheActualMachinaDecoderCanLoop(uint synSequence, int prefixLength)
    {
        var messages = 0;
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, _, _) => messages++));
        buffer.Offer(Packet(false, synSequence, 2), 101);
        buffer.Pump(new[] { Owned() }); // A non-wrapping SYN is safe in the actual dependency.
        var bundle = Bundle(9);
        var start = synSequence + 1;
        if (prefixLength != 0)
        {
            buffer.Offer(Packet(false, start, 24, bundle[..prefixLength]), 101);
            buffer.Pump(new[] { Owned() }); // Safe prefix stops short of the uint boundary.
        }
        var overlapOffset = prefixLength / 2;
        buffer.Offer(Packet(false, start + (uint)overlapOffset, 24, bundle[overlapOffset..]), 101);
        // This assertion precedes Pump deliberately: a broken guard must fail the test,
        // never execute the known non-terminating dependency path during a RED run.
        Assert.NotNull(buffer.Failure);
        Assert.Equal((0, 0, 0), buffer.Usage);
        buffer.Pump(new[] { Owned() });
        buffer.Stop();
        Assert.Equal(0, messages);
    }

    [Fact]
    public void MaximumSynSequenceIsRejectedBeforeZeroCanBecomeMachinaNextSequence()
    {
        var created = 0;
        var buffer = new FirstPacketBuffer(Local, 42, c =>
        {
            created++;
            return new FirstPacketDecoder(c, (_, _, _, _) => { });
        });
        buffer.Offer(Packet(false, uint.MaxValue, 2), 101);
        Assert.NotNull(buffer.Failure);
        buffer.Pump(new[] { Owned() });
        buffer.Stop();
        Assert.Equal(0, created);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Theory]
    [InlineData(uint.MaxValue - 200, 12u)] // A low-sequence continuation after an uncaptured wrap.
    [InlineData(100u, 0x80000065u)] // Exactly half the sequence space is ambiguous.
    public void AmbiguousSequenceDistanceFaultsWithoutAdvancingTheDecoder(uint synSequence, uint sequence)
    {
        var sink = new Sink();
        var buffer = new FirstPacketBuffer(Local, 42, _ => sink);
        buffer.Offer(Packet(false, synSequence, 2), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Offer(Packet(false, sequence, 24, Bundle(9)), 101);
        Assert.NotNull(buffer.Failure);
        buffer.Pump(new[] { Owned() });
        Assert.Single(sink.Packets); // Only the established SYN reached the decoder.
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Theory]
    [InlineData(uint.MaxValue - 77)] // End at uint.MaxValue remains valid; only crossing zero is refused.
    [InlineData(0u)] // Initial SYN zero is valid because its next sequence is one.
    public void NonWrappingBoundarySequencesStillDecodeExactlyOnce(uint synSequence)
    {
        var messages = 0;
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, _, _) => messages++));
        buffer.Offer(Packet(false, synSequence, 2), 101);
        buffer.Offer(Packet(false, synSequence + 1, 24, Bundle(9)), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Offer(Packet(false, synSequence + 1, 24, Bundle(9)), 101);
        buffer.Pump(new[] { Owned() });
        buffer.Stop();
        Assert.Null(buffer.Failure);
        Assert.Equal(1, messages);
    }

    [Fact]
    public void AFullBudgetGivesUpTheOldestUnclaimedConnectionInsteadOfTheWholeCapture()
    {
        // The pcap filter is "this local address", so every program on the machine competes
        // for this budget, and ownership is held open for thirty seconds. Faulting here would
        // let one download end a working capture; the fault is global while the loss is not,
        // since the game's own stream is the one with the decoder.
        var time = TimeSpan.Zero;
        var messages = new List<ushort>();
        var buffer = new FirstPacketBuffer(Local, 42, c => new FirstPacketDecoder(c,
            (_, _, bytes, _) => messages.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18)))),
            new FirstPacketLimits(Tuples: 2), clock: () => time);

        // The game's connection, confirmed and decoding.
        buffer.Offer(Packet(false, 100, 2), 101);
        buffer.Offer(Packet(true, 500, 18), 101);
        buffer.Pump(new[] { Owned() });

        // Two more handshakes from somebody else. The budget is two, so the second one has to
        // displace the first rather than end everything.
        time = TimeSpan.FromSeconds(1);
        buffer.Offer(Packet(false, 100, 2, port: 41001), 101);
        time = TimeSpan.FromSeconds(2);
        buffer.Offer(Packet(false, 100, 2, port: 41002), 101);

        Assert.Null(buffer.Failure);
        Assert.Equal(1, buffer.Counters.ExpiredStreams);

        // And the connection that was actually decoding is untouched.
        buffer.Offer(Packet(true, 501, 24, Bundle(5)), 101);
        buffer.Pump(new[] { Owned() });
        Assert.Equal(new ushort[] { 5 }, messages);
    }

    internal sealed class Sink : IFirstPacketDecoder
    {
        internal readonly List<byte[]> Packets = new();
        public void Feed(byte[] ipv4, bool inbound) => Packets.Add(ipv4.ToArray());
    }

    internal static byte[] Packet(bool inbound, uint sequence, byte flags, byte[]? payload = null,
        int link = 101, ushort port = 41000)
    {
        payload ??= Array.Empty<byte>();
        var offset = link switch { 1 => 14, 0 or 108 => 4, _ => 0 };
        var bytes = new byte[offset + 40 + payload.Length];
        if (link == 1) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 0x0800);
        if (link == 0) BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        if (link == 108) BinaryPrimitives.WriteUInt32BigEndian(bytes, 2);
        var ip = bytes.AsSpan(offset);
        ip[0] = 0x45; ip[8] = 64; ip[9] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ip.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], inbound ? Remote : Local);
        BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], inbound ? Local : Remote);
        BinaryPrimitives.WriteUInt16BigEndian(ip[20..], inbound ? (ushort)55000 : port);
        BinaryPrimitives.WriteUInt16BigEndian(ip[22..], inbound ? port : (ushort)55000);
        BinaryPrimitives.WriteUInt32BigEndian(ip[24..], sequence);
        ip[32] = 0x50; ip[33] = flags;
        payload.CopyTo(ip[40..]);
        return bytes;
    }

    internal static byte[] Bundle(ushort opcode)
    {
        var bundle = new byte[76];
        BinaryPrimitives.WriteUInt32LittleEndian(bundle, 0x41a05252);
        BinaryPrimitives.WriteUInt64LittleEndian(bundle.AsSpan(16), 123456789);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(24), (uint)bundle.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bundle.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bundle.AsSpan(36), 36);
        var segment = bundle.AsSpan(40);
        BinaryPrimitives.WriteUInt32LittleEndian(segment, 36);
        BinaryPrimitives.WriteUInt16LittleEndian(segment[12..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(segment[18..], opcode);
        new byte[] { 1, 2, 3, 4 }.CopyTo(segment[32..]);
        return bundle;
    }
}
