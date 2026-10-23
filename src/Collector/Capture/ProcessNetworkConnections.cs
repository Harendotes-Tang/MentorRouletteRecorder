using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MentorRecorder.Collector.Capture;

/// <summary>Whether one bounded operating-system query produced a complete answer.</summary>
public enum NetworkQueryStatus
{
    Unmeasured,
    Success,
    Failed,
    Truncated,
}

/// <summary>
/// One game-owned TCP tuple, retained only inside the Collector. This is not a diagnostic
/// or wire model; the default string representation deliberately omits its endpoints.
/// </summary>
public sealed record ProcessNetworkConnection(
    IPAddress LocalAddress,
    ushort LocalPort,
    IPAddress RemoteAddress,
    ushort RemotePort)
{
    public override string ToString() => nameof(ProcessNetworkConnection);
}

/// <summary>A complete per-family answer, or an explicit reason that counts are unknown.</summary>
public sealed record ProcessNetworkConnectionQuery(
    NetworkQueryStatus Status,
    IReadOnlyList<ProcessNetworkConnection> Connections)
{
    public static ProcessNetworkConnectionQuery Unmeasured { get; } =
        new(NetworkQueryStatus.Unmeasured, Array.Empty<ProcessNetworkConnection>());

    public static ProcessNetworkConnectionQuery Failed { get; } =
        new(NetworkQueryStatus.Failed, Array.Empty<ProcessNetworkConnection>());

    public static ProcessNetworkConnectionQuery Truncated { get; } =
        new(NetworkQueryStatus.Truncated, Array.Empty<ProcessNetworkConnection>());
}

/// <summary>Independent IPv4 and IPv6 query results for the same requested PID.</summary>
public sealed record ProcessNetworkConnectionTables(
    ProcessNetworkConnectionQuery IPv4,
    ProcessNetworkConnectionQuery IPv6)
{
    public static ProcessNetworkConnectionTables Unmeasured { get; } = new(
        ProcessNetworkConnectionQuery.Unmeasured, ProcessNetworkConnectionQuery.Unmeasured);
}

/// <summary>Reads system connection ownership without opening or inspecting a game process.</summary>
public interface IProcessNetworkConnectionProvider
{
    ProcessNetworkConnectionTables Read(int processId);
}

/// <summary>The sole native entry point, injectable for malformed-buffer and growth tests.</summary>
internal interface IExtendedTcpTableReader
{
    uint Read(IntPtr buffer, ref uint bytes, int addressFamily);
}

/// <summary>
/// Reads both OWNER_PID TCP tables through IP Helper. Each family uses at most 1 MiB,
/// 16,384 rows and three data attempts; growth or unsupported queries never mean zero.
/// Native buffers are always freed before returning and no raw endpoint is logged.
/// </summary>
public sealed class SystemProcessConnectionTable : IProcessTcpTable, IProcessNetworkConnectionProvider
{
    public const int MaxTableBytes = 1024 * 1024;
    public const int MaxTableRows = 16_384;
    public const int MaxReadAttempts = 3;
    private const uint InsufficientBuffer = 122;
    private readonly IExtendedTcpTableReader _reader;

    public static SystemProcessConnectionTable Instance { get; } = new(new NativeTcpTableReader());

