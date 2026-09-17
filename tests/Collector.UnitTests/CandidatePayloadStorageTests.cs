using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Export;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

public sealed class CandidatePayloadStorageTests : IDisposable
{
    private readonly TestDatabase _fixture = new();
    private readonly CandidateObservationRepository _ledger;
    private readonly SettingsRepository _settings;
    private readonly string _session = Guid.NewGuid().ToString("D");
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(),
        "MentorRecorder.CandidatePayloadTests", Guid.NewGuid().ToString("N"));

    public CandidatePayloadStorageTests()
    {
        _ledger = new CandidateObservationRepository(_fixture.Database, _fixture.Clock);
        _settings = new SettingsRepository(_fixture.Database, _fixture.Clock);
    }

    [Fact]
    public void DisabledCandidateValidationCannotStorePayloadEvenWithAWhitelist()
    {
        Whitelist("0x0323");
        Assert.False(_ledger.Add(Observation(40, new string('a', 80))));
        Assert.Equal(0, _ledger.Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("[\"0x0104\"]")]
    [InlineData("{}")]
    [InlineData("\"0x0323\"")]
    [InlineData("[803]")]
    public void MissingOrNonMatchingWhitelistKeepsOnlyMetadata(string? whitelist)
    {
        Enable();
        if (whitelist is not null)
            _settings.SetSetting(CaptureSettingsStore.ResearchPayloadOpcodesSetting, whitelist);
        var observation = Observation(40, new string('a', 80));

        Assert.True(_ledger.Add(observation));
        Assert.Equal(observation with { PayloadHex = null }, _ledger.Get(observation.ObservationId)!.Observation);
    }

    [Fact]
    public void AddRechecksWhitelistAfterObservationWasProduced()
    {
        Enable();
        Whitelist("0x0323");
        var retained = Observation(40, new string('a', 80));
        Assert.True(_ledger.Add(retained));
        var pending = Observation(40, new string('b', 80));

        Whitelist();
        Assert.True(_ledger.Add(pending));

        Assert.Null(_ledger.Get(pending.ObservationId)!.Observation.PayloadHex);
        Assert.Equal(retained.PayloadHex, _ledger.Get(retained.ObservationId)!.Observation.PayloadHex);
        _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "false");
        Assert.False(_ledger.Add(Observation(40, new string('c', 80))));
        Assert.Equal(2, _ledger.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(456)]
    [InlineData(512)]
    public void StorageAcceptsCanonicalPayloadThroughThe512ByteBoundary(int length)
    {
        Enable();
        Whitelist("0x0323");
        var observation = Observation(length, new string('a', length * 2));

        Assert.True(_ledger.Add(observation));
        Assert.Equal(observation, _ledger.Get(observation.ObservationId)!.Observation);
    }

    [Theory]
    [InlineData(2, "aa")]
    [InlineData(2, "00AA")]
    [InlineData(2, "00gg")]
    [InlineData(1, "a")]
    public void InvalidPayloadIsDroppedWithoutLosingMetadata(int length, string hex)
    {
        Enable();
        Whitelist("0x0323");
        var observation = Observation(length, hex);

        Assert.True(_ledger.Add(observation));
        Assert.Null(_ledger.Get(observation.ObservationId)!.Observation.PayloadHex);
    }

    [Fact]
    public void PayloadBeyond512BytesIsDroppedAndSqliteAlsoEnforcesTheBoundary()
    {
        Enable();
        Whitelist("0x0323");
        var observation = Observation(513, new string('a', 1026));
        Assert.True(_ledger.Add(observation));
        Assert.Null(_ledger.Get(observation.ObservationId)!.Observation.PayloadHex);

        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "UPDATE candidate_observations SET payload_hex = $hex WHERE observation_id = $id;";
        command.Parameters.AddWithValue("$id", observation.ObservationId);
        command.Parameters.AddWithValue("$hex", observation.PayloadHex!);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("00AA")]
    [InlineData("00gg")]
    [InlineData("000000")]
    [InlineData("0000\0hidden")]
    public void SqliteRejectsMalformedPayloadEvenIfRepositoryIsBypassed(string hex)
    {
        Enable();
        var observation = Observation(2, null);
        Assert.True(_ledger.Add(observation));

        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "UPDATE candidate_observations SET payload_hex = $hex WHERE observation_id = $id;";
        command.Parameters.AddWithValue("$id", observation.ObservationId);
        command.Parameters.AddWithValue("$hex", hex);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void ZoneAnchorsNeverStorePayloadAndSqliteRejectsDirectAssignment()
    {
        Enable();
        Whitelist("0x0323");
        var now = _fixture.Clock.UtcNow;
        var anchor = new CandidateObservation(Guid.NewGuid().ToString("D"), _session, "candidate-test",
            "ZONE_LOAD", "zone_load", "NONE", null, null, null, "abcdef012345", now, 3000,
            now.AddSeconds(-2), now, 1000, 3000, "aa");
        Assert.True(_ledger.Add(anchor));
        Assert.Null(_ledger.Get(anchor.ObservationId)!.Observation.PayloadHex);

        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "UPDATE candidate_observations SET payload_hex = 'aa' WHERE observation_id = $id;";
        command.Parameters.AddWithValue("$id", anchor.ObservationId);
        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void ExportDeclaresAndKeepsOldPayloadAfterResearchIsDisabled()
    {
        Enable();
        Whitelist("0x0323");
        var hex = string.Concat(Enumerable.Repeat("deadbeef", 10));
        var observation = Observation(40, hex);
        Assert.True(_ledger.Add(observation));
        _ledger.Review(Guid.NewGuid().ToString("D"), observation.ObservationId, "UNSURE", "待离线核对");
        Whitelist();
        _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "false");

        var result = Export("retained.json");
        var bytes = File.ReadAllBytes(result.TargetPath);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.True(root.GetProperty("contains_raw_payload").GetBoolean());
        Assert.Equal(new[] { "0x0323" }, root.GetProperty("research_payload_opcodes")
            .EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(hex, Assert.Single(root.GetProperty("observations").EnumerateArray()).GetProperty("payload_hex").GetString());
        Assert.Equal(1, result.ObservationCount);
        Assert.Equal(1, result.ReviewCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(result.Sha256 + "  retained.json\n", File.ReadAllText(result.Sha256Path));

        var ordinaryProjection = CandidateWire.Observation(_ledger.Get(observation.ObservationId)!);
        Assert.False(ordinaryProjection.ContainsKey("payload_hex"));
        Assert.DoesNotContain(hex, ordinaryProjection.ToJsonString());
        using var command = _fixture.Database.CreateCommand();
        command.CommandText = "SELECT response_json FROM ipc_idempotency;";
        Assert.DoesNotContain(hex, Assert.IsType<string>(command.ExecuteScalar()));
        command.CommandText = "SELECT (SELECT COUNT(*) FROM mentor_runs) + (SELECT COUNT(*) FROM run_events) + " +
            "(SELECT COUNT(*) FROM run_revisions) + (SELECT COUNT(*) FROM parser_errors);";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void ResearchSnapshotListsOnlyDistinctStoredPayloadOpcodesInOrder()
    {
        Enable();
        Whitelist("0x0343", "0x0323", "0x0104");
        Assert.True(_ledger.Add(Observation(2, "aabb", 0x0343)));
        Assert.True(_ledger.Add(Observation(2, "ccdd", 0x0323)));
        Assert.True(_ledger.Add(Observation(2, "eeff", 0x0323)));
        Assert.True(_ledger.Add(Observation(2, null, 0x0104)));
        Whitelist();

        _ledger.VisitResearchEvidence((opcodes, observationCount, reviewCount, observations, reviews) =>
        {
            Assert.Equal(new[] { "0x0323", "0x0343" }, opcodes);
            Assert.Equal(4, observationCount);
            Assert.Equal(0, reviewCount);
            Assert.Equal(3, observations.Count(item => item.Observation.PayloadHex is not null));
            Assert.Empty(reviews);
        });
    }

    [Fact]
    public void ExportHeaderUsesRetainedDataRatherThanCurrentWhitelist()
    {
        Enable();
        Whitelist("0x0323");
        Assert.True(_ledger.Add(Observation(40, null)));
        using (var metadata = JsonDocument.Parse(File.ReadAllBytes(Export("metadata.json").TargetPath)))
        {
            Assert.False(metadata.RootElement.GetProperty("contains_raw_payload").GetBoolean());
            Assert.Empty(metadata.RootElement.GetProperty("research_payload_opcodes").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, Assert.Single(metadata.RootElement.GetProperty("observations")
                .EnumerateArray()).GetProperty("payload_hex").ValueKind);
        }
        Assert.True(_ledger.Add(Observation(40, new string('a', 80))));
        _fixture.Clock.UtcNow = _fixture.Clock.UtcNow.AddDays(31);

        using var expired = JsonDocument.Parse(File.ReadAllBytes(Export("expired.json").TargetPath));
        Assert.False(expired.RootElement.GetProperty("contains_raw_payload").GetBoolean());
        Assert.Empty(expired.RootElement.GetProperty("research_payload_opcodes").EnumerateArray());
        Assert.Empty(expired.RootElement.GetProperty("observations").EnumerateArray());
    }

    private CandidateObservation Observation(int length, string? payload, int opcode = 0x0323) => new(
        Guid.NewGuid().ToString("D"), _session, "candidate-test", "FINDER_STATE_NOTIFICATION", null,
        "S2C", opcode, length, "0123456789ab", "abcdef012345", _fixture.Clock.UtcNow, 1000,
        PayloadHex: payload);

    private void Enable() => _settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "true");
    private void Whitelist(params string[] opcodes) => _settings.SetSetting(
        CaptureSettingsStore.ResearchPayloadOpcodesSetting, JsonSerializer.Serialize(opcodes));
    private CandidateExportResult Export(string name) => new CandidateEvidenceExporter(_ledger, _fixture.Clock)
        .Export(Path.Combine(_outputDirectory, name));

    public void Dispose()
    {
        _fixture.Dispose();
        if (Directory.Exists(_outputDirectory)) Directory.Delete(_outputDirectory, recursive: true);
    }
}
