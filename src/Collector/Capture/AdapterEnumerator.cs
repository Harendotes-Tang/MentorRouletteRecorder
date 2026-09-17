using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MentorRecorder.Collector.Capture;

/// <summary>One local network interface, as far as capture is concerned.</summary>
/// <param name="Id">Interface identifier; opaque, used as <c>adapter_id</c> on the wire.</param>
/// <param name="FriendlyName">Name the user sees in Windows.</param>
/// <param name="Description">Driver description.</param>
/// <param name="IsUp">True when the interface is operational.</param>
/// <param name="IsLoopback">True for the loopback interface.</param>
/// <param name="IPv4Addresses">Unicast IPv4 addresses bound to the interface.</param>
/// <param name="HasDefaultRoute">True when the interface has an IPv4 default gateway.</param>
public sealed record AdapterInfo(
    string Id,
    string FriendlyName,
    string Description,
    bool IsUp,
    bool IsLoopback,
    IReadOnlyList<IPAddress> IPv4Addresses,
    bool HasDefaultRoute = false);

/// <summary>An adapter as it is shown to a client: addresses masked, carrier flag resolved.</summary>
/// <param name="Id">Opaque adapter identifier.</param>
/// <param name="FriendlyName">Name the user sees in Windows.</param>
/// <param name="Description">Driver description.</param>
/// <param name="IsUp">True when the interface is operational.</param>
/// <param name="IsLoopback">True for the loopback interface.</param>
/// <param name="MaskedIPv4">IPv4 addresses with the host part replaced, for display only.</param>
/// <param name="CarriesGameTraffic">True when the game's TCP connections are bound to this interface.</param>
/// <param name="Recommended">True when this is the adapter capture should use.</param>
public sealed record CaptureAdapterView(
    string Id,
    string FriendlyName,
    string Description,
    bool IsUp,
    bool IsLoopback,
    IReadOnlyList<string> MaskedIPv4,
    bool CarriesGameTraffic,
    bool Recommended)
{
    /// <summary>Unmasked address Machina binds its capture to. Never rendered to a client.</summary>
    public IPAddress? BindAddress { get; init; }

    /// <summary>
    /// True when this interface has an IPv4 default gateway, i.e. it is the one Windows would
    /// send a new connection over. Used only before the game has opened any connection at
    /// all, so capture can already be listening when it does.
    /// </summary>
    public bool HasDefaultRoute { get; init; }

    /// <summary>
    /// True when this is the adapter the user chose earlier and it no longer carries the
    /// game's traffic while another one does. Reported so the UI can explain the switch
    /// rather than capturing on a card with nothing on it.
    /// </summary>
    public bool PreferenceStale { get; init; }
}

/// <summary>Lists local network interfaces. Abstracted so masking and recommendation can be tested.</summary>
public interface IAdapterProvider
{
    /// <summary>Lists every interface on this machine.</summary>
    IReadOnlyList<AdapterInfo> List();
}

/// <summary>
/// Answers "which local addresses does this process currently have TCP connections on".
///
/// The only implementation reads the system TCP table, which is a read of operating-system
/// state, not of the game process (docs/privacy-boundary.md section 2).
/// </summary>
public interface IProcessTcpTable
{
    /// <summary>Local IPv4 addresses of the process's current TCP connections.</summary>
    /// <param name="processId">Process identifier.</param>
    IReadOnlyList<IPAddress> LocalAddresses(int processId);
}

/// <summary>
/// Enumerates capture adapters, marks the one that currently carries the game's connections,
/// and masks every address before it can reach a client.
///
/// Addresses are masked to a /24 for display (<c>192.168.1.34</c> becomes
/// <c>192.168.1.x</c>): enough for a user to recognise their own network card, not enough to
/// be a network identifier in a log or a bug report. The unmasked address stays inside this
/// process, where Machina needs it to pick a capture device
/// (docs/capture-diagnostics.md section 3).
/// </summary>
public sealed class AdapterEnumerator
{
    /// <summary>Placeholder that replaces the host part of a displayed address.</summary>
    public const string HostPlaceholder = "x";

    private readonly IAdapterProvider _adapters;
    private readonly IProcessTcpTable _tcpTable;

