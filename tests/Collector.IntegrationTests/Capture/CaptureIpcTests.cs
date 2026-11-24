using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Storage;
using static MentorRecorder.Collector.IntegrationTests.SyntheticTraffic;

namespace MentorRecorder.Collector.IntegrationTests;

/// <summary>
/// The capture messages over a real Named Pipe.
///
/// The first half supplies explicit no-Npcap/no-game detector inputs, so it stays valid on a
/// machine where Npcap is installed or the game is running: the software must report the
/// declared state. The second half injects a fake source so the same dispatcher can be driven
/// through a whole capture.
/// </summary>
public sealed class CaptureIpcTests
{
    [Fact]
    public async Task GetCaptureStatus_ReportsUnavailable_OnAMachineWithoutNpcap()
    {
        await using var fixture = CaptureServerFixture.Start();

        var payload = (await fixture.CallAsync("GetCaptureStatus")).Require();

        Assert.Equal("STOPPED", payload["state"]!.GetValue<string>());
        Assert.False(payload["npcap_installed"]!.GetValue<bool>());
        Assert.False(payload["ffxiv_running"]!.GetValue<bool>());
        Assert.Equal("NONE", payload["profile_status"]!.GetValue<string>());
        Assert.Equal("UNAVAILABLE", payload["last_error_code"]!.GetValue<string>());
        Assert.Null(payload["capture_session_id"]);
        // UNAVAILABLE that no refusal worded: the sentence fields are sent, and empty.
        Assert.True(payload.ContainsKey("last_error_text"));
        Assert.True(payload.ContainsKey("last_error_ref"));
        Assert.Null(payload["last_error_text"]);
        Assert.Null(payload["last_error_ref"]);

        // The two boundary constants are reported at runtime so a user can check them.
        Assert.Equal("WinPCap", payload["monitor_type"]!.GetValue<string>());
        Assert.False(payload["injected_hook_enabled"]!.GetValue<bool>());
    }

