using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Synthetic host-table and topology evidence; no game or accelerator acceptance.</summary>
public sealed class NetworkPathDiagnosticsTests
{
    private const int GamePid = 42;

    [Fact]
    public void NativeIPv4ParserUsesCompletePidAndNetworkByteOrderAndExcludesListeners()
    {
        var bytes = Table(
            Row4(GamePid, "10.8.0.2", 52001, "203.0.113.20", 54994),
            Row4(99, "10.8.0.3", 52002, "203.0.113.20", 54994),
            Row4(GamePid, "0.0.0.0", 52003, "0.0.0.0", 0, state: 2),
            Row4(GamePid, "10.8.0.2", 52004, "203.0.113.20", 54994, state: 1),
            Row4(GamePid, "10.8.0.2", 52005, "203.0.113.20", 54994, state: 12));

        var result = SystemProcessConnectionTable.Parse(bytes, AddressFamily.InterNetwork, GamePid);

        Assert.Equal(NetworkQueryStatus.Success, result.Status);
        var connection = Assert.Single(result.Connections);
        Assert.Equal(IPAddress.Parse("10.8.0.2"), connection.LocalAddress);
        Assert.Equal(52001, connection.LocalPort);
        Assert.Equal(IPAddress.Parse("203.0.113.20"), connection.RemoteAddress);
        Assert.Equal(54994, connection.RemotePort);
        Assert.DoesNotContain("10.8.0.2", connection.ToString());
    }

