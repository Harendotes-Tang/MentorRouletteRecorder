using System.Buffers.Binary;
using Machina.Infrastructure;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

public sealed class LoopbackPacketTests
{
    private static readonly uint Loopback = FirstPacketTests.Address("127.3.2.1");
    private static readonly uint OtherLocal = FirstPacketTests.Address("192.0.2.11");

    [Theory]
    [InlineData(0, true, 41000)]
    [InlineData(1, false, 55001)]
    [InlineData(12, true, 55001)]
    [InlineData(101, false, 41000)]
    [InlineData(108, true, 41000)]
    [InlineData(228, false, 55001)]
    public void SameAddressUsesOwnedTupleForBothRealMachinaDirections(int link, bool gameInitiates, int localPort)
    {
        var owned = Connection(Loopback, Loopback, (ushort)localPort);
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = RealBuffer(new[] { FirstPacketTests.Local }, messages, includeLoopback: true);

        Handshake(buffer, owned, gameInitiates, link);
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(11), link), link);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(22), link), link);
        buffer.Pump(Array.Empty<TCPConnection>());
        Assert.Empty(messages);
        // A normal local proxy owns the opposite tuple, under a different process id. That
        // reverse row is not evidence for, or ambiguity about, the selected game's direction.
        buffer.Pump(new[] { Reverse(owned, 43), owned });

        Assert.Equal(2, messages.Count);
        Assert.Contains((false, (ushort)11), messages);
        Assert.Contains((true, (ushort)22), messages);
        Assert.Equal(1, buffer.Counters.Handshakes);
        Assert.Equal((0, 0, 1), buffer.Usage);
        // Another path may expose a different link header for the identical TCP segment.
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(11), 0), 0);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(22), 1), 1);
        buffer.Pump(new[] { owned });
        Assert.Equal(2, messages.Count);
        Assert.Null(buffer.Failure);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("local-address")]
    [InlineData("remote-address")]
    [InlineData("local-port")]
    [InlineData("remote-port")]
    [InlineData("proxy-reverse")]
    public void IncorrectPidOrAnyTupleFieldCannotCreateDecoder(string mismatch)
    {
        var owned = Connection(Loopback, Loopback);
        var wrong = Connection(Loopback, Loopback);
        switch (mismatch)
        {
            case "pid": wrong.ProcessId = 43; break;
            case "local-address": wrong.LocalIP = FirstPacketTests.Address("127.3.2.2"); break;
            case "remote-address": wrong.RemoteIP = FirstPacketTests.Address("127.3.2.2"); break;
            case "local-port": wrong.LocalPort++; break;
            case "remote-port": wrong.RemotePort++; break;
            case "proxy-reverse": wrong = Reverse(owned, 43); break;
        }
        var created = 0;
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            connection => { created++; return new FirstPacketTests.Sink(); }, includeLoopback: true);
        Handshake(buffer, owned, gameInitiates: false);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(1)), 101);

        buffer.Pump(new[] { wrong });
        Assert.Equal(0, created);
        buffer.Pump(new[] { owned, Reverse(owned, 43) });
        Assert.Equal(1, created);
    }

    [Fact]
    public void BothEndpointsOwnedByGameAreAmbiguousAndRequireNewColdStart()
    {
        var owned = Connection(Loopback, Loopback);
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = RealBuffer(Array.Empty<uint>(), messages, includeLoopback: true);
        Handshake(buffer, owned);
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(1)), 101);
        buffer.Pump(new[] { owned, Reverse(owned, 42) });
        buffer.Pump(new[] { owned });
        Assert.Empty(messages);
        Assert.Equal((0, 0, 0), buffer.Usage);

        // A later unambiguous row cannot resurrect bytes that were rejected as ambiguous.
        buffer.Offer(Packet(owned, false, 177, 24, FirstPacketTests.Bundle(2)), 101);
        buffer.Pump(new[] { owned });
        Assert.Empty(messages);
        Handshake(buffer, owned);
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(3)), 101);
        buffer.Pump(new[] { owned });
        Assert.Equal(new[] { (false, (ushort)3) }, messages);
    }

    [Fact]
    public void OwnershipCannotReverseAnExistingDecoderWithoutAnotherSyn()
    {
        var owned = Connection(Loopback, Loopback);
        var ended = new List<TCPConnection>();
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            connection => new FirstPacketDecoder(connection, (_, _, bytes, inbound) =>
                messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            ownedStreamEnded: ended.Add, includeLoopback: true);
        Handshake(buffer, owned, gameInitiates: false);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(1)), 101);
        buffer.Pump(new[] { owned });
        buffer.Offer(Packet(owned, true, 577, 24, FirstPacketTests.Bundle(2)), 101);
        buffer.Pump(new[] { Reverse(owned, 42) });
        Assert.Single(messages);
        Assert.Single(ended);
        Assert.Same(owned, ended[0]);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Fact]
    public void DelayedReverseOwnershipKeepsColdPrefixThroughOwnershipAge()
    {
        var owned = Connection(Loopback, Loopback);
        var now = TimeSpan.Zero;
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            connection => new FirstPacketDecoder(connection, (_, _, bytes, inbound) =>
                messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            new FirstPacketLimits(OwnershipAge: TimeSpan.FromSeconds(2)), () => now, includeLoopback: true);
        Handshake(buffer, owned, gameInitiates: false);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(7)), 101);
        now = TimeSpan.FromSeconds(3);
        buffer.Pump(new[] { owned });
        Assert.Equal(new[] { (true, (ushort)7) }, messages);
        Assert.Equal(0, buffer.Counters.ExpiredStreams);
    }

    [Fact]
    public void MultipleSelectedLocalAddressesShareOneBudgetAndDecodeTheirOwnConnections()
    {
        var direct = Connection(FirstPacketTests.Local, FirstPacketTests.Remote);
        var virtualPath = Connection(OtherLocal, FirstPacketTests.Remote, 41001);
        var localForwarder = Connection(FirstPacketTests.Local, OtherLocal, 41002);
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = RealBuffer(new[] { FirstPacketTests.Local, OtherLocal }, messages);
        foreach (var owned in new[] { direct, virtualPath, localForwarder })
        {
            Handshake(buffer, owned, gameInitiates: false);
            buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(owned.LocalPort)), 101);
            buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(owned.LocalPort)), 101);
        }
        buffer.Pump(new[] { direct, virtualPath, localForwarder });
        Assert.Equal(6, messages.Count);
        Assert.All(new ushort[] { 41000, 41001, 41002 }, port =>
        {
            Assert.Contains((true, port), messages);
            Assert.Contains((false, port), messages);
        });
        Assert.Equal((0, 0, 3), buffer.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedPathCopiesDoNotSpendPendingBudget(bool localEndpoints)
    {
        var owned = localEndpoints ? Connection(Loopback, Loopback) : FirstPacketTests.Owned();
        var messages = new List<(bool Inbound, ushort Opcode)>();
        // Exactly enough for the two SYN packets and one real bundle: a duplicate admitted
        // even once would evict the unowned stream and lose its cold prefix.
        var buffer = new FirstPacketBuffer(new[] { FirstPacketTests.Local }, 42,
            connection => new FirstPacketDecoder(connection, (_, _, bytes, inbound) =>
                messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            new FirstPacketLimits(Bytes: 196, Packets: 3, Tuples: 1), includeLoopback: localEndpoints);
        Handshake(buffer, owned, gameInitiates: false);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(9)), 101);
        var initialUsage = buffer.Usage;
        for (var copy = 0; copy < 5000; copy++)
        {
            buffer.Offer(Packet(owned, true, 500, 2, link: 0), 0);
            buffer.Offer(Packet(owned, false, 100, 18, link: 1), 1);
            buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(9), 108), 108);
        }
        Assert.Equal((196, 3, 1), initialUsage);
        Assert.Equal(initialUsage, buffer.Usage);
        Assert.Equal(1, buffer.Counters.Handshakes);
        Assert.Equal(0, buffer.Counters.ExpiredStreams);
        buffer.Pump(new[] { owned });
        Assert.Equal(new[] { (true, (ushort)9) }, messages);
        Assert.Equal((0, 0, 1), buffer.Usage);
        Assert.Null(buffer.Failure);
    }

    [Fact]
    public void TwoUnrelatedInterfaceFlowsCompeteForTheSameTupleLimit()
    {
        var buffer = new FirstPacketBuffer(new[] { FirstPacketTests.Local, OtherLocal }, 42,
            _ => new FirstPacketTests.Sink(), new FirstPacketLimits(Tuples: 1));
        buffer.Offer(Packet(Connection(FirstPacketTests.Local, FirstPacketTests.Remote), false, 100, 2), 101);
        buffer.Offer(Packet(Connection(OtherLocal, FirstPacketTests.Remote), false, 100, 2), 101);
        Assert.Equal(1, buffer.Usage.Tuples);
        Assert.Equal(1, buffer.Counters.ExpiredStreams);
        Assert.Null(buffer.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopbackStillRequiresOriginAndSynForEachDirection(bool originOnly)
    {
        var owned = Connection(Loopback, Loopback);
        var created = 0;
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            _ => { created++; return new FirstPacketTests.Sink(); }, includeLoopback: true);
        if (originOnly) buffer.Offer(Packet(owned, false, 100, 2), 101);
        else buffer.Offer(Packet(owned, true, 500, 18), 101);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(1)), 101);
        buffer.Pump(new[] { owned });
        Assert.Equal(0, created);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Fact]
    public void IdenticalEndpointsCannotSupplyAUsableDirection()
    {
        var owned = Connection(Loopback, Loopback, 55000);
        var created = 0;
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            _ => { created++; return new FirstPacketTests.Sink(); }, includeLoopback: true);
        buffer.Offer(Packet(owned, false, 100, 2), 101);
        buffer.Pump(new[] { owned });
        Assert.Equal(0, created);
        Assert.Equal(0, buffer.Usage.Tuples);
        Assert.Equal(1, buffer.Counters.DroppedNoStream);
    }

    [Fact]
    public void LoopbackRangeRequiresRetainedLoopbackCapabilityOrExplicitAddress()
    {
        var owned = Connection(Loopback, Loopback);
        var buffer = new FirstPacketBuffer(new[] { FirstPacketTests.Local }, 42,
            _ => new FirstPacketTests.Sink());
        Handshake(buffer, owned);
        buffer.Pump(new[] { owned });
        Assert.Equal((0, 0, 0), buffer.Usage);
        Assert.Equal(0, buffer.Counters.RawPackets);
    }

    [Fact]
    public void ReverseWireDirectionDamageAndEndUseCanonicalGameConnection()
    {
        var owned = Connection(Loopback, Loopback);
        var damaged = new List<(TCPConnection Connection, bool Inbound)>();
        var ended = new List<TCPConnection>();
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = new FirstPacketBuffer(Array.Empty<uint>(), 42,
            connection => new FirstPacketDecoder(connection, (_, _, bytes, inbound) =>
                messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            ownedStreamEnded: ended.Add, directionDamaged: (connection, inbound) => damaged.Add((connection, inbound)),
            includeLoopback: true);
        Handshake(buffer, owned, gameInitiates: false);
        buffer.Offer(Packet(owned, true, 501, 24, FirstPacketTests.Bundle(1)), 101);
        buffer.Pump(new[] { owned });
        buffer.Offer(Packet(owned, true, 0x8000_0300, 24, new byte[] { 1 }), 101);
        buffer.Pump(new[] { owned });
        Assert.Equal(new[] { (owned, true) }, damaged);
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(2)), 101);
        buffer.Offer(Packet(owned, false, 177, 17), 101);
        buffer.Pump(new[] { owned });
        Assert.Contains((false, (ushort)2), messages);
        Assert.Equal(new[] { owned }, ended);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameAddressDirectionsKeepIndependentSequenceStateAcrossWireWrap(bool gameInitiates)
    {
        var owned = Connection(Loopback, Loopback);
        var messages = new List<(bool Inbound, ushort Opcode)>();
        var buffer = RealBuffer(Array.Empty<uint>(), messages, includeLoopback: true);
        const uint inboundSyn = uint.MaxValue - 50;
        buffer.Offer(Packet(owned, !gameInitiates, gameInitiates ? 100u : inboundSyn, 2), 101);
        buffer.Offer(Packet(owned, gameInitiates, gameInitiates ? inboundSyn : 100u, 18), 101);
        buffer.Pump(new[] { owned });
        var bundle = FirstPacketTests.Bundle(4);
        // The second half is observed first, and the bundle spans the wire's uint wrap.
        buffer.Offer(Packet(owned, true, unchecked(inboundSyn + 21), 24, bundle[20..]), 101);
        buffer.Offer(Packet(owned, true, inboundSyn + 1, 24, bundle[..20]), 101);
        buffer.Offer(Packet(owned, false, 101, 24, FirstPacketTests.Bundle(5)), 101);
        buffer.Pump(new[] { owned });
        Assert.Equal(2, messages.Count);
        Assert.Contains((true, (ushort)4), messages);
        Assert.Contains((false, (ushort)5), messages);
        Assert.Equal(0, buffer.Counters.DamagedGameDirections);
        Assert.Null(buffer.Failure);
    }

    [Fact]
    public void StopReleasesAllPendingPathsAndPreventsOwnershipFromCreatingDecoders()
    {
        var owned = Connection(Loopback, Loopback);
        var created = 0;
        var buffer = new FirstPacketBuffer(new[] { FirstPacketTests.Local, OtherLocal }, 42,
            _ => { created++; return new FirstPacketTests.Sink(); }, includeLoopback: true);
        foreach (var connection in new[] { owned, Connection(FirstPacketTests.Local, OtherLocal), FirstPacketTests.Owned() })
        {
            Handshake(buffer, connection);
            buffer.Offer(Packet(connection, true, 501, 24, FirstPacketTests.Bundle(1)), 101);
        }
        Assert.Equal(3, buffer.Usage.Tuples);
        buffer.Stop();
        buffer.Pump(new[] { owned });
        Handshake(buffer, owned);
        Assert.Equal(0, created);
        Assert.Equal((0, 0, 0), buffer.Usage);
    }

    private static FirstPacketBuffer RealBuffer(IReadOnlyCollection<uint> localIPs,
        List<(bool Inbound, ushort Opcode)> messages, bool includeLoopback = false) =>
        new(localIPs, 42, connection => new FirstPacketDecoder(connection, (_, _, bytes, inbound) =>
            messages.Add((inbound, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18))))),
            includeLoopback: includeLoopback);

    private static TCPConnection Connection(uint local, uint remote, ushort localPort = 41000) => new()
    { LocalIP = local, RemoteIP = remote, LocalPort = localPort, RemotePort = 55000, ProcessId = 42 };

    private static TCPConnection Reverse(TCPConnection connection, uint pid) => new()
    {
        LocalIP = connection.RemoteIP, RemoteIP = connection.LocalIP,
        LocalPort = connection.RemotePort, RemotePort = connection.LocalPort, ProcessId = pid
    };

    private static void Handshake(FirstPacketBuffer buffer, TCPConnection connection, bool gameInitiates = true, int link = 101)
    {
        buffer.Offer(Packet(connection, !gameInitiates, gameInitiates ? 100u : 500u, 2, link: link), link);
        buffer.Offer(Packet(connection, gameInitiates, gameInitiates ? 500u : 100u, 18, link: link), link);
    }

    private static byte[] Packet(TCPConnection connection, bool inbound, uint sequence, byte flags,
        byte[]? payload = null, int link = 101)
    {
        var bytes = FirstPacketTests.Packet(inbound, sequence, flags, payload, link, connection.LocalPort);
        var offset = link switch { 1 => 14, 0 or 108 => 4, _ => 0 };
        var ip = bytes.AsSpan(offset);
        BinaryPrimitives.WriteUInt32LittleEndian(ip[12..], inbound ? connection.RemoteIP : connection.LocalIP);
        BinaryPrimitives.WriteUInt32LittleEndian(ip[16..], inbound ? connection.LocalIP : connection.RemoteIP);
        BinaryPrimitives.WriteUInt16BigEndian(ip[20..], inbound ? connection.RemotePort : connection.LocalPort);
        BinaryPrimitives.WriteUInt16BigEndian(ip[22..], inbound ? connection.LocalPort : connection.RemotePort);
        return bytes;
    }
}
