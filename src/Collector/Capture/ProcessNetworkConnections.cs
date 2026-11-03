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

/// <summary>
/// A complete per-family answer, or an explicit reason that counts are unknown.
///
/// <see cref="Detail"/> is a closed-vocabulary token for the diagnostics report and the log,
/// never an endpoint, an identity or exception text (contracts/ipc-v1.schema.json
/// <c>NetworkPath.query_ipv4_detail</c>). On a failed or truncated query it names the check
/// that gave up: <c>native_size:&lt;code&gt;</c>, <c>native_read:&lt;code&gt;</c>,
/// <c>size_too_small</c>, <c>returned_length</c>, <c>header_short</c>, <c>row_count</c>,
/// <c>family</c>, <c>no_pid</c>, <c>exception:&lt;type&gt;</c>, <c>provider_exception</c>,
/// <c>frozen_row</c>, <c>table_too_large</c>, <c>table_growing</c>, <c>rows_too_many</c>.
/// On success it counts the rows the parser passed over,
/// <c>skipped_rows:&lt;n&gt;[;unknown_state:&lt;value&gt;]</c>, and is null when there were none.
/// </summary>
public sealed record ProcessNetworkConnectionQuery(
    NetworkQueryStatus Status,
    IReadOnlyList<ProcessNetworkConnection> Connections,
    string? Detail = null)
{
    public static ProcessNetworkConnectionQuery Unmeasured { get; } =
        new(NetworkQueryStatus.Unmeasured, Array.Empty<ProcessNetworkConnection>());

    /// <summary>A failed query that names the check which gave up.</summary>
    /// <param name="detail">Closed-vocabulary token; see the type summary.</param>
    public static ProcessNetworkConnectionQuery FailedBecause(string detail) =>
        new(NetworkQueryStatus.Failed, Array.Empty<ProcessNetworkConnection>(), detail);

    /// <summary>A truncated query that names the bound it ran into.</summary>
    /// <param name="detail">Closed-vocabulary token; see the type summary.</param>
    public static ProcessNetworkConnectionQuery TruncatedBecause(string detail) =>
        new(NetworkQueryStatus.Truncated, Array.Empty<ProcessNetworkConnection>(), detail);
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

    /// <summary><c>MIB_TCP_STATE_RESERVED</c>: a socket bound to a port but neither listening nor connected.</summary>
    private const uint BoundState = 100;

    /// <summary>Longest exception type name kept in an <c>exception:</c> token (contract grammar bound).</summary>
    private const int MaxExceptionTokenLength = 40;
    private readonly IExtendedTcpTableReader _reader;

    public static SystemProcessConnectionTable Instance { get; } = new(new NativeTcpTableReader());

    internal SystemProcessConnectionTable(IExtendedTcpTableReader reader) =>
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public ProcessNetworkConnectionTables Read(int processId) => processId <= 0
        ? ProcessNetworkConnectionTables.Unmeasured
        : new(ReadFamily(processId, AddressFamily.InterNetwork), ReadFamily(processId, AddressFamily.InterNetworkV6));

    /// <summary>
    /// Returns distinct local addresses from the complete family queries. An IPv4 table that
    /// could not be read throws a fixed, endpoint-free error so adapter selection cannot mistake
    /// unreadable for idle (audit 2026-10-03, OB-4). An IPv6 table that could not be read is
    /// tolerated: capture reads IPv4 only, so the IPv6 answer can at most veto the title-screen
    /// rule, and a machine whose IPv6 table fails must still get its card from the IPv4 table
    /// (report 2026-11-03).
    /// </summary>
    public IReadOnlyList<IPAddress> LocalAddresses(int processId)
    {
        if (processId <= 0)
        {
            return Array.Empty<IPAddress>();
        }

        var tables = Read(processId);
        if (tables.IPv4.Status != NetworkQueryStatus.Success)
        {
            throw new InvalidOperationException("System IPv4 TCP table is not available.");
        }

        var ipv6 = tables.IPv6.Status == NetworkQueryStatus.Success
            ? tables.IPv6.Connections
            : Array.Empty<ProcessNetworkConnection>();
        return tables.IPv4.Connections.Concat(ipv6)
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
                return ProcessNetworkConnectionQuery.FailedBecause(FormattableString.Invariant($"native_size:{result}"));
            }

            for (var attempt = 0; attempt < MaxReadAttempts; attempt++)
            {
                if (required > MaxTableBytes)
                {
                    return ProcessNetworkConnectionQuery.TruncatedBecause("table_too_large");
                }

                if (required < sizeof(uint))
                {
                    return ProcessNetworkConnectionQuery.FailedBecause("size_too_small");
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

                    if (result != 0)
                    {
                        return ProcessNetworkConnectionQuery.FailedBecause(FormattableString.Invariant($"native_read:{result}"));
                    }

                    if (returned < sizeof(uint) || returned > allocated)
                    {
                        return ProcessNetworkConnectionQuery.FailedBecause("returned_length");
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

            return ProcessNetworkConnectionQuery.TruncatedBecause("table_growing");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Query state and the exception's type leave this boundary; its message can contain
            // native or host data and does not.
            return ProcessNetworkConnectionQuery.FailedBecause(ExceptionToken(exception));
        }
    }

    /// <summary>
    /// <c>exception:&lt;type&gt;</c> from the type name alone: lower-cased, reduced to the token
    /// alphabet (a generic arity marker such as <c>`1</c> is dropped) and bounded, so that the
    /// token always fits the contract grammar (<see cref="NetworkPathWire.DetailPattern"/>).
    /// </summary>
    /// <param name="exception">The exception; only its type is read.</param>
    private static string ExceptionToken(Exception exception)
    {
        var name = new string(exception.GetType().Name.ToLowerInvariant()
            .Where(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
            .Take(MaxExceptionTokenLength)
            .ToArray());
        return "exception:" + (name.Length == 0 || !char.IsAsciiLetterLower(name[0]) ? "unknown" : name);
    }

    /// <summary>
    /// Parses documented DWORD-aligned OWNER_PID rows.
    ///
    /// Structural damage -- a short header, more rows than the buffer holds -- fails the family.
    /// A single row beside the evidence does not: a state outside the twelve MIB states or a game
    /// row without both ports and both addresses is passed over and counted in
    /// <see cref="ProcessNetworkConnectionQuery.Detail"/>, because failing the table for one such
    /// row left a whole session without a card (report 2026-11-03,
    /// <c>capture.start.adapter_undetermined</c>). Skipping is never allowed to stand in for the
    /// evidence itself: when the only rows the game owns are rows that could not be read, or when
    /// no row of the table could be read at all, the family is unreadable, so that adapter
    /// selection refuses rather than applies the title-screen rule to a game that may be talking
    /// on another card (audit 2026-10-03, OB-4). A bound, unconnected socket
    /// (<c>MIB_TCP_STATE_RESERVED</c>, 100; "Bound" to Get-NetTCPConnection) is a known
    /// non-path state like LISTEN, not an unreadable row.
    /// </summary>
    internal static ProcessNetworkConnectionQuery Parse(ReadOnlySpan<byte> bytes, AddressFamily family, int processId)
    {
        var rowBytes = family switch
        {
            AddressFamily.InterNetwork => 24,
            AddressFamily.InterNetworkV6 => 56,
            _ => 0,
        };
        if (rowBytes == 0)
        {
            return ProcessNetworkConnectionQuery.FailedBecause("family");
        }

        if (processId <= 0)
        {
            return ProcessNetworkConnectionQuery.FailedBecause("no_pid");
        }

        if (bytes.Length < sizeof(uint))
        {
            return ProcessNetworkConnectionQuery.FailedBecause("header_short");
        }

        if (bytes.Length > MaxTableBytes)
        {
            return ProcessNetworkConnectionQuery.TruncatedBecause("table_too_large");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > MaxTableRows)
        {
            return ProcessNetworkConnectionQuery.TruncatedBecause("rows_too_many");
        }

        if (count > (uint)((bytes.Length - sizeof(uint)) / rowBytes))
        {
            return ProcessNetworkConnectionQuery.FailedBecause("row_count");
        }

        var connections = new List<ProcessNetworkConnection>();
        var untrusted = 0;
        var ownUntrusted = 0;
        uint? unknownState = null;
        for (var index = 0; index < count; index++)
        {
            var row = bytes.Slice(sizeof(uint) + index * rowBytes, rowBytes);
            var ipv4 = family == AddressFamily.InterNetwork;
            var state = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 0 : 48)..]);
            var pid = BinaryPrimitives.ReadUInt32LittleEndian(row[(ipv4 ? 20 : 52)..]);
            var own = pid == (uint)processId;

            // LISTEN/CLOSED/DELETE_TCB and a bound, unconnected socket are not active paths.
            if (state is 1 or 2 or 12 or BoundState)
            {
                continue;
            }

            if (state is < 1 or > 12)
            {
                untrusted++;
                if (own) ownUntrusted++;
                unknownState ??= state;
                continue;
            }

            // Other PIDs are never retained.
            if (!own)
            {
                continue;
            }

            var localPort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 8 : 20)..]);
            var remotePort = BinaryPrimitives.ReadUInt16BigEndian(row[(ipv4 ? 16 : 44)..]);
            if (localPort == 0 || remotePort == 0)
            {
                untrusted++;
                ownUntrusted++;
                continue;
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
                untrusted++;
                ownUntrusted++;
                continue;
            }

            connections.Add(new ProcessNetworkConnection(local, localPort, remote, remotePort));
        }

        if (count > 0 && untrusted == count)
        {
            return ProcessNetworkConnectionQuery.FailedBecause("rows_untrusted");
        }

        if (connections.Count == 0 && ownUntrusted > 0)
        {
            return ProcessNetworkConnectionQuery.FailedBecause("own_rows_unreadable");
        }

        return new ProcessNetworkConnectionQuery(
            NetworkQueryStatus.Success, connections.ToArray(), SkippedDetail(untrusted, unknownState));
    }

    /// <summary>
    /// <c>skipped_rows:&lt;n&gt;[;unknown_state:&lt;value&gt;]</c>. A sound table holds only the
    /// twelve MIB states and 100; a value beyond a byte is a misread row's bytes rendered as an
    /// integer and is reported as <c>other</c> rather than copied out.
    /// </summary>
    private static string? SkippedDetail(int skipped, uint? unknownState) => skipped == 0
        ? null
        : unknownState is { } state
            ? FormattableString.Invariant($"skipped_rows:{skipped};unknown_state:{(state <= byte.MaxValue ? state.ToString(System.Globalization.CultureInfo.InvariantCulture) : "other")}")
            : FormattableString.Invariant($"skipped_rows:{skipped}");

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
