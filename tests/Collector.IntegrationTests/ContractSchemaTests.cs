using System.Text;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Speech;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// Every frame of a scripted session, validated against contracts/ipc-v1.schema.json.
///
/// The session below touches every business message the contract declares, both mutation
/// outcomes (applied and idempotently replayed), every error shape the transport can produce
/// (a business refusal, an unknown message type, a malformed frame) and the live events the
/// mutations emit. The count is never written down here: it is
/// <see cref="ContractSchema.BusinessMessageTypes"/>, read out of the schema, and the coverage
/// assertion at the end of the scripted session enforces it.
///
/// Each frame is checked three ways: the whole envelope against the root schema, the envelope
/// against its own <c>$defs</c> shape, and the payload against the per-message response schema.
/// Every one of those has <c>additionalProperties: false</c>, so a field the Collector starts
/// writing without declaring it fails here.
/// </summary>
public sealed class ContractSchemaTests
{
    private static string NewId() => Guid.NewGuid().ToString("D");

    [Fact]
    public async Task ScriptedSession_EveryResponseAndEventMatchesTheContract()
    {
        // A capture-ready fake environment, so the StartCapture and StopCapture success shapes
        // are exercised instead of being excluded from the coverage assertion on a machine with
        // neither Npcap nor the game (review M-12).
        using var source = new FakeCaptureSource();

        // A speech service that answers every request with a short WAV, and a kill switch that
        // reads as unset for this client only, so the SynthesizeSpeech success shape is covered.
        var speech = new OnlineSpeechClient(
            (request, _) => Task.FromResult(new SpeechTransportResponse(
                200, request.Uri, "audio/wav", null, new MemoryStream(WaveFile.Silence()))),
            readEnvironment: _ => null);
        await using var fixture = ServerFixture.Start(capture: CaptureFakes.Ready(source), speech: speech);
        var workspace = Directory.CreateTempSubdirectory("MentorRecorder.Contract").FullName;

        var envelopes = new List<JsonObject>();
        await using var client = await fixture.ConnectAsync();
        client.EnvelopeReceived += envelope =>
        {
            lock (envelopes)
            {
                envelopes.Add(envelope.DeepClone().AsObject());
            }
        };

        try
        {
            // -- subscribe first, so the mutations below are observed as live events ------
            var subscribe = await client.SendAsync(
                "SubscribeLiveEvents", new JsonObject { ["heartbeat_interval_ms"] = 5000 });
            Assert.True(subscribe.Ok);

            // -- read-only messages -------------------------------------------------------
            foreach (var messageType in new[]
                     {
                         "GetVersion", "GetStatus", "GetCaptureStatus", "ListCaptureAdapters",
                         "GetProtocolProfileStatus", "GetCurrentRun",
                         "GetCaptureValidationStatus", "StopCaptureValidation",
                     })
            {
                Assert.True((await client.SendAsync(messageType)).Ok, messageType);
            }

            // The update check rides on GetStatus rather than a message of its own.
            // $defs/UpdateStatus is additionalProperties: false, so a field the Collector starts
            // writing without declaring it fails here.
            var update = (await client.SendAsync("GetStatus")).Require()["update"]!.AsObject();
            ContractSchema.Validate("$defs/UpdateStatus", update, "update status");
            Assert.True(update["enabled"]!.GetValue<bool>());
            Assert.False(update["update_available"]!.GetValue<bool>());

            // -- create two runs so the statistics and the exports have something to say --
            var firstRunId = await CreateRunAsync(client, "COMPLETED", 900001, 19);
            _ = await CreateRunAsync(client, "LEFT_OR_ABANDONED", 900002, 24);

            var queried = await client.SendAsync(
                "QueryRuns",
                new JsonObject
                {
                    ["filter"] = new JsonObject { ["include_deleted"] = true },
                    ["page"] = 1,
                    ["page_size"] = 50,
                });
            Assert.Equal(2, queried.Require()["items"]!.AsArray().Count);

            var stats = new JsonObject { ["filter"] = new JsonObject() };
            Assert.True((await client.SendAsync("GetDashboardStats", stats.DeepClone().AsObject())).Ok);

            // Every trend granularity, because each one produces a different bucket count and
            // $defs/TrendSeries caps the array.
            foreach (var granularity in new[] { "day", "week", "month" })
            {
                var trend = await client.SendAsync(
                    "GetDashboardStats",
                    new JsonObject
                    {
                        ["filter"] = new JsonObject(),
                        ["trend_granularity"] = granularity,
                    });
                var series = trend.Require()["trend"]!.AsObject();
                Assert.Equal(granularity, series["granularity"]!.GetValue<string>());
                Assert.NotEmpty(series["buckets"]!.AsArray());
            }

            Assert.True((await client.SendAsync("GetResultStats", stats.DeepClone().AsObject())).Ok);
            foreach (var messageType in new[] { "GetDungeonStats", "GetJobStats" })
            {
                Assert.True((await client.SendAsync(
                    messageType,
                    new JsonObject
                    {
                        ["filter"] = new JsonObject(),
                        ["page"] = 1,
                        ["page_size"] = 200,
                        ["sort"] = new JsonObject
                        {
                            ["field"] = "attempt_count",
                            ["direction"] = "desc",
                        },
                    })).Ok, messageType);
            }

            // -- corrections, deletion, restore -------------------------------------------
            var correct = await client.SendAsync(
                "CorrectRun",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = 1,
                    ["reason"] = "队友截图确认结果有误",
                    ["changes"] = new JsonObject
                    {
                        ["result"] = "LEFT_OR_ABANDONED",
                        ["note"] = "契约测试写入的备注",
                    },
                });
            var revision = correct.Require()["revision"]!.GetValue<int>();

            Assert.True((await client.SendAsync(
                "GetRunRevisions", new JsonObject { ["run_id"] = firstRunId })).Ok);

            // The event trail of that run, reduced to the shape a client may see. It is empty
            // for a manually created run, and the empty answer is itself part of the contract.
            var events = await client.SendAsync(
                "GetRunEvents", new JsonObject { ["run_id"] = firstRunId });
            Assert.Equal(firstRunId, events.Require()["run_id"]!.GetValue<string>());

            // Undo appends; it never rewrites. The revision it returns is the one the next
            // mutation has to expect.
            var undone = await client.SendAsync(
                "UndoRevision",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = revision,
                    ["reason"] = "改错了，撤销上一次修正",
                });
            revision = undone.Require()["revision"]!.GetValue<int>();
            Assert.Equal(3, revision);

