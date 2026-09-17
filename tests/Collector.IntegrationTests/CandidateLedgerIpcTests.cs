using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;

namespace MentorRecorder.Collector.IntegrationTests;

public sealed class CandidateLedgerIpcTests
{
    internal static CandidateObservation Seed(CollectorHost host)
    {
        var enabled = CaptureSettingsStore.Read(host.Settings).CandidateValidationEnabled;
        host.Settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "true");
        var row = new CandidateObservation(Guid.NewGuid().ToString("D"), host.CaptureSessionId,
            "cn.2026.08.05.candidate", "FINDER_STATUS", "queue", "S2C", 0x0323, 40,
            "0123456789ab", "abcdef012345", host.Clock.UtcNow, 1000);
        Assert.True(host.Candidates.Add(row));
        host.Settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, enabled ? "true" : "false");
        return row;
    }

    [Fact]
    public async Task QueryReviewAndExportUseIndependentLedgerAndPreserveIdempotency()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var row = Seed(fixture.Host);
        var query = (await client.SendAsync("QueryCandidateObservations", new JsonObject
            { ["session_id"] = row.CaptureSessionId, ["page"] = 1, ["page_size"] = 200 })).Require();
        ContractSchema.Validate("$defs/Responses/QueryCandidateObservations", query, "candidate query");
        Assert.Single(query["items"]!.AsArray());
        Assert.Null(query["items"]![0]!["review_verdict"]);

        var request = Guid.NewGuid().ToString("D");
        var body = new JsonObject { ["observation_id"] = row.ObservationId, ["verdict"] = "CORRECT", ["note"] = "实际弹窗一致" };
        var reviewed = (await client.SendAsync("ReviewCandidateObservation", body, request)).Require();
        ContractSchema.Validate("$defs/Responses/ReviewCandidateObservation", reviewed, "candidate review");
        var replayed = (await client.SendAsync("ReviewCandidateObservation", body.DeepClone().AsObject(), request)).Require();
        Assert.True(JsonNode.DeepEquals(reviewed, replayed));
        body["verdict"] = "WRONG";
        Assert.Equal(ErrorCodes.IdempotencyConflict,
            (await client.SendAsync("ReviewCandidateObservation", body.DeepClone().AsObject(), request)).ErrorCode);
        Assert.Single(fixture.Host.Candidates.ReadEvidence().Reviews);

        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "candidate.json");
        var exported = (await client.SendAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).Require();
        ContractSchema.Validate("$defs/Responses/ExportCandidateEvidence", exported, "candidate export");
        var bytes = await File.ReadAllBytesAsync(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(hash, exported["sha256"]!.GetValue<string>());
        Assert.StartsWith(hash + "  ", await File.ReadAllTextAsync(path + ".sha256"), StringComparison.Ordinal);
        var document = JsonNode.Parse(bytes)!;
        Assert.False(document["contains_raw_payload"]!.GetValue<bool>());
        Assert.Equal("CANDIDATE", document["profile_status"]!.GetValue<string>());
        Assert.Single(document["observations"]!.AsArray());
        Assert.Single(document["reviews"]!.AsArray());
        Assert.Equal("CORRECT", document["observations"]![0]!["review_verdict"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.ExportFailed,
            (await client.SendAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).ErrorCode);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
        foreach (var table in new[] { "mentor_runs", "run_events", "run_revisions" })
        {
            using var command = fixture.Host.Database.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + table;
            Assert.Equal(0L, command.ExecuteScalar());
        }
    }

    [Fact]
    public async Task ExportPreservesLargeAppendOnlyReviewHistory()
    {
        await using var fixture = ServerFixture.Start();
        var row = Seed(fixture.Host);
        var note = new string('核', 2000);
        for (var i = 0; i < 500; i++)
            fixture.Host.Candidates.Review(Guid.NewGuid().ToString("D"), row.ObservationId, "UNSURE", note);
        var path = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "history.json");
        var exported = (await fixture.CallAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).Require();
        Assert.Equal(500, exported["review_count"]!.GetValue<int>());
        Assert.True(new FileInfo(path).Length > 1_000_000);
        using var stream = File.OpenRead(path);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
        Assert.Equal(500, document.RootElement.GetProperty("reviews").GetArrayLength());
        Assert.All(document.RootElement.GetProperty("reviews").EnumerateArray(),
            review => Assert.Equal(note, review.GetProperty("note").GetString()));
    }

    [Fact]
    public async Task LivePipelineWritesAndNotifiesOnlyCandidateObservation()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        using var subscription = fixture.Host.LiveEvents.Subscribe(Guid.NewGuid().ToString("D"));
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["candidate_validation_enabled"] = true })).Require();
        var pipeline = fixture.Host.LiveProtocol!;
        pipeline.Refresh(GameProcessDetection.NotRunning with { Region = Region.Cn, GameBuild = "2026.08.05.0000.0000" });
        pipeline.OnCaptureStarted(fixture.Host.CaptureSessionId);
        pipeline.Accept(new DecodedMessage(fixture.Host.CaptureSessionId, MessageDirection.Outbound,
            fixture.Host.Clock.UtcNow, TimeSpan.FromMilliseconds(234100), 0, 3, 0x03bb, new byte[128], "private endpoint"));
        Assert.Equal(1, fixture.Host.Candidates.Count());
        var observed = await subscription.ReadAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(observed);
        Assert.Equal("candidate_observed", observed["kind"]!.GetValue<string>());
        ContractSchema.Validate("$defs/LiveEvent", observed, "candidate live event");
        Assert.Null(observed["run"]);
        Assert.DoesNotContain("private endpoint", observed.ToJsonString(), StringComparison.Ordinal);
        var status = (await client.SendAsync("GetCaptureStatus")).Require();
        ContractSchema.Validate("$defs/CaptureStatus", status, "candidate status");
        Assert.True(status["candidate_validation_enabled"]!.GetValue<bool>());
        Assert.Equal("cn.2026.08.05.candidate", status["candidate_profile_id"]!.GetValue<string>());
        Assert.Equal(1, status["candidate_observation_count"]!.GetValue<int>());
        // The formal VERIFIED profile stays bound while the candidate ledger observes; the
        // registration opcode is unknown to it, so it counts a parser error and no run.
        Assert.Equal("VERIFIED", status["profile_status"]!.GetValue<string>());
        Assert.Equal(RunState.Idle, pipeline.RunState);
        Assert.Empty((await client.SendAsync("QueryRuns")).Require()["items"]!.AsArray());
        (await client.SendAsync("UpdateCaptureSettings", new JsonObject { ["candidate_validation_enabled"] = false })).Require();
        pipeline.Accept(new DecodedMessage(fixture.Host.CaptureSessionId, MessageDirection.Outbound,
            fixture.Host.Clock.UtcNow, TimeSpan.FromMilliseconds(234101), 0, 3, 0x03bb, new byte[128], "private endpoint"));
        Assert.Equal(1, fixture.Host.Candidates.Count());
    }

    [Theory]
    [InlineData("QueryCandidateObservations", "{\"page_size\":201}")]
    [InlineData("QueryCandidateObservations", "{\"page\":null}")]
    [InlineData("QueryCandidateObservations", "{\"session_id\":\"bad\"}")]
    [InlineData("ReviewCandidateObservation", "{\"observation_id\":\"22222222-2222-4222-8222-222222222222\",\"verdict\":\"VERIFIED\"}")]
    [InlineData("ExportCandidateEvidence", "{\"upload\":true}")]
    public async Task MalformedRequestsAreRefused(string type, string json)
    {
        await using var fixture = ServerFixture.Start();
        var response = await fixture.CallAsync(type, JsonNode.Parse(json)!.AsObject());
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    [Fact]
    public async Task MissingObservationAndUnsafeExportPathAreRefused()
    {
        await using var fixture = ServerFixture.Start();
        Assert.Equal(ErrorCodes.CandidateObservationNotFound, (await fixture.CallAsync("ReviewCandidateObservation",
            new JsonObject { ["observation_id"] = Guid.NewGuid().ToString("D"), ["verdict"] = "UNSURE" })).ErrorCode);
        Assert.Equal(ErrorCodes.ExportFailed, (await fixture.CallAsync("ExportCandidateEvidence",
            new JsonObject { ["target_path"] = @"\\mentor-test.invalid\reports\candidate-evidence.json" })).ErrorCode);
    }
}
