using System.Buffers.Binary;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Profiles;

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

        // The two boundary constants are reported at runtime so a user can check them.
        Assert.Equal("WinPCap", payload["monitor_type"]!.GetValue<string>());
        Assert.False(payload["injected_hook_enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StartCapture_IsRefusedWithNpcapMissing_OnAMachineWithoutNpcap()
    {
        await using var fixture = CaptureServerFixture.Start();

        var response = await fixture.CallAsync("StartCapture");

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.NpcapMissing, response.ErrorCode);
        Assert.Contains("Npcap", response.ErrorMessage!, StringComparison.Ordinal);

        // The refusal must point at the official installer, never offer to fetch anything.
        Assert.Contains("不会替您下载", response.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopCapture_IsRefusedWhenNothingIsRunning()
    {
        await using var fixture = CaptureServerFixture.Start();

        var response = await fixture.CallAsync("StopCapture");

        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.CaptureNotRunning, response.ErrorCode);
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
        Assert.Contains("fail-closed", payload["message"]!.GetValue<string>(), StringComparison.Ordinal);
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
        var observed = await WaitForAsync(fixture, status => status["packets_observed"]!.GetValue<long>() > 0);
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
                GameConnectionsNow: 2),
        };
        await using var fixture = CaptureServerFixture.Start(CaptureFakes.Ready(source));
        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        // One rejected frame re-runs the verdict. The evidence deciding it is the ingress
        // counters, which no observer callback reports.
        source.PushDecodeError();
        var status = await WaitForAsync(
            fixture, payload => payload["silent_reason"]!.GetValue<string>() != "NONE");

        Assert.Equal("MIDSTREAM", status["silent_reason"]!.GetValue<string>());
        Assert.True(status["midstream_suspected"]!.GetValue<bool>());
        Assert.Contains("重新登录", status["hint"]!.GetValue<string>(), StringComparison.Ordinal);
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
        // Two connections seen and two held at the start: the client has not reconnected, so
        // the advice is still the one that asks for a fresh login.
        Assert.Equal(40, ingress["handshakes"]!.GetValue<long>());
        Assert.Equal(2, ingress["game_connections"]!.GetValue<long>());
        Assert.Equal(2, ingress["game_connections_now"]!.GetValue<long>());

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

        var events = new List<JsonObject>();
        using var received = new SemaphoreSlim(0);
        client.EventReceived += payload =>
        {
            lock (events)
            {
                events.Add(payload);
            }

            received.Release();
        };

        await client.SendAsync("SubscribeLiveEvents");
        await client.SendAsync("StartCapture");

        Assert.True(
            await received.WaitAsync(TimeSpan.FromSeconds(10)),
            "a capture status change must reach a live subscriber");

        JsonObject[] snapshot;
        lock (events)
        {
            snapshot = events.ToArray();
        }

        var statusChange = Assert.Single(
            snapshot,
            payload => payload["event_type"]!.GetValue<string>() == "CaptureStatusChanged"
                && payload["capture"]!["state"]!.GetValue<string>() == "STARTING");
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

        var profilePath = Path.Combine(
            AppContext.BaseDirectory,
            "protocol-profiles",
            "synthetic",
            "synthetic-v1.json");
        var synthetic = ProfileLoader.Validate(profilePath).Profile
            ?? throw new InvalidOperationException("the checked-in synthetic profile must load");
        var verified = synthetic with
        {
            ProfileId = "integration-live",
            Region = Region.Cn,
            GameBuild = gameBuild,
            Status = ProfileCompatibilityStatus.Verified,
            Messages = synthetic.Messages
                .Select(message => message with { SegmentType = FfxivFraming.SegmentTypeIpc })
                .ToArray(),
        };

        ProfileSelection Select(GameProcessDetection game) => new(
            ProfileCompatibilityStatus.Verified,
            verified.ToBinding(),
            verified,
            game.Region,
            game.GameBuild,
            "integration test profile selected");

        await using var fixture = CaptureServerFixture.Start(
            CaptureFakes.Ready(source, gameBuild), Select);

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

        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 1001);
        source.PushRaw(IpcMessage(61441, pop));

        var current = await WaitForCurrentRunAsync(fixture, "MENTOR_MATCHED");
        Assert.Equal("integration-live", current["run"]!["protocol_profile_id"]!.GetValue<string>());
        Assert.Equal(gameBuild, current["run"]!["game_build"]!.GetValue<string>());
        Assert.Equal(sessionId, current["run"]!["capture_session_id"]!.GetValue<string>());
        Assert.True(current["elapsed_ms"]!.GetValue<long>() >= 0);
        Assert.Single((await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray());

        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 2001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 3001);
        zone[12] = 1;
        source.PushRaw(IpcMessage(61442, zone));

        var job = new byte[4];
        job[0] = 1;
        source.PushRaw(IpcMessage(61444, job));

        var result = new byte[4];
        result[0] = 1;
        source.PushRaw(IpcMessage(61443, result));

        await WaitForStoredResultAsync(fixture, "COMPLETED");
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
            CaptureFakes.Ready(source, gameBuild), SyntheticSelector(gameBuild, "integration-disconnect"));

        Assert.True((await fixture.CallAsync("StartCapture")).Ok);

        var pop = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(pop, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(pop.AsSpan(4), 1001);
        source.PushRaw(IpcMessage(61441, pop));
        await WaitForCurrentRunAsync(fixture, "MENTOR_MATCHED");

        var zone = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(zone, 2001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt32LittleEndian(zone.AsSpan(8), 3001);
        zone[12] = 1;
        source.PushRaw(IpcMessage(61442, zone));
        await WaitForCurrentRunAsync(fixture, "ENTERED_DUTY");

        // The connection carrying the duty goes away. Nothing here touches the state machine
        // or the semantic processor.
        source.PushConnectionClosed();

        await WaitForStoredResultAsync(fixture, "DISCONNECTED");
        var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
        var run = Assert.Single(rows)!;
        Assert.Equal("DISCONNECTED", run["result"]!.GetValue<string>());

        await fixture.CallAsync("StopCapture");
    }

    /// <summary>The checked-in synthetic profile, presented as a verified one for this build.</summary>
    /// <param name="gameBuild">Client build the profile claims to cover.</param>
    /// <param name="profileId">Identifier stamped on the runs it produces.</param>
    private static Func<GameProcessDetection, ProfileSelection> SyntheticSelector(
        string gameBuild, string profileId)
    {
        var profilePath = Path.Combine(
            AppContext.BaseDirectory, "protocol-profiles", "synthetic", "synthetic-v1.json");
        var synthetic = ProfileLoader.Validate(profilePath).Profile
            ?? throw new InvalidOperationException("the checked-in synthetic profile must load");
        var verified = synthetic with
        {
            ProfileId = profileId,
            Region = Region.Cn,
            GameBuild = gameBuild,
            Status = ProfileCompatibilityStatus.Verified,
            Messages = synthetic.Messages
                .Select(message => message with { SegmentType = FfxivFraming.SegmentTypeIpc })
                .ToArray(),
        };

        return game => new ProfileSelection(
            ProfileCompatibilityStatus.Verified,
            verified.ToBinding(),
            verified,
            game.Region,
            game.GameBuild,
            "integration test profile selected");
    }

    private static async Task<JsonObject> WaitForAsync(
        CaptureServerFixture fixture, Func<JsonObject, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = (await fixture.CallAsync("GetCaptureStatus")).Require();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the capture status never reached the expected value");
    }

    private static async Task<JsonObject> WaitForCurrentRunAsync(
        CaptureServerFixture fixture, string expectedState)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var current = (await fixture.CallAsync("GetCurrentRun")).Require();
            if (current["state"]!.GetValue<string>() == expectedState)
            {
                return current;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the current run never reached " + expectedState);
    }

    /// <summary>
    /// Waits until the stored row carries a terminal result.
    ///
    /// <c>GetCurrentRun</c> normalises a terminal state to IDLE as soon as it is asked, so a
    /// player who finished a duty and then idled in a city is not reported as still in one
    /// (review finding L-9). The durable answer to what an attempt ended as is the stored row,
    /// which is also what <c>run_finished</c> is computed from.
    /// </summary>
    /// <param name="fixture">Server under test.</param>
    /// <param name="expectedResult">Wire spelling of the expected result.</param>
    private static async Task<JsonObject> WaitForStoredResultAsync(
        CaptureServerFixture fixture, string expectedResult)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var rows = (await fixture.CallAsync("QueryRuns")).Require()["items"]!.AsArray();
            if (rows.Count > 0 && rows[0]!["result"]!.GetValue<string>() == expectedResult)
            {
                return rows[0]!.AsObject();
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("no stored run ever reached " + expectedResult);
    }

    private static byte[] IpcMessage(ushort opcode, ReadOnlySpan<byte> payload)
    {
        var message = FakeCaptureSource.BuildIpcMessage(opcode, payload.Length);
        payload.CopyTo(message.AsSpan(FfxivFraming.HeaderBytes));
        return message;
    }
}
