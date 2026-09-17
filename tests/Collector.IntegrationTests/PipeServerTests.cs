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

    private static CreateManualRunCommand ManualRun(int index) => new()
    {
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

    [Fact]
    public async Task CaptureMessages_ReportMissingNpcapWithoutStartingCapture()
    {
        await using var fixture = ServerFixture.Start();

        var adapters = (await fixture.CallAsync("ListCaptureAdapters")).Require();
        Assert.False(adapters["npcap_installed"]!.GetValue<bool>());
        Assert.NotNull(adapters["install_hint"]);

        // Adapters are listed even without Npcap, because enumerating them needs no driver and
        // an empty list is less useful than the cards plus the install hint. Nothing is
        // recommended without the game's connections, and every address is masked to a /24.
        // See tests/Collector.IntegrationTests/CaptureIpcTests.cs.
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
    public async Task QueryRuns_PagesThroughTheHistory()
    {
        await using var fixture = ServerFixture.Start(host =>
        {
            for (var index = 0; index < 7; index++)
            {
                host.Mutations.CreateManualRun(ManualRun(index));
            }
        });

        await using var client = await fixture.ConnectAsync();

        var first = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["page"] = 1,
            ["page_size"] = 3,
            ["sort"] = new JsonObject { ["field"] = "entered_at_utc", ["direction"] = "asc" },
        })).Require();

        Assert.Equal(3, first["items"]!.AsArray().Count);
        Assert.Equal(7, first["page_info"]!["total"]!.GetValue<int>());

        var last = (await client.SendAsync("QueryRuns", new JsonObject
        {
            ["page"] = 3,
            ["page_size"] = 3,
            ["sort"] = new JsonObject { ["field"] = "entered_at_utc", ["direction"] = "asc" },
        })).Require();

        Assert.Single(last["items"]!.AsArray());
        Assert.Equal(3, last["page_info"]!["page"]!.GetValue<int>());

        // Pages must not overlap.
        var firstIds = first["items"]!.AsArray().Select(item => item!["run_id"]!.GetValue<string>());
        var lastIds = last["items"]!.AsArray().Select(item => item!["run_id"]!.GetValue<string>());
        Assert.Empty(firstIds.Intersect(lastIds, StringComparer.Ordinal));
    }

    [Fact]
    public async Task QueryRuns_WithAnOversizedPageSize_IsRefused()
    {
        await using var fixture = ServerFixture.Start();

        var response = await fixture.CallAsync("QueryRuns", new JsonObject { ["page_size"] = 201 });

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
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
        var events = new List<JsonObject>();
        using var received = new SemaphoreSlim(0);
        subscriber.EventReceived += payload =>
        {
            lock (events)
            {
                events.Add(payload);
            }

            received.Release();
        };

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

        Assert.True(await received.WaitAsync(TimeSpan.FromSeconds(10)), "no live event arrived");

        JsonObject[] snapshot;
        lock (events)
        {
            snapshot = events.ToArray();
        }

        var updated = snapshot.First(payload => payload["kind"]!.GetValue<string>() == "run_updated");
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

    [Fact]
    public async Task ExportAndBackup_WriteRealFiles()
    {
        await using var fixture = ServerFixture.Start(host =>
            host.Mutations.CreateManualRun(ManualRun(1)));

        var directory = Path.Combine(
            AppContext.BaseDirectory, "MentorRecorder.ExportIt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var client = await fixture.ConnectAsync();

            var csv = (await client.SendAsync("ExportCsv", new JsonObject
            {
                ["target_path"] = Path.Combine(directory, "runs.csv"),
            })).Require();
            Assert.Equal(1, csv["row_count"]!.GetValue<int>());
            Assert.True(File.Exists(csv["target_path"]!.GetValue<string>()));

            var json = (await client.SendAsync("ExportJson", new JsonObject
            {
                ["target_path"] = Path.Combine(directory, "runs.json"),
            })).Require();
            Assert.Equal(1, json["row_count"]!.GetValue<int>());

            var backup = (await client.SendAsync("BackupDatabase", new JsonObject
            {
                ["target_path"] = Path.Combine(directory, "backup.db"),
            })).Require();
            Assert.True(backup["integrity_check_passed"]!.GetValue<bool>());
            Assert.True(backup["byte_count"]!.GetValue<long>() > 0);
            Assert.True(File.Exists(backup["target_path"]!.GetValue<string>()));

            foreach (var message in new[] { "ExportDiagnosticsReport", "ExportCandidateEvidence" })
            {
                var target = Path.Combine(directory, message + ".json");
                var result = (await client.SendAsync(message, new JsonObject
                {
                    ["target_path"] = target,
                })).Require();
                Assert.Equal(target, result["target_path"]!.GetValue<string>());
                Assert.NotNull(JsonNode.Parse(File.ReadAllText(target)));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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

    [Fact]
    public async Task EveryContractMessageTypeIsAnswered()
    {
        await using var fixture = ServerFixture.Start();
        await using var client = await fixture.ConnectAsync();

        foreach (var messageType in MessageDispatcher.KnownMessageTypes)
        {
            var response = await client.SendAsync(messageType, PayloadFor(messageType));

            // Either a result or an explicit contract error; never a silent drop or a hang.
            Assert.True(
                response.Ok || !string.IsNullOrEmpty(response.ErrorCode),
                messageType + " produced neither a payload nor an error code");
        }
    }

    private static JsonObject PayloadFor(string messageType) => messageType switch
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
            ["target_path"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".out"),
        },
        _ => new JsonObject(),
    };
}
