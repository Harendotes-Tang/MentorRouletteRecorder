using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MentorRecorder.Collector.Capture;

/// <summary>The single endpoint-free whitelist shared by IPC and exported diagnostic reports.</summary>
public static partial class NetworkPathWire
{
    public const int MaxConnectionCount = 2 * SystemProcessConnectionTable.MaxTableRows;
    public const int MaxCaptureInterfaceCount = 8;

    /// <summary>Longest query detail the contract admits (<c>NetworkPath.query_ipv4_detail</c>).</summary>
    public const int MaxDetailLength = 64;

    /// <summary>
    /// The one grammar of a query detail, verbatim the <c>pattern</c> of
    /// <c>NetworkPath.query_ipv4_detail</c> in contracts/ipc-v1.schema.json (a contract test pins
    /// the two equal). A closed vocabulary rather than a character class: a lower-case IPv6
    /// literal or a MAC address is made of the same characters and must still be refused.
    /// </summary>
    public const string DetailPattern =
        "^(?:(?:native_size|native_read):[0-9]{1,10}|size_too_small|returned_length|header_short|row_count|family|no_pid"
        + "|provider_exception|frozen_row|table_too_large|table_growing|rows_too_many|rows_untrusted|own_rows_unreadable"
        + "|exception:[a-z][a-z0-9_]{0,39}|skipped_rows:[0-9]{1,5}(?:;unknown_state:(?:[0-9]{1,3}|other))?)$";

    public static JsonObject ToJson(NetworkPathDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new JsonObject
        {
            ["query_ipv4"] = Query(snapshot.QueryIPv4),
            ["query_ipv6"] = Query(snapshot.QueryIPv6),
            ["query_adapters"] = Query(snapshot.QueryAdapters),
            ["query_ipv4_detail"] = Detail(snapshot.QueryIPv4Detail),
            ["query_ipv6_detail"] = Detail(snapshot.QueryIPv6Detail),
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

    /// <summary>
    /// Admits only a query detail that is a sentence of <see cref="DetailPattern"/>
    /// (<c>native_size:50</c>, <c>skipped_rows:3;unknown_state:100</c>) of at most
    /// <see cref="MaxDetailLength"/> characters. Anything else -- an endpoint, a path,
    /// exception text -- is dropped here rather than trusted.
    /// </summary>
    /// <param name="detail">Token to admit.</param>
    internal static string? Detail(string? detail) =>
        string.IsNullOrEmpty(detail) || detail.Length > MaxDetailLength || !DetailRegex().IsMatch(detail)
            ? null
            : detail;

    [GeneratedRegex(DetailPattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex DetailRegex();

    /// <summary>The contract token of a query status (<c>NetworkQueryStatus</c>).</summary>
    /// <param name="status">Status to name.</param>
    internal static string Query(NetworkQueryStatus status) => status switch
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
