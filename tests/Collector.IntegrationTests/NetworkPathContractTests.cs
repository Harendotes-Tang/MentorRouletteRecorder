using System.Net;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>Production schema and real pipe projection tests using declared host facts only.</summary>
public sealed class NetworkPathContractTests
{
    private static readonly string[] Whitelist =
    {
        "query_ipv4", "query_ipv6", "query_adapters", "ipv4_connections", "ipv6_connections",
        "loopback_connections", "local_connections", "selected_interface_connections",
        "other_interface_connections", "unmapped_ipv4_connections", "matching_interfaces", "kind",
        "capture_interfaces", "candidate_interfaces", "capture_open_failures", "candidate_limit_reached",
        "loopback_capture_available", "ipv6_capture_supported",
    };

    [Fact]
    public void EveryProductionKindAndQueryTokenAndNullableCountMatchesTheMainSchema()
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        var queries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in Enum.GetValues<NetworkPathKind>())
        {
            foreach (var query in Enum.GetValues<NetworkQueryStatus>())
            {
                var snapshot = new NetworkPathDiagnosticsSnapshot
                {
                    Kind = kind,
                    QueryIPv4 = query,
                    QueryIPv6 = query,
                    QueryAdapters = query,
                };
                var wire = NetworkPathWire.ToJson(snapshot);
                ContractSchema.Validate("$defs/NetworkPath", wire, kind + "/" + query);
                Assert.Equal(Whitelist.Order(StringComparer.Ordinal), wire.Select(pair => pair.Key).Order(StringComparer.Ordinal));
                kinds.Add(wire["kind"]!.GetValue<string>());
                queries.Add(wire["query_ipv4"]!.GetValue<string>());
            }
        }

        var definitions = ContractSchema.Root["$defs"]!;
        var declaredKinds = definitions["NetworkPath"]!["properties"]!["kind"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>());
        var declaredQueries = definitions["NetworkQueryStatus"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>());
        Assert.Equal(declaredKinds.Order(StringComparer.Ordinal), kinds.Order(StringComparer.Ordinal));
        Assert.Equal(declaredQueries.Order(StringComparer.Ordinal), queries.Order(StringComparer.Ordinal));
        ContractSchema.Validate("$defs/NetworkPath", new JsonObject(), "older partial network facts");
    }

    [Fact]
    public void ProductionMaximumCountsAndBothMeasuredCapabilityValuesMatchTheSchema()
    {
        var snapshot = new NetworkPathDiagnosticsSnapshot
        {
            QueryIPv4 = NetworkQueryStatus.Success,
            QueryIPv6 = NetworkQueryStatus.Success,
            QueryAdapters = NetworkQueryStatus.Success,
            IPv4Connections = 16384,
            IPv6Connections = 16384,
            LoopbackConnections = 32768,
            LocalConnections = 32768,
            SelectedInterfaceConnections = 16384,
            OtherInterfaceConnections = 16384,
            UnmappedIPv4Connections = 16384,
            MatchingInterfaces = 256,
            CandidateInterfaces = 8,
            CaptureInterfaces = 8,
            CaptureOpenFailures = 8,
            CandidateLimitReached = true,
            LoopbackCaptureAvailable = true,
        };
        ContractSchema.Validate("$defs/NetworkPath", NetworkPathWire.ToJson(snapshot), "maximum bounded facts");
        ContractSchema.Validate("$defs/NetworkPath", NetworkPathWire.ToJson(snapshot with
        {
            CandidateLimitReached = false,
            LoopbackCaptureAvailable = false,
        }), "measured unavailable loopback");
    }

    [Theory]
    [InlineData("ipv4_connections", 16384)]
    [InlineData("ipv6_connections", 16384)]
    [InlineData("loopback_connections", 32768)]
    [InlineData("local_connections", 32768)]
    [InlineData("selected_interface_connections", 16384)]
    [InlineData("other_interface_connections", 16384)]
    [InlineData("unmapped_ipv4_connections", 16384)]
    [InlineData("matching_interfaces", 256)]
    [InlineData("capture_interfaces", 8)]
    [InlineData("candidate_interfaces", 8)]
    [InlineData("capture_open_failures", 8)]
    public void ContractRejectsOutOfRangeNonIntegralAndStringCounts(string field, int maximum)
    {
        foreach (var invalid in new JsonNode?[]
                 { JsonValue.Create(-1), JsonValue.Create(maximum + 1), JsonValue.Create(0.5), JsonValue.Create("192.0.2.29") })
        {
            var wire = NetworkPathWire.ToJson(NetworkPathDiagnosticsSnapshot.Unmeasured);
            wire[field] = invalid;
            Assert.False(ContractSchema.Evaluate("$defs/NetworkPath", wire).IsValid,
                field + " must reject " + invalid);
        }
    }

    [Theory]
    [InlineData("local_ip", "192.0.2.29")]
    [InlineData("remote_endpoint", "203.0.113.29:54994")]
    [InlineData("adapter_id", "private-interface-identity")]
    [InlineData("process_path", "C:\\Users\\private\\ffxiv_dx11.exe")]
    [InlineData("failure", "private native exception text")]
    public void ContractRejectsRawAndUnrecognisedProperties(string field, string value)
    {
        var wire = NetworkPathWire.ToJson(NetworkPathDiagnosticsSnapshot.Unmeasured);
        wire[field] = value;
        Assert.False(ContractSchema.Evaluate("$defs/NetworkPath", wire).IsValid);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("query_ipv4")]
    [InlineData("query_ipv6")]
    [InlineData("query_adapters")]
    public void ContractRejectsUnrecognisedTokensAndEndpointTextInTokenFields(string field)
    {
        foreach (var token in new[] { "UNRECOGNISED", "192.0.2.29", "203.0.113.29:54994" })
        {
            var wire = NetworkPathWire.ToJson(NetworkPathDiagnosticsSnapshot.Unmeasured);
            wire[field] = token;
            Assert.False(ContractSchema.Evaluate("$defs/NetworkPath", wire).IsValid);
        }
    }

    [Fact]
    public void ContractRefusesToClaimSupportedIPv6Capture()
    {
        var wire = NetworkPathWire.ToJson(NetworkPathDiagnosticsSnapshot.Unmeasured);
        wire["ipv6_capture_supported"] = true;
        Assert.False(ContractSchema.Evaluate("$defs/NetworkPath", wire).IsValid);
    }

    [Fact]
    public void CaptureStatusFromAnOlderCollectorRemainsValidWithoutNetworkPath()
    {
        var oldStatus = new JsonObject
        {
            ["state"] = "STOPPED",
            ["npcap_installed"] = false,
            ["ffxiv_running"] = false,
            ["profile_status"] = "NONE",
        };
        ContractSchema.Validate("$defs/CaptureStatus", oldStatus, "old Collector without network_path");
        ContractSchema.Validate("$defs/Responses/GetCaptureStatus", oldStatus, "old GetCaptureStatus response");

        oldStatus["network_path"] = NetworkPathWire.ToJson(NetworkPathDiagnosticsSnapshot.Unmeasured);
        ContractSchema.Validate("$defs/CaptureStatus", oldStatus, "new unmeasured network facts");
    }

    [Fact]
    public async Task RealNamedPipeStatusAndExportedReportUseTheSameEndpointFreeWhitelist()
    {
        using var source = new FakeCaptureSource();
        var connections = new DeclaredConnections();
        var services = CaptureFakes.Ready(source) with
        {
            NetworkPaths = new NetworkPathInspector(new DeclaredAdapters(), connections),
        };
        await using var fixture = CaptureServerFixture.Start(services);
        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/Responses/GetCaptureStatus", status, "real pipe with declared network paths");
        var statusPath = status["network_path"]!.AsObject();
        Assert.Equal("OK", statusPath["query_ipv4"]!.GetValue<string>());
        Assert.Equal("OK", statusPath["query_ipv6"]!.GetValue<string>());
        Assert.Equal("SELECTED_INTERFACE", statusPath["kind"]!.GetValue<string>());
        Assert.Equal(1, statusPath["ipv4_connections"]!.GetValue<int>());
        Assert.Null(statusPath["capture_interfaces"]);
        Assert.Null(statusPath["loopback_capture_available"]);
        Assert.Equal(Whitelist.Order(StringComparer.Ordinal), statusPath.Select(pair => pair.Key).Order(StringComparer.Ordinal));

        var target = Path.Combine(fixture.DataDirectory, "network-path-diagnostics.json");
        var exported = (await fixture.CallAsync("ExportDiagnosticsReport", new JsonObject { ["target_path"] = target })).Require();
        ContractSchema.Validate("$defs/Responses/ExportDiagnosticsReport", exported, "real pipe diagnostic export");
        var reportText = await File.ReadAllTextAsync(exported["target_path"]!.GetValue<string>());
        var report = JsonNode.Parse(reportText)!;
        var reportPath = report["network_path"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(statusPath, reportPath), "IPC and disk export must project the same facts.");
        ContractSchema.Validate("$defs/NetworkPath", reportPath, "exported network whitelist");
        Assert.Equal(2, report["report_version"]!.GetValue<int>());
        Assert.NotEqual(0, connections.Reads);
        Assert.Equal(4321, connections.LastPid);

        foreach (var secret in new[]
                 { "192.168.31.77", "203.0.113.29", "private-driver-name", "private-interface-description" })
        {
            Assert.DoesNotContain(secret, statusPath.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, reportText, StringComparison.Ordinal);
        }

        // Check raw ports in the network object; unrelated timestamp/hash digits in a whole
        // report can coincidentally contain the same number without exposing an endpoint.
        foreach (var port in new[] { "54123", "54994" })
        {
            Assert.DoesNotContain(port, statusPath.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain(port, reportPath.ToJsonString(), StringComparison.Ordinal);
        }

        Assert.True((await fixture.CallAsync("StopCapture")).Ok);
    }

    private sealed class DeclaredAdapters : IAdapterProvider
    {
        public IReadOnlyList<AdapterInfo> List() => new[]
        {
            new AdapterInfo("{TEST-ADAPTER}", "private-driver-name", "private-interface-description", true, false,
                new[] { IPAddress.Parse("192.168.31.77") }),
        };
    }

    private sealed class DeclaredConnections : IProcessNetworkConnectionProvider
    {
        public int Reads { get; private set; }
        public int LastPid { get; private set; }

        public ProcessNetworkConnectionTables Read(int processId)
        {
            Reads++;
            LastPid = processId;
            return new ProcessNetworkConnectionTables(
                new ProcessNetworkConnectionQuery(NetworkQueryStatus.Success, new[]
                {
                    new ProcessNetworkConnection(IPAddress.Parse("192.168.31.77"), 54123,
                        IPAddress.Parse("203.0.113.29"), 54994),
                }),
                new ProcessNetworkConnectionQuery(NetworkQueryStatus.Success, Array.Empty<ProcessNetworkConnection>()));
        }
    }
}