            var deleteId = NewId();
            var deleted = await client.SendAsync(
                "SoftDeleteRun",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = revision,
                    ["reason"] = "重复记录，先删除再恢复",
                },
                deleteId);
            var deletedRevision = deleted.Require()["revision"]!.GetValue<int>();

            // The same request id with the same body: an idempotent replay, not a change.
            var replay = await client.SendAsync(
                "SoftDeleteRun",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = revision,
                    ["reason"] = "重复记录，先删除再恢复",
                },
                deleteId);
            Assert.True(replay.Require()["idempotent_replay"]!.GetValue<bool>());

            Assert.True((await client.SendAsync(
                "RestoreRun",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = deletedRevision,
                    ["reason"] = "误删，恢复",
                })).Ok);

            // -- 导随心得: written, then read back through the summary ---------------------
            var reflection = await client.SendAsync(
                "SetRunReflection",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["mood"] = "good",
                    ["text"] = "契约测试写入的导随心得。",
                });
            Assert.Equal("good", reflection.Require()["reflection"]!["mood"]!.GetValue<string>());

            var summary = await client.SendAsync(
                "GetReflectionSummary", new JsonObject { ["recent_limit"] = 3 });
            Assert.Equal(1, summary.Require()["reflection_count"]!.GetValue<int>());

            // The filter bit belongs to the same feature and has to be legal in a real request.
            var filtered = await client.SendAsync(
                "QueryRuns",
                new JsonObject
                {
                    ["filter"] = new JsonObject { ["with_reflection"] = true },
                });
            Assert.Single(filtered.Require()["items"]!.AsArray());

            Assert.True((await client.SendAsync(
                "UpdateAchievementBaseline",
                new JsonObject
                {
                    ["goal_count"] = 2000,
                    ["baseline_completed_count"] = 137,
                    ["baseline_effective_at"] = "2026-09-04T12:00:00.000Z",
                    ["reason"] = "首次设定基线",
                })).Ok);

            // -- export and backup ---------------------------------------------------------
            var candidate = CandidateLedgerIpcTests.Seed(fixture.Host);
            Assert.True((await client.SendAsync("QueryCandidateObservations")).Ok);
            Assert.True((await client.SendAsync("ReviewCandidateObservation", new JsonObject
                { ["observation_id"] = candidate.ObservationId, ["verdict"] = "UNSURE" })).Ok);
            Assert.True((await client.SendAsync("ExportCandidateEvidence", new JsonObject
                { ["target_path"] = Path.Combine(workspace, "candidates.json") })).Ok);

            // -- calibration: nothing is being calibrated here, so discarding is a no-op and
            // confirming is refused; the successful path needs a build with no profile and a
            // played roulette, which CalibrationPipelineTests exercises without a pipe.
            Assert.True((await client.SendAsync("DiscardCalibration")).Ok);
            var notReady = await client.SendAsync("ConfirmCalibration", new JsonObject
            {
                ["verdicts"] = new JsonArray(new JsonObject { ["event_id"] = "pop-1", ["verdict"] = "CORRECT" }),
            });
            Assert.False(notReady.Ok);

            // -- shared calibration: nothing is calibrated here either, so each request answers
            // with its "nothing to do" outcome and the share code is refused;
            // SharedCalibrationContractTests holds the success shapes.
            Assert.True((await client.SendAsync("CheckSharedCalibration")).Ok);
            Assert.True((await client.SendAsync("AcceptSharedQueueInference")).Ok);
            Assert.True((await client.SendAsync("RejectSharedCalibration")).Ok);
            Assert.True((await client.SendAsync("ImportCalibrationCode", new JsonObject { ["code"] = "MRC1.???" })).Ok);
            Assert.Equal(ErrorCodes.ShareCodeUnavailable, (await client.SendAsync("GetCalibrationShareCode")).ErrorCode);
            Assert.True((await client.SendAsync(
                "ExportCsv",
                new JsonObject
                {
                    ["target_path"] = Path.Combine(workspace, "runs.csv"),
                    ["filter"] = new JsonObject(),
                    ["overwrite"] = true,
                })).Ok);
            Assert.True((await client.SendAsync(
                "ExportJson",
                new JsonObject
                {
                    ["target_path"] = Path.Combine(workspace, "runs.json"),
                    ["filter"] = new JsonObject(),
                    ["overwrite"] = true,
                })).Ok);

            // -- online speech and the integrity check ----------------------------------------
            var speechDefaults = (await client.SendAsync("GetSpeechSettings")).Require();
            Assert.Equal("none", speechDefaults["provider"]!.GetValue<string>());
            Assert.False(speechDefaults["has_key"]!.GetValue<bool>());
            Assert.Equal(ErrorCodes.SpeechNotConfigured, (await client.SendAsync(
                "SynthesizeSpeech", new JsonObject { ["text"] = "匹配成功" })).ErrorCode);
            var speechSettings = (await client.SendAsync("UpdateSpeechSettings", new JsonObject
            {
                ["provider"] = "openai_compatible",
                ["openai_base_url"] = "https://speech.example.com/v1/",
                ["openai_model"] = "gpt-4o-mini-tts",
                ["voice"] = "alloy",
                ["api_key"] = "contract-test-key",
            })).Require();
            Assert.True(speechSettings["configured"]!.GetValue<bool>());
            Assert.DoesNotContain("contract-test-key", speechSettings.ToJsonString(), StringComparison.Ordinal);
            var spoken = (await client.SendAsync("SynthesizeSpeech", new JsonObject
            {
                ["text"] = "副本结算，距离目标还差 11 次", ["rate_percent"] = 120, ["test"] = true,
            })).Require();
            Assert.False(spoken["from_cache"]!.GetValue<bool>());
            Assert.True((await client.SendAsync("CheckDatabaseIntegrity")).Require()["passed"]!.GetValue<bool>());

            // target_path omitted on purpose: the managed backups folder, and pruned_count.
            var backup = await client.SendAsync("BackupDatabase", new JsonObject());
            Assert.True(backup.Require()["integrity_check_passed"]!.GetValue<bool>());

            // The sanitized diagnostics report, written into the workspace so the assertions
            // below can read the bytes that actually landed on disk.
            var report = await client.SendAsync(
                "ExportDiagnosticsReport",
                new JsonObject { ["target_path"] = Path.Combine(workspace, "diag.json") });
            var reportPath = report.Require()["target_path"]!.GetValue<string>();
            Assert.True(File.Exists(reportPath));
            Assert.True(report.Require()["byte_count"]!.GetValue<long>() > 0);

            var reportText = await File.ReadAllTextAsync(reportPath);
            Assert.Contains("\"live_capture_status\": \"" + CaptureDiagnosticsSnapshot.LiveCaptureStatus + "\"", reportText, StringComparison.Ordinal);
            Assert.Contains("\"public_distribution_ready\": true", reportText, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", reportText);
            Assert.DoesNotMatch(@"[A-Za-z]:\\", reportText);

            // Network destinations are refused before attempting to write a report.
            var refused = await client.SendAsync(
                "ExportDiagnosticsReport",
                new JsonObject { ["target_path"] = @"\\mentor-test.invalid\reports\mentor-diag.json" });
            Assert.Equal(ErrorCodes.ExportFailed, refused.ErrorCode);

            // -- every error shape ----------------------------------------------------------
            var missing = await client.SendAsync(
                "SoftDeleteRun",
                new JsonObject
                {
                    ["run_id"] = NewId(),
                    ["expected_revision"] = 1,
                    ["reason"] = "不存在的记录",
                });
            Assert.Equal(ErrorCodes.NotFound, missing.ErrorCode);

            // Same request id, different body: the dedicated idempotency conflict code.
            var conflict = await client.SendAsync(
                "SoftDeleteRun",
                new JsonObject
                {
                    ["run_id"] = firstRunId,
                    ["expected_revision"] = 99,
                    ["reason"] = "换了内容的同一个 request_id",
                },
                deleteId);
            Assert.Equal(ErrorCodes.IdempotencyConflict, conflict.ErrorCode);

            // -- capture settings, then a capture that actually starts ---------------------
            var defaults = (await client.SendAsync("GetCaptureSettings")).Require();
            // Follow-the-game defaults on: capture has to be listening before login, which is
            // not reliable if the user must remember to enable it.
            Assert.True(defaults["follow_game"]!.GetValue<bool>());
            Assert.Equal(7, defaults["log_retention_days"]!.GetValue<int>());

            // allow_without_profile lets the counters-only capture below start with no verified
            // profile. It is set through the contract so the switch is shown to take effect,
            // not merely to write a row.
            var updated = (await client.SendAsync(
                "UpdateCaptureSettings",
                new JsonObject
                {
                    ["follow_game"] = false,
                    ["allow_without_profile"] = true,
                    ["log_retention_days"] = 14,
                    ["adapter_id"] = null,
                    ["region_override"] = "CN",
                })).Require();
            Assert.True(updated["allow_without_profile"]!.GetValue<bool>());
            Assert.Equal(14, updated["log_retention_days"]!.GetValue<int>());
            Assert.Equal("CN", updated["region_override"]!.GetValue<string>());

            var startedCapture = await client.SendAsync("StartCapture", new JsonObject());
            Assert.True(startedCapture.Ok, startedCapture.ErrorMessage);
            Assert.Equal("RUNNING", startedCapture.Require()["state"]!.GetValue<string>());
            var stoppedCapture = await client.SendAsync("StopCapture");
            Assert.True(stoppedCapture.Ok, stoppedCapture.ErrorMessage);

            Assert.Equal(
                ErrorCodes.BadRequest,
                (await client.SendAsync("NoSuchMessage")).ErrorCode);

            // A frame that is not JSON at all: answered against the zero uuid and closed off
            // as message_type "Error".
            var beforeMalformed = Count(envelopes);
            await client.SendRawAsync(Encoding.UTF8.GetBytes("{not json"));
            await WaitForAsync(envelopes, beforeMalformed + 1);

            // Live events are asynchronous; the mutations above published several.
            await WaitForEventsAsync(envelopes, 4);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // A backup handle can linger on Windows; the temp cleaner will get it.
            }
        }

        // -- validation ---------------------------------------------------------------------
        List<JsonObject> captured;
        lock (envelopes)
        {
            captured = envelopes.ToList();
        }

        Assert.NotEmpty(captured);

        var seenResponses = new HashSet<string>(StringComparer.Ordinal);
        var seenEventKinds = new HashSet<string>(StringComparer.Ordinal);
        var sawFailure = false;
        var sawMalformed = false;

        foreach (var envelope in captured)
        {
            var messageType = envelope["message_type"]!.GetValue<string>();
            var ok = envelope["ok"]!.GetValue<bool>();
            var payload = envelope["payload"]!.AsObject();

            ContractSchema.Validate(string.Empty, envelope, $"root envelope of {messageType}");
            ContractSchema.Validate(
                "$defs/ResponseEnvelope", envelope, $"response envelope of {messageType}");

            if (messageType == "Event")
            {
                ContractSchema.Validate("$defs/EventEnvelope", envelope, "event envelope");
                ContractSchema.Validate("$defs/LiveEvent", payload, "live event payload");
                seenEventKinds.Add(payload["kind"]!.GetValue<string>());
                continue;
            }

            if (!ok)
            {
                sawFailure = true;
                ContractSchema.Validate(
                    "$defs/ErrorPayload", envelope["error"], $"error object of {messageType}");
                ContractSchema.Validate(
                    "$defs/ErrorPayload", payload, $"error payload of {messageType}");

                if (messageType == "Error")
                {
                    ContractSchema.Validate("$defs/ErrorEnvelope", envelope, "error envelope");
                    sawMalformed |= envelope["request_id"]!.GetValue<string>() ==
                        "00000000-0000-0000-0000-000000000000";
                }

                continue;
            }

            ContractSchema.Validate(
                $"$defs/Responses/{messageType}", payload, $"{messageType} response payload");
            seenResponses.Add(messageType);
        }

        // Coverage: the script must have exercised the whole contract. A response may be
        // missing only when it is listed below, and may never be unexpected.
        var required = ContractSchema.ResponseMessageTypes
            // CaptureValidationIpcTests covers successful start/marker responses with a
            // synthetic source and a real pipe; a validation session cannot share this
            // connection because it takes the exclusive capture lease.
            // GetCalibrationShareCode can only refuse here (no local calibration is in force);
            // SharedCalibrationContractTests validates its success shape from the renderer.
            .Where(name => name is not ("StartCaptureValidation" or "AddCaptureValidationMarker" or "ConfirmCalibration"
                or "GetCalibrationShareCode"))
            .ToArray();
        Assert.Empty(required.Except(seenResponses, StringComparer.Ordinal));
        Assert.Empty(seenResponses.Except(ContractSchema.ResponseMessageTypes, StringComparer.Ordinal));
        Assert.True(sawFailure, "脚本应至少产生一个错误应答。");
        Assert.True(sawMalformed, "脚本应至少产生一个无法解析的帧的错误信封。");
        Assert.Contains("run_created", seenEventKinds);
        Assert.Contains("run_updated", seenEventKinds);
        Assert.Contains("stats_invalidated", seenEventKinds);
    }

    [Fact]
    public void ContractDeclaresAResponseSchemaForEveryBusinessMessage() =>
        Assert.Equal(
            ContractSchema.BusinessMessageTypes.OrderBy(name => name, StringComparer.Ordinal),
            ContractSchema.ResponseMessageTypes.OrderBy(name => name, StringComparer.Ordinal));

    [Fact]
    public void ContractDeclaresTheSameMessageTypesTheDispatcherAnswers() =>
        Assert.Equal(
            ContractSchema.BusinessMessageTypes.OrderBy(name => name, StringComparer.Ordinal),
            MessageDispatcher.KnownMessageTypes.OrderBy(name => name, StringComparer.Ordinal));

    [Fact]
    public void ContractDeclaresTheFrameSizeTheCodeEnforces()
    {
        var description = ContractSchema.Root["description"]!.GetValue<string>();
        Assert.Contains(
            FrameCodec.MaxFrameBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ContractDeclaresEveryErrorCodeTheCollectorCanReturn()
    {
        var declared = ContractSchema.Root["$defs"]!["ErrorPayload"]!["properties"]!["code"]!["enum"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var implemented = typeof(ErrorCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            declared.OrderBy(code => code, StringComparer.Ordinal),
            implemented.OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void ContractDeclaresEveryLiveEventKindTheBusEmits()
    {
        var declared = ContractSchema.Root["$defs"]!["LiveEvent"]!["properties"]!["kind"]!["enum"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        var emittedKinds = Enum.GetValues<LiveEventKind>().Select(LiveEventBus.KindToken);
        var emittedTypes = Enum.GetValues<LiveEventKind>().Select(LiveEventBus.SchemaEventType);

        Assert.Equal(
            declared.OrderBy(kind => kind, StringComparer.Ordinal),
            emittedKinds.Distinct().OrderBy(kind => kind, StringComparer.Ordinal));

        var declaredTypes = ContractSchema.Root["$defs"]!["LiveEvent"]!["properties"]!["event_type"]!["enum"]!
            .AsArray()
            .Select(node => node!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(emittedTypes, type => Assert.Contains(type, declaredTypes));
    }

    private static async Task<string> CreateRunAsync(
        PipeClient client, string result, int contentId, int jobId)
    {
        var response = await client.SendAsync(
            "CreateManualRun",
            new JsonObject
            {
                ["content_id"] = contentId,
                ["job_id"] = jobId,
                ["duty_name"] = "石卫塔",
                ["matched_at_utc"] = "2026-09-04T11:58:00.000Z",
                ["entered_at_utc"] = "2026-09-04T12:00:00.000Z",
                ["ended_at_utc"] = "2026-09-04T12:26:00.000Z",
                ["result"] = result,
                ["contributes_to_goal"] = true,
                ["note"] = "契约测试补录",
                ["reason"] = "契约测试补录",
            });

        return response.Require()["run_id"]!.GetValue<string>();
    }

    private static int Count(List<JsonObject> envelopes)
    {
        lock (envelopes)
        {
            return envelopes.Count;
        }
    }

    private static async Task WaitForAsync(List<JsonObject> envelopes, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Count(envelopes) < atLeast && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(Count(envelopes) >= atLeast, "等待帧超时。");
    }

    private static async Task WaitForEventsAsync(List<JsonObject> envelopes, int atLeast)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (envelopes)
            {
                if (envelopes.Count(envelope =>
                        envelope["message_type"]!.GetValue<string>() == "Event") >= atLeast)
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail("等待实时事件超时。");
    }
}
