using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Ipc;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The pipe end to end: a real server, a real client, real frames.
/// </summary>
public sealed class PipeServerTests
{
    private static string NewId() => Guid.NewGuid().ToString("D");

    /// <summary>The <paramref name="index"/>th of a series of hand-entered runs an hour apart; every fourth one left.</summary>
    /// <param name="index">Position in the series.</param>
    internal static CreateManualRunCommand ManualRun(int index) => new()
    {
        Region = Region.Cn,
        RequestId = NewId(),
        Reason = "补录第 " + index + " 次导随",
        Result = index % 4 == 0 ? RunResult.LeftOrAbandoned : RunResult.Completed,
        ContentId = 900001 + (index % 3),
        JobId = 19,
        MatchedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).AddHours(index),
        EnteredAtUtc = new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero).AddHours(index),
        EndedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 31, 0, TimeSpan.Zero).AddHours(index),
    };

    [Fact]
    public async Task GetVersion_AnswersTheHandshake()
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync("GetVersion");

        Assert.True(response.Ok);
        Assert.Equal("GetVersion", response.MessageType);
        Assert.Equal(Program.Version, response.Payload["collector_version"]!.GetValue<string>());
        Assert.Equal(1, response.Payload["protocol_version"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipeClient_DuplicateIdKeepsFirstWaiter(bool cancelFirst)
    {
        var pipeName = $"MentorRecorderTest.{NewId()}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new PipeClient(pipeName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var connecting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), deadline.Token);
        await connecting;
        var channel = new FrameChannel(server);

        async Task ExpectVersionRequestAsync(string id)
        {
            var body = await channel.ReadFrameAsync(deadline.Token);
            Assert.NotNull(body);
            var request = JsonNode.Parse(body)!.AsObject();
            Assert.Equal(id, request["request_id"]!.GetValue<string>());
            Assert.Equal("GetVersion", request["message_type"]!.GetValue<string>());
        }

        var requestId = NewId();
        var first = client.SendAsync("GetVersion", requestId: requestId, cancellationToken: cancel.Token);
        await ExpectVersionRequestAsync(requestId);
        var duplicate = await Assert.ThrowsAsync<CollectorException>(() =>
            client.SendAsync("GetStatus", requestId: requestId, cancellationToken: deadline.Token));
        Assert.Equal(ErrorCodes.BadRequest, duplicate.Code);
        Assert.False(first.IsCompleted);

        if (cancelFirst)
        {
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        else
        {
            await channel.WriteAsync(IpcEnvelope.Success(requestId, "GetVersion",
                new JsonObject { ["collector_version"] = "first" }), deadline.Token);
            Assert.Equal("first", (await first).Require()["collector_version"]!.GetValue<string>());
        }

        // Building the envelope throws when its payload already belongs to a JSON tree.
        var wrapper = new JsonObject { ["owned"] = new JsonObject() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync(
            "GetVersion", wrapper["owned"]!.AsObject(), requestId, cancellationToken: deadline.Token));

        var retry = client.SendAsync("GetVersion", requestId: requestId, cancellationToken: deadline.Token);
        await ExpectVersionRequestAsync(requestId);
        await channel.WriteAsync(IpcEnvelope.Success(requestId, "GetVersion",
            new JsonObject { ["collector_version"] = "retry" }), deadline.Token);
        Assert.Equal("retry", (await retry).Require()["collector_version"]!.GetValue<string>());

        var malformedReply = client.SendAsync("GetVersion", requestId: requestId, cancellationToken: deadline.Token);
        await ExpectVersionRequestAsync(requestId);
        var envelope = IpcEnvelope.Success(requestId, "GetVersion", new JsonObject());
        envelope["ok"] = "invalid";
        await channel.WriteAsync(envelope, deadline.Token);
        Assert.Equal(ErrorCodes.Internal, (await malformedReply).ErrorCode);
    }

    [Fact]
    public async Task GetStatus_ReportsTheDatabaseAndTheHonestCaptureState()
    {
        await using var fixture = ServerFixture.Start();
        Assert.NotNull(fixture.Host.LiveProtocol);

        var payload = (await fixture.CallAsync("GetStatus")).Require();

        Assert.True(payload["database_ready"]!.GetValue<bool>());
        Assert.Equal(MentorRecorder.Collector.Storage.MigrationRunner.LatestVersion, payload["schema_version"]!.GetValue<int>());

        var capture = payload["capture"]!.AsObject();
        Assert.Equal("STOPPED", capture["state"]!.GetValue<string>());
        Assert.False(capture["npcap_installed"]!.GetValue<bool>());
        Assert.False(capture["injected_hook_enabled"]!.GetValue<bool>());
        Assert.Equal("WinPCap", capture["monitor_type"]!.GetValue<string>());
        Assert.Equal("UNAVAILABLE", capture["last_error_code"]!.GetValue<string>());
    }

    /// <summary>
    /// A fresh server without Npcap, on the live protocol bridge and the shipping catalogue: here the profile
    /// status and the current run come from the live pipeline. CaptureIpcTests' fixture has no bridge, so its
    /// answers to those two messages are the capture layer's constants.
    /// </summary>
    [Fact]
    public async Task CaptureMessages_ReportMissingNpcapWithoutStartingCapture()
    {
        await using var fixture = ServerFixture.Start();
        Assert.NotNull(fixture.Host.LiveProtocol);

        var adapters = (await fixture.CallAsync("ListCaptureAdapters")).Require();
        Assert.False(adapters["npcap_installed"]!.GetValue<bool>());
        Assert.NotNull(adapters["install_hint"]);

        // Adapters are listed even without Npcap, because enumerating them needs no driver and
        // an empty list is less useful than the cards plus the install hint. Nothing is
        // recommended without the game's connections, and every address is masked to a /24.
        // See tests/Collector.IntegrationTests/Capture/CaptureIpcTests.cs.
        foreach (var adapter in adapters["adapters"]!.AsArray())
        {
            Assert.False(adapter!["recommended"]!.GetValue<bool>());
        }

        var profile = (await fixture.CallAsync("GetProtocolProfileStatus")).Require();
        Assert.Equal("NONE", profile["status"]!.GetValue<string>());

        var start = await fixture.CallAsync("StartCapture", new JsonObject());
        Assert.False(start.Ok);
        Assert.Equal(ErrorCodes.NpcapMissing, start.ErrorCode);

        var stop = await fixture.CallAsync("StopCapture");
        Assert.False(stop.Ok);
        Assert.Equal(ErrorCodes.CaptureNotRunning, stop.ErrorCode);

        var current = (await fixture.CallAsync("GetCurrentRun")).Require();
        Assert.Equal("IDLE", current["state"]!.GetValue<string>());
        Assert.Null(current["run"]);
    }

    [Fact]
    public async Task CorrectRun_ThenGetRunRevisions_ShowsTheWholeChain()
    {
        string runId = string.Empty;
        await using var fixture = ServerFixture.Start(host =>
            runId = host.Mutations.CreateManualRun(ManualRun(1)).RunId);

        await using var client = await fixture.ConnectAsync();

        var corrected = (await client.SendAsync("CorrectRun", new JsonObject
        {
            ["run_id"] = runId,
            ["expected_revision"] = 1,
            ["reason"] = "其实是掉线了",
            ["changes"] = new JsonObject { ["result"] = "DISCONNECTED" },
        })).Require();

        Assert.Equal(2, corrected["revision"]!.GetValue<int>());
        Assert.Equal("DISCONNECTED", corrected["run"]!["result"]!.GetValue<string>());
        Assert.True(corrected["run"]!["manually_corrected"]!.GetValue<bool>());

        var revisions = (await client.SendAsync("GetRunRevisions", new JsonObject
        {
            ["run_id"] = runId,
        })).Require();

        var items = revisions["items"]!.AsArray();
        Assert.Equal(2, items.Count);
        Assert.Equal("CREATE_MANUAL", items[0]!["change_kind"]!.GetValue<string>());
        Assert.Equal("CORRECT", items[1]!["change_kind"]!.GetValue<string>());
        Assert.Equal("其实是掉线了", items[1]!["reason"]!.GetValue<string>());

        // The original auto-entered value is still readable through revision 1.
        var original = items[0]!["changes"]!.AsArray()
            .Single(change => change!["field"]!.GetValue<string>() == "result");
        Assert.Equal("COMPLETED", original!["new_value"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorrectRun_SentTwiceWithTheSameRequestId_AppliesOnce()
    {
        string runId = string.Empty;
        await using var fixture = ServerFixture.Start(host =>
            runId = host.Mutations.CreateManualRun(ManualRun(1)).RunId);

        await using var client = await fixture.ConnectAsync();
        var requestId = NewId();
        var payload = new JsonObject
        {
            ["run_id"] = runId,
            ["expected_revision"] = 1,
            ["reason"] = "掉线了",
            ["changes"] = new JsonObject { ["result"] = "DISCONNECTED" },
        };

        var first = (await client.SendAsync("CorrectRun", payload.DeepClone().AsObject(), requestId)).Require();
        var second = (await client.SendAsync("CorrectRun", payload.DeepClone().AsObject(), requestId)).Require();

        Assert.False(first["idempotent_replay"]!.GetValue<bool>());
        Assert.True(second["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal(first["revision"]!.GetValue<int>(), second["revision"]!.GetValue<int>());
        Assert.Equal(
            first["audit_event_id"]!.GetValue<string>(),
            second["audit_event_id"]!.GetValue<string>());

        var revisions = (await client.SendAsync("GetRunRevisions", new JsonObject
        {
            ["run_id"] = runId,
        })).Require();
        Assert.Equal(2, revisions["page_info"]!["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedFrame_IsAnsweredWithBadRequestAndTheConnectionSurvives()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        await client.SendRawAsync(Encoding.UTF8.GetBytes("{ this is not json"));

        // Unparseable JSON carries no correlatable request id, so the observable contract is
        // that the connection survives and the next real request is still answered.
        var afterwards = await client.SendAsync("GetVersion");
        Assert.True(afterwards.Ok);
    }

    /// <summary>
    /// Audit 2026-10-03 OF-7. A repeated key used to escape the envelope parser as an unhandled
    /// exception (envelope) or come back as ERR_INTERNAL (payload). Both are a request the
    /// Collector cannot read, answered as ERR_BAD_REQUEST against the id, and the connection
    /// keeps serving.
    /// </summary>
    [Theory]
    [InlineData("{{\"protocol_version\":1,\"request_id\":\"{0}\",\"message_type\":\"GetVersion\",\"message_type\":\"GetStatus\",\"payload\":{{}}}}")]
    [InlineData("{{\"protocol_version\":1,\"request_id\":\"{0}\",\"message_type\":\"QueryRuns\",\"payload\":{{\"page\":1,\"page\":2}}}}")]
    public async Task ARepeatedKey_IsABadRequestAndTheConnectionSurvives(string template)
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();
        var requestId = Guid.NewGuid().ToString("D");
        var log = new FrameLog(client);
        bool Answer(JsonObject envelope) => envelope["request_id"]?.GetValue<string>() == requestId;

        await client.SendRawAsync(Encoding.UTF8.GetBytes(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, requestId)));

        await log.WaitForEnvelopesAsync(envelopes => envelopes.Any(Answer), "the repeated-key request was never answered");
        var response = log.Envelopes().First(Answer);
        Assert.False(response["ok"]!.GetValue<bool>());
        Assert.Equal(ErrorCodes.BadRequest, response["error"]!["code"]!.GetValue<string>());
        Assert.True((await client.SendAsync("GetVersion")).Ok);
    }

    [Fact]
    public async Task UnknownMessageType_IsRefusedExplicitly()
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync("GetSecretPlans", new JsonObject());

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
        Assert.Equal("Error", response.MessageType);
    }

    [Fact]
    public async Task WrongProtocolVersion_IsRefused()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var response = await client.SendAsync(
            "GetVersion", new JsonObject(), protocolVersion: 2);

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.ProtocolVersion, response.ErrorCode);

        // The connection stays usable for a correctly versioned request.
        Assert.True((await client.SendAsync("GetVersion")).Ok);
    }

    [Fact]
    public async Task OversizedFrame_ClosesTheConnection()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        // A length prefix past the cap is unrecoverable: the byte stream can no longer be
        // resynchronised, so the server answers once and drops the connection. The client sees
        // either that refusal or a broken pipe on its next write; both are the rejection, and
        // neither is a silently accepted oversized allocation.
        var broken = false;
        try
        {
            // The server may close right after reading the prefix, before the client's
            // FlushAsync completes. That is the same successful rejection.
            await client.SendRawPrefixAsync(FrameCodec.MaxFrameBytes + 1u);
            var response = await client.SendAsync("GetVersion", timeout: TimeSpan.FromSeconds(5));
            broken = !response.Ok;
        }
        catch (IOException)
        {
            broken = true;
        }

        Assert.True(broken, "the server accepted an oversized frame instead of closing");
    }

    [Fact]
    public async Task TwoConcurrentClients_AreServedIndependently()
    {
        await using var fixture = ServerFixture.Start();
        await using var first = await fixture.ConnectAsync();
        await using var second = await fixture.ConnectAsync();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(async index =>
            {
                var client = index % 2 == 0 ? first : second;
                var response = await client.SendAsync("GetVersion");
                return response.Ok;
            }));

        Assert.All(results, Assert.True);
        Assert.True(fixture.Server.AcceptedCount >= 2);
    }

    [Fact]
    public async Task SubscribeLiveEvents_DeliversRunUpdatedAfterAMutation()
    {
        string runId = string.Empty;
        await using var fixture = ServerFixture.Start(host =>
            runId = host.Mutations.CreateManualRun(ManualRun(1)).RunId);

        await using var subscriber = await fixture.ConnectAsync();
        var log = new FrameLog(subscriber);

        var acknowledgement = (await subscriber.SendAsync("SubscribeLiveEvents", new JsonObject())).Require();
        Assert.True(Guid.TryParseExact(
            acknowledgement["subscription_id"]!.GetValue<string>(), "D", out _));

        await using var mutator = await fixture.ConnectAsync();
        await mutator.SendAsync("CorrectRun", new JsonObject
        {
            ["run_id"] = runId,
            ["expected_revision"] = 1,
            ["reason"] = "其实是掉线了",
            ["changes"] = new JsonObject { ["result"] = "DISCONNECTED" },
        });

        static bool RunUpdated(JsonObject payload) => payload["kind"]!.GetValue<string>() == "run_updated";
        await log.WaitForEventsAsync(events => events.Any(RunUpdated), "no live event arrived");

        var updated = log.Events().First(RunUpdated);
        Assert.Equal("RunUpdated", updated["event_type"]!.GetValue<string>());
        Assert.Equal(runId, updated["run"]!["run_id"]!.GetValue<string>());
        Assert.Equal("DISCONNECTED", updated["run"]!["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task SubscribeLiveEvents_ServesTwoSubscribersAtOnce()
    {
        await using var fixture = ServerFixture.Start();
        await using var first = await fixture.ConnectAsync();
        await using var second = await fixture.ConnectAsync();

        using var firstReceived = new SemaphoreSlim(0);
        using var secondReceived = new SemaphoreSlim(0);
        first.EventReceived += _ => firstReceived.Release();
        second.EventReceived += _ => secondReceived.Release();

        await first.SendAsync("SubscribeLiveEvents", new JsonObject());
        await second.SendAsync("SubscribeLiveEvents", new JsonObject());

        await using var mutator = await fixture.ConnectAsync();
        await mutator.SendAsync("CreateManualRun", new JsonObject
        {
            ["result"] = "COMPLETED",
            ["reason"] = "补录一次",
            ["entered_at_utc"] = "2026-09-03T10:00:00.000Z",
            ["ended_at_utc"] = "2026-09-03T10:30:00.000Z",
        });

        Assert.True(await firstReceived.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await secondReceived.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Statistics_AreServedForEveryTable()
    {
        await using var fixture = ServerFixture.Start(host =>
        {
            for (var index = 0; index < 4; index++)
            {
                host.Mutations.CreateManualRun(ManualRun(index));
            }
        });

        await using var client = await fixture.ConnectAsync();

        var dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject())).Require();
        Assert.Equal(4, dashboard["attempt_count"]!.GetValue<int>());
        Assert.Equal(3, dashboard["completed_count"]!.GetValue<int>());
        Assert.Equal(0.75, dashboard["completion_rate"]!.GetValue<double>());
        Assert.Equal(0.25, dashboard["leave_rate"]!.GetValue<double>());

        var results = (await client.SendAsync("GetResultStats", new JsonObject())).Require();
        Assert.Equal(6, results["buckets"]!.AsArray().Count);

        var dungeons = (await client.SendAsync("GetDungeonStats", new JsonObject())).Require();
        Assert.Equal(3, dungeons["page_info"]!["total"]!.GetValue<int>());

        var jobs = (await client.SendAsync("GetJobStats", new JsonObject())).Require();
        Assert.Equal(1, jobs["page_info"]!["total"]!.GetValue<int>());
        Assert.Equal("骑士", jobs["items"]![0]!["job_name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false, 0, null)]
    [InlineData(true, false, 1, 0.0)]
    [InlineData(true, true, 2, 0.5)]
    public async Task ResultSharesUseAttemptsAndKeepUnknownCounts(
        bool includeCompleted, bool includeEnteredUnknown, int expectedAttempts, double? expectedUnknownShare)
    {
        await using var fixture = ServerFixture.Start(host =>
        {
            var undatedUnknown = ManualRun(0) with
            {
                Result = RunResult.Unknown,
                MatchedAtUtc = null,
                EnteredAtUtc = null,
                EndedAtUtc = null,
            };
            host.Mutations.CreateManualRun(undatedUnknown);
            host.Mutations.CreateManualRun(undatedUnknown with { RequestId = NewId() });
            if (includeCompleted)
            {
                host.Mutations.CreateManualRun(ManualRun(1));
            }

            if (includeEnteredUnknown)
            {
                host.Mutations.CreateManualRun(ManualRun(0) with { Result = RunResult.Unknown });
            }
        });
        await using var client = await fixture.ConnectAsync();

        var dashboard = (await client.SendAsync("GetDashboardStats")).Require();
        var results = (await client.SendAsync("GetResultStats")).Require();
        ContractSchema.Validate("$defs/Responses/GetDashboardStats", dashboard, "attempt-based dashboard shares");
        ContractSchema.Validate("$defs/Responses/GetResultStats", results, "attempt-based result shares");
        Assert.Equal(expectedAttempts, dashboard["attempt_count"]!.GetValue<int>());

        foreach (var breakdown in new[] { dashboard["result_breakdown"]!, results })
        {
            Assert.Equal(expectedAttempts, breakdown["attempt_count"]!.GetValue<int>());
            var unknown = Assert.Single(breakdown["buckets"]!.AsArray(),
                bucket => bucket!["result"]!.GetValue<string>() == "UNKNOWN")!;
            Assert.Equal(includeEnteredUnknown ? 3 : 2, unknown["count"]!.GetValue<int>());
            Assert.Equal(expectedUnknownShare, unknown["share"]?.GetValue<double>());
        }
    }

    [Fact]
    public async Task ExportAndBackup_WriteRealFiles()
    {
        await using var fixture = ServerFixture.Start(host =>
            host.Mutations.CreateManualRun(ManualRun(1)));

        using var directory = new TempDirectory("ExportIt", parent: AppContext.BaseDirectory, strict: true);
        await using var client = await fixture.ConnectAsync();

        var csv = (await client.SendAsync("ExportCsv", new JsonObject
        {
            ["target_path"] = directory.Combine("runs.csv"),
        })).Require();
        Assert.Equal(1, csv["row_count"]!.GetValue<int>());
        Assert.True(File.Exists(csv["target_path"]!.GetValue<string>()));

        var json = (await client.SendAsync("ExportJson", new JsonObject
        {
            ["target_path"] = directory.Combine("runs.json"),
        })).Require();
        Assert.Equal(1, json["row_count"]!.GetValue<int>());

        var backup = (await client.SendAsync("BackupDatabase", new JsonObject
        {
            ["target_path"] = directory.Combine("backup.db"),
        })).Require();
        Assert.True(backup["integrity_check_passed"]!.GetValue<bool>());
        Assert.True(backup["byte_count"]!.GetValue<long>() > 0);
        Assert.True(File.Exists(backup["target_path"]!.GetValue<string>()));

        foreach (var message in new[] { "ExportDiagnosticsReport", "ExportCandidateEvidence" })
        {
            var target = directory.Combine(message + ".json");
            var result = (await client.SendAsync(message, new JsonObject
            {
                ["target_path"] = target,
            })).Require();
            Assert.Equal(target, result["target_path"]!.GetValue<string>());
            var document = JsonNode.Parse(File.ReadAllText(target));
            Assert.NotNull(document);
            if (message == "ExportDiagnosticsReport")
            {
                Assert.Contains((string?)document["desktop_hang"]?["status"],
                    new[] { "found", "not_found", "unavailable" });
            }
        }
    }

    [Theory]
    [InlineData("ExportCsv")]
    [InlineData("ExportJson")]
    [InlineData("BackupDatabase")]
    [InlineData("ExportDiagnosticsReport")]
    [InlineData("ExportCandidateEvidence")]
    public async Task Export_NetworkDestination_IsRefused(string message)
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync(message, new JsonObject
        {
            ["target_path"] = @"\\export-server\share\runs.csv",
        });

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.ExportFailed, response.ErrorCode);
    }

    [Fact]
    public async Task UpdateAchievementBaseline_IsServedAndIdempotent()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        var requestId = NewId();
        var payload = new JsonObject
        {
            ["goal_count"] = 2000,
            ["baseline_completed_count"] = 1500,
            ["baseline_effective_at"] = "2026-01-01T00:00:00.000Z",
            ["reason"] = "开始使用前已完成 1500 次",
        };

        var first = (await client.SendAsync(
            "UpdateAchievementBaseline", payload.DeepClone().AsObject(), requestId)).Require();
        var second = (await client.SendAsync(
            "UpdateAchievementBaseline", payload.DeepClone().AsObject(), requestId)).Require();

        Assert.Equal(1500, first["baseline_completed_count"]!.GetValue<int>());
        Assert.True(second["idempotent_replay"]!.GetValue<bool>());
        Assert.Equal(
            first["audit_event_id"]!.GetValue<string>(),
            second["audit_event_id"]!.GetValue<string>());

        var dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject())).Require();
        Assert.Equal(1500, dashboard["achievement_progress"]!.GetValue<int>());
        Assert.Equal(500, dashboard["remaining"]!.GetValue<int>());
    }

    /// <summary>
    /// Audit 2026-10-03 CS-7: the Desktop sends the save time with every save, a goal-only edit
    /// included. The answer shows what was stored: the unchanged baseline keeps its effective time,
    /// and only a new baseline takes the time it is sent with. The progress adds the recorded
    /// completion to whichever baseline is stored, also to the new one sent with a later time (B3-1).
    /// </summary>
    [Fact]
    public async Task UpdateAchievementBaseline_GoalOnlyChange_KeepsTheEffectiveTimeAndTheProgress()
    {
        // ManualRun(1) is a completion that ended at 2026-09-01T01:31Z.
        await using var fixture = ServerFixture.Start(host =>
            host.Mutations.CreateManualRun(ManualRun(1)));
        await using var client = await fixture.ConnectAsync();

        JsonObject Save(int goal, int baseline, string effectiveAt) => new()
        {
            ["goal_count"] = goal,
            ["baseline_completed_count"] = baseline,
            ["baseline_effective_at"] = effectiveAt,
            ["reason"] = "保存成就设置",
        };

        (await client.SendAsync(
            "UpdateAchievementBaseline", Save(2000, 1500, "2026-09-01T00:00:00.000Z"))).Require();
        var goalOnly = (await client.SendAsync(
            "UpdateAchievementBaseline", Save(2500, 1500, "2026-10-03T08:00:00.000Z"))).Require();

        var dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject())).Require();
        Assert.Equal(1501, dashboard["achievement_progress"]!.GetValue<int>());
        Assert.Equal(999, dashboard["remaining"]!.GetValue<int>());
        Assert.Equal(2500, goalOnly["goal_count"]!.GetValue<int>());
        Assert.Equal("2026-09-01T00:00:00.000Z", goalOnly["baseline_effective_at"]!.GetValue<string>());

        var rebased = (await client.SendAsync(
            "UpdateAchievementBaseline", Save(2500, 1600, "2026-10-03T09:00:00.000Z"))).Require();

        Assert.Equal("2026-10-03T09:00:00.000Z", rebased["baseline_effective_at"]!.GetValue<string>());
        dashboard = (await client.SendAsync("GetDashboardStats", new JsonObject())).Require();
        Assert.Equal(1601, dashboard["achievement_progress"]!.GetValue<int>());
    }

    /// <summary>
    /// Audit 2026-10-03 S33-3: saving the achievement settings as they are - the same goal and the
    /// same baseline, sent with a later time as the Desktop always does - changes nothing. It is
    /// answered with what is stored, as a fresh request rather than a replay, and no client is told
    /// to read its statistics again.
    /// </summary>
    [Fact]
    public async Task UpdateAchievementBaseline_UnchangedSave_AnswersTheStoredValuesAndPublishesNothing()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        JsonObject Save(string effectiveAt, string reason) => new()
        {
            ["goal_count"] = 2000,
            ["baseline_completed_count"] = 1500,
            ["baseline_effective_at"] = effectiveAt,
            ["reason"] = reason,
        };

        var first = (await client.SendAsync(
            "UpdateAchievementBaseline", Save("2026-09-01T00:00:00.000Z", "开始使用前已完成 1500 次"))).Require();
        var again = (await client.SendAsync(
            "UpdateAchievementBaseline", Save("2026-10-04T08:00:00.000Z", "原样保存"))).Require();

        Assert.False(again["idempotent_replay"]!.GetValue<bool>());
        foreach (var field in new[]
            { "goal_count", "baseline_completed_count", "baseline_effective_at", "updated_at_utc", "audit_event_id" })
        {
            Assert.Equal(first[field]!.ToJsonString(), again[field]!.ToJsonString());
        }

        using var events = fixture.Host.LiveEvents.Subscribe(NewId());
        var invalidated = 0;
        for (var read = 0; read < LiveEventBus.ReplayCapacity; read++)
        {
            if (await events.ReadAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None) is not { } live)
            {
                break;
            }

            if (live["kind"]?.GetValue<string>() == "stats_invalidated" &&
                live["message"]?.GetValue<string>() == "成就基线已更新，统计需要重新查询。")
            {
                invalidated++;
            }
        }

        Assert.Equal(1, invalidated);
    }

    [Fact]
    public async Task EveryContractMessageTypeIsAnswered()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        foreach (var messageType in MessageDispatcher.KnownMessageTypes)
        {
            var response = await client.SendAsync(
                messageType, PayloadFor(messageType, fixture.DataDirectory));

            // Either a result or an explicit contract error; never a silent drop or a hang.
            Assert.True(
                response.Ok || !string.IsNullOrEmpty(response.ErrorCode),
                messageType + " produced neither a payload nor an error code");

            // Every file the sweep writes stays in the fixture's folder, which is deleted with
            // it: neither %TEMP% nor the managed data root keeps anything from this test.
            if (response.Ok && response.Payload["target_path"]?.GetValue<string>() is { } written)
            {
                Assert.StartsWith(
                    fixture.DataDirectory + Path.DirectorySeparatorChar,
                    written,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static JsonObject PayloadFor(string messageType, string directory) => messageType switch
    {
        "CreateManualRun" => new JsonObject
        {
            ["result"] = "COMPLETED",
            ["reason"] = "契约覆盖用例",
            ["entered_at_utc"] = "2026-09-03T10:00:00.000Z",
            ["ended_at_utc"] = "2026-09-03T10:30:00.000Z",
        },
        "CorrectRun" => new JsonObject
        {
            ["run_id"] = NewId(),
            ["expected_revision"] = 1,
            ["reason"] = "契约覆盖用例",
            ["changes"] = new JsonObject { ["result"] = "COMPLETED" },
        },
        "SoftDeleteRun" or "RestoreRun" => new JsonObject
        {
            ["run_id"] = NewId(),
            ["expected_revision"] = 1,
            ["reason"] = "契约覆盖用例",
        },
        "GetRunRevisions" => new JsonObject { ["run_id"] = NewId() },
        "UpdateAchievementBaseline" => new JsonObject
        {
            ["baseline_completed_count"] = 0,
            ["baseline_effective_at"] = "2026-01-01T00:00:00.000Z",
            ["reason"] = "契约覆盖用例",
        },
        "ExportCsv" or "ExportJson" => new JsonObject
        {
            ["target_path"] = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".out"),
        },
        _ => new JsonObject(),
    };
}
