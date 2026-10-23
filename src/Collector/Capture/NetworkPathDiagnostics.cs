using System.Net;
using System.Net.Sockets;

namespace MentorRecorder.Collector.Capture;

/// <summary>Measured topology only: these values make no accelerator-brand or UDP inference.</summary>
public enum NetworkPathKind
{
    Unknown,
    NoConnections,
    IPv6Only,
    Loopback,
    Local,
    MultipleInterfaces,
    SelectedInterface,
    OtherInterface,
    Unmapped,
}

/// <summary>
/// Export-safe network facts. Counts are null when their source query was not complete;
/// local/loopback counts cover both families, interface counts cover IPv4 only.
/// No endpoint, PID, interface identity, name or exception belongs in this model.
/// </summary>
public sealed record NetworkPathDiagnosticsSnapshot
{
    public static NetworkPathDiagnosticsSnapshot Unmeasured { get; } = new();
    public NetworkQueryStatus QueryIPv4 { get; init; }
    public NetworkQueryStatus QueryIPv6 { get; init; }
    public NetworkQueryStatus QueryAdapters { get; init; }
    public int? IPv4Connections { get; init; }
    public int? IPv6Connections { get; init; }
    public int? LoopbackConnections { get; init; }
    public int? LocalConnections { get; init; }
    public int? SelectedInterfaceConnections { get; init; }
    public int? OtherInterfaceConnections { get; init; }
    public int? UnmappedIPv4Connections { get; init; }
    public int? MatchingInterfaces { get; init; }
    public NetworkPathKind Kind { get; init; }
    public int? CaptureInterfaces { get; init; }
    public int? CandidateInterfaces { get; init; }
    public int? CaptureOpenFailures { get; init; }
    public bool CandidateLimitReached { get; init; }
    public bool? LoopbackCaptureAvailable { get; init; }
    public bool IPv6CaptureSupported => false;
}

/// <summary>
/// Acquires bounded host facts outside capture's state lock and applies a pure path
/// classifier. A single PID/fact sample is reused for less than one second; capture-open
/// counters are copied separately on every call so they cannot become stale in that cache.
/// </summary>
public sealed class NetworkPathInspector
{
    public const int MaxInterfaces = 256;
    public const int MaxAddressesPerInterface = 64;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(1);
    private readonly object _cacheLock = new();
    private readonly IAdapterProvider _adapters;
    private readonly IProcessNetworkConnectionProvider _connections;
    private readonly TimeProvider _time;
    private Measurement? _cached;

