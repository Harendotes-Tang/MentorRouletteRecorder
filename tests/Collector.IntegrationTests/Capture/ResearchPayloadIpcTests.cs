using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class ResearchPayloadIpcTests
{
    [Fact]
    public async Task ExpandedZonePayloadsTraverseLivePipelineAndEvidenceWithoutEnteringOrdinaryIpc()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        using var subscription = fixture.Host.LiveEvents.Subscribe(Guid.NewGuid().ToString("D"));
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject
        {
            ["candidate_validation_enabled"] = true,
            ["research_payload_opcodes"] = new JsonArray("0x014a", "0x031d", "0x0153", "0x0214"),
        })).Require();
        var pipeline = fixture.Host.LiveProtocol!;
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "2026.08.05.0000.0000" });
        pipeline.OnCaptureStarted(fixture.Host.CaptureSessionId);

        // Five distinct members form a real candidate zone burst. The fifth member is
        // deliberately too long for research; it must still contribute metadata only.
        var members = new (ushort Opcode, int Length)[]
            { (0x014a, 456), (0x031d, 448), (0x0153, 360), (0x0214, 424), (0x0077, 640) };
        var expected = new Dictionary<int, string>();
        foreach (var (opcode, length) in members)
        {
            var payload = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
            if (length <= 512) expected.Add(opcode, Convert.ToHexString(payload).ToLowerInvariant());
            pipeline.Accept(new DecodedMessage(fixture.Host.CaptureSessionId, MessageDirection.Inbound,
                fixture.Host.Clock.UtcNow, TimeSpan.FromMilliseconds(1000 + expected.Count), 0, 3,
                opcode, payload, "private expanded-payload connection"));
        }
        var stored = fixture.Host.Candidates.ReadEvidence().Observations;
        Assert.Equal(6, stored.Count);
        foreach (var (opcode, hex) in expected)
            Assert.Equal(hex, Assert.Single(stored, row => row.Observation.Opcode == opcode).Observation.PayloadHex);
        Assert.Null(Assert.Single(stored, row => row.Observation.Opcode == 0x0077).Observation.PayloadHex);
        Assert.Null(Assert.Single(stored, row => row.Observation.HypothesisName == "ZONE_LOAD").Observation.PayloadHex);

        var query = (await client.SendAsync("QueryCandidateObservations")).Require();
        Assert.All(query["items"]!.AsArray(), row => Assert.False(row!.AsObject().ContainsKey("payload_hex")));
        for (var i = 0; i < 6; i++)
        {
            var observed = await subscription.ReadAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(observed);
            Assert.Equal("candidate_observed", observed["kind"]!.GetValue<string>());
            Assert.DoesNotContain("payload_hex", observed.ToJsonString(), StringComparison.Ordinal);
            Assert.All(expected.Values, raw => Assert.DoesNotContain(raw, observed.ToJsonString(), StringComparison.Ordinal));
        }
        // Turning research off must not strip historical evidence or mislabel its export.
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject
            { ["candidate_validation_enabled"] = false, ["research_payload_opcodes"] = new JsonArray() })).Require();
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var path = Path.Combine(directory, "expanded-research.json");
        var exported = (await client.SendAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).Require();
        var bytes = await File.ReadAllBytesAsync(path);
        var evidence = JsonNode.Parse(bytes)!;
        Assert.True(evidence["contains_raw_payload"]!.GetValue<bool>());
        Assert.Equal(4, evidence["research_payload_opcodes"]!.AsArray().Count);
        Assert.Equal("CANDIDATE", evidence["profile_status"]!.GetValue<string>());
        foreach (var hex in expected.Values)
            Assert.Single(evidence["observations"]!.AsArray(), row => row!["payload_hex"]?.GetValue<string>() == hex);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            exported["sha256"]!.GetValue<string>());
        var diagnostics = (await client.SendAsync("ExportDiagnosticsReport", new JsonObject
            { ["target_path"] = Path.Combine(directory, "diagnostics.json") })).Require();
        var diagnosticText = await File.ReadAllTextAsync(diagnostics["target_path"]!.GetValue<string>());
        Assert.DoesNotContain("payload_hex", diagnosticText, StringComparison.Ordinal);
        foreach (var raw in expected.Values)
        {
            Assert.DoesNotContain(raw, diagnosticText, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.Log, text => text.Contains(raw, StringComparison.Ordinal));
        }
        Assert.Equal(RunState.Idle, pipeline.RunState);
        foreach (var table in new[] { "mentor_runs", "run_events", "run_revisions" })
        {
            using var command = fixture.Host.Database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    [Fact]
    public async Task RawPayloadOnlyReachesExplicitEvidenceAndRetainsItsHistoricalHeaderAfterDisable()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var settings = (await client.SendAsync("GetCaptureSettings")).Require();
        Assert.Empty(settings["research_payload_opcodes"]!.AsArray());
        settings = (await client.SendAsync("UpdateCaptureSettings", new JsonObject
        {
            ["candidate_validation_enabled"] = true,
            ["research_payload_opcodes"] = new JsonArray("0x03BB"),
        })).Require();
        ContractSchema.Validate("$defs/CaptureSettings", settings, "research settings");
        Assert.Equal("0x03bb", settings["research_payload_opcodes"]![0]!.GetValue<string>());
        var pipeline = fixture.Host.LiveProtocol!;
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "2026.08.05.0000.0000" });
        pipeline.OnCaptureStarted(fixture.Host.CaptureSessionId);
        var bytes = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        var raw = Convert.ToHexString(bytes).ToLowerInvariant();
        var message = new DecodedMessage(fixture.Host.CaptureSessionId, MessageDirection.Outbound,
            fixture.Host.Clock.UtcNow, TimeSpan.FromMilliseconds(123), 0, 3, 0x03bb, bytes, "private connection");
        pipeline.Accept(message);
        var stored = Assert.Single(fixture.Host.Candidates.ReadEvidence().Observations);
        Assert.Equal(raw, stored.Observation.PayloadHex);
        var query = (await client.SendAsync("QueryCandidateObservations")).Require();
        Assert.False(query["items"]![0]!.AsObject().ContainsKey("payload_hex"));
        Assert.DoesNotContain(raw, query.ToJsonString(), StringComparison.Ordinal);

        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["research_payload_opcodes"] = new JsonArray() })).Require();
        pipeline.Accept(message with { Mono = TimeSpan.FromMilliseconds(124) });
        Assert.Equal(2, fixture.Host.Candidates.Count());
        Assert.Single(fixture.Host.Candidates.ReadEvidence().Observations, item => item.Observation.PayloadHex is not null);
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["candidate_validation_enabled"] = false })).Require();
        pipeline.Accept(message with { Mono = TimeSpan.FromMilliseconds(125) });
        Assert.Equal(2, fixture.Host.Candidates.Count());

        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var path = Path.Combine(directory, "research.json");
        (await client.SendAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).Require();
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(evidence["contains_raw_payload"]!.GetValue<bool>());
        Assert.Equal("0x03bb", Assert.Single(evidence["research_payload_opcodes"]!.AsArray())!.GetValue<string>());
        Assert.Single(evidence["observations"]!.AsArray(), item => item!["payload_hex"]?.GetValue<string>() == raw);

        var diagnostics = (await client.SendAsync("ExportDiagnosticsReport", new JsonObject { ["target_path"] = Path.Combine(directory, "diagnostics.json") })).Require();
        var diagnosticText = await File.ReadAllTextAsync(diagnostics["target_path"]!.GetValue<string>());
        Assert.DoesNotContain(raw, diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain("payload_hex", diagnosticText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Log, text => text.Contains(raw, StringComparison.Ordinal));
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[3]")]
    [InlineData("[\"0x01b8\"]")]
    [InlineData("[\"0xffff\"]")]
    [InlineData("[\"0x03bb\",\"0x03BB\"]")]
    [InlineData("\"0x03bb\"")]
    public async Task InvalidWhitelistDoesNotPersistAnySetting(string list)
    {
        await using var fixture = ServerFixture.Start();
        var response = await fixture.CallAsync("UpdateCaptureSettings", JsonNode.Parse(
            "{\"candidate_validation_enabled\":true,\"research_payload_opcodes\":" + list + "}")!.AsObject());
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.False(CaptureSettingsStore.Read(fixture.Host.Settings).CandidateValidationEnabled);
        Assert.Empty(CaptureSettingsStore.Read(fixture.Host.Settings).ResearchPayloadOpcodes!);
    }

    [Fact]
    public async Task NonEmptyWhitelistRequiresExplicitCandidateMode()
    {
        await using var fixture = ServerFixture.Start();
        Assert.Equal(ErrorCodes.BadRequest, (await fixture.CallAsync("UpdateCaptureSettings", new JsonObject
            { ["research_payload_opcodes"] = new JsonArray("0x03bb") })).ErrorCode);
    }
}
