using System.Net;
using MentorRecorder.Collector.Contracts.Errors;
using SharpPcap;
using SharpPcap.LibPcap;

namespace MentorRecorder.Collector.Capture;

internal delegate void NpcapPacketReceiver(ReadOnlySpan<byte> packet, int linkType);

/// <summary>Read-only native boundary. Caller owns the reader thread and must join it before disposal.</summary>
internal interface INpcapPacketReader : IDisposable
{
    void Open();
    bool Read(NpcapPacketReceiver receive);

    /// <summary>Packets Npcap reported as lost so far, at the driver or at the interface.</summary>
    long DroppedPackets => 0;
}

/// <summary>
/// One exact Npcap device, synchronous nonblocking reads, no SharpPcap background capture queues.
/// The independent native kernel buffer is 16 MiB; it is outside the application's raw packet budget.
/// </summary>
internal sealed class NpcapPacketReader : INpcapPacketReader
{
    /// <summary>Lost packets tolerated before the capture is declared unable to frame a stream.</summary>
    internal const long DropFaultFloor = 64;

    /// <summary>Loss ratio, as a percentage of accepted packets, above which capture faults.</summary>
    internal const long DropFaultPercent = 5;

    /// <summary>
    /// Shown when the selected card has left the system device list. No jargon and no advice to
    /// reinstall: the ordinary cause is a 加速器 or VPN card appearing or disappearing between
    /// the recommendation and the open, and the adapter is re-chosen within a second
    /// (review finding H-1).
    /// </summary>
    public const string AdapterListChangedMessage =
        "网卡列表已经发生变化，刚才选中的网卡现在找不到了。正在重新扫描并挑选承载游戏流量的网卡，请稍候。";

    private readonly CaptureStartOptions _options;
    private readonly Func<IReadOnlyList<LibPcapLiveDevice>> _listDevices;
    private LibPcapLiveDevice? _device;
    private long _lastStatistics;
    private long _dropped, _received;

    /// <summary>Creates a reader over the machine's live device list.</summary>
    /// <param name="options">Session and adapter selection.</param>
    /// <param name="listDevices">Device enumeration; null enumerates the live machine on every call.</param>
    internal NpcapPacketReader(
        CaptureStartOptions options, Func<IReadOnlyList<LibPcapLiveDevice>>? listDevices = null)
    {
        _options = options;
        _listDevices = listDevices ?? EnumerateDevices;
    }

    /// <inheritdoc />
    public long DroppedPackets => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Enumerates the machine's capture devices freshly on every call.
    ///
    /// <c>LibPcapLiveDeviceList.Instance</c> is a lazy static singleton: it calls
    /// <c>pcap_findalldevs</c> once per process and answers from that snapshot, so a card
    /// created afterwards -- a 加速器 TAP adapter, a VPN interface, a card that changed address
    /// over DHCP -- stays invisible to it while <see cref="AdapterEnumerator"/> lists the real
    /// machine and recommends that very card. <c>New()</c> asks the driver again
    /// (review finding H-1).
    /// </summary>
    private static IReadOnlyList<LibPcapLiveDevice> EnumerateDevices() =>
        LibPcapLiveDeviceList.New().ToArray();

    /// <summary>
    /// Refusal raised when the selected card has left the device list. Bad-request rather than
    /// "Npcap is missing": the driver is fine and only the selection is stale, so the follow
    /// poll retries on its one-second cadence instead of backing off for thirty seconds behind
    /// a message telling the user to reinstall Npcap (review finding H-1).
    /// </summary>
    /// <param name="adapterId">Adapter that could not be resolved.</param>
    public static CollectorException AdapterListChanged(string? adapterId) => new(
        ErrorCodes.BadRequest,
        AdapterListChangedMessage,
        new Dictionary<string, object?> { ["adapter_id"] = adapterId },
        field: "adapter_id",
        retryable: true);

    public void Open()
    {
        var address = _options.BindAddress;
        if (address?.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("未确定所选网卡的 IPv4 地址。");
        var devices = _listDevices().Where(d =>
            Matches(d.Name, d.Addresses.Select(a => a.Addr?.ipAddress), _options.AdapterId, address)).ToArray();
        if (devices.Length != 1) throw AdapterListChanged(_options.AdapterId);
        // Retain the selected object even if Open fails midway, so rollback can close it.
        _device = devices[0];
        _device.Open(new DeviceConfiguration
        {
            Mode = DeviceModes.None, ReadTimeout = 50, Snaplen = 65535 + 64,
            // The reader thread hands every frame to the reassembler under the same lock the
            // decoder holds while Oodle runs, so a zone-load burst can stall ingress for tens
            // of milliseconds - about eighty frames' worth of 1 MiB on a hundred-megabit link.
            // Overflow is discarded silently by the driver, and one lost SYN costs the whole
            // connection: it cannot be framed again until the player logs in afresh.
            KernelBufferSize = 16 * 1024 * 1024,
        });
        _device.Filter = $"ip and tcp and host {address}";
        _device.NonBlockingMode = true;
        if (!_device.NonBlockingMode) throw new InvalidOperationException("无法启用安全停止所需的非阻塞捕获。");
        if ((int)_device.LinkType is not (0 or 1 or 12 or 101 or 108 or 228))
            throw new InvalidOperationException("所选网卡的数据链路类型不受支持。");
    }

    internal static bool Matches(string name, IEnumerable<IPAddress?> addresses, string? id, IPAddress address)
    {
        if (!addresses.Any(a => address.Equals(a))) return false;
        if (string.IsNullOrWhiteSpace(id)) return true; // The exact selected IP must still identify one device.
        if (string.Equals(name, id, StringComparison.OrdinalIgnoreCase)) return true;
        return Guid.TryParse(id, out var requested) && name.StartsWith(@"\Device\NPF_", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(name[@"\Device\NPF_".Length..], out var found) && found == requested;
    }

    public bool Read(NpcapPacketReceiver receive)
    {
        var device = _device ?? throw new InvalidOperationException("Npcap 未打开。");
        // No GetPacket(), RawCapture or background queue: the native span is copied directly
        // into the single bounded application buffer before the next native read reuses it.
        var status = device.GetNextPacket(out var packet);
        if (status == GetPacketStatus.PacketRead)
        {
            if (packet.Header is not PcapHeader header || header.CaptureLength != header.PacketLength)
                throw new InvalidDataException("捕获报文被截断，无法确认完整前缀。");
            _received++;
            receive(packet.Data, (int)device.LinkType);
        }
        else if (status != GetPacketStatus.ReadTimeout)
            throw new IOException("Npcap 报文读取失败。");
        if (Environment.TickCount64 - _lastStatistics >= 1000)
        {
            _lastStatistics = Environment.TickCount64;
            var statistics = device.Statistics;
            // A handful of lost packets is ordinary on a busy machine and costs at most one
            // connection prefix, while faulting the capture would leave the controller Faulted
            // for the rest of the process and cost every later connection too. Only sustained
            // loss means this adapter can no longer frame a stream at all.
            var lost = (long)statistics.DroppedPackets + (long)statistics.InterfaceDroppedPackets;
            Interlocked.Exchange(ref _dropped, lost);
            if (lost > DropFaultFloor && lost * 100 > Math.Max(_received, 1) * DropFaultPercent)
                throw new IOException("Npcap 持续丢包，无法确认完整前缀。");
        }
        return status == GetPacketStatus.PacketRead;
    }

    public void Dispose()
    {
        _device?.Close();
        _device = null;
    }
}