    /// <summary>Creates an enumerator.</summary>
    /// <param name="adapters">Interface source; the real machine when null.</param>
    /// <param name="tcpTable">TCP table source; the system table when null.</param>
    public AdapterEnumerator(IAdapterProvider? adapters = null, IProcessTcpTable? tcpTable = null)
    {
        _adapters = adapters ?? SystemAdapterProvider.Instance;
        _tcpTable = tcpTable ?? MachinaProcessTcpTable.Instance;
    }

    /// <summary>
    /// Masks an IPv4 address to its /24 for display. Anything that is not a dotted quad comes
    /// back fully masked rather than partially leaked.
    /// </summary>
    /// <param name="address">Address to mask.</param>
    public static string Mask(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return string.Join('.', HostPlaceholder, HostPlaceholder, HostPlaceholder, HostPlaceholder);
        }

        var octets = address.GetAddressBytes();
        return $"{octets[0]}.{octets[1]}.{octets[2]}.{HostPlaceholder}";
    }

    /// <summary>
    /// Lists adapters, flagging the one that carries the game's traffic and the one capture
    /// should use.
    ///
    /// Recommendation is deliberately conservative: an adapter is recommended only when the
    /// user has chosen it before, or when it demonstrably carries the game's connections. When
    /// neither is true nothing is recommended and the user picks -- guessing an adapter would
    /// produce a capture that silently observes nothing.
    /// </summary>
    /// <param name="gameProcessId">Game process id, when one is running.</param>
    /// <param name="preferredAdapterId">Adapter the user chose previously, if any.</param>
    public IReadOnlyList<CaptureAdapterView> List(int? gameProcessId, string? preferredAdapterId = null)
    {
        var gameAddresses = gameProcessId is { } pid ? SafeLocalAddresses(pid) : Array.Empty<IPAddress>();
        var gameAddressSet = new HashSet<IPAddress>(gameAddresses);

        // The game is running and holds no TCP connection yet: the title screen. That is the
        // only moment at which capture can start early enough to observe the connection's
        // SYN, and it is exactly the moment the traffic-based rule has nothing to say.
        var gameIsIdle = gameProcessId is not null && gameAddressSet.Count == 0;

        var views = new List<CaptureAdapterView>();
        foreach (var adapter in SafeList())
        {
            var carries = adapter.IPv4Addresses.Any(gameAddressSet.Contains);
            views.Add(new CaptureAdapterView(
                adapter.Id,
                adapter.FriendlyName,
                adapter.Description,
                adapter.IsUp,
                adapter.IsLoopback,
                adapter.IPv4Addresses.Select(Mask).ToArray(),
                carries,
                Recommended: false)
            {
                BindAddress = carries
                    ? adapter.IPv4Addresses.First(gameAddressSet.Contains)
                    : adapter.IPv4Addresses.FirstOrDefault(),
                HasDefaultRoute = adapter.HasDefaultRoute,
            });
        }

        var (chosen, stale) = Choose(views, preferredAdapterId, gameIsIdle);
        for (var i = 0; i < views.Count; i++)
        {
            if (stale is not null && ReferenceEquals(views[i], stale))
            {
                views[i] = stale with { PreferenceStale = true };
            }
            else if (chosen is not null && ReferenceEquals(views[i], chosen))
            {
                views[i] = chosen with { Recommended = true };
            }
        }

        return views;
    }

    /// <summary>Finds one adapter by its opaque identifier, or null.</summary>
    /// <param name="adapters">Adapters as returned by <see cref="List"/>.</param>
    /// <param name="adapterId">Identifier to look for.</param>
    public static CaptureAdapterView? Find(IReadOnlyList<CaptureAdapterView> adapters, string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return null;
        }

        return adapters.FirstOrDefault(
            adapter => string.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the adapter capture should use, and names the remembered one when it has gone
    /// stale.
    ///
    /// The remembered choice stays authoritative while it can still work. It is overridden in
    /// exactly one case: it demonstrably does not carry the game's traffic and another adapter
    /// demonstrably does. That is the 加速器/VPN case, where honouring the stale preference
    /// binds capture to a card the game no longer uses, so the pcap filter and the
    /// local-address check drop every packet and capture reports RUNNING while observing
    /// nothing.
    /// </summary>
    /// <param name="adapters">Adapters as built by <see cref="List"/>.</param>
    /// <param name="preferredAdapterId">Adapter the user chose previously, if any.</param>
    /// <param name="gameIsIdle">True when the game is running with no TCP connection yet.</param>
    private static (CaptureAdapterView? Chosen, CaptureAdapterView? Stale) Choose(
        IReadOnlyList<CaptureAdapterView> adapters, string? preferredAdapterId, bool gameIsIdle)
    {
        var carrier = adapters.FirstOrDefault(
            adapter => adapter.CarriesGameTraffic && adapter.IsUp && !adapter.IsLoopback);

        if (Find(adapters, preferredAdapterId) is { } remembered)
        {
            return carrier is null || remembered.CarriesGameTraffic
                ? (remembered, null)
                : (carrier, remembered);
        }

        if (carrier is not null)
        {
            return (carrier, null);
        }

        // No traffic to follow yet. Guessing is normally refused, but at the title screen the
        // default route is not a guess about which card the user prefers: it is the card
        // Windows will itself use for the connection that is about to open. Starting there is
        // the difference between capturing the handshake and never decoding this session.
        return gameIsIdle
            ? (adapters.FirstOrDefault(adapter =>
                adapter.HasDefaultRoute && adapter.IsUp && !adapter.IsLoopback &&
                adapter.BindAddress is not null), null)
            : (null, null);
    }

    private IReadOnlyList<AdapterInfo> SafeList()
    {
        try
        {
            return _adapters.List();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<AdapterInfo>();
        }
    }

    private IReadOnlyList<IPAddress> SafeLocalAddresses(int processId)
    {
        try
        {
            return _tcpTable.LocalAddresses(processId);
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or NotSupportedException)
        {
            // Without the TCP table nothing is recommended and the user picks the adapter.
            return Array.Empty<IPAddress>();
        }
    }
}