    [Fact]
    public void NativeIPv6ParserKeepsFullAddressPortAndDocumentedNetworkOrderScope()
    {
        var bytes = Table(Row6(GamePid, "fe80::1234", 51001, "fe80::5678", 54995, 17, 18));
        var result = SystemProcessConnectionTable.Parse(bytes, AddressFamily.InterNetworkV6, GamePid);

        var connection = Assert.Single(result.Connections);
        Assert.Equal(NetworkQueryStatus.Success, result.Status);
        Assert.Equal(IPAddress.Parse("fe80::1234%17"), connection.LocalAddress);
        Assert.Equal(IPAddress.Parse("fe80::5678%18"), connection.RemoteAddress);
        Assert.Equal(51001, connection.LocalPort);
        Assert.Equal(54995, connection.RemotePort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(27)]
    public void NativeParserRejectsIncompleteHeadersOrRows(int bytes)
    {
        var complete = Table(Row4(GamePid, "10.0.0.2", 51001, "203.0.113.20", 54994));
        var result = SystemProcessConnectionTable.Parse(complete.AsSpan(0, bytes), AddressFamily.InterNetwork, GamePid);
        Assert.Equal(NetworkQueryStatus.Failed, result.Status);
        Assert.Empty(result.Connections);
    }

    [Fact]
    public void NativeParserRejectsImpossibleRowsAndOversizedRowCounts()
    {
        var invalidState = Table(Row4(GamePid, "10.0.0.2", 51001, "203.0.113.20", 54994, state: 13));
        var invalidPort = Table(Row4(GamePid, "10.0.0.2", 0, "203.0.113.20", 54994));
        var wildcard = Table(Row4(GamePid, "0.0.0.0", 51001, "203.0.113.20", 54994));
        var countOnly = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(countOnly, SystemProcessConnectionTable.MaxTableRows + 1u);

        foreach (var bytes in new[] { invalidState, invalidPort, wildcard })
        {
            var result = SystemProcessConnectionTable.Parse(bytes, AddressFamily.InterNetwork, GamePid);
            Assert.Equal(NetworkQueryStatus.Failed, result.Status);
            Assert.Empty(result.Connections);
        }

        Assert.Equal(NetworkQueryStatus.Truncated,
            SystemProcessConnectionTable.Parse(countOnly, AddressFamily.InterNetwork, GamePid).Status);
    }

    [Fact]
    public void NativeQueryReadsBothFamiliesWithoutMachinaAndReturnsBothLocalAddresses()
    {
        var reader = new FixtureNativeReader(
            Table(Row4(GamePid, "10.8.0.2", 51001, "203.0.113.20", 54994)),
            Table(Row6(GamePid, "2001:db8::2", 51002, "2001:db8::20", 54995)));
        var addresses = new SystemProcessConnectionTable(reader).LocalAddresses(GamePid);

        Assert.Equal(new[] { IPAddress.Parse("10.8.0.2"), IPAddress.Parse("2001:db8::2") }, addresses);
        Assert.Equal(new[] { 2, 2, 23, 23 }, reader.Families);
    }

    [Fact]
    public void NativeQueryPreservesFailureForOneFamilyAndNeverReturnsIdleOnPartialFailure()
    {
        var reader = new FixtureNativeReader(Table(), Table()) { FailIPv6 = true };
        var table = new SystemProcessConnectionTable(reader);
        var result = table.Read(GamePid);

        Assert.Equal(NetworkQueryStatus.Success, result.IPv4.Status);
        Assert.Empty(result.IPv4.Connections);
        Assert.Equal(NetworkQueryStatus.Failed, result.IPv6.Status);
        Assert.Throws<InvalidOperationException>(() => table.LocalAddresses(GamePid));
    }

    [Fact]
    public void NativeQueryRefusesOversizedAllocationAndBoundsRepeatedGrowth()
    {
        var oversized = new FixtureNativeReader(Table(), Table())
        {
            RequiredBytes = SystemProcessConnectionTable.MaxTableBytes + 1u,
        };
        var overResult = new SystemProcessConnectionTable(oversized).Read(GamePid);
        Assert.Equal(NetworkQueryStatus.Truncated, overResult.IPv4.Status);
        Assert.Equal(NetworkQueryStatus.Truncated, overResult.IPv6.Status);
        Assert.Equal(2, oversized.Families.Count);
        Assert.Equal(0, oversized.DataReads);

        var growing = new FixtureNativeReader(Table(), Table()) { AlwaysGrow = true };
        var growthResult = new SystemProcessConnectionTable(growing).Read(GamePid);
        Assert.Equal(NetworkQueryStatus.Truncated, growthResult.IPv4.Status);
        Assert.Equal(NetworkQueryStatus.Truncated, growthResult.IPv6.Status);
        Assert.Equal(2 * SystemProcessConnectionTable.MaxReadAttempts, growing.DataReads);
    }

    [Fact]
    public void NativeQueryRejectsReturnedLengthOutsideAllocatedBuffer()
    {
        var reader = new FixtureNativeReader(Table(), Table()) { InvalidReturnedLength = true };
        var result = new SystemProcessConnectionTable(reader).Read(GamePid);

        Assert.Equal(NetworkQueryStatus.Failed, result.IPv4.Status);
        Assert.Equal(NetworkQueryStatus.Failed, result.IPv6.Status);
    }

    [Fact]
    public void NoPidIsUnmeasuredAndDoesNotReadHostTables()
    {
        var connections = new FakeConnections(Tables());
        var adapters = new ThrowingAdapters();
        var inspector = new NetworkPathInspector(adapters, connections);

        var snapshot = inspector.Inspect(null, "card");

        Assert.Equal(0, connections.Reads);
        Assert.Equal(0, adapters.Reads);
        Assert.Equal(NetworkPathKind.Unknown, snapshot.Kind);
        Assert.Equal(NetworkQueryStatus.Unmeasured, snapshot.QueryIPv4);
        Assert.Equal(NetworkQueryStatus.Unmeasured, snapshot.QueryAdapters);
        Assert.Null(snapshot.IPv4Connections);
    }

    [Fact]
    public void SuccessfulEmptyQueriesAreNoConnectionsRatherThanUnknown()
    {
        var snapshot = Inspect(Tables());
        Assert.Equal(NetworkPathKind.NoConnections, snapshot.Kind);
        Assert.Equal(0, snapshot.IPv4Connections);
        Assert.Equal(0, snapshot.IPv6Connections);
        Assert.Equal(0, snapshot.UnmappedIPv4Connections);
    }

    [Fact]
    public void DirectAndVirtualPathsUseAddressFactsRatherThanDriverBrands()
    {
        var adapters = Adapters();
        var direct = Inspect(Tables(v4: new[] { Connection("192.0.2.2", "203.0.113.20") }), adapters, "physical");
        var virtualPath = Inspect(Tables(v4: new[] { Connection("10.8.0.2", "203.0.113.20") }), adapters, "physical");

        Assert.Equal(NetworkPathKind.SelectedInterface, direct.Kind);
        Assert.Equal(1, direct.SelectedInterfaceConnections);
        Assert.Equal(NetworkPathKind.OtherInterface, virtualPath.Kind);
        Assert.Equal(1, virtualPath.OtherInterfaceConnections);
        Assert.Equal(1, virtualPath.MatchingInterfaces);
        Assert.Equal(0, virtualPath.UnmappedIPv4Connections);
    }

    [Fact]
    public void MultihomedAndUnmappedConnectionsAreDistinctMeasuredPaths()
    {
        var multi = Inspect(Tables(v4: new[]
        {
            Connection("192.0.2.2", "203.0.113.20"),
            Connection("10.8.0.2", "203.0.113.21"),
        }), Adapters(), "physical");
        var unmapped = Inspect(Tables(v4: new[] { Connection("10.9.0.2", "203.0.113.20") }), Adapters(), "physical");

        Assert.Equal(NetworkPathKind.MultipleInterfaces, multi.Kind);
        Assert.Equal(2, multi.MatchingInterfaces);
        Assert.Equal(1, multi.SelectedInterfaceConnections);
        Assert.Equal(1, multi.OtherInterfaceConnections);
        Assert.Equal(NetworkPathKind.Unmapped, unmapped.Kind);
        Assert.Equal(1, unmapped.UnmappedIPv4Connections);
    }

    [Fact]
    public void SameAddressProxyAndLocalInterfaceProxyAreClassifiedWithoutPortMagnitudeRules()
    {
        var loopback = Inspect(Tables(v4: new[] { Connection("127.0.0.1", "127.0.0.1", 1000, 60000) }), Adapters(), "physical");
        var local = Inspect(Tables(v4: new[] { Connection("192.0.2.2", "10.8.0.2", 60000, 1000) }), Adapters(), "physical");

        Assert.Equal(NetworkPathKind.Loopback, loopback.Kind);
        Assert.Equal(1, loopback.LoopbackConnections);
        Assert.Equal(1, loopback.LocalConnections);
        Assert.Equal(NetworkPathKind.Local, local.Kind);
        Assert.Equal(0, local.LoopbackConnections);
        Assert.Equal(1, local.LocalConnections);
    }

    [Fact]
    public void CanonicalNpcapLoopbackCoversAll127AddressesWithoutGuessingFromDisplayName()
    {
        var rows = Tables(v4: new[] { Connection("127.0.0.2", "127.0.0.3") });
        var adapters = new FakeAdapterProvider().Add(CapturePathPlan.LoopbackDeviceId,
            "arbitrary display", isLoopback: true, addresses: "127.0.0.1");
        var snapshot = Inspect(rows, adapters, CapturePathPlan.LoopbackDeviceId);

        Assert.Equal(NetworkPathKind.Loopback, snapshot.Kind);
        Assert.Equal(1, snapshot.SelectedInterfaceConnections);
        Assert.Equal(1, snapshot.MatchingInterfaces);
        Assert.Equal(0, snapshot.UnmappedIPv4Connections);

        var nameOnly = new FakeAdapterProvider().Add("unrelated", "Npcap Loopback Adapter", addresses: "127.0.0.1");
        Assert.Equal(1, Inspect(rows, nameOnly).UnmappedIPv4Connections);
    }

    [Fact]
    public void IPv6OnlyIsMeasuredButNeverClaimsPayloadCaptureSupportOrKnownLocalCoverage()
    {
        var snapshot = Inspect(Tables(v6: new[] { Connection("2001:db8::2", "2001:db8::20") }));
        Assert.Equal(NetworkPathKind.IPv6Only, snapshot.Kind);
        Assert.Equal(1, snapshot.IPv6Connections);
        Assert.Equal(0, snapshot.IPv4Connections);
        Assert.Null(snapshot.LocalConnections);
        Assert.False(snapshot.IPv6CaptureSupported);
    }

    [Fact]
    public void IPv6LoopbackCanBeCountedWithoutClaimingItIsCapturable()
    {
        var snapshot = Inspect(Tables(v6: new[] { Connection("::1", "::1") }));
        Assert.Equal(NetworkPathKind.IPv6Only, snapshot.Kind);
        Assert.Equal(1, snapshot.LoopbackConnections);
        Assert.Equal(1, snapshot.LocalConnections);
        Assert.False(snapshot.IPv6CaptureSupported);
    }

    [Theory]
    [InlineData(NetworkQueryStatus.Failed)]
    [InlineData(NetworkQueryStatus.Unmeasured)]
    [InlineData(NetworkQueryStatus.Truncated)]
    public void IncompleteQueriesNeverBecomeNoConnections(NetworkQueryStatus status)
    {
        var tables = new ProcessNetworkConnectionTables(
            new ProcessNetworkConnectionQuery(status, Array.Empty<ProcessNetworkConnection>()),
            Success());
        var snapshot = Inspect(tables);

        Assert.Equal(NetworkPathKind.Unknown, snapshot.Kind);
        Assert.Equal(status, snapshot.QueryIPv4);
        Assert.Null(snapshot.IPv4Connections);
        Assert.Null(snapshot.LoopbackConnections);
        Assert.Null(snapshot.SelectedInterfaceConnections);
        Assert.Equal(0, snapshot.IPv6Connections);
    }

    [Fact]
    public void FailedOrTruncatedInterfaceEnumerationIsUnknownRatherThanUnmapped()
    {
        var tables = Tables(v4: new[] { Connection("192.0.2.2", "203.0.113.20") });
        var failed = new NetworkPathInspector(new ThrowingAdapters(), new FakeConnections(tables)).Inspect(GamePid, "physical");
        Assert.Equal(NetworkQueryStatus.Failed, failed.QueryAdapters);
        Assert.Equal(NetworkPathKind.Unknown, failed.Kind);
        Assert.Null(failed.UnmappedIPv4Connections);

        var tooMany = new FakeAdapterProvider();
        for (var index = 0; index <= NetworkPathInspector.MaxInterfaces; index++)
        {
            tooMany.Add("card-" + index, "arbitrary display", addresses: "192.0.2.2");
        }

        var truncated = new NetworkPathInspector(tooMany, new FakeConnections(tables)).Inspect(GamePid, "physical");
        Assert.Equal(NetworkQueryStatus.Truncated, truncated.QueryAdapters);
        Assert.Null(truncated.MatchingInterfaces);
    }

    [Fact]
    public void InterfaceAddressListAndInjectedRowsAreAlsoBounded()
    {
        var adapter = new FakeAdapterProvider().Add("card", "arbitrary",
            addresses: Enumerable.Repeat("192.0.2.2", NetworkPathInspector.MaxAddressesPerInterface + 1).ToArray());
        var tooManyAddresses = new NetworkPathInspector(adapter, new FakeConnections(Tables())).Inspect(GamePid, "card");
        Assert.Equal(NetworkQueryStatus.Truncated, tooManyAddresses.QueryAdapters);

        var tables = Tables(v4: Enumerable.Repeat(Connection("192.0.2.2", "203.0.113.20"),
            SystemProcessConnectionTable.MaxTableRows + 1).ToArray());
        var tooManyRows = Inspect(tables);
        Assert.Equal(NetworkQueryStatus.Truncated, tooManyRows.QueryIPv4);
        Assert.Null(tooManyRows.IPv4Connections);
    }

    [Fact]
    public void CacheExpiresAtOneSecondChangesPidAndRefreshesCaptureFactsEveryCall()
    {
        var time = new ManualTimeProvider();
        var provider = new FakeConnections(Tables(v4: new[] { Connection("192.0.2.2", "203.0.113.20") }));
        var inspector = new NetworkPathInspector(Adapters(), provider, time);
        var initial = inspector.Inspect(GamePid, "physical", new CapturePathStatus(3, 2, 1, false, true));
        time.Advance(999);
        var cached = inspector.Inspect(GamePid, "virtual", new CapturePathStatus(4, 3, 1, true, false));

        Assert.Equal(1, provider.Reads);
        Assert.Equal(NetworkPathKind.SelectedInterface, initial.Kind);
        Assert.Equal(NetworkPathKind.OtherInterface, cached.Kind);
        Assert.Equal(3, cached.CaptureInterfaces);
        Assert.Equal(4, cached.CandidateInterfaces);
        Assert.True(cached.CandidateLimitReached);
        Assert.False(cached.LoopbackCaptureAvailable);

        time.Advance(1);
        inspector.Inspect(GamePid, "physical");
        Assert.Equal(2, provider.Reads);
        inspector.Inspect(GamePid + 1, "physical");
        Assert.Equal(3, provider.Reads);
        inspector.Inspect(null, "physical");
        inspector.Inspect(GamePid + 1, "physical");
        Assert.Equal(4, provider.Reads);
    }

    [Fact]
    public void CachedRawFactsAreFrozenRatherThanAliasedToInjectableMutableLists()
    {
        var rows = new List<ProcessNetworkConnection> { Connection("192.0.2.2", "203.0.113.20") };
        var provider = new FakeConnections(Tables(v4: rows));
        var inspector = new NetworkPathInspector(Adapters(), provider);
        inspector.Inspect(GamePid, "physical");
        rows.Clear();

        Assert.Equal(1, inspector.Inspect(GamePid, "physical").IPv4Connections);
        Assert.Equal(1, provider.Reads);
    }

    [Fact]
    public void WireIsAnExactBoundedWhitelistAndCannotExposeHostIdentitiesOrExceptionText()
    {
        var raw = Tables(v4: new[] { Connection("192.0.2.2", "127.0.0.1", 52567, 60432) });
        var adapters = new FakeAdapterProvider().Add("private-adapter-identity", "private-name", addresses: "192.0.2.2");
        var snapshot = Inspect(raw, adapters, "private-adapter-identity");
        var wire = NetworkPathWire.ToJson(snapshot);
        var expected = new[]
        {
            "query_ipv4", "query_ipv6", "query_adapters", "ipv4_connections", "ipv6_connections",
            "loopback_connections", "local_connections", "selected_interface_connections",
            "other_interface_connections", "unmapped_ipv4_connections", "matching_interfaces", "kind",
            "capture_interfaces", "candidate_interfaces", "capture_open_failures", "candidate_limit_reached",
            "loopback_capture_available", "ipv6_capture_supported",
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), wire.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        var json = wire.ToJsonString();
        foreach (var secret in new[] { "192.0.2.2", "127.0.0.1", "52567", "60432", "private-adapter-identity", "private-name" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        var failed = new FakeConnections(raw) { Failure = new InvalidOperationException("private-host C:\\Users\\private-user 10.1.2.3:5000") };
        var failedJson = NetworkPathWire.ToJson(new NetworkPathInspector(adapters, failed).Inspect(GamePid, null)).ToJsonString();
        Assert.DoesNotContain("private-host", failedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", failedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", failedJson, StringComparison.Ordinal);
        Assert.Equal("FAILED", System.Text.Json.Nodes.JsonNode.Parse(failedJson)?["query_ipv4"]?.GetValue<string>());
    }

    [Fact]
    public void WireDefendsItsClosedTokensAndBoundsEvenForArtificialSnapshots()
    {
        var wire = NetworkPathWire.ToJson(new NetworkPathDiagnosticsSnapshot
        {
            QueryIPv4 = (NetworkQueryStatus)999,
            Kind = (NetworkPathKind)999,
            IPv4Connections = int.MaxValue,
            LocalConnections = int.MaxValue,
            MatchingInterfaces = int.MaxValue,
            CaptureInterfaces = int.MaxValue,
            CaptureOpenFailures = -1,
        });

        Assert.Equal("UNMEASURED", wire["query_ipv4"]?.GetValue<string>());
        Assert.Equal("UNKNOWN", wire["kind"]?.GetValue<string>());
        Assert.Equal(16384, wire["ipv4_connections"]?.GetValue<int>());
        Assert.Equal(32768, wire["local_connections"]?.GetValue<int>());
        Assert.Equal(256, wire["matching_interfaces"]?.GetValue<int>());
        Assert.Equal(8, wire["capture_interfaces"]?.GetValue<int>());
        Assert.Null(wire["capture_open_failures"]);
        Assert.Null(wire["loopback_capture_available"]);
        Assert.False(wire["ipv6_capture_supported"]!.GetValue<bool>());
    }

    private static NetworkPathDiagnosticsSnapshot Inspect(
        ProcessNetworkConnectionTables tables, IAdapterProvider? adapters = null, string? selected = "physical") =>
        new NetworkPathInspector(adapters ?? Adapters(), new FakeConnections(tables)).Inspect(GamePid, selected);

    private static FakeAdapterProvider Adapters() => new FakeAdapterProvider()
        .Add("physical", "arbitrary hardware", addresses: "192.0.2.2")
        .Add("virtual", "arbitrary hardware", addresses: "10.8.0.2")
        .Add("loopback", "arbitrary hardware", isLoopback: true, addresses: "127.0.0.1");

    private static ProcessNetworkConnection Connection(string local, string remote, ushort localPort = 51001, ushort remotePort = 54994) =>
        new(IPAddress.Parse(local), localPort, IPAddress.Parse(remote), remotePort);

    private static ProcessNetworkConnectionQuery Success(IReadOnlyList<ProcessNetworkConnection>? connections = null) =>
        new(NetworkQueryStatus.Success, connections ?? Array.Empty<ProcessNetworkConnection>());

    private static ProcessNetworkConnectionTables Tables(
        IReadOnlyList<ProcessNetworkConnection>? v4 = null, IReadOnlyList<ProcessNetworkConnection>? v6 = null) =>
        new(Success(v4), Success(v6));

    private static byte[] Table(params byte[][] rows)
    {
        var bytes = new byte[4 + rows.Sum(row => row.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)rows.Length);
        var offset = 4;
        foreach (var row in rows)
        {
            row.CopyTo(bytes, offset);
            offset += row.Length;
        }

        return bytes;
    }

    private static byte[] Row4(int pid, string local, ushort localPort, string remote, ushort remotePort, uint state = 5)
    {
        var row = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(row, state);
        IPAddress.Parse(local).GetAddressBytes().CopyTo(row, 4);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(8), localPort);
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 12);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(16), remotePort);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(20), (uint)pid);
        return row;
    }

    private static byte[] Row6(int pid, string local, ushort localPort, string remote, ushort remotePort, uint localScope = 0, uint remoteScope = 0)
    {
        var row = new byte[56];
        IPAddress.Parse(local).GetAddressBytes().CopyTo(row, 0);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(16), localScope);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(20), localPort);
        IPAddress.Parse(remote).GetAddressBytes().CopyTo(row, 24);
        BinaryPrimitives.WriteUInt32BigEndian(row.AsSpan(40), remoteScope);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(44), remotePort);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(48), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(52), (uint)pid);
        return row;
    }

    private sealed class FakeConnections(ProcessNetworkConnectionTables tables) : IProcessNetworkConnectionProvider
    {
        public int Reads { get; private set; }
        public Exception? Failure { get; init; }

        public ProcessNetworkConnectionTables Read(int processId)
        {
            Reads++;
            return Failure is { } failure ? throw failure : tables;
        }
    }

    private sealed class ThrowingAdapters : IAdapterProvider
    {
        public int Reads { get; private set; }

        public IReadOnlyList<AdapterInfo> List()
        {
            Reads++;
            throw new InvalidOperationException("private-interface-enumeration-detail");
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public void Advance(int milliseconds) => _milliseconds += milliseconds;
    }

    private sealed class FixtureNativeReader(byte[] ipv4, byte[] ipv6) : IExtendedTcpTableReader
    {
        public List<int> Families { get; } = new();
        public int DataReads { get; private set; }
        public bool FailIPv6 { get; init; }
        public uint? RequiredBytes { get; init; }
        public bool AlwaysGrow { get; init; }
        public bool InvalidReturnedLength { get; init; }

        public uint Read(IntPtr buffer, ref uint bytes, int addressFamily)
        {
            Families.Add(addressFamily);
            if (FailIPv6 && addressFamily == (int)AddressFamily.InterNetworkV6)
            {
                return 50;
            }

            var source = addressFamily == (int)AddressFamily.InterNetwork ? ipv4 : ipv6;
            if (buffer == IntPtr.Zero)
            {
                bytes = RequiredBytes ?? (uint)source.Length;
                return 122;
            }

            DataReads++;
            if (AlwaysGrow)
            {
                bytes += 4;
                return 122;
            }

            if (InvalidReturnedLength)
            {
                bytes++;
                return 0;
            }

            Marshal.Copy(source, 0, buffer, source.Length);
            bytes = (uint)source.Length;
            return 0;
        }
    }
}
