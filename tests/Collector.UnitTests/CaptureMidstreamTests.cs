using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The midstream verdict: "this capture attached after the client had already connected, so
/// nothing on that connection can ever be decompressed".
///
/// Oodle's TCP decompressor is stateful and per connection, and the CN client keeps its zone
/// connection across teleports, so changing zones cannot recover it; only a relog can. Without
/// the verdict the user sees only a rising decode-error count, which reads as "the protocol is
/// wrong" rather than "log out and back in" (docs/live-validation-guide.md section 6).
/// </summary>
public sealed class CaptureMidstreamTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;
    private readonly List<FakeCaptureSource> _sources = new();
    private readonly RecordingStatusListener _status = new();
    private readonly FakeGameProcessProvider _processes = new();
    private readonly FakeProcessTcpTable _tcp = new();
    private readonly FakeAdapterProvider _adapters = new();
    private int? _preexisting;
    private TimeSpan _uptime = TimeSpan.Zero;

    public CaptureMidstreamTests()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        _settings.EnsureDefaults();
        _adapters.Add("wifi", "Wi-Fi", addresses: "192.168.31.77");
        _processes.Add(
            GameProcessLocator.Dx11ProcessName,
            4321,
            DateTimeOffset.UnixEpoch,
            @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
        _tcp.With(4321, "192.168.31.77");
    }

    private FakeCaptureSource Source => _sources[^1];

    [Fact]
    public void ACaptureThatStartedCleanIsNeverCalledMidstream()
    {
        _preexisting = 0;
        using var controller = Build();
        controller.Start();

        for (var index = 0; index < CaptureController.MidstreamRejectionThreshold * 2; index++)
        {
            Source.PushDecodeError();
        }

        var snapshot = controller.Snapshot();
        Assert.False(snapshot.MidstreamSuspected);
        Assert.Null(snapshot.Hint);
    }

    [Fact]
    public void ARejectionRunOnACaptureThatStartedWithConnectionsIsCalledMidstream()
    {
        _preexisting = 2;
        using var controller = Build();
        controller.Start();

        // One below the threshold is still just noise: a busy adapter rejects the odd frame.
        for (var index = 0; index < CaptureController.MidstreamRejectionThreshold - 1; index++)
        {
            Source.PushDecodeError();
        }

        Assert.False(controller.Snapshot().MidstreamSuspected);

        Source.PushDecodeError();
        var snapshot = WaitFor(controller, status => status.MidstreamSuspected == true);

        Assert.Equal(CaptureController.MidstreamHint, snapshot.Hint);
        Assert.Contains("重新登录", snapshot.Hint!, StringComparison.Ordinal);

        // The user has to be told without asking: the verdict is pushed as a status change.
        WaitForStatus(CaptureController.MidstreamHint);
    }

    [Fact]
    public void OneDecodedIpcMessageWithdrawsTheVerdict()
    {
        _preexisting = 3;
        using var controller = Build();
        controller.Start();

        for (var index = 0; index < CaptureController.MidstreamRejectionThreshold; index++)
        {
            Source.PushDecodeError();
        }

        WaitFor(controller, status => status.MidstreamSuspected == true);

        // Only an IPC segment can provide evidence that business messages are readable;
        // control segments can still arrive when the compressed stream is unavailable.
        Source.PushOpcode(0x0142, payloadLength: 16);
        var snapshot = WaitFor(controller, status => status.MidstreamSuspected == false);

        Assert.Null(snapshot.Hint);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void ControlSegmentsNeitherPreventNorWithdrawTheMidstreamVerdict(
        int segmentType, bool beforeErrors)
    {
        _preexisting = 2;
        using var controller = Build();
        controller.Start();
        var control = FakeCaptureSource.BuildIpcMessage(0, 8, (ushort)segmentType);
        if (beforeErrors)
            Source.PushRaw(control);

        for (var index = 0; index < CaptureController.MidstreamRejectionThreshold; index++)
            Source.PushDecodeError();

        Assert.True(controller.Snapshot().MidstreamSuspected);
        if (!beforeErrors)
            Source.PushRaw(control);

        var snapshot = controller.Snapshot();
        Assert.True(snapshot.MidstreamSuspected);
        Assert.Equal(CaptureController.MidstreamHint, snapshot.Hint);
        Assert.Equal(1, snapshot.MessagesDecoded);
    }

    [Fact]
    public void AClientThatReconnectedIsNeverToldToLogInAgain()
    {
        // A connection the operating system lists as opened after capture started means the
        // player has already relogged, so the login is not the missing piece and repeating
        // the instruction is wrong.
        _preexisting = 2;
        using var controller = Build();
        controller.Start();
        Source.IngressCounters = new CaptureIngressCounters(GameConnections: 3);

        for (var index = 0; index < CaptureController.MidstreamRejectionThreshold; index++)
        {
            Source.PushDecodeError();
        }

        var snapshot = controller.Snapshot();
        Assert.True(snapshot.MidstreamSuspected);
        Assert.Equal(CaptureController.MidstreamReconnectedHint, snapshot.Hint);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("\u518d\u767b\u5f55\u4e00\u6b21\u4e5f\u6ca1\u7528", StringComparison.Ordinal));

        // The original sentence remains correct when no new connection was ever seen.
        Source.IngressCounters = new CaptureIngressCounters(GameConnections: 2);
        Assert.Equal(CaptureController.MidstreamHint, controller.Snapshot().Hint);
    }

    [Fact]
    public void MidstreamIsNotMeasuredWhileNoCaptureIsRunning()
    {
        _preexisting = 4;
        using var controller = Build();

        // Absent, not false: an absent counter means "not measured" by contract, and claiming
        // a clean start for a capture that never ran would be false.
        Assert.Null(controller.Snapshot().MidstreamSuspected);

        var wire = CaptureWire.CaptureStatus(controller.Snapshot());
        Assert.False(wire.ContainsKey("midstream_suspected"));
    }

    [Fact]
    public void TheSignatureSourceInForceIsReportedWhileRunningAndNotOtherwise()
    {
        _preexisting = 0;
        using var controller = Build();
        Assert.Null(controller.Snapshot().OodleSignature.Source);

        controller.Start();
        Source.SignatureUse = new OodleSignatureUse("profile", "cn.2026.08.05", "VERIFIED");

        var wire = CaptureWire.CaptureStatus(controller.Snapshot());
        Assert.Equal("profile", wire["oodle_signature_source"]!.GetValue<string>());
        Assert.Equal("cn.2026.08.05", wire["oodle_profile_id"]!.GetValue<string>());
        Assert.Equal("VERIFIED", wire["oodle_profile_status"]!.GetValue<string>());
    }

    [Fact]
    public void PreexistingConnectionsAndAQuietMinuteAreEnoughWithoutASingleDecodeError()
    {
        // Capture running and the profile matched, yet no parse failures and no valid event:
        // nothing reaches the observer at all, so a rejection threshold alone can never be
        // crossed. Preexisting connections plus a quiet minute must be enough on their own.
        _preexisting = 3;
        StopFollowing(); // Keep the capture up; the adapter retry has its own test.
        using var controller = Build();
        controller.Start();

        Assert.Equal(CaptureSilentReason.None, controller.Snapshot().SilentReason);

        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        var snapshot = controller.Snapshot();
        Assert.Equal(CaptureSilentReason.Midstream, snapshot.SilentReason);
        Assert.True(snapshot.MidstreamSuspected);
        Assert.Equal(CaptureController.MidstreamHint, snapshot.Hint);
        Assert.Equal(3, snapshot.PreexistingConnections);
        Assert.Equal(0, snapshot.DecodeErrors);

        var wire = CaptureWire.CaptureStatus(snapshot);
        Assert.Equal("MIDSTREAM", wire["silent_reason"]!.GetValue<string>());
        Assert.Equal(3, wire["preexisting_connections"]!.GetValue<int>());
        WaitForStatus(CaptureController.MidstreamHint);
    }

    [Fact]
    public void EveryFrameDroppedForWantOfAHandshakeIsMidstreamWithoutWaitingOutTheGrace()
    {
        _preexisting = null; // The TCP table is unreadable; the wire still reports what happened.
        StopFollowing();
        using var controller = Build();
        controller.Start();

        // The wire shape of a mid-connection attach: nearly everything has no tracked stream
        // at all because the handshake predates the capture, and only the few connections that
        // showed one direction's SYN and not the other's land in dropped_no_syn.
        Source.IngressCounters = new CaptureIngressCounters(
            RawPackets: 400, DroppedNoStream: 380, DroppedNoSyn: 20);
        controller.Poll();

        var snapshot = controller.Snapshot();
        Assert.Equal(CaptureSilentReason.Midstream, snapshot.SilentReason);
        Assert.Equal(400, snapshot.RawPacketsObserved);
        Assert.Equal(0, snapshot.PacketsObserved);
        Assert.True(snapshot.UptimeMs < (long)CaptureController.LivenessGrace.TotalMilliseconds);

        var wire = CaptureWire.CaptureStatus(snapshot);
        Assert.Equal(400, wire["raw_packets_observed"]!.GetValue<long>());
        Assert.Equal(380, wire["ingress"]!["dropped_no_stream"]!.GetValue<long>());
        Assert.Equal(20, wire["ingress"]!["dropped_no_syn"]!.GetValue<long>());
    }

    [Fact]
    public void OrdinaryBackgroundChatterIsTooSmallASampleToAccuseTheStartupOrder()
    {
        // The pcap filter is "this local address", not "this process", so a handful of frames
        // from another program's long-lived connection is enough to make every observed frame
        // a discarded one. That sample is too small to accuse the startup order.
        _preexisting = 0;
        StopFollowing();
        using var controller = Build();
        controller.Start();

        Source.IngressCounters = new CaptureIngressCounters(RawPackets: 6, DroppedNoStream: 6);
        controller.Poll();

        Assert.Equal(CaptureSilentReason.None, controller.Snapshot().SilentReason);
        Assert.Null(controller.Snapshot().Hint);
    }

    [Fact]
    public void NoPacketAtAllOnTheSelectedAdapterIsReportedAsSuchAfterAMinute()
    {
        _preexisting = 0;
        StopFollowing();
        using var controller = Build();
        controller.Start();

        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        var snapshot = controller.Snapshot();
        Assert.Equal(CaptureSilentReason.NoPacketsOnAdapter, snapshot.SilentReason);
        Assert.False(snapshot.MidstreamSuspected);
        Assert.Equal(CaptureController.NoPacketsHint, snapshot.Hint);
        Assert.Contains("加速器", snapshot.Hint!, StringComparison.Ordinal);
        Assert.Equal("NO_PACKETS_ON_ADAPTER", CaptureWire.CaptureStatus(snapshot)["silent_reason"]!.GetValue<string>());
    }

    [Fact]
    public void TrafficThatNeverBelongsToTheGameIsReportedAsAnOwnershipProblem()
    {
        _preexisting = 0;
        StopFollowing();
        using var controller = Build();
        controller.Start();

        Source.IngressCounters = new CaptureIngressCounters(RawPackets: 900, UnconfirmedTuples: 4);
        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        var snapshot = controller.Snapshot();
        Assert.Equal(CaptureSilentReason.NoStreamOwnership, snapshot.SilentReason);
        Assert.Equal(CaptureController.NoOwnershipHint, snapshot.Hint);
        Assert.Equal(4, CaptureWire.CaptureStatus(snapshot)["ingress"]!["unconfirmed_tuples"]!.GetValue<long>());
    }

    [Fact]
    public void AnAdapterThatSawNothingIsChosenAgainOnceWhileFollowingTheGame()
    {
        // A guessed adapter that carries nothing must be chosen again: following the game
        // means retrying rather than reporting a healthy silence forever.
        _preexisting = 0;
        using var controller = Build();
        controller.Start();

        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Single(_sources);

        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(2, _sources.Count);

        // Once. A second silent run is left alone: restarting forever would be worse than
        // the silence, and the hint already tells the user what to do.
        _uptime = TimeSpan.FromSeconds(200);
        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(2, _sources.Count);
        Assert.Equal(CaptureSilentReason.NoPacketsOnAdapter, controller.Snapshot().SilentReason);
    }

    [Fact]
    public void AnAdapterTheUserChoseIsNeverSwappedOutFromUnderThem()
    {
        _preexisting = 0;
        using var controller = Build();
        controller.Start("wifi");

        _uptime = TimeSpan.FromSeconds(61);
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Single(_sources);
        Assert.Equal(CaptureSilentReason.NoPacketsOnAdapter, controller.Snapshot().SilentReason);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _database.Dispose();
    }

    private CaptureController Build() => new(new CaptureServices
    {
        Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
        Game = new GameProcessLocator(
            _processes,
            new FakeGameFileReader().With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000")),
        Adapters = new AdapterEnumerator(_adapters, _tcp),
        SourceFactory = CreateSource,
        Profile = new FakeProfileStatusProvider(ProfileStatus.Verified),
        StatusListener = _status,
        Clock = _database.Clock,
        Uptime = () => _uptime,
        Settings = _settings,
        Sessions = new CaptureSessionRepository(_database.Database),
        Database = _database.Database,
        CollectorVersion = "0.1.0",
        DetectionTtl = TimeSpan.Zero,
        EnableFollowTimer = false,
    });

    /// <summary>
    /// Turns off the follow poll's own reaction to a silent capture, so a test can inspect
    /// the verdict instead of racing the automatic adapter retry that follows from it.
    /// </summary>
    private void StopFollowing() =>
        _settings.SetSetting(CaptureController.FollowGameSetting, "false");

    private ICaptureSource CreateSource()
    {
        var source = new FakeCaptureSource { PreexistingTcpConnections = _preexisting };
        _sources.Add(source);
        return source;
    }

    private void WaitForStatus(string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (_status.Entries.Any(entry => entry.Message == message))
            {
                return;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("the midstream verdict never reached a status listener");
    }

    private static CaptureDiagnosticsSnapshot WaitFor(
        CaptureController controller, Func<CaptureDiagnosticsSnapshot, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = controller.Snapshot();
            if (predicate(snapshot))
            {
                return snapshot;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("the capture controller never reached the expected state");
    }
}