/// <summary>Lists interfaces with <see cref="NetworkInterface.GetAllNetworkInterfaces"/>.</summary>
public sealed class SystemAdapterProvider : IAdapterProvider
{
    /// <summary>Shared instance.</summary>
    public static SystemAdapterProvider Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<AdapterInfo> List()
    {
        var results = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        addresses.Add(unicast.Address);
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // An interface that refuses to describe itself is listed without addresses
                // rather than dropped: the user may still recognise it by name.
            }

            results.Add(new AdapterInfo(
                nic.Id,
                nic.Name,
                nic.Description,
                nic.OperationalStatus == OperationalStatus.Up,
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                addresses,
                HasDefaultRoute(nic)));
        }

        return results;
    }

    /// <summary>
    /// True when the interface has a usable IPv4 default gateway. An interface that refuses
    /// to describe itself is reported as having none rather than being guessed at.
    /// </summary>
    private static bool HasDefaultRoute(NetworkInterface nic)
    {
        try
        {
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address is { AddressFamily: AddressFamily.InterNetwork } address &&
                    !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Same rule as the address list above: unreadable means unknown, never assumed.
        }

        return false;
    }
}

/// <summary>
/// Reads the system TCP table through Machina's <c>ProcessTCPInfo</c>, which wraps the IP
/// Helper <c>GetExtendedTcpTable</c> API.
///
/// This is an operating-system query about connections, the same information
/// <c>netstat -ano</c> prints. It opens nothing belonging to the game.
/// </summary>
public sealed class MachinaProcessTcpTable : IProcessTcpTable
{
    /// <summary>Shared instance.</summary>
    public static MachinaProcessTcpTable Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> LocalAddresses(int processId)
    {
        if (processId <= 0)
        {
            return Array.Empty<IPAddress>();
        }

        try
        {
            var info = new Machina.Infrastructure.ProcessTCPInfo { ProcessID = (uint)processId };
            var connections = new List<Machina.Infrastructure.TCPConnection>();
            info.UpdateTCPIPConnections(connections);

            var addresses = new List<IPAddress>();
            foreach (var connection in connections)
            {
                var address = new IPAddress(connection.LocalIP);
                if (!addresses.Contains(address))
                {
                    addresses.Add(address);
                }
            }

            return addresses;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // The TCP table only informs the recommendation; nothing here may take the process
            // down or block a capture the user asked for explicitly.
            return Array.Empty<IPAddress>();
        }
    }
}