    internal SystemProcessConnectionTable(IExtendedTcpTableReader reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public ProcessNetworkConnectionTables Read(int processId) => processId <= 0
        ? ProcessNetworkConnectionTables.Unmeasured
        : new(ReadFamily(processId, AddressFamily.InterNetwork), ReadFamily(processId, AddressFamily.InterNetworkV6));

    /// <summary>
    /// Returns distinct addresses from both complete family queries. A partial failure throws
    /// a fixed, endpoint-free error so adapter selection cannot mistake unreadable for idle.
    /// </summary>
    public IReadOnlyList<IPAddress> LocalAddresses(int processId)
    {
        if (processId <= 0)
        {
            return Array.Empty<IPAddress>();
        }

        var tables = Read(processId);
        if (tables.IPv4.Status != NetworkQueryStatus.Success || tables.IPv6.Status != NetworkQueryStatus.Success)
        {
            throw new InvalidOperationException("System TCP tables are not completely available.");
        }

        return tables.IPv4.Connections.Concat(tables.IPv6.Connections)
            .Select(connection => connection.LocalAddress).Distinct().ToArray();
    }

    private ProcessNetworkConnectionQuery ReadFamily(int processId, AddressFamily family)
    {
        try
        {
            uint required = 0;
            var result = _reader.Read(IntPtr.Zero, ref required, (int)family);
            if (result != 0 && result != InsufficientBuffer)
            {
                return ProcessNetworkConnectionQuery.Failed;
            }

            for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
            {
                if (required > MaxTableBytes)
                {
                    return ProcessNetworkConnectionQuery.Truncated;
                }

                if (required < sizeof(uint))
                {
                    return ProcessNetworkConnectionQuery.Failed;
                }

                var allocated = checked((int)required);
                var buffer = Marshal.AllocHGlobal(allocated);
                try
                {
                    var returned = required;
                    result = _reader.Read(buffer, ref returned, (int)family);
                    if (result == InsufficientBuffer)
                    {
                        required = returned;
                        continue;
                    }

                    if (result != 0 || returned < sizeof(uint) || returned > allocated)
                    {
                        return ProcessNetworkConnectionQuery.Failed;
                    }

                    var bytes = new byte[checked((int)returned)];
                    Marshal.Copy(buffer, bytes, 0, bytes.Length);
                    return Parse(bytes, family, processId);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return ProcessNetworkConnectionQuery.Truncated;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Query state only leaves this boundary; exceptions can contain native or host data.
            return ProcessNetworkConnectionQuery.Failed;
        }
    }

    /// <summary>Parses documented DWORD-aligned OWNER_PID rows, rejecting incomplete tables.</summary>
    internal static ProcessNetworkConnectionQuery Parse(ReadOnlySpan<byte> bytes, AddressFamily family, int processId)
    {
        var rowBytes = family switch
        {
            AddressFamily.InterNetwork => 24,
            AddressFamily.InterNetworkV6 => 56,
            _ => 0,
        };
        if (rowBytes == 0 || bytes.Length < sizeof(uint) || bytes.Length > MaxTableBytes || processId <= 0)
        {
            return ProcessNetworkConnectionQuery.Failed;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > MaxTableRows)
        {
            return ProcessNetworkConnectionQuery.Truncated;
        }

        if (count > (uint)((bytes.Length - sizeof(uint)) / rowBytes))
        {
            return ProcessNetworkConnectionQuery.Failed;
        }

        var connections = new List<ProcessNetworkConnection>();
        for (var index = 0; index < count; index++)
        {
            var row = bytes.Slice(sizeof(uint) + index * rowBytes, rowBytes);
            var ipv4 = family == AddressFamily.InterNetwork;
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 0 : 48)..]);
            if (state is < 1 or > 12)
            {
                return ProcessNetworkConnectionQuery.Failed;
            }

            // LISTEN/CLOSED/DELETE_TCB are not active paths. Other PIDs are never retained.
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 20 : 52)..]);
            if (pid != (uint)processId || state is 1 or 2 or 12)
            {
                continue;
            }

            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 8 : 20)..]);
            var remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 16 : 44)..]);
            if (localPort == 0 || remotePort == 0)
            {
                return ProcessNetworkConnectionQuery.Failed;
            }

            var local = ipv4
                ? new IPAddress(row.Slice(4, 4))
                : new IPAddress(row[..16], BinaryPrimitives.ReadUInt32BigEndian(row[16..]));
            var remote = ipv4
                ? new IPAddress(row.Slice(12, 4))
                : new IPAddress(row.Slice(24, 16), BinaryPrimitives.ReadUInt32BigEndian(row[40..]));
            if (local.Equals(IPAddress.Any) || local.Equals(IPAddress.IPv6Any) ||
                remote.Equals(IPAddress.Any) || remote.Equals(IPAddress.IPv6Any))
            {
                return ProcessNetworkConnectionQuery.Failed;
            }

            connections.Add(new ProcessNetworkConnection(local, localPort, remote, remotePort));
        }

        return new ProcessNetworkConnectionQuery(NetworkQueryStatus.Success, connections.ToArray());
    }

    private sealed class NativeTcpTableReader : IExtendedTcpTableReader
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr table, ref uint bytes, [MarshalAs(UnmanagedType.Bool)] bool order,
            int family, int tableClass, uint reserved);

        public uint Read(IntPtr buffer, ref uint bytes, int addressFamily) =>
            GetExtendedTcpTable(buffer, ref bytes, false, addressFamily, 5, 0);
    }
}
