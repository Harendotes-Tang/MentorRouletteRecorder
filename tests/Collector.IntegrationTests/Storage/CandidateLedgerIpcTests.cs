using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.StateMachine;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Storage;

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
    public async Task ARowTheLedgerRefusesIsDroppedAndTheRowsAfterItAreStillStored()
    {
        await using var fixture = ServerFixture.Start();
        var host = fixture.Host;
        host.Settings.SetSetting(CaptureSettingsStore.CandidateValidationSetting, "true");
        CandidateObservation Row(int occurrences) => new(Guid.NewGuid().ToString("D"), host.CaptureSessionId,
            "cn.2026.08.05.candidate", "FINDER_STATUS", "queue", "S2C", 0x0323, 40,
            "0123456789ab", "abcdef012345", host.Clock.UtcNow, 1000, Occurrences: occurrences);

        // The table refuses a zero count. On the capture's message path that exception used to
        // stop the capture; now the row is counted and the next one goes in.
        host.StoreCandidate(Row(0));
        host.StoreCandidate(Row(1));

        Assert.Equal(1, host.CandidateRowsRefused);
        Assert.Equal(1, host.Candidates.Count());
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

        var path = Path.Combine(fixture.DataDirectory, "candidate.json");
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
        fixture.AssertTablesEmpty("mentor_runs", "run_events", "run_revisions");
    }

    [Fact]
    public async Task PlayerExportContainsOnlyItsClosedRoundAcrossOverlappingClocks()
    {
        const string profile = "global.2026.11.03.candidate";
        // The round is offered only for a build that does not record yet. The shipped GLOBAL
        // 2026.11.03 records since 2026-11-22, so the formal selection here stands for a later
        // build that has a candidate and nothing else.
        await using var fixture = ServerFixture.Start(capture: CaptureFakes.RememberedInstall(
            "2026.11.03.0000.0000", npcapInstalled: false,
            executablePath: @"D:\Square Enix\Final Fantasy XIV Online\game\ffxiv_dx11.exe"),
            profileSelector: game => new ProfileSelection(ProfileCompatibilityStatus.Unsupported,
                ProfileBinding.FailClosed, null, game.Region, game.GameBuild, ProfileSelector.NoProfileMatchesReason,
                ReasonCode: ProfileSelectionReason.NoMatch));
        var host = fixture.Host;
        var offered = (await fixture.CallAsync("GetCaptureStatus")).Require();
        Assert.Equal("GLOBAL", offered["region"]!.GetValue<string>());
        var timestamp = host.Clock.UtcNow.AddMinutes(-1);
        var settings = (await fixture.CallAsync("GetCaptureSettings", new JsonObject())).Require();
        Assert.True(settings.ContainsKey("candidate_round_id"));
        Assert.Null(settings["candidate_round_id"]);

        string NewId() => Guid.NewGuid().ToString("D");
        CandidateObservation Observation(int code, string payload, string id = profile) =>
            new(NewId(), NewId(), id, "SYNTHETIC", "queue", code == 0x016f ? "S2C" : "C2S",
                code, payload.Length / 2, "0123456789ab", "abcdef012345", timestamp, 1000, PayloadHex: payload);
        Task<IpcResponse> WriteSettings(JsonObject body) => fixture.CallAsync("UpdateCaptureSettings", body);
        JsonObject Start(string id, string candidate = profile) => new()
        {
            ["candidate_validation_enabled"] = true, ["candidate_round_id"] = id, ["candidate_round_profile_id"] = candidate,
            ["research_payload_opcodes"] = new JsonArray("0x0215", "0x016f"),
        };
        JsonObject Stop(string id) => new()
        {
            ["candidate_validation_enabled"] = false, ["candidate_round_id"] = id,
            ["research_payload_opcodes"] = new JsonArray(),
        };
        string FilePath(string name) => Path.Combine(fixture.DataDirectory, name + ".json");
        JsonObject ScopedExport(string id, string path) => new()
        {
            ["target_path"] = path, ["candidate_round_id"] = id, ["round_answers"] = new JsonObject { ["first_roulette"] = "实际任务" },
        };
        void Review(CandidateObservation item) => host.Candidates.Review(NewId(), item.ObservationId, "CORRECT");

        // This is a legitimate older maintenance row with a payload that must not be declared in a player export.
        (await WriteSettings(new JsonObject
        {
            ["candidate_validation_enabled"] = true, ["research_payload_opcodes"] = new JsonArray("0x022b"),
        })).Require();
        var legacy = Observation(0x022b, new string('0', 48));
        Assert.True(host.Candidates.Add(legacy));
        Review(legacy);
        (await WriteSettings(new JsonObject { ["candidate_validation_enabled"] = false })).Require();

        var first = NewId();
        var second = NewId();
        Assert.Equal(ErrorCodes.BadRequest, (await WriteSettings(Start(first, "cn.2026.08.05.candidate"))).ErrorCode);
        ContractSchema.Validate("$defs/UpdateCaptureSettingsRequest", Start(first), "start player request");
        ContractSchema.Validate("$defs/UpdateCaptureSettingsRequest", Stop(first), "stop player request");
        var firstStarted = (await WriteSettings(Start(first))).Require();
        ContractSchema.Validate("$defs/Responses/UpdateCaptureSettings", firstStarted, "start player round");
        Assert.Equal(first, firstStarted["candidate_round_id"]!.GetValue<string>());
        Assert.Equal(first, (await WriteSettings(Start(first))).Require()["candidate_round_id"]!.GetValue<string>());
        var old = Observation(0x0215, "02" + new string('0', 46));
        Assert.True(host.Candidates.Add(old));
        var firstStatus = (await fixture.CallAsync("GetCaptureStatus")).Require();
        var firstGuesses = firstStatus["candidate_round_guesses"]!.AsObject();
        Assert.Equal(first, firstGuesses["candidate_round_id"]!.GetValue<string>());
        Assert.Equal(2, firstGuesses["first_roulette"]!["id"]!.GetValue<int>());
        Review(old);
        (await WriteSettings(Stop(first))).Require();

        (await WriteSettings(Start(second))).Require();
        Assert.Equal(ErrorCodes.BadRequest, (await WriteSettings(Stop(first))).ErrorCode);
        var wrong = Observation(0x0215, "03" + new string('0', 46), "cn.2026.08.05.candidate");
        Assert.False(host.Candidates.Add(wrong));
        // The second round includes distinct sessions with the same observed time as the old rows.
        var queue = Observation(0x0215, "09" + new string('0', 46));
        var job = Observation(0x016f, "0018" + new string('0', 76));
        Assert.True(host.Candidates.Add(queue));
        Assert.True(host.Candidates.Add(job));
        var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
        Assert.Equal(2, status["candidate_observation_count"]!.GetValue<int>());
        ContractSchema.Validate("$defs/CaptureStatus", status, "player-round status");
        var secondGuesses = status["candidate_round_guesses"]!.AsObject();
        Assert.Equal(second, secondGuesses["candidate_round_id"]!.GetValue<string>());
        Assert.Equal(9, secondGuesses["first_roulette"]!["id"]!.GetValue<int>());
        Review(queue);
        var unfinished = FilePath("unfinished");
        Assert.Equal(ErrorCodes.BadRequest, (await fixture.CallAsync("ExportCandidateEvidence", ScopedExport(second, unfinished))).ErrorCode);
        Assert.False(File.Exists(unfinished));

        // Each CallAsync uses a fresh connection. Ignore the first acknowledgement and retry with the original identity.
        await WriteSettings(Stop(second));
        var stopped = (await WriteSettings(Stop(second))).Require();
        Assert.False(stopped["candidate_validation_enabled"]!.GetValue<bool>());
        Assert.Equal(second, stopped["candidate_round_id"]!.GetValue<string>());
        Assert.Equal(profile, stopped["candidate_round_profile_id"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.BadRequest, (await WriteSettings(Start(first))).ErrorCode);

        var invalid = FilePath("invalid");
        Assert.Equal(ErrorCodes.BadRequest, (await fixture.CallAsync("ExportCandidateEvidence", ScopedExport(first, invalid))).ErrorCode);
        Assert.False(File.Exists(invalid));
        var output = FilePath("player");
        var exportRequest = ScopedExport(second, output);
        ContractSchema.Validate("$defs/ExportCandidateEvidenceRequest", exportRequest, "player round request");
        var reply = (await fixture.CallAsync("ExportCandidateEvidence", exportRequest)).Require();
        var bytes = await File.ReadAllBytesAsync(output);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal(hash, reply["sha256"]!.GetValue<string>());
        Assert.Equal(hash + "  player.json\n", await File.ReadAllTextAsync(output + ".sha256"));
        Assert.Equal(2, reply["observation_count"]!.GetValue<int>());
        Assert.Equal(1, reply["review_count"]!.GetValue<int>());

        var document = JsonNode.Parse(bytes)!;
        Assert.True(document["contains_raw_payload"]!.GetValue<bool>());
        Assert.Equal(new[] { "0x016f", "0x0215" }, document["research_payload_opcodes"]!.AsArray().Select(node => node!.GetValue<string>()));
        Assert.Equal(new[] { job.ObservationId, queue.ObservationId }.Order(),
            document["observations"]!.AsArray().Select(node => node!["observation_id"]!.GetValue<string>()).Order());
        Assert.Equal(queue.ObservationId, Assert.Single(document["reviews"]!.AsArray())!["observation_id"]!.GetValue<string>());
        Assert.Equal(9, document["round_report"]!["guessed"]!["first_roulette"]!["id"]!.GetValue<int>());
        Assert.False(document["round_report"]!["guessed"]!.AsObject().ContainsKey("candidate_round_id"));
        Assert.Equal("实际任务", document["round_report"]!["answered"]!["first_roulette"]!.GetValue<string>());

        var all = (await fixture.CallAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = FilePath("maintainer") })).Require();
        Assert.Equal(4, all["observation_count"]!.GetValue<int>());
        Assert.Equal(3, all["review_count"]!.GetValue<int>());
    }

    /// <summary>
    /// With the shipped profiles, GLOBAL 2026.11.03 records (2026-11-22), so its candidate no longer
    /// opens a player round: the request is refused and nothing switches to candidate validation.
    /// </summary>
    [Fact]
    public async Task AGlobalBuildThatRecordsOffersNoPlayerRound()
    {
        await using var fixture = ServerFixture.Start(capture: CaptureFakes.RememberedInstall(
            "2026.11.03.0000.0000", npcapInstalled: false,
            executablePath: @"D:\Square Enix\Final Fantasy XIV Online\game\ffxiv_dx11.exe"));
        var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
        Assert.Equal("GLOBAL", status["region"]!.GetValue<string>());
        Assert.Equal("VERIFIED", status["profile_status"]!.GetValue<string>());

        var refused = await fixture.CallAsync("UpdateCaptureSettings", new JsonObject
        {
            ["candidate_validation_enabled"] = true, ["candidate_round_id"] = Guid.NewGuid().ToString("D"),
            ["candidate_round_profile_id"] = "global.2026.11.03.candidate",
            ["research_payload_opcodes"] = new JsonArray("0x0215", "0x016f"),
        });

        Assert.Equal(ErrorCodes.BadRequest, refused.ErrorCode);
        Assert.False(CaptureSettingsStore.Read(fixture.Host.Settings).CandidateValidationEnabled);
    }

    [Fact]
    public async Task ExportPreservesLargeAppendOnlyReviewHistory()
    {
        await using var fixture = ServerFixture.Start();
        var row = Seed(fixture.Host);
        var note = new string('核', 2000);
        for (var i = 0; i < 500; i++)
            fixture.Host.Candidates.Review(Guid.NewGuid().ToString("D"), row.ObservationId, "UNSURE", note);
        var path = Path.Combine(fixture.DataDirectory, "history.json");
        var exported = (await fixture.CallAsync("ExportCandidateEvidence", new JsonObject { ["target_path"] = path })).Require();
        Assert.Equal(500, exported["review_count"]!.GetValue<int>());
        Assert.True(new FileInfo(path).Length > 1_000_000);
        using var stream = File.OpenRead(path);
        using var document = await System.Text.Json.JsonDocument.ParseAsync(stream);
        Assert.Equal(500, document.RootElement.GetProperty("reviews").GetArrayLength());
        Assert.All(document.RootElement.GetProperty("reviews").EnumerateArray(),
            review => Assert.Equal(note, review.GetProperty("note").GetString()));
    }

    /// <summary>
    /// A request that names no file exports into <c>exports\</c> beside the open database, as the
    /// backup and the diagnostics report already do. The folder used to come from the managed data
    /// root alone, so a Collector on a throw-away database -- every fixture here, run by a bare
    /// <c>dotnet test</c> -- wrote an empty evidence file into the real
    /// %LOCALAPPDATA%\MentorRecorder\exports on every run.
    /// </summary>
    [Fact]
    public async Task AnExportWithoutATargetStaysBesideTheDatabase()
    {
        await using var fixture = ServerFixture.Start();
        var expected = Path.Combine(fixture.DataDirectory, "exports");

        var exported = (await fixture.CallAsync("ExportCandidateEvidence", new JsonObject())).Require();

        var target = exported["target_path"]!.GetValue<string>();
        var sidecar = exported["sha256_path"]!.GetValue<string>();
        Assert.Equal(expected, Path.GetDirectoryName(target));
        Assert.Equal(expected, Path.GetDirectoryName(sidecar));
        Assert.True(File.Exists(target));
        Assert.True(File.Exists(sidecar));
        Assert.False(
            target.StartsWith(DatabasePaths.ResolveRoot(null) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            target + " 落在了真实用户的数据目录里。");
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
