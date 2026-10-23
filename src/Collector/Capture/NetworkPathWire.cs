using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Capture;

/// <summary>The single endpoint-free whitelist shared by IPC and exported diagnostic reports.</summary>
public static class NetworkPathWire
{
    public const int MaxConnectionCount = 2 * SystemProcessConnectionTable.MaxTableRows;
    public const int MaxCaptureInterfaceCount = 8;

    public static JsonObject ToJson(NetworkPathDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonObject
        {
            ["query_ipv4"] = Query(snapshot.QueryIPv4),
            ["query_ipv6"] = Query(snapshot.QueryIPv6),
            ["query_adapters"] = Query(snapshot.QueryAdapters),
            ["ipv4_connections"] = Count(snapshot.IPv4Connections, SystemProcessConnectionTable.MaxTableRows),
            ["ipv6_connections"] = Count(snapshot.IPv6Connections, SystemProcessConnectionTable.MaxTableRows),
            ["loopback_connections"] = Count(snapshot.LoopbackConnections, MaxConnectionCount),
            ["local_connections"] = Count(snapshot.LocalConnections, MaxConnectionCount),
            ["selected_interface_connections"] = Count(snapshot.SelectedInterfaceConnections, SystemProcessConnectionTable.MaxTableRows),
            ["other_interface_connections"] = Count(snapshot.OtherInterfaceConnections, SystemProcessConnectionTable.MaxTableRows),
            ["unmapped_ipv4_connections"] = Count(snapshot.UnmappedIPv4Connections, SystemProcessConnectionTable.MaxTableRows),
            ["matching_interfaces"] = Count(snapshot.MatchingInterfaces, NetworkPathInspector.MaxInterfaces),
            ["kind"] = Kind(snapshot.Kind),
            ["capture_interfaces"] = Count(snapshot.CaptureInterfaces, MaxCaptureInterfaceCount),
            ["candidate_interfaces"] = Count(snapshot.CandidateInterfaces, MaxCaptureInterfaceCount),
            ["capture_open_failures"] = Count(snapshot.CaptureOpenFailures, MaxCaptureInterfaceCount),
            ["candidate_limit_reached"] = snapshot.CandidateLimitReached,
            ["loopback_capture_available"] = snapshot.LoopbackCaptureAvailable,
            ["ipv6_capture_supported"] = false,
        };
    }

    private static int? Count(int? value, int maximum) => value is >= 0 ? Math.Min(value.Value, maximum) : null;

    private static string Query(NetworkQueryStatus status) => status switch
    {
        NetworkQueryStatus.Success => "OK",
        NetworkQueryStatus.Failed => "FAILED",
        NetworkQueryStatus.Truncated => "TRUNCATED",
        _ => "UNMEASURED",
    };

    private static string Kind(NetworkPathKind kind) => kind switch
    {
        NetworkPathKind.NoConnections => "NO_CONNECTIONS",
        NetworkPathKind.IPv6Only => "IPV6_ONLY",
        NetworkPathKind.Loopback => "LOOPBACK",
        NetworkPathKind.Local => "LOCAL",
        NetworkPathKind.MultipleInterfaces => "MULTIPLE_INTERFACES",
        NetworkPathKind.SelectedInterface => "SELECTED_INTERFACE",
        NetworkPathKind.OtherInterface => "OTHER_INTERFACE",
        NetworkPathKind.Unmapped => "UNMAPPED",
        _ => "UNKNOWN",
    };
}
