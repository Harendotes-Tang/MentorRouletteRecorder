using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Protocol.Decoded;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The controller's state machine, driven end to end with a fake source on a machine that has
/// neither Npcap nor the game.
///
/// Every refusal asserted here is a documented contract error rather than a capture that starts
/// and silently observes nothing, which would leave the user believing runs were being recorded.
/// </summary>
public sealed class CaptureControllerTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly SettingsRepository _settings;
    private readonly List<FakeCaptureSource> _sources = new();
    private readonly RecordingStatusListener _status = new();
    private readonly RecordingLifecycleListener _lifecycle = new();
    private readonly FakeGameProcessProvider _processes = new();
    private readonly FakeProcessTcpTable _tcp = new();
    private readonly FakeAdapterProvider _adapters = new();
    private FakeNpcapEnvironment _npcap = FakeNpcapEnvironment.Healthy();
    private FakeProfileStatusProvider _profile = new(ProfileStatus.Verified);
    private IDecodedMessageSink? _sink;
    private CollectorException? _startFailure;
    private string? _faultDuringStart;

    public CaptureControllerTests()
    {
        _settings = new SettingsRepository(_database.Database, _database.Clock);
        _settings.EnsureDefaults();
        _adapters.Add("wifi", "Wi-Fi", addresses: "192.168.31.77");
    }

    /// <summary>
    /// The most recently created source. The controller owns and disposes the source it was
    /// handed, so a restart legitimately gets a fresh one; the factory records them all.
    /// </summary>
    private FakeCaptureSource Source => _sources[^1];

    private int StartCount => _sources.Sum(source => source.StartCount);

    private int StopCount => _sources.Sum(source => source.StopCount);

    [Fact]
    public void ReportsUnavailable_WhenNpcapIsMissing()
    {
        _npcap = new FakeNpcapEnvironment();
        using var controller = Build();

        var snapshot = controller.Snapshot();

        Assert.Equal(CaptureControllerState.Unavailable, snapshot.State);
        Assert.Equal(CaptureState.Stopped, snapshot.ContractState);
        Assert.False(snapshot.Npcap.Installed);
        Assert.Equal("UNAVAILABLE", CaptureWire.LastErrorCode(snapshot));
    }

    [Fact]
    public void RefusesToStart_WithoutNpcap()
    {
        _npcap = new FakeNpcapEnvironment();
        WithGame();
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.NpcapMissing, error.Code);
        Assert.Equal(0, StartCount);
    }

    [Fact]
    public void RefusesToStart_WhenTheGameIsNotRunning()
    {
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.FfxivNotRunning, error.Code);
        Assert.True(error.Retryable);
        Assert.Equal(0, StartCount);
    }

    [Fact]
    public void RefusesToStart_WhenTheProfileIsNotVerified()
    {
        _profile = new FakeProfileStatusProvider(ProfileStatus.None);
        WithGame();
        using var controller = Build();

        // Fail-closed, exactly as contracts/error-codes.md requires: no profile means no
        // capture at all, not a capture that records nothing.
        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.ProfileUnsupported, error.Code);
        Assert.Equal(0, StartCount);
    }

    [Fact]
    public void StartsInDiagnosticsOnlyMode_WhenTheOperatorOptedIn()
    {
        _profile = new FakeProfileStatusProvider(ProfileStatus.None);
        _settings.SetSetting(CaptureController.AllowWithoutProfileSetting, "true");
        WithGame();
        using var controller = Build();

        var snapshot = controller.Start();

        // The opt-in exists for the live-validation pass that has to happen before any
        // profile can be written. It buys packet counters and nothing else: the profile is
        // still NONE, so nothing is parsed and nothing is recorded.
        Assert.Equal(CaptureControllerState.Running, snapshot.State);
        Assert.Equal(ProfileStatus.None, snapshot.Profile.Status);
        Assert.Contains(snapshot.Warnings, warning => warning.Contains("fail-closed", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusesAnAdapterThatDoesNotExist()
    {
        WithGame();
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start("no-such-adapter"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("adapter_id", error.Field);
    }

    [Fact]
    public void RefusesAnExplicitAdapterWithoutAnIpv4BindAddress()
    {
        _adapters.Add("no-ipv4", "No IPv4 adapter");
        WithGame();
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start("no-ipv4"));

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("adapter_id", error.Field);
        Assert.Contains("IPv4", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, StartCount);
    }

    [Fact]
    public void AStaleAdapterPreferenceYieldsToTheOneActuallyCarryingTheGame()
    {
        // The 加速器/VPN case. Honouring the remembered card binds capture to an address the
        // game no longer uses, and the pcap filter then drops every packet: a capture that
        // reports RUNNING and observes nothing for the whole session.
        WithGame();
        _adapters.Add("vpn", "VPN", addresses: "10.8.0.2");
        _settings.SetSetting(CaptureController.AdapterSetting, "\"vpn\"");
        using var controller = Build();

        var adapters = controller.RescanAdapters();
        var vpn = adapters.Single(adapter => adapter.Id == "vpn");
        var wifi = adapters.Single(adapter => adapter.Id == "wifi");

        Assert.True(vpn.PreferenceStale);
        Assert.False(vpn.Recommended);
        Assert.True(wifi.Recommended);
        Assert.False(wifi.PreferenceStale);
        Assert.True(CaptureWire.Adapter(vpn)["preference_stale"]!.GetValue<bool>());

        // The capture follows the traffic, not the remembered preference.
        Assert.Equal("wifi", controller.Start().AdapterId);
    }

    [Fact]
    public void ARememberedAdapterIsKeptWhileNoOtherAdapterCarriesTheGame()
    {
        WithGame(withTraffic: false);
        _adapters.Add("vpn", "VPN", addresses: "10.8.0.2");
        _settings.SetSetting(CaptureController.AdapterSetting, "\"vpn\"");
        using var controller = Build();

        var vpn = controller.RescanAdapters().Single(adapter => adapter.Id == "vpn");

        Assert.True(vpn.Recommended);
        Assert.False(vpn.PreferenceStale);
    }

    [Fact]
    public void BeforeTheFirstConnectionTheDefaultRouteCarriesTheGuess()
    {
        // At the title screen the game has no connection to follow, and that is exactly the
        // moment capture has to be listening already: the Oodle stream can only be decoded
        // from the connection's first packet.
        WithGame(withTraffic: false);
        _adapters.Add("eth", "Ethernet", hasDefaultRoute: true, addresses: "192.168.31.5");
        using var controller = Build();

        Assert.Equal("eth", controller.Start().AdapterId);
    }

    [Fact]
    public void RefusesToGuessAnAdapter_WhenTheGamesTrafficCannotBeLocated()
    {
        WithGame(withTraffic: false);
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.BadRequest, error.Code);
        Assert.Equal("adapter_id", error.Field);
    }

    [Fact]
    public void StartsCountsAndStops()
    {
        WithGame();
        using var controller = Build();

        var started = controller.Start();
        Assert.Equal(CaptureControllerState.Running, started.State);
        Assert.Equal(CaptureState.Running, started.ContractState);
        Assert.Equal("wifi", started.AdapterId);
        Assert.Equal(4321, started.Game.ProcessId);
        Assert.Equal(1, StartCount);
        Assert.Equal(new[] { "started" }, _lifecycle.Events);

        Source.PushOpcode(0x0142, payloadLength: 8);
        Source.PushOpcode(0x0142);
        Source.PushRaw(new byte[] { 1, 2, 3 });

        var running = WaitFor(controller, snapshot => snapshot.PacketsObserved == 3);
        Assert.Equal(2, running.MessagesDecoded);
        Assert.Equal(1, running.DecodeErrors);
        Assert.Equal(0, running.DroppedCount);
        Assert.Equal(1, running.ConnectionCount);

        var stopped = controller.Stop();
        Assert.Equal(CaptureControllerState.Idle, stopped.State);
        Assert.Equal(1, StopCount);
        Assert.Equal(new[] { "started", "stopped:UserStop" }, _lifecycle.Events);
    }

    [Fact]
    public void RefusesASecondStartWhileRunning()
    {
        WithGame();
        using var controller = Build();
        controller.Start();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.CaptureAlreadyRunning, error.Code);
        Assert.Equal(1, StartCount);
    }

    /// <summary>
    /// A card that vanished from the device list is a readiness wait, not a driver fault.
    ///
    /// Reporting it as ERR_NPCAP_MISSING buys a thirty-second back-off behind a message telling
    /// the user to reinstall Npcap. The card -- or a better one -- is usually back on the very
    /// next tick, and the point of the one-second poll is to be listening before the client
    /// opens its connection (review finding H-1).
    /// </summary>
    [Fact]
    public void AVanishedAdapterIsRetriedOnTheNextTickRatherThanBackedOffFor30Seconds()
    {
        WithGame();
        _startFailure = NpcapPacketReader.AdapterListChanged("wifi");
        _settings.SetSetting(CaptureController.FollowGameSetting, "true");
        using var controller = new CaptureController(Services() with
        {
            FollowRetryInterval = TimeSpan.FromMinutes(5),
        });

        // Counted as attempts, not successes: the source is refused before it starts.
        controller.Poll();
        Assert.Single(_sources);

        // A driver back-off would skip this tick entirely.
        controller.Poll();
        Assert.Equal(2, _sources.Count(source => source is not null));

        var snapshot = controller.Snapshot();
        Assert.Equal(ErrorCodes.BadRequest, snapshot.LastErrorCode);
        Assert.DoesNotContain("Npcap", snapshot.LastErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CaptureControllerState.Idle, snapshot.State);
    }

    /// <summary>
    /// Two overlapping ticks of the follow timer must not leave CAPTURE_ALREADY_RUNNING in
    /// last_error for the rest of a perfectly healthy session (review finding L-1).
    /// </summary>
    [Fact]
    public async Task OverlappingPollTicksNeitherRaceNorRecordAnAlreadyRunningError()
    {
        WithGame();
        _settings.SetSetting(CaptureController.FollowGameSetting, "true");
        using var controller = Build();

        // Four ticks at once is the shape of a pre-flight that outran the interval.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(controller.Poll)));

        Assert.Equal(1, StartCount);
        var snapshot = controller.Snapshot();
        Assert.Equal(CaptureControllerState.Running, snapshot.State);
        Assert.Null(snapshot.LastErrorCode);

        // And a poll issued while a start is already in force stays quiet.
        controller.Poll();
        Assert.Null(controller.Snapshot().LastErrorCode);
    }

    /// <summary>
    /// The state machine is told about a queue overflow while the run it happened to is still
    /// in flight. Reporting only at teardown leaves a duty whose exit marker was dropped
    /// sitting in ENTERED_DUTY with nothing recording that the sequence had a hole
    /// (review finding H-7).
    /// </summary>
    [Fact]
    public void QueueOverflowIsReportedWhileTheCaptureIsStillRunning()
    {
        WithGame();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _sink = new WaitingSink(entered, release);
        _settings.SetSetting(
            CaptureController.QueueCapacitySetting,
            DecodedMessageQueue.MinCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var controller = Build();
        try
        {
            controller.Start();
            Source.PushOpcode(1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            for (var i = 0; i < DecodedMessageQueue.MinCapacity + 50; i++) Source.PushOpcode(2);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_lifecycle.DroppedTotal == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);

            Assert.True(_lifecycle.DroppedTotal > 0, "an overflow inside a live session must be reported at once");
            Assert.Equal(CaptureControllerState.Running, controller.State);
            Assert.DoesNotContain("stopped:UserStop", _lifecycle.Events);
        }
        finally
        {
            release.Set();
        }
    }

    /// <summary>
    /// A closed game connection reaches the lifecycle listener, which is the only way
    /// DISCONNECTED can ever be produced by a live capture (review finding H-6).
    /// </summary>
    [Fact]
    public void AClosedGameConnectionIsForwardedToTheLifecycleListener()
    {
        WithGame();
        using var controller = Build();
        controller.Start();

        Source.PushConnectionClosed();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_lifecycle.Events.Contains("connection_lost") && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Contains("connection_lost", _lifecycle.Events);

        // The capture itself carries on: the client may open a new connection.
        Assert.Equal(CaptureControllerState.Running, controller.State);
        controller.Stop();
    }

    /// <summary>
    /// The game closing sends its FIN and leaves the OS connection table well before the
    /// one-second follow poll notices the process is gone. Forwarded blindly, an ordinary
    /// "quit while inside a duty" is recorded as DISCONNECTED instead of INTERRUPTED,
    /// contradicting docs/state-machine.md section 3.6 (review finding R-2).
    /// </summary>
    [Fact]
    public void AClosedConnectionIsNotForwardedOnceTheGameProcessHasGone()
    {
        WithGame();
        using var controller = Build();
        controller.Start();

        _processes.Clear();
        Source.PushConnectionClosed();

        // Long enough for the marker to travel the queue and be refused; the negative is what
        // matters, so it is given the same budget the positive case gets.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!_lifecycle.Events.Contains("connection_lost") && DateTime.UtcNow < deadline) Thread.Sleep(10);

        Assert.DoesNotContain("connection_lost", _lifecycle.Events);
        controller.Stop();
    }

    /// <summary>
    /// The connection loss must not be dispatched straight to the listener while the messages
    /// that same connection already delivered -- the DUTY_RESULT that ends the run properly --
    /// are still queued behind a busy parser: the run would be closed as DISCONNECTED first and
    /// the result then discarded as belonging to a finished run (review finding R-4).
    /// </summary>
    [Fact]
    public void AClosedConnectionIsDeliveredBehindTheMessagesStillInTheQueue()
    {
        WithGame();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _sink = new WaitingSink(entered, release);
        using var controller = Build();
        try
        {
            controller.Start();
            Source.PushOpcode(1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            // Queued behind the message the parser is still holding.
            Source.PushOpcode(2);
            Source.PushConnectionClosed();

            Thread.Sleep(200);
            Assert.DoesNotContain("connection_lost", _lifecycle.Events);
        }
        finally
        {
            release.Set();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_lifecycle.Events.Contains("connection_lost") && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Contains("connection_lost", _lifecycle.Events);
        controller.Stop();
    }

    /// <summary>
    /// A drop counted by a queue whose session was already torn down is never reported -- the
    /// flush task returns without clearing once the generation no longer matches -- so the
    /// count must be discarded at the next start, or it downgrades a run in flight to
    /// INTERRUPTED over a hole in somebody else's sequence (review finding R-5).
    /// </summary>
    [Fact]
    public void DropsLeftOverFromARetiredSessionDoNotReachTheNextOne()
    {
        WithGame();
        using var controller = Build();
        controller.Start();
        controller.Stop();

        // The in-flight callback that arrives after teardown. Nobody is listening any more, so
        // the count is neither reported nor cleared -- it simply stays.
        controller.NoteDroppedFromRetiredSession(7, generation: long.MaxValue);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (controller.PendingDroppedCount != 7 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert.Equal(7, controller.PendingDroppedCount);
        Assert.Equal(0, _lifecycle.DroppedTotal);

        controller.Start();

        Assert.Equal(0, controller.PendingDroppedCount);

        // And the new session is never told about a hole in somebody else's sequence.
        Thread.Sleep(200);
        Assert.Equal(0, _lifecycle.DroppedTotal);
        controller.Stop();
    }

    [Fact]
    public void RefusesToStopWhenNothingIsRunning()
    {
        using var controller = Build();

        Assert.Equal(ErrorCodes.CaptureNotRunning, Assert.Throws<CollectorException>(controller.Stop).Code);
    }

    [Fact]
    public void RemembersTheAdapterTheUserChose()
    {
        WithGame();
        using var controller = Build();

        controller.Start("wifi");
        controller.Stop();

        Assert.Equal("\"wifi\"", _settings.GetSetting(CaptureController.AdapterSetting));
    }

    [Fact]
    public void WritesAndClosesACaptureSessionRow()
    {
        WithGame();
        var sessions = new CaptureSessionRepository(_database.Database);
        using var controller = Build();

        var started = controller.Start();
        var open = sessions.Get(started.CaptureSessionId!);
        Assert.NotNull(open);
        Assert.Null(open!.EndedAtUtc);
        Assert.Equal("wifi", open.AdapterId);
        Assert.Equal(Region.Cn, open.Region);
        Assert.Equal("2024.06.18.0000.0000", open.GameBuild);

        controller.Stop();

        var closed = sessions.Get(started.CaptureSessionId!);
        Assert.NotNull(closed!.EndedAtUtc);
        Assert.Equal(CaptureEndReason.UserStop, closed.EndReason);
    }

    [Fact]
    public void AMonitorFaultEndsTheSessionAsAnError()
    {
        WithGame();
        using var controller = Build();
        var started = controller.Start();

        Source.Fault("模拟的监视器故障");

        var snapshot = WaitFor(controller, current => current.State == CaptureControllerState.Faulted);
        Assert.Equal(CaptureState.Failed, snapshot.ContractState);
        Assert.Equal(ErrorCodes.Internal, snapshot.LastErrorCode);
        Assert.Contains("stopped:Error", _lifecycle.Events);

        var sessions = new CaptureSessionRepository(_database.Database);
        Assert.Equal(CaptureEndReason.Error, sessions.Get(started.CaptureSessionId!)!.EndReason);
    }

    [Fact]
    public void ASinkFailureEndsTheSessionInsteadOfSilentlyLosingLaterMessages()
    {
        WithGame();
        _sink = new AlwaysThrowingSink();
        using var controller = Build();
        var started = controller.Start();

        Source.PushOpcode(0x0142);

        var snapshot = WaitFor(controller, current => current.State == CaptureControllerState.Faulted);
        Assert.Equal(CaptureState.Failed, snapshot.ContractState);
        Assert.Equal(ErrorCodes.Internal, snapshot.LastErrorCode);
        Assert.Contains("漏记", snapshot.LastErrorMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("stopped:Error", _lifecycle.Events);

        var sessions = new CaptureSessionRepository(_database.Database);
        Assert.Equal(CaptureEndReason.Error, sessions.Get(started.CaptureSessionId!)!.EndReason);
    }

    [Fact]
    public void CanRestartAfterAFault()
    {
        WithGame();
        using var controller = Build();
        controller.Start();
        Source.Fault("模拟的监视器故障");
        WaitFor(controller, current => current.State == CaptureControllerState.Faulted);

        Assert.Equal(CaptureControllerState.Running, controller.Start().State);
    }

    [Fact]
    public void PropagatesAStartFailureAndStaysIdle()
    {
        WithGame();
        _startFailure = new CollectorException(ErrorCodes.NpcapMissing, "无法打开网卡。");
        using var controller = Build();

        Assert.Equal(ErrorCodes.NpcapMissing, Assert.Throws<CollectorException>(() => controller.Start()).Code);
        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(ErrorCodes.NpcapMissing, controller.Snapshot().LastErrorCode);
        Assert.Equal(new[] { "started", "stopped:Error" }, _lifecycle.Events);

        Assert.Empty(new CaptureSessionRepository(_database.Database).ListOpen(transaction: null));
    }

    [Fact]
    public void AnImmediateFaultCannotBeOverwrittenByTheRunningTransition()
    {
        WithGame();
        _faultDuringStart = "模拟的启动回调故障";
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.Internal, error.Code);
        Assert.Contains("启动回调故障", error.Message, StringComparison.Ordinal);
        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(new[] { "started", "stopped:Error" }, _lifecycle.Events);
        Assert.Empty(new CaptureSessionRepository(_database.Database).ListOpen(transaction: null));
    }

    [Fact]
    public void RefusesVerifiedCapture_WhenTheSessionRowCannotBeWritten()
    {
        WithGame();
        _database.Database.RunInTransaction(transaction =>
        {
            using var drop = _database.Database.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = "DROP TABLE capture_sessions;";
            drop.ExecuteNonQuery();
        });
        using var controller = Build();

        var error = Assert.Throws<CollectorException>(() => controller.Start());

        Assert.Equal(ErrorCodes.Internal, error.Code);
        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(0, StartCount);
        Assert.Empty(_lifecycle.Events);
    }

    [Fact]
    public void FollowStartsCaptureWhenTheGameAppears()
    {
        _settings.SetSetting(CaptureController.FollowGameSetting, "true");
        using var controller = Build();

        controller.Poll();
        Assert.Equal(CaptureControllerState.Idle, controller.State);

        WithGame();
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(1, StartCount);
    }

    [Fact]
    public void FollowIsOnByDefault()
    {
        // Nothing written to capture.follow_game or the legacy capture.autostart: the poll
        // still starts capture, because the Oodle stream can only be read from the start of
        // a connection and nobody should have to remember to press a button before login.
        WithGame();
        using var controller = Build();

        Assert.True(controller.FollowGameEnabled);
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(1, StartCount);
    }

    [Fact]
    public void FollowBacksOffAfterARefusedAutoStart()
    {
        WithGame();
        _startFailure = new CollectorException(ErrorCodes.NpcapMissing, "无法打开网卡。");
        using var controller = new CaptureController(Services() with
        {
            FollowRetryInterval = TimeSpan.FromHours(1),
        });

        controller.Poll();
        controller.Poll();
        controller.Poll();

        // One attempt, not one per tick: the refusal is recorded and retried later.
        Assert.Single(_sources);
        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(ErrorCodes.NpcapMissing, controller.Snapshot().LastErrorCode);

        // A manual start is never held back by the poll's back-off.
        Assert.Throws<CollectorException>(() => controller.Start());
        Assert.Equal(2, _sources.Count);
    }

    [Fact]
    public void FollowRetriesOnTheNextTickWhenGameTrafficIdentifiesTheAdapter()
    {
        WithGame(withTraffic: false);
        using var controller = new CaptureController(Services() with
        {
            FollowInterval = TimeSpan.FromSeconds(1),
            FollowRetryInterval = TimeSpan.FromHours(1),
        });

        controller.Poll();
        Assert.Empty(_sources);
        Assert.Equal(ErrorCodes.BadRequest, controller.Snapshot().LastErrorCode);

        _tcp.With(4321, "192.168.31.77");
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(1, StartCount);
    }

    [Fact]
    public void FollowRetriesOnTheNextTickWhenTheProfileBecomesReadable()
    {
        WithGame();
        _profile.Current = _profile.Current with { Status = ProfileStatus.None };
        using var controller = new CaptureController(Services() with
        {
            FollowInterval = TimeSpan.FromSeconds(1),
            FollowRetryInterval = TimeSpan.FromHours(1),
        });

        controller.Poll();
        Assert.Empty(_sources);
        Assert.Equal(ErrorCodes.ProfileUnsupported, controller.Snapshot().LastErrorCode);

        _profile.Current = _profile.Current with { Status = ProfileStatus.Verified };
        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(1, StartCount);
    }

    [Fact]
    public void FollowRetriesOnceTheBackOffElapses()
    {
        WithGame();
        _startFailure = new CollectorException(ErrorCodes.NpcapMissing, "无法打开网卡。");
        using var controller = new CaptureController(Services() with
        {
            FollowRetryInterval = TimeSpan.Zero,
        });

        controller.Poll();
        controller.Poll();

        Assert.Equal(2, _sources.Count);
    }

    [Fact]
    public void FollowWaitsSilentlyWhileAnotherSessionHoldsTheCapture()
    {
        WithGame();
        var ownership = new CaptureOwnership();
        using var controller = new CaptureController(Services() with { Ownership = ownership });

        using (ownership.Acquire())
        {
            controller.Poll();
            controller.Poll();

            // Held by a validation session: no attempt, no error, no back-off.
            Assert.Equal(CaptureControllerState.Idle, controller.State);
            Assert.Empty(_sources);
            Assert.Null(controller.Snapshot().LastErrorCode);
        }

        // Released: the very next tick starts.
        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(1, StartCount);
    }

    [Fact]
    public void FollowDoesNothingWhenTheSettingIsOff()
    {
        _settings.SetSetting(CaptureController.FollowGameSetting, "false");
        WithGame();
        using var controller = Build();

        controller.Poll();

        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(0, StartCount);
    }

    [Fact]
    public void FollowStopsCaptureWhenTheGameExits()
    {
        _settings.SetSetting(CaptureController.FollowGameSetting, "true");
        WithGame();
        using var controller = Build();
        controller.Start();
        Assert.Equal(CaptureControllerState.Running, controller.State);

        // The game is gone from the next listing; nothing else changes.
        _processes.Clear();
        controller.Poll();

        Assert.Equal(CaptureControllerState.Idle, controller.State);
        Assert.Equal(1, StopCount);
        Assert.Contains("stopped:ProcessExit", _lifecycle.Events);
    }

    [Fact]
    public void PublishesEveryStatusChange()
    {
        WithGame();
        using var controller = Build();

        controller.Start();
        controller.Stop();

        var states = _status.Entries.Select(entry => entry.State).ToArray();
        Assert.Contains(CaptureControllerState.Starting, states);
        Assert.Contains(CaptureControllerState.Running, states);
        Assert.Contains(CaptureControllerState.Idle, states);
    }

    [Fact]
    public void ChoosesTheLibraryOodleModeWhenTheUserSuppliedTheirOwnDll()
    {
        WithGame();
        _settings.SetSetting(CaptureController.OodleLibrarySetting, "\"D:\\\\tools\\\\oo2net_9_win64.dll\"");
        using var controller = Build();

        var snapshot = controller.Start();

        // DEC-OODLE-01: a user-supplied library is preferred because it never touches the
        // game binary, and the disclosure flips with it.
        Assert.Equal(OodleMode.LibraryTcp, snapshot.Oodle);
        Assert.False(snapshot.ReadsGameExecutable);
        Assert.Equal(OodleMode.LibraryTcp, Source.LastOptions!.Oodle);
    }

    [Fact]
    public void DefaultsToTheModeThatReadsTheGameExecutable()
    {
        WithGame();
        using var controller = Build();

        var snapshot = controller.Start();

        Assert.Equal(OodleMode.FfxivTcp, snapshot.Oodle);
        Assert.True(snapshot.ReadsGameExecutable);
    }

    [Fact]
    public void RecoversFromAFaultOnceTheBackOffElapsesInsteadOfStayingFaultedForever()
    {
        // Poll must follow from Faulted as well as Idle, or one transient driver failure ends
        // capture for the lifetime of the process.
        WithGame();
        using var controller = new CaptureController(Services() with
        {
            FaultRetryInterval = TimeSpan.Zero,
        });
        controller.Start();
        Source.Fault("模拟的监视器故障");
        var faulted = WaitFor(controller, snapshot => snapshot.State == CaptureControllerState.Faulted);
        Assert.Equal(ErrorCodes.Internal, faulted.LastErrorCode);

        controller.Poll();

        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(2, _sources.Count);
        // Cleared only by a start that actually succeeded.
        Assert.Null(controller.Snapshot().LastErrorCode);
    }

    [Fact]
    public void ASessionThatFaultsImmediatelyMakesTheBackOffGrowInsteadOfLoopingForever()
    {
        // A monitor that accepts Start and faults half a second later repeats on every
        // back-off, and each attempt copies the game executable into the temp folder until the
        // system drive fills up. The back-off is therefore reset by a session that lasted
        // (HealthySessionMs), not by a start that merely succeeded.
        WithGame();
        long elapsedMs = 0;
        using var controller = new CaptureController(Services() with
        {
            FaultRetryInterval = TimeSpan.FromMilliseconds(40),
            ProcessUptime = () => TimeSpan.FromMilliseconds(Interlocked.Read(ref elapsedMs)),
        });

        controller.Start();
        Source.Fault("模拟的监视器故障");
        WaitFor(controller, snapshot => snapshot.State == CaptureControllerState.Faulted);

        // Advance a monotonic clock: a real 60 ms sleep can overshoot the second 80 ms
        // deadline on a busy machine, making correct backoff look broken.
        Interlocked.Add(ref elapsedMs, 60);
        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(2, _sources.Count);

        Source.Fault("模拟的监视器故障");
        WaitFor(controller, snapshot => snapshot.State == CaptureControllerState.Faulted);

        // Second wait is doubled, because the session that just started did not last.
        Interlocked.Add(ref elapsedMs, 60);
        controller.Poll();
        Assert.Equal(CaptureControllerState.Faulted, controller.State);
        Assert.Equal(2, _sources.Count);

        Interlocked.Add(ref elapsedMs, 20);
        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(3, _sources.Count);
    }

    [Fact]
    public void AFaultIsHeldThroughItsBackOffAndReleasedWhenTheGameExits()
    {
        WithGame();
        using var controller = new CaptureController(Services() with
        {
            FaultRetryInterval = TimeSpan.FromHours(1),
        });
        controller.Start();
        Source.Fault("模拟的监视器故障");
        WaitFor(controller, snapshot => snapshot.State == CaptureControllerState.Faulted);

        controller.Poll();

        // Still faulted, and still explaining itself: a retry storm against a broken driver
        // helps nobody, and the error code has to survive for the diagnostics page.
        Assert.Equal(CaptureControllerState.Faulted, controller.State);
        Assert.Equal(ErrorCodes.Internal, controller.Snapshot().LastErrorCode);
        Assert.Single(_sources);

        // The client went away: the next one deserves a capture, back-off or not.
        _processes.Clear();
        controller.Poll();
        Assert.Equal(CaptureControllerState.Idle, controller.State);

        WithGame();
        controller.Poll();
        Assert.Equal(CaptureControllerState.Running, controller.State);
        Assert.Equal(2, _sources.Count);
    }

    [Fact]
    public void HonoursTheConfiguredQueueCapacity()
    {
        WithGame();
        _settings.SetSetting(CaptureController.QueueCapacitySetting, "8192");
        using var controller = Build();

        Assert.Equal(8192, controller.Start().QueueCapacity);
    }

    [Fact]
    public void ClampsAnAbsurdQueueCapacity()
    {
        WithGame();
        _settings.SetSetting(CaptureController.QueueCapacitySetting, "1");
        using var controller = Build();

        Assert.Equal(DecodedMessageQueue.MinCapacity, controller.Start().QueueCapacity);
    }

    [Fact]
    public void SurvivesAMalformedSetting()
    {
        WithGame();
        _settings.SetSetting(CaptureController.QueueCapacitySetting, "{not json");
        _settings.SetSetting(CaptureController.AdapterSetting, "42");
        using var controller = Build();

        // A settings row is user-editable state. Nonsense in it degrades to the default, and
        // never stops capture from starting.
        Assert.Equal(DecodedMessageQueue.DefaultCapacity, controller.Start().QueueCapacity);
    }

    private void WithGame(bool withTraffic = true)
    {
        _processes.Add(
            GameProcessLocator.Dx11ProcessName,
            4321,
            DateTimeOffset.UnixEpoch,
            @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
        if (withTraffic)
        {
            _tcp.With(4321, "192.168.31.77");
        }
    }

    private CaptureController Build() => new(Services());

    private ICaptureSource CreateSource()
    {
        var source = new FakeCaptureSource
        {
            StartFailure = _startFailure,
            FaultDuringStart = _faultDuringStart,
        };
        _sources.Add(source);
        return source;
    }

    private CaptureServices Services() => new()
    {
        Npcap = new NpcapDetector(_npcap),
        Game = new GameProcessLocator(
            _processes,
            new FakeGameFileReader().With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000")),
        Adapters = new AdapterEnumerator(_adapters, _tcp),
        SourceFactory = CreateSource,
        Sink = _sink,
        Profile = _profile,
        Lifecycle = _lifecycle,
        StatusListener = _status,
        Clock = _database.Clock,
        Settings = _settings,
        Sessions = new CaptureSessionRepository(_database.Database),
        Database = _database.Database,
        CollectorVersion = "0.1.0",
        DetectionTtl = TimeSpan.Zero,
        EnableFollowTimer = false,
    };

    private sealed class AlwaysThrowingSink : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message) =>
            throw new InvalidOperationException("simulated live sink failure");
    }

    [Fact]
    public void StopTimeoutKeepsTheSessionAndLeaseUntilTheInFlightSinkReturns()
    {
        WithGame();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _sink = new WaitingSink(entered, release);
        var services = Services();
        using var controller = new CaptureController(services);
        var started = controller.Start();
        Source.PushOpcode(1);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        for (var i = 0; i < 10; i++) Source.PushOpcode(2);
        try
        {
            Assert.Throws<CollectorException>(() => controller.Stop());
            Assert.Equal(CaptureControllerState.Faulted, controller.State);
            Assert.DoesNotContain("stopped:UserStop", _lifecycle.Events);
            Assert.Null(new CaptureSessionRepository(_database.Database).Get(started.CaptureSessionId!)!.EndedAtUtc);
            Assert.Throws<CollectorException>(() => services.Ownership.Acquire());
            Assert.Throws<CollectorException>(() => controller.Start());
        }
        finally { release.Set(); }
        controller.Stop();
        Assert.Contains("stopped:UserStop", _lifecycle.Events);
        Assert.NotNull(new CaptureSessionRepository(_database.Database).Get(started.CaptureSessionId!)!.EndedAtUtc);
        using var lease = services.Ownership.Acquire();
    }

    private sealed class WaitingSink(ManualResetEventSlim entered, ManualResetEventSlim release) : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message) { entered.Set(); release.Wait(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticStopTimeoutDoesNotReadStatusBehindTheStillRunningSink(bool sourceFault)
    {
        WithGame();
        var pipelineGate = new object();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _sink = new LockedWaitingSink(pipelineGate, entered, release);
        var services = Services() with
        {
            CandidateStatus = () =>
            {
                lock (pipelineGate) return (false, null, 0);
            },
        };
        using var controller = new CaptureController(services);
        controller.Start();
        Source.PushOpcode(1);
        Task poll = Task.CompletedTask;
        Task<Exception?>? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (sourceFault) Source.Fault("simulated monitor failure during a blocked sink");
            else
            {
                _processes.Clear();
                poll = Task.Run(controller.Poll);
            }

            Assert.True(SpinWait.SpinUntil(
                () => controller.State == CaptureControllerState.Faulted, TimeSpan.FromSeconds(5)));
            await poll.WaitAsync(TimeSpan.FromSeconds(1));

            // A fault task that reads the pipeline status while keeping the lifecycle gate
            // would prevent this retry from reaching its own bounded queue shutdown.
            stop = Task.Run<Exception?>(() => Record.Exception(() => controller.Stop()));
            var error = await stop.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsType<CollectorException>(error);
            Assert.True(services.Ownership.IsHeld);
            Assert.DoesNotContain("stopped:UserStop", _lifecycle.Events);
        }
        finally
        {
            release.Set();
            await poll.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        controller.Stop();
        Assert.False(services.Ownership.IsHeld);
    }

    [Fact]
    public async Task StartQueuedDuringDisposeCannotCreateAnotherCaptureSession()
    {
        WithGame();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var startAttempted = new ManualResetEventSlim();
        var services = Services() with { Lifecycle = new WaitingStopLifecycle(entered, release) };
        using var controller = new CaptureController(services);
        controller.Start();
        var dispose = Task.Run(controller.Dispose);
        Task<Exception?>? start = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            start = Task.Run<Exception?>(() =>
            {
                startAttempted.Set();
                return Record.Exception(() => controller.Start());
            });
            Assert.True(startAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(start.IsCompleted);
        }
        finally
        {
            release.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsType<ObjectDisposedException>(await start!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, StartCount);
        Assert.False(services.Ownership.IsHeld);
    }

    private sealed class LockedWaitingSink(
        object gate, ManualResetEventSlim entered, ManualResetEventSlim release) : IDecodedMessageSink
    {
        public void Accept(DecodedMessage message)
        {
            lock (gate) { entered.Set(); release.Wait(); }
        }
    }

    private sealed class WaitingStopLifecycle(
        ManualResetEventSlim entered, ManualResetEventSlim release) : ICaptureLifecycleListener
    {
        public void OnCaptureStarted(string captureSessionId) { }
        public void OnCaptureStopped(string captureSessionId, CaptureEndReason reason)
        {
            entered.Set();
            release.Wait();
        }
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

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        _database.Dispose();
    }
}
