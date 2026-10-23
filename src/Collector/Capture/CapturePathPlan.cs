using System.Net;
using System.Net.Sockets;

namespace MentorRecorder.Collector.Capture;

/// <summary>One capture device and its private, bounded IPv4 address set.</summary>
public sealed record CaptureInterfaceBinding(
    string AdapterId, IReadOnlyList<IPAddress> LocalAddresses, bool IsLoopback);

/// <summary>
/// Internal session configuration. Candidates describe interfaces to observe, not proof that
/// a game connection exists. Explicit selection retains exactly one interface.
/// </summary>
public sealed record CapturePathPlan(
    IReadOnlyList<CaptureInterfaceBinding> Bindings, bool Automatic, bool CandidateLimitReached)
{
    public const int MaxInterfaces = 8;
    public const int MaxAddressesPerInterface = 16;
    public const string LoopbackDeviceId = @"\Device\NPF_Loopback";

    /// <summary>Builds a deterministic bounded plan, always retaining the required selection.</summary>
    public static CapturePathPlan Create(
        IReadOnlyList<CaptureAdapterView> adapters, CaptureAdapterView selected, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(selected);
        if (!IsUsable(selected)) throw new InvalidOperationException("所选网卡没有可捕获的 IPv4 路径。");

        var candidates = new[] { selected }.Concat(automatic
            ? adapters.Where(IsUsable).OrderByDescending(a => a.CarriesGameTraffic)
                .ThenByDescending(a => a.PreferredRoute || a.HasDefaultRoute)
                .ThenByDescending(a => IsCanonicalLoopback(a.Id))
            : Enumerable.Empty<CaptureAdapterView>());
        var bindings = new List<CaptureInterfaceBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        foreach (var candidate in candidates)
        {
            var identity = NormalizeIdentity(candidate.Id);
            if (!seen.Add(identity)) continue;
            if (bindings.Count == MaxInterfaces) { truncated = true; continue; }
            var addresses = Addresses(candidate).Distinct().Take(MaxAddressesPerInterface + 1).ToArray();
            truncated |= addresses.Length > MaxAddressesPerInterface;
            bindings.Add(new CaptureInterfaceBinding(candidate.Id,
                addresses.Take(MaxAddressesPerInterface).ToArray(), IsCanonicalLoopback(candidate.Id)));
        }
        return new CapturePathPlan(bindings.ToArray(), automatic, truncated);
    }

    /// <summary>Only the well-known Npcap identity grants loopback capture semantics.</summary>
    public static bool IsCanonicalLoopback(string? id) =>
        string.Equals(id, LoopbackDeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shared eligibility rule for normal capture, trace and validation.</summary>
    public static bool IsUsable(CaptureAdapterView adapter) => adapter.IsUp &&
        (IsCanonicalLoopback(adapter.Id) || (!adapter.IsLoopback && Addresses(adapter).Any()));

    internal static IEnumerable<IPAddress> Addresses(CaptureAdapterView adapter)
    {
        var loopback = IsCanonicalLoopback(adapter.Id);
        var addresses = (adapter.BindAddress is { } address
            ? new[] { address } : Array.Empty<IPAddress>()).Concat(adapter.LocalAddresses)
            .Where(a => IsUsableAddress(a) && (loopback || !IPAddress.IsLoopback(a)));
        return loopback ? addresses.Append(IPAddress.Loopback).Distinct() : addresses;
    }

    internal static bool IsUsableAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.None) &&
        address.GetAddressBytes()[0] is not (>= 224);

    internal static string NormalizeIdentity(string id)
    {
        const string prefix = @"\Device\NPF_";
        var token = id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? id[prefix.Length..] : id;
        return Guid.TryParse(token, out var guid) ? guid.ToString("D") : id;
    }

    internal static CapturePathPlan FromOptions(CaptureStartOptions options)
    {
        if (options.PathPlan is { } plan) return plan;
        var addresses = options.BindAddress is { } address && IsUsableAddress(address)
            ? new[] { address } : Array.Empty<IPAddress>();
        if (addresses.Length == 0 && !IsCanonicalLoopback(options.AdapterId))
            throw new InvalidOperationException("未确定所选网卡的 IPv4 地址。");
        return new CapturePathPlan(new[] { new CaptureInterfaceBinding(options.AdapterId ?? string.Empty,
            addresses, IsCanonicalLoopback(options.AdapterId)) }, false, false);
    }
}

/// <summary>Sanitized native-open facts; null counts mean no measurement was made.</summary>
public sealed record CapturePathStatus(int? CandidateInterfaces, int? OpenInterfaces,
    int? OpenFailures, bool CandidateLimitReached, bool? LoopbackOpened)
{
    public static CapturePathStatus Unmeasured { get; } = new(null, null, null, false, null);
}
