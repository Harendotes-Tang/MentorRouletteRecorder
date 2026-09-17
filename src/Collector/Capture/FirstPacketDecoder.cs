using Machina.Decoders;
using Machina.FFXIV;
using Machina.Headers;
using Machina.Infrastructure;

namespace MentorRecorder.Collector.Capture;

/// <summary>Each owned full tuple has separate inbound/outbound Machina IP, TCP and bundle state.</summary>
internal sealed class FirstPacketDecoder : IFirstPacketDecoder
{
    private readonly Chain _inbound, _outbound;
    private readonly TCPConnection _connection;
    private readonly Action<TCPConnection, long, byte[], bool> _message;

    internal FirstPacketDecoder(TCPConnection connection, Action<TCPConnection, long, byte[], bool> message)
    {
        _connection = connection; _message = message;
        _inbound = new(connection.RemoteIP, connection.LocalIP, connection.RemotePort, connection.LocalPort);
        _outbound = new(connection.LocalIP, connection.RemoteIP, connection.LocalPort, connection.RemotePort);
    }

    public void Feed(byte[] ipv4, bool inbound)
    {
        var chain = inbound ? _inbound : _outbound;
        chain.IP.FilterAndStoreData(ipv4, ipv4.Length);
        while (chain.IP.GetNextIPPayload() is { } tcp) chain.TCP.FilterAndStoreData(tcp);
        while (chain.TCP.GetNextTCPDatagram() is { } datagram)
        {
            chain.Bundle.StoreData(datagram);
            while (chain.Bundle.GetNextFFXIVMessage() is { } result)
                _message(_connection, result.Item1, result.Item2, inbound);
        }
    }

    private sealed class Chain(uint source, uint target, ushort sourcePort, ushort targetPort)
    {
        internal readonly IPDecoder IP = new(source, target, IPProtocol.TCP);
        internal readonly TCPDecoder TCP = new(sourcePort, targetPort);
        internal readonly FFXIVBundleDecoder Bundle = new();
    }
}