    /// <summary>
    /// The 2026-11-03 report: a start the Collector refused left the Desktop with the code alone,
    /// and a code it could not word became "an unrecognised problem" while the refusal had told the
    /// player what to do. The status now carries the refusal's own sentence beside the code, with
    /// its catalogue reference for the interface language (contracts/CHANGELOG.md 2026-11-04).
    /// </summary>
    [Fact]
    public async Task GetCaptureStatus_CarriesTheRefusalSentenceBesideItsCode()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled: true));

        var refusal = await fixture.CallAsync("StartCapture");
        Assert.False(refusal.Ok);
        Assert.Equal(ErrorCodes.FfxivNotRunning, refusal.ErrorCode);

        var payload = (await fixture.CallAsync("GetCaptureStatus")).Require();

        ContractSchema.Validate("$defs/CaptureStatus", payload, "the capture status after a refused start");
        Assert.Equal(ErrorCodes.FfxivNotRunning, payload["last_error_code"]!.GetValue<string>());
        Assert.Equal(refusal.ErrorMessage, payload["last_error_text"]!.GetValue<string>());
        Assert.Equal("capture.start.game_not_running", payload["last_error_ref"]!["key"]!.GetValue<string>());
        CollectorTextCatalogue.AssertParallel(payload["last_error_text"], payload["last_error_ref"]);
    }

    [Fact]
    public async Task StartCapture_IsRefusedWithNpcapMissing_OnAMachineWithoutNpcap()
    {
        await using var fixture = CaptureServerFixture.Start();

        var response = await fixture.CallAsync("StartCapture");

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.NpcapMissing, response.ErrorCode);
        Assert.Contains("Npcap", response.ErrorMessage!, StringComparison.Ordinal);

        // The refusal must point at the official installer and never offer to fetch anything at
        // run time; only the installer ever downloads Npcap's (audit 2026-10-03, CS-8).
        Assert.Contains("运行时不会替您下载", response.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopCapture_IsRefusedWhenNothingIsRunning()
    {
        await using var fixture = CaptureServerFixture.Start();

        var response = await fixture.CallAsync("StopCapture");

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.CaptureNotRunning, response.ErrorCode);

        var current = (await fixture.CallAsync("GetCurrentRun")).Require();
        Assert.Equal("IDLE", current["state"]!.GetValue<string>());
        Assert.Null(current["run"]);
    }

    [Fact]
    public async Task ListCaptureAdapters_ListsMaskedAddressesWithoutNpcap()
    {
        await using var fixture = CaptureServerFixture.Start();

        var payload = (await fixture.CallAsync("ListCaptureAdapters")).Require();
        var adapters = payload["adapters"]!.AsArray();

        // The shipping enumerator still masks and renders the declared adapter even when
        // the supplied detector environment contains neither Npcap nor a running game.
        var declaredAdapter = Assert.Single(adapters)!;
        Assert.Equal("{TEST-ADAPTER}", declaredAdapter["adapter_id"]!.GetValue<string>());
        Assert.Equal("192.168.31.x", Assert.Single(declaredAdapter["ipv4_addresses"]!.AsArray())!.GetValue<string>());
        Assert.False(payload["npcap_installed"]!.GetValue<bool>());
        Assert.Contains("Npcap", payload["install_hint"]!.GetValue<string>(), StringComparison.Ordinal);

        foreach (var adapter in adapters)
        {
            Assert.NotNull(adapter!["adapter_id"]);
            Assert.NotNull(adapter["description"]);
            foreach (var address in adapter["ipv4_addresses"]!.AsArray())
            {
                // Every displayed address is masked to a /24 before it leaves the process.
                Assert.EndsWith(".x", address!.GetValue<string>(), StringComparison.Ordinal);
            }
        }

        // Nothing may be recommended while the game's traffic cannot be located.
        Assert.DoesNotContain(
            adapters, adapter => adapter!["recommended"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(false, "NOT_INSTALLED", ErrorCodes.NpcapMissing)]
    [InlineData(true, "READY", ErrorCodes.FfxivNotRunning)]
    public async Task NoGameEnvironment_ReportsConfiguredDriverAndRefusesCapture(
        bool npcapInstalled, string expectedNpcapStatus, string expectedError)
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.NoGame(npcapInstalled));

        var status = (await fixture.CallAsync("GetStatus")).Require();
        Assert.Equal(expectedNpcapStatus, status["npcap"]!["status"]!.GetValue<string>());
        Assert.Equal(npcapInstalled, status["capture"]!["npcap_installed"]!.GetValue<bool>());
        Assert.False(status["game"]!["running"]!.GetValue<bool>());

        var start = await fixture.CallAsync("StartCapture");
        Assert.False(start.Ok);
        Assert.Equal(expectedError, start.ErrorCode);
        Assert.Null((await fixture.CallAsync("GetCaptureStatus")).Require()["capture_session_id"]);
    }

    [Fact]
    public async Task GetProtocolProfileStatus_IsNeverVerifiedInThisBuild()
    {
        await using var fixture = CaptureServerFixture.Start();

        var payload = (await fixture.CallAsync("GetProtocolProfileStatus")).Require();

        Assert.Equal("NONE", payload["status"]!.GetValue<string>());
        Assert.Null(payload["profile_id"]);
        Assert.Null(payload["verified_at_utc"]);
        // Plain words for the player, no design term (review A2-4).
        Assert.Contains("不会自动写入任何记录", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("fail-closed", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStatus_DisclosesTheGameNpcapAndTheOodleMode()
    {
        await using var fixture = CaptureServerFixture.Start();

        var payload = (await fixture.CallAsync("GetStatus")).Require();

        Assert.True(payload["database_ready"]!.GetValue<bool>());
        Assert.Equal("STOPPED", payload["capture"]!["state"]!.GetValue<string>());
        Assert.False(payload["game"]!["running"]!.GetValue<bool>());
        Assert.Equal("NOT_INSTALLED", payload["npcap"]!["status"]!.GetValue<string>());

        // DEC-OODLE-01 requires these two to be visible so the Desktop can show them on the
        // first-run page rather than leaving them in the implementation.
        Assert.Equal("FfxivTcp", payload["oodle_mode"]!.GetValue<string>());
        Assert.True(payload["reads_game_executable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StartCaptureRejectsAnUnknownField()
    {
        await using var fixture = CaptureServerFixture.Start();

        var response = await fixture.CallAsync(
            "StartCapture", new JsonObject { ["use_deucalion"] = true });

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    [Fact]
    public async Task StartAndStopCapture_RoundTripThroughTheDispatcher()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));

        var started = (await fixture.CallAsync("StartCapture")).Require();
        Assert.Equal("RUNNING", started["state"]!.GetValue<string>());
        Assert.True(started["npcap_installed"]!.GetValue<bool>());
        Assert.True(started["ffxiv_running"]!.GetValue<bool>());
        Assert.Equal(4321, started["ffxiv_process_id"]!.GetValue<int>());
        Assert.Equal("{TEST-ADAPTER}", started["adapter_id"]!.GetValue<string>());
        Assert.NotNull(started["capture_session_id"]);
        Assert.Equal(4096, started["queue_capacity"]!.GetValue<int>());

        var second = await fixture.CallAsync("StartCapture");
        Assert.Equal(ErrorCodes.CaptureAlreadyRunning, second.ErrorCode);

        source.PushOpcode(0x0142, payloadLength: 16);
        var observed = await fixture.WaitForCaptureStatusAsync(status => status["packets_observed"]!.GetValue<long>() > 0);
        Assert.Equal(0, observed["packets_dropped"]!.GetValue<long>());

        var stopped = (await fixture.CallAsync("StopCapture")).Require();
        Assert.Equal("STOPPED", stopped["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetCaptureStatus_CarriesTheIngressFactsThatExplainASilentCapture()
    {
        // "RUNNING, 0 parse failures, no valid event" must be diagnosable from a single status
        // frame, without asking the user to reproduce anything.
        using var source = new FakeCaptureSource
        {
            PreexistingTcpConnections = 2,
            IngressCounters = new CaptureIngressCounters(
                RawPackets: 1200,
                DroppedNoStream: 100,
                DroppedNoSyn: 1100,
                ExpiredStreams: 1,
                UnconfirmedTuples: 2,
                StreamResets: 3,
                AdapterDropped: 7,
                Handshakes: 40,
                GameConnections: 2,
                GameConnectionsNow: 2,
                DamagedGameDirections: 4),
        };
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));
        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        // One rejected frame re-runs the verdict. The evidence deciding it is the ingress
        // counters, which no observer callback reports.
        source.PushDecodeError();
        var status = await fixture.WaitForCaptureStatusAsync(
            payload => payload["silent_reason"]!.GetValue<string>() != "NONE");

        Assert.Equal("MIDSTREAM", status["silent_reason"]!.GetValue<string>());
        Assert.True(status["midstream_suspected"]!.GetValue<bool>());
        Assert.Equal("RECOVERING", status["path_recovery"]!["state"]!.GetValue<string>());
        Assert.Contains("自动寻找", status["hint"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain("重新登录", status["hint"]!.GetValue<string>(), StringComparison.Ordinal);
        // 1200 frames arrived and exactly one reached the message level. That gap is the
        // diagnosis, and packets_observed alone cannot show it.
        Assert.Equal(1200, status["raw_packets_observed"]!.GetValue<long>());
        Assert.Equal(1, status["packets_observed"]!.GetValue<long>());
        Assert.Equal(2, status["preexisting_connections"]!.GetValue<int>());

        var ingress = status["ingress"]!;
        Assert.Equal(100, ingress["dropped_no_stream"]!.GetValue<long>());
        Assert.Equal(1100, ingress["dropped_no_syn"]!.GetValue<long>());
        Assert.Equal(1, ingress["expired_streams"]!.GetValue<long>());
        Assert.Equal(2, ingress["unconfirmed_tuples"]!.GetValue<long>());
        Assert.Equal(3, ingress["stream_resets"]!.GetValue<long>());
        Assert.Equal(7, ingress["adapter_dropped"]!.GetValue<long>());
        // Raw ownership/start facts remain unchanged while automatic recovery defers
        // manual instructions; progress is not evidence that decoding has recovered.
        Assert.Equal(40, ingress["handshakes"]!.GetValue<long>());
        Assert.Equal(2, ingress["game_connections"]!.GetValue<long>());
        Assert.Equal(2, ingress["game_connections_now"]!.GetValue<long>());
        // A game direction abandoned after a gap is lost observation too; without it a session
        // that stopped counting as evidence would have no visible reason.
        Assert.Equal(4, ingress["damaged_game_directions"]!.GetValue<long>());

        // Driver loss is loss: it is reported as dropped and the capture reads as degraded.
        Assert.Equal(7, status["packets_dropped"]!.GetValue<long>());
        Assert.Equal("DEGRADED", status["state"]!.GetValue<string>());

        ContractSchema.Validate("$defs/CaptureStatus", status, "GetCaptureStatus");
        Assert.True((await fixture.CallAsync("StopCapture")).Ok);
    }

    [Fact]
    public async Task ListCaptureAdapters_MarksARememberedAdapterThatNoLongerCarriesTheGame()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));

        var payload = (await fixture.CallAsync("ListCaptureAdapters")).Require();
        var adapter = Assert.Single(payload["adapters"]!.AsArray())!;

        // Nothing is stale here: the one declared adapter is the one carrying the traffic.
        Assert.False(adapter["preference_stale"]!.GetValue<bool>());
        Assert.True(adapter["recommended"]!.GetValue<bool>());
        ContractSchema.Validate("$defs/CaptureAdapter", adapter, "ListCaptureAdapters");
    }

    [Fact]
    public async Task StartCapture_RefusesAnAdapterThatDoesNotExist()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));

        var response = await fixture.CallAsync(
            "StartCapture", new JsonObject { ["adapter_id"] = "{NO-SUCH-ADAPTER}" });

        // contracts/error-codes.md has no dedicated code for this yet; ERR_BAD_REQUEST with
        // the offending field is the closest declared answer. See CONTRACT_CHANGE_REQUESTS.
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.BadRequest, response.ErrorCode);
    }

    [Fact]
    public async Task ACaptureStatusChangeReachesASubscriber()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));
        await using var client = await fixture.ConnectAsync();

        var log = new FrameLog(client);

        await client.SendAsync("SubscribeLiveEvents");
        await client.SendAsync("StartCapture");

        static bool Starting(JsonObject payload) =>
            payload["event_type"]!.GetValue<string>() == "CaptureStatusChanged"
            && payload["capture"]!["state"]!.GetValue<string>() == "STARTING";
        await log.WaitForEventsAsync(
            events => events.Any(Starting), "a capture status change must reach a live subscriber");

        var statusChange = Assert.Single(log.Events(), Starting);
        Assert.Equal("collector_status", statusChange["kind"]!.GetValue<string>());
        Assert.NotNull(statusChange["capture"]);
    }

    [Fact]
    public async Task ACaptureSessionRowIsWrittenAndClosed()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));

        var started = (await fixture.CallAsync("StartCapture")).Require();
        var sessionId = started["capture_session_id"]!.GetValue<string>();

        Assert.Null(fixture.Host.Sessions.Get(sessionId)!.EndedAtUtc);

        await fixture.CallAsync("StopCapture");

        Assert.NotNull(fixture.Host.Sessions.Get(sessionId)!.EndedAtUtc);
    }

    [Fact]
    public async Task VerifiedProfileFlowsFromCaptureThroughParserIntoLiveIpcAndStorage()
    {
        const string gameBuild = "integration-live-build";
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(
            CaptureFakes.Ready(source, gameBuild), Selector(VerifiedProfile("integration-live", gameBuild)));

        var started = (await fixture.CallAsync("StartCapture")).Require();
        var sessionId = started["capture_session_id"]!.GetValue<string>();

        // Transport control segments precede real duty traffic on the same pipeline.
        // They must not poison parser diagnostics or prevent the following run from saving.
        foreach (ushort segmentType in new ushort[] { 7, 8 })
        {
            var heartbeat = IpcMessage(0, new byte[8]);
            BinaryPrimitives.WriteUInt16LittleEndian(heartbeat.AsSpan(12), segmentType);
            source.PushRaw(heartbeat);
        }

        source.PushRaw(IpcMessage(PopOpcode, Pop(42, 1001)));

        var current = await fixture.WaitForCurrentRunAsync("MENTOR_MATCHED");
        Assert.Equal("integration-live", current["run"]!["protocol_profile_id"]!.GetValue<string>());
        Assert.Equal(gameBuild, current["run"]!["game_build"]!.GetValue<string>());
        Assert.Equal(sessionId, current["run"]!["capture_session_id"]!.GetValue<string>());
        // Time in the duty: absent until the run enters one (audit 2026-10-20, D1-3).
        Assert.Null(current["elapsed_ms"]);
        Assert.Single((await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray());

        source.PushRaw(IpcMessage(ZoneOpcode, Zone(2001, 1001, 3001)));
        source.PushRaw(IpcMessage(JobOpcode, Job(1)));
        source.PushRaw(IpcMessage(ResultOpcode, Result(1)));

        await fixture.WaitForStoredResultAsync("COMPLETED");
        var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
        var run = Assert.Single(rows)!;
        Assert.Equal("COMPLETED", run["result"]!.GetValue<string>());
        Assert.Equal("integration-live", run["protocol_profile_id"]!.GetValue<string>());
        Assert.Equal(gameBuild, run["game_build"]!.GetValue<string>());
        Assert.Equal(4, fixture.Host.Capture.Snapshot().ParseOkCount);
        Assert.Equal(0, fixture.Host.Capture.Snapshot().ParseFailCount);
        Assert.Equal(6, fixture.Host.Capture.Snapshot().MessagesDecoded);

        // Decision 5: the kind of the last decoded event travels with its time. The duty result
        // was the last message pushed.
        var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
        Assert.Equal("DUTY_RESULT", status["last_valid_event_kind"]!.GetValue<string>());
        Assert.NotNull(status["last_valid_event_at_utc"]);
        ContractSchema.Validate("$defs/Responses/GetCaptureStatus", status, "capture status after a decoded run");

        await fixture.CallAsync("StopCapture");
        Assert.NotNull(fixture.Host.Sessions.Get(sessionId)!.EndedAtUtc);
    }

    /// <summary>
    /// A game connection that closes while the player is inside a duty ends the run as
    /// DISCONNECTED, all the way from the capture source to the stored row.
    ///
    /// The terminal state is declared in docs/state-machine.md, in the live-validation guide's
    /// "pull the network cable" scenario and in the statistics definitions, so it must be
    /// reachable from the capture source rather than only by calling the processor directly
    /// (review finding H-6).
    /// </summary>
    [Fact]
    public async Task AClosedGameConnectionInsideADutyIsRecordedAsDisconnected()
    {
        const string gameBuild = "integration-disconnect-build";
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(
            CaptureFakes.Ready(source, gameBuild), Selector(VerifiedProfile("integration-disconnect", gameBuild)));

        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        source.PushRaw(IpcMessage(PopOpcode, Pop(42, 1001)));
        await fixture.WaitForCurrentRunAsync("MENTOR_MATCHED");

        source.PushRaw(IpcMessage(ZoneOpcode, Zone(2001, 1001, 3001)));
        await fixture.WaitForCurrentRunAsync("ENTERED_DUTY");

        // The connection carrying the duty goes away. Nothing here touches the state machine
        // or the semantic processor.
        source.PushConnectionClosed();

        await fixture.WaitForStoredResultAsync("DISCONNECTED");
        var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
        var run = Assert.Single(rows)!;
        Assert.Equal("DISCONNECTED", run["result"]!.GetValue<string>());

        await fixture.CallAsync("StopCapture");
    }

    // -------------------------------------------- remembered install, game closed ----
    // The wire when the client version is known but the game is not running. The contract already
    // allows it - game_build has never depended on ffxiv_running - and the two must be reported
    // independently: a client that infers "no version yet" from "not running" would be wrong, and
    // one that infers "the game is up" from a version would start a capture that cannot work.

    private const string InstalledBuild = "2026.09.18.0000.0000";

    [Fact]
    public async Task GetCaptureStatus_ReportsTheInstalledBuildWithTheGameClosed()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(InstalledBuild));

        var payload = (await fixture.CallAsync("GetCaptureStatus")).Require();

        Assert.False(payload["ffxiv_running"]!.GetValue<bool>());
        Assert.Null(payload["ffxiv_process_id"]);
        Assert.Equal(InstalledBuild, payload["game_build"]!.GetValue<string>());
        Assert.Equal("CN", payload["region"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetStatus_NeverRendersTheRememberedInstallPath()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(InstalledBuild));

        var payload = (await fixture.CallAsync("GetStatus")).Require();
        var game = payload["game"]!;

        Assert.False(game["running"]!.GetValue<bool>());
        Assert.Equal(InstalledBuild, game["game_build"]!.GetValue<string>());
        Assert.Equal(0, game["instance_count"]!.GetValue<int>());

        // The path itself stays inside the service: install_path_readable speaks for the
        // running client's path and there is no running client (docs/privacy-boundary.md §5).
        Assert.False(game["install_path_readable"]!.GetValue<bool>());
        Assert.DoesNotContain("SdoA", payload.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheShippingHostWritesTheInstallNoteBesideItsDatabase()
    {
        using var source = new FakeCaptureSource();
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source, InstalledBuild));

        // One ordinary status poll is enough: the locator sees the client and remembers where
        // it found it, so the next launch knows the version before the game is started.
        _ = (await fixture.CallAsync("GetCaptureStatus")).Require();

        var note = Path.Combine(fixture.DataDirectory, DatabasePaths.GameInstallFileName);
        Assert.True(File.Exists(note), note + " was not written");
        var written = File.ReadAllText(note);
        Assert.Contains(FileGameInstallMemory.PathField, written, StringComparison.Ordinal);
        Assert.Contains("ffxiv_dx11.exe", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartCapture_IsStillRefused_WhileTheGameIsNotRunning()
    {
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.RememberedInstall(InstalledBuild));

        var response = await fixture.CallAsync("StartCapture");

        // Knowing the version says nothing about whether there is a client to observe.
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.FfxivNotRunning, response.ErrorCode);
    }
}