    public NetworkPathInspector(
        IAdapterProvider? adapters = null,
        IProcessNetworkConnectionProvider? connections = null,
        TimeProvider? timeProvider = null)
    {
        _adapters = adapters ?? SystemAdapterProvider.Instance;
        _connections = connections ?? SystemProcessConnectionTable.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Samples the selected game's host-owned TCP tuples and local interfaces. Callers must
    /// not hold the capture state gate: native queries execute synchronously on a cache miss.
    /// An absent/invalid PID is unmeasured, not a claim that the game has no connection.
    /// </summary>
    public NetworkPathDiagnosticsSnapshot Inspect(
        int? gameProcessId, string? selectedAdapterId, CapturePathStatus? captureStatus = null)
    {
        if (gameProcessId is not > 0)
        {
            lock (_cacheLock)
            {
                _cached = null;
            }

            return WithCapture(NetworkPathDiagnosticsSnapshot.Unmeasured, captureStatus);
        }

        Measurement measurement;
        lock (_cacheLock)
        {
            var now = _time.GetTimestamp();
            if (_cached is { } cached && cached.ProcessId == gameProcessId &&
                _time.GetElapsedTime(cached.Timestamp, now) is var elapsed &&
                elapsed >= TimeSpan.Zero && elapsed < CacheLifetime)
            {
                measurement = cached;
            }
            else
            {
                var tables = ReadConnections(gameProcessId.Value);
                var (adapters, status) = ReadAdapters();
                measurement = new Measurement(gameProcessId.Value, now, tables, adapters, status);
                _cached = measurement;
            }
        }

        return Classify(measurement.Tables, measurement.Adapters, measurement.AdapterQuery,
            selectedAdapterId, captureStatus);
    }

    private ProcessNetworkConnectionTables ReadConnections(int processId)
    {
        try
        {
            var tables = _connections.Read(processId);
            return new ProcessNetworkConnectionTables(
                Freeze(tables.IPv4, AddressFamily.InterNetwork),
                Freeze(tables.IPv6, AddressFamily.InterNetworkV6));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return new ProcessNetworkConnectionTables(
                ProcessNetworkConnectionQuery.Failed, ProcessNetworkConnectionQuery.Failed);
        }
    }

    private static ProcessNetworkConnectionQuery Freeze(ProcessNetworkConnectionQuery query, AddressFamily family)
    {
        if (query.Status != NetworkQueryStatus.Success)
        {
            return query.Status switch
            {
                NetworkQueryStatus.Unmeasured => ProcessNetworkConnectionQuery.Unmeasured,
                NetworkQueryStatus.Truncated => ProcessNetworkConnectionQuery.Truncated,
                _ => ProcessNetworkConnectionQuery.Failed,
            };
        }

        if (query.Connections.Count > SystemProcessConnectionTable.MaxTableRows)
        {
            return ProcessNetworkConnectionQuery.Truncated;
        }

        var frozen = new ProcessNetworkConnection[query.Connections.Count];
        for (var index = 0; index < frozen.Length; index++)
        {
            var connection = query.Connections[index];
            if (connection.LocalAddress.AddressFamily != family || connection.RemoteAddress.AddressFamily != family ||
                connection.LocalPort == 0 || connection.RemotePort == 0)
            {
                return ProcessNetworkConnectionQuery.Failed;
            }

            frozen[index] = connection with
            {
                LocalAddress = CopyAddress(connection.LocalAddress),
                RemoteAddress = CopyAddress(connection.RemoteAddress),
            };
        }

        return new ProcessNetworkConnectionQuery(NetworkQueryStatus.Success, frozen);
    }

    private (IReadOnlyList<AdapterInfo> Adapters, NetworkQueryStatus Status) ReadAdapters()
    {
        try
        {
            var adapters = _adapters.List();
            if (adapters.Count > MaxInterfaces || adapters.Any(adapter => adapter.IPv4Addresses.Count > MaxAddressesPerInterface))
            {
                return (Array.Empty<AdapterInfo>(), NetworkQueryStatus.Truncated);
            }

            return (adapters.Select(adapter => adapter with
            {
                IPv4Addresses = adapter.IPv4Addresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(CopyAddress).ToArray(),
            }).ToArray(), NetworkQueryStatus.Success);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return (Array.Empty<AdapterInfo>(), NetworkQueryStatus.Failed);
        }
    }

    private static IPAddress CopyAddress(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6
        ? new IPAddress(address.GetAddressBytes(), address.ScopeId)
        : new IPAddress(address.GetAddressBytes());

    /// <summary>Classifies measured facts only; no operating-system query or logging occurs here.</summary>
    internal static NetworkPathDiagnosticsSnapshot Classify(
        ProcessNetworkConnectionTables tables,
        IReadOnlyList<AdapterInfo> adapters,
        NetworkQueryStatus adapterQuery,
        string? selectedAdapterId,
        CapturePathStatus? captureStatus = null)
    {
        var ipv4Complete = tables.IPv4.Status == NetworkQueryStatus.Success;
        var ipv6Complete = tables.IPv6.Status == NetworkQueryStatus.Success;
        var adaptersComplete = adapterQuery == NetworkQueryStatus.Success;
        var ipv4 = ipv4Complete ? tables.IPv4.Connections : Array.Empty<ProcessNetworkConnection>();
        var ipv6 = ipv6Complete ? tables.IPv6.Connections : Array.Empty<ProcessNetworkConnection>();
        var usableAdapters = adapters.Where(adapter => adapter.IsUp).ToArray();
        var localAddresses = usableAdapters.SelectMany(adapter => adapter.IPv4Addresses).ToHashSet();
        var allConnections = ipv4.Concat(ipv6).ToArray();
        var loopback = allConnections.Count(connection =>
            IPAddress.IsLoopback(connection.LocalAddress) && IPAddress.IsLoopback(connection.RemoteAddress));
        var local = allConnections.Count(connection =>
            IPAddress.IsLoopback(connection.RemoteAddress) || localAddresses.Contains(connection.RemoteAddress));

        var matchedInterfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selected = 0;
        var other = 0;
        var unmapped = 0;
        foreach (var connection in ipv4)
        {
            var matches = usableAdapters.Where(adapter => adapter.IPv4Addresses.Contains(connection.LocalAddress) ||
                (CapturePathPlan.IsCanonicalLoopback(adapter.Id) && IPAddress.IsLoopback(connection.LocalAddress))).ToArray();
            foreach (var match in matches)
            {
                matchedInterfaces.Add(match.Id);
            }

            if (matches.Length == 0)
            {
                unmapped++;
            }
            else if (matches.Any(adapter => string.Equals(adapter.Id, selectedAdapterId, StringComparison.OrdinalIgnoreCase)))
            {
                selected++;
            }
            else
            {
                other++;
            }
        }

        var kind = NetworkPathKind.Unknown;
        if (ipv4Complete && ipv6Complete)
        {
            kind = allConnections.Length == 0 ? NetworkPathKind.NoConnections
                : ipv4.Count == 0 ? NetworkPathKind.IPv6Only
                : !adaptersComplete ? NetworkPathKind.Unknown
                : matchedInterfaces.Count > 1 ? NetworkPathKind.MultipleInterfaces
                : loopback == allConnections.Length ? NetworkPathKind.Loopback
                : local == allConnections.Length ? NetworkPathKind.Local
                : unmapped > 0 ? NetworkPathKind.Unmapped
                : selected == ipv4.Count ? NetworkPathKind.SelectedInterface
                : NetworkPathKind.OtherInterface;
        }

        return WithCapture(new NetworkPathDiagnosticsSnapshot
        {
            QueryIPv4 = tables.IPv4.Status,
            QueryIPv6 = tables.IPv6.Status,
            QueryAdapters = adapterQuery,
            IPv4Connections = ipv4Complete ? ipv4.Count : null,
            IPv6Connections = ipv6Complete ? ipv6.Count : null,
            LoopbackConnections = ipv4Complete && ipv6Complete ? loopback : null,
            // Non-loopback local IPv6 addresses are not in the IPv4 adapter source; avoid
            // presenting their absence as a measured zero for all local communication.
            LocalConnections = ipv4Complete && ipv6Complete && adaptersComplete &&
                ipv6.All(connection => IPAddress.IsLoopback(connection.RemoteAddress)) ? local : null,
            SelectedInterfaceConnections = ipv4Complete && adaptersComplete ? selected : null,
            OtherInterfaceConnections = ipv4Complete && adaptersComplete ? other : null,
            UnmappedIPv4Connections = ipv4Complete && adaptersComplete ? unmapped : null,
            MatchingInterfaces = ipv4Complete && adaptersComplete ? matchedInterfaces.Count : null,
            Kind = kind,
        }, captureStatus);
    }

    private static NetworkPathDiagnosticsSnapshot WithCapture(
        NetworkPathDiagnosticsSnapshot snapshot, CapturePathStatus? status) => snapshot with
    {
        CaptureInterfaces = status?.OpenInterfaces,
        CandidateInterfaces = status?.CandidateInterfaces,
        CaptureOpenFailures = status?.OpenFailures,
        CandidateLimitReached = status?.CandidateLimitReached ?? false,
        LoopbackCaptureAvailable = status?.LoopbackOpened,
    };

    private sealed record Measurement(
        int ProcessId,
        long Timestamp,
        ProcessNetworkConnectionTables Tables,
        IReadOnlyList<AdapterInfo> Adapters,
        NetworkQueryStatus AdapterQuery);
}
