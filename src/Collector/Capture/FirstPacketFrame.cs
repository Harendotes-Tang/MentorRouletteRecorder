using System.Buffers.Binary;

namespace MentorRecorder.Collector.Capture;

internal readonly record struct FirstPacketTuple(uint LocalIP, ushort LocalPort, uint RemoteIP, ushort RemotePort)
{
    internal FirstPacketTuple Reverse => new(RemoteIP, RemotePort, LocalIP, LocalPort);
}

/// <summary>Validated IPv4/TCP boundaries. Addresses retain Machina's native little endian representation.</summary>
internal readonly record struct FirstPacketFrame(FirstPacketTuple Tuple, bool Inbound, uint Sequence,
    byte Flags, int PayloadLength, int Offset, int Length, bool LocalEndpoints = false)
{
    public bool Syn => (Flags & 2) != 0;
    public bool Origin => Syn && (Flags & 16) == 0;
    public bool Closed => (Flags & 5) != 0;

    // Link numbers are the libpcap DLT values, not heuristics on the packet contents.
    internal static bool TryRead(ReadOnlySpan<byte> data, int link, uint localIP,
        out FirstPacketFrame frame, out bool unsafeSelectedPacket)
        => TryRead(data, link, new[] { localIP }, false, out frame, out unsafeSelectedPacket);

    // With two local endpoints, Tuple is initially the wire source/target tuple. Inbound is
    // only a coordinate within that wire tuple until Pump resolves the complete game-owned
    // tuple. Neither address equality nor port magnitude establishes game direction.
    internal static bool TryRead(ReadOnlySpan<byte> data, int link, IReadOnlyCollection<uint> localIPs,
        bool includeLoopback, out FirstPacketFrame frame, out bool unsafeSelectedPacket)
    {
        frame = default;
        unsafeSelectedPacket = false;
        var offset = link switch { 1 => 14, 0 or 108 => 4, 12 or 101 or 228 => 0, _ => -1 };
        if (offset < 0 || data.Length < offset) return false;
        if (link == 1)
        {
            var ether = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
            for (var vlan = 0; ether is 0x8100 or 0x88a8 && vlan < 2; vlan++)
            {
                if (data.Length < offset + 4) return false;
                ether = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
                offset += 4;
            }
            if (ether != 0x0800) return false;
        }
        if (link is 0 or 108 && (data.Length < 4 ||
            (link == 0 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : BinaryPrimitives.ReadUInt32BigEndian(data)) != 2))
            return false;
        if (data.Length < offset + 20) return false;
        var ip = data[offset..];
        if (ip[0] >> 4 != 4 || ip[9] != 6) return false;
        var source = BinaryPrimitives.ReadUInt32LittleEndian(ip[12..]);
        var target = BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]);
        var sourceLocal = localIPs.Contains(source) || (includeLoopback && (source & 0xff) == 127);
        var targetLocal = localIPs.Contains(target) || (includeLoopback && (target & 0xff) == 127);
        if (!sourceLocal && !targetLocal) return false;
        unsafeSelectedPacket = true;
        var header = (ip[0] & 15) * 4;
        var size = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        // No fragment reaches Machina: later fragments cannot be associated with a full tuple.
        if (header < 20 || size < header + 20 || size > ip.Length ||
            (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0xbfff) != 0) return false;
        var tcp = ip.Slice(header, size - header);
        var tcpHeader = (tcp[12] >> 4) * 4;
        if (tcpHeader < 20 || tcpHeader > tcp.Length) return false;
        var from = BinaryPrimitives.ReadUInt16BigEndian(tcp);
        var to = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
        if (from == 0 || to == 0) return false;
        // A tuple that is its own reverse cannot identify either TCP direction, even with PID
        // ownership. Do not let it enter the stream table as a guessed outbound connection.
        if (sourceLocal && targetLocal && source == target && from == to) return false;
        var localEndpoints = sourceLocal && targetLocal;
        var inbound = !sourceLocal && targetLocal;
        frame = new FirstPacketFrame(inbound ? new(target, to, source, from) : new(source, from, target, to),
            inbound, BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), tcp[13], tcp.Length - tcpHeader, offset, size,
            localEndpoints);
        unsafeSelectedPacket = false;
        return true;
    }
}
