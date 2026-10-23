using System.Net;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>Declared host facts and monotonic clocks exercise recovery without changing the host network.</summary>
public sealed class NetworkRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownLateAttachTriesAutomaticRecoveryBeforeAskingForRelogButPreservesManualScope(bool manual)
    {
        using var fixture = new Fixture();
        fixture.NewSourcePreexisting = 2;

        fixture.Start(manual ? "wifi" : null);

        var snapshot = fixture.Status;
        Assert.Equal(2, snapshot.PreexistingConnections);
        Assert.Equal(CaptureSilentReason.None, snapshot.SilentReason);
        Assert.Single(fixture.Sources);
        if (manual)
        {
            Assert.Equal(PathRecoverySnapshot.None, snapshot.PathRecovery);
            Assert.Contains(snapshot.Warnings, warning => warning.Key == CollectorTexts.CaptureWarningPreexisting().Key);
        }
        else
        {
            AssertRecovering(snapshot, 0);
            Assert.DoesNotContain(snapshot.Warnings, warning => warning.Key == CollectorTexts.CaptureWarningPreexisting().Key);
            Assert.DoesNotContain(snapshot.Warnings, warning => warning.Key == CollectorTexts.CaptureWarningPreexistingReconnected().Key);
        }
    }

    [Fact]
    public void AddedVirtualInterfaceIsFoundAfterAnEarlierIpcDecoded()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        original.Inner.PushOpcode(0x142);
        fixture.Adapters.Add("virtual", "10.24.0.2");

        fixture.Notify(100);
        fixture.Tick(2_099);
        Assert.Single(fixture.Sources);
        Assert.Equal(0, original.Inner.StopCount);
        fixture.Tick(2_100);

        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        Assert.Equal(1, original.Inner.StopCount);
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Tick(2_100);
        Assert.Equal(2, fixture.Sources.Count);
        Assert.Contains(fixture.Plan.Bindings, binding => binding.AdapterId == "virtual");
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Source.Inner.PushRaw(FakeCaptureSource.BuildIpcMessage(0, 8, 7));
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Source.Inner.PushOpcode(0x143);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingAnInterfaceOrChangingItsAddressesRebuildsTheEffectivePlan(bool remove)
    {
        using var fixture = new Fixture();
        fixture.Adapters.Add("virtual", "10.24.0.2", "10.24.0.3");
        fixture.Start();
        if (remove) fixture.Adapters.Remove("virtual");
        else fixture.Adapters.Replace("virtual", "10.24.0.4");

        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(2_100);

        Assert.Equal(2, fixture.Sources.Count);
        if (remove) Assert.DoesNotContain(fixture.Plan.Bindings, binding => binding.AdapterId == "virtual");
        else Assert.Equal(new[] { "10.24.0.4" }, Assert.Single(fixture.Plan.Bindings,
            binding => binding.AdapterId == "virtual").LocalAddresses.Select(address => address.ToString()));
    }

    [Fact]
    public void RemovingTheOldSelectionAutomaticallyFollowsTheNewUsableInterface()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Remove("wifi");
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Tcp.With(Fixture.FirstPid, "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(2_100);

        Assert.Equal(2, fixture.Sources.Count);
        Assert.Equal("virtual", fixture.Source.Inner.LastOptions!.AdapterId);
        Assert.Equal("virtual", Assert.Single(fixture.Plan.Bindings).AdapterId);
    }

    [Fact]
    public void SameInterfaceNodeSwitchAndUnrelatedNotificationsKeepTheReaderAndDecoder()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var source = fixture.Source;
        var session = source.Inner.LastOptions!.CaptureSessionId;
        source.Inner.PushOpcode(0x142);
        // A node switch can change remote endpoints and ports while keeping this local path.
        fixture.Tcp.With(Fixture.FirstPid, "192.0.2.10");
        for (var index = 0; index < 4; index++)
        {
            fixture.Notify(100 + index * 3_000);
            fixture.Tick(2_100 + index * 3_000);
        }
        fixture.Tick(40_000);

        Assert.Single(fixture.Sources);
        Assert.Equal(0, source.Inner.StopCount);
        Assert.Equal(session, fixture.Status.CaptureSessionId);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        source.Inner.PushOpcode(0x143);
        Assert.Equal(2, fixture.Status.MessagesDecoded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedLossOfEveryPathExhaustsRecoveryEvenAfterHistoricalAndBufferedOldIpc(bool downOnly)
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        original.Inner.PushOpcode(0x142);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        if (downOnly) fixture.Adapters.SetUp("wifi", false);
        else fixture.Adapters.Remove("wifi");

        fixture.Notify(100);
        fixture.Tick(2_100);

        AssertRecovering(fixture.Status, 1);
        Assert.Equal(CaptureSilentReason.None, fixture.Status.SilentReason);
        original.Inner.PushOpcode(0x143);
        AssertRecovering(fixture.Status, 1);
        fixture.Tick(17_100);
        AssertRecovering(fixture.Status, 2);
        original.Inner.PushOpcode(0x144);
        fixture.Tick(47_100);
        AssertRecovering(fixture.Status, 3);
        original.Inner.PushOpcode(0x145);
        fixture.Tick(107_099);
        AssertRecovering(fixture.Status, 3);
        fixture.Tick(107_100);

        Assert.Equal(new PathRecoverySnapshot(PathRecoveryState.Failed, 3, 0), fixture.Status.PathRecovery);
        original.Inner.PushOpcode(0x146);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Single(fixture.Sources);
        Assert.Equal(0, original.Inner.StopCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReturningTheOriginalIdentityAndAddressRebuildsAfterLossAndWaitsForNewReaderIpc(bool downOnly)
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        var originalSession = original.Inner.LastOptions!.CaptureSessionId;
        original.Inner.PushOpcode(0x142);
        if (downOnly) fixture.Adapters.SetUp("wifi", false);
        else fixture.Adapters.Remove("wifi");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(17_100);
        fixture.Tick(47_100);
        fixture.Tick(107_100);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);

        if (downOnly) fixture.Adapters.SetUp("wifi", true);
        else fixture.Adapters.Add("wifi", "192.0.2.10");
        fixture.Notify(108_000);
        fixture.Tick(110_000);

        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        Assert.Equal(1, original.Inner.StopCount);
        AssertRecovering(fixture.Status, 1);
        fixture.Tick(110_000);
        Assert.Equal(2, fixture.Sources.Count);
        Assert.NotEqual(originalSession, fixture.Status.CaptureSessionId);
        var binding = Assert.Single(fixture.Plan.Bindings);
        Assert.Equal("wifi", binding.AdapterId);
        Assert.Equal("192.0.2.10", Assert.Single(binding.LocalAddresses).ToString());
        AssertRecovering(fixture.Status, 1);
        original.Inner.PushOpcode(0x143);
        AssertRecovering(fixture.Status, 1);
        fixture.Source.Inner.PushOpcode(0x144);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IdleFailedRecoveryRearmsOnlyAfterConfirmedLossAndReturnAndRetainsGlobalThrottle(
        bool downOnly, bool failFactory)
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Source.Inner.PushOpcode(0x142);

        void SetAvailable(bool available)
        {
            if (downOnly) fixture.Adapters.SetUp("wifi", available);
            else if (available) fixture.Adapters.Add("wifi", "192.0.2.10");
            else fixture.Adapters.Remove("wifi");
        }

        SetAvailable(false);
        fixture.Notify(100);
        fixture.Tick(2_100);
        AssertRecovering(fixture.Status, 1);
        SetAvailable(true);
        fixture.FactoryFails = failFactory;
        fixture.NewSourceStartFails = !failFactory;
        fixture.Notify(3_000);
        fixture.Tick(5_000);
        fixture.Tick(5_000);
        for (var second = 6; second <= 240; second++) fixture.Tick(second * 1_000L);

        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        Assert.Equal(new PathRecoverySnapshot(PathRecoveryState.Failed, 3, 0), fixture.Status.PathRecovery);
        Assert.InRange(fixture.FactoryAttempts, 2, 4);
        var exhaustedFactories = fixture.FactoryAttempts;
        fixture.FactoryFails = false;
        fixture.NewSourceStartFails = false;

        // Repeated notifications over the same up interface cannot renew the episode.
        for (var index = 0; index < 3; index++)
        {
            fixture.Notify(241_000 + index * 3_000);
            fixture.Tick(243_000 + index * 3_000);
            Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
            Assert.Equal(exhaustedFactories, fixture.FactoryAttempts);
        }

        // An unavailable inventory is not evidence that the interface disappeared. Its
        // next successful sample of the same interface must remain the exhausted path.
        fixture.Adapters.Failure = true;
        fixture.Notify(250_000);
        fixture.Tick(252_000);
        fixture.Adapters.Failure = false;
        fixture.Notify(253_000);
        fixture.Tick(255_000);
        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(exhaustedFactories, fixture.FactoryAttempts);

        fixture.Tcp.Failure = new InvalidOperationException("synthetic unavailable TCP table");
        fixture.Notify(256_000);
        fixture.Tick(258_000);
        fixture.Tcp.Failure = null;
        fixture.Notify(259_000);
        fixture.Tick(261_000);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(exhaustedFactories, fixture.FactoryAttempts);

        // Only a successful low-frequency sample confirming loss, followed by a real
        // return, may rearm an Idle/FAILED controller with the identical ID and address.
        SetAvailable(false);
        fixture.Tick(275_999);
        fixture.Tick(276_000);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(exhaustedFactories, fixture.FactoryAttempts);
        SetAvailable(true);
        fixture.Notify(277_000);
        fixture.Tick(279_000);
        fixture.Tick(279_000);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        Assert.Equal(exhaustedFactories + 1, fixture.FactoryAttempts);
        AssertRecovering(fixture.Status, 1);
        Assert.Equal("wifi", Assert.Single(fixture.Plan.Bindings).AdapterId);
        fixture.Source.Inner.PushOpcode(0x143);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);

        // A second same-path return soon afterward retains the process-wide 30-second
        // replacement interval even though both loss and return are genuine changes.
        SetAvailable(false);
        fixture.Notify(280_000);
        fixture.Tick(282_000);
        SetAvailable(true);
        fixture.Notify(283_000);
        fixture.Tick(285_000);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        Assert.Equal(exhaustedFactories + 1, fixture.FactoryAttempts);
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Tick(308_999);
        Assert.Equal(exhaustedFactories + 1, fixture.FactoryAttempts);
        fixture.Tick(309_000);
        fixture.Tick(309_000);
        Assert.Equal(exhaustedFactories + 2, fixture.FactoryAttempts);
        AssertRecovering(fixture.Status, 1);
        fixture.Source.Inner.PushOpcode(0x144);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
    }

    [Fact]
    public void StormingNotificationsHaveAFiveSecondCeiling()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Add("virtual", "10.24.0.2");
        for (var second = 1; second <= 5; second++)
        {
            fixture.Notify(second * 1_000);
            Assert.Single(fixture.Sources);
            Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        }

        fixture.Notify(6_000);

        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        Assert.Equal(1, fixture.Source.Inner.StopCount);
        fixture.Tick(6_000);
        Assert.Equal(2, fixture.Sources.Count);
    }

    [Fact]
    public void MissingNotificationsStillDiscoverNewPathsAfterFifteenSeconds()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Tick(14_999);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        fixture.Tick(15_000);
        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        fixture.Tick(15_000);
        Assert.Equal(2, fixture.Sources.Count);
    }

    [Fact]
    public void ASecondRealChangeRemainsAutomaticButCannotReplaceWithinThirtySeconds()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Add("first", "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(2_100);
        fixture.Source.Inner.PushOpcode(0x142);
        fixture.Adapters.Add("second", "10.25.0.2");
        fixture.Notify(3_000);
        fixture.Tick(5_000);

        Assert.Equal(2, fixture.Sources.Count);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Tick(32_099);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        fixture.Tick(32_100);
        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        fixture.Tick(32_100);
        Assert.Equal(3, fixture.Sources.Count);
        Assert.Contains(fixture.Plan.Bindings, binding => binding.AdapterId == "second");
    }

    [Fact]
    public void OldReaderIpcCannotCancelANewPathWaitingForTheReplacementThrottle()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Add("first", "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(2_100);
        fixture.Source.Inner.PushOpcode(0x142);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);

        fixture.Adapters.Add("second", "10.25.0.2");
        fixture.Notify(3_000);
        fixture.Tick(5_000);
        fixture.Tick(6_000);
        var stillListening = fixture.Source;
        stillListening.Inner.PushOpcode(0x143);

        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        Assert.DoesNotContain(fixture.Plan.Bindings, binding => binding.AdapterId == "second");
        Assert.Equal(0, stillListening.Inner.StopCount);
        fixture.Tick(32_099);
        Assert.Equal(2, fixture.Sources.Count);
        fixture.Tick(32_100);
        fixture.Tick(32_100);

        Assert.Equal(3, fixture.Sources.Count);
        Assert.Equal(1, stillListening.Inner.StopCount);
        Assert.Contains(fixture.Plan.Bindings, binding => binding.AdapterId == "second");
        Assert.Equal(PathRecoveryState.Recovering, fixture.Status.PathRecovery.State);
        fixture.Source.Inner.PushOpcode(0x144);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
    }

    [Fact]
    public void SilentUnchangedPathGetsThreeAttemptsWithoutReaderChurnAndOnlyThenUserAction()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Source.Inner.IngressCounters = new CaptureIngressCounters(RawPackets: 900, UnconfirmedTuples: 4);
        fixture.Tick(61_000);
        AssertRecovering(fixture.Status, 1);
        Assert.Equal(CaptureSilentReason.NoStreamOwnership, fixture.Status.SilentReason);
        fixture.Tick(75_999);
        AssertRecovering(fixture.Status, 1);
        fixture.Tick(76_000);
        AssertRecovering(fixture.Status, 2);
        fixture.Tick(105_999);
        AssertRecovering(fixture.Status, 2);
        fixture.Tick(106_000);
        AssertRecovering(fixture.Status, 3);
        fixture.Tick(165_999);
        AssertRecovering(fixture.Status, 3);
        fixture.Tick(166_000);

        var failed = fixture.Status;
        Assert.Equal(new PathRecoverySnapshot(PathRecoveryState.Failed, 3, 0), failed.PathRecovery);
        Assert.NotNull(failed.Hint);
        Assert.NotEqual(CollectorTexts.CaptureHintPathRecovering(), failed.Hint);
        Assert.Equal(CaptureSilentReason.NoStreamOwnership, failed.SilentReason);
        Assert.Single(fixture.Sources);
        Assert.Equal(0, fixture.Source.Inner.StopCount);
        fixture.Source.Inner.PushRaw(FakeCaptureSource.BuildIpcMessage(0, 8, 8));
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        fixture.Source.Inner.PushOpcode(0x142);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        Assert.Null(fixture.Status.Hint);
    }

    [Fact]
    public void RepeatedNotificationsDoNotRenewFailureButANewEffectiveTopologyDoes()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.ExhaustSilentRecovery();
        for (var index = 0; index < 4; index++)
        {
            fixture.Notify(167_000 + index * 3_000);
            fixture.Tick(169_000 + index * 3_000);
            Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
            Assert.Equal(3, fixture.Status.PathRecovery.Attempts);
        }
        Assert.Single(fixture.Sources);
        fixture.Adapters.Add("new-path", "10.24.0.2");
        fixture.Notify(180_000);
        fixture.Tick(182_000);
        fixture.Tick(182_000);

        Assert.Equal(2, fixture.Sources.Count);
        AssertRecovering(fixture.Status, 1);
    }

    [Fact]
    public void RetryingOptionalNativeOpenFailuresRespectsBothReplacementThrottleAndAttemptBudget()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Source.OpenFailures = 1;
        fixture.NewSourceOpenFailures = 1;
        fixture.Source.Inner.IngressCounters = new CaptureIngressCounters(RawPackets: 900, UnconfirmedTuples: 4);
        fixture.Tick(61_000);
        fixture.Tick(61_000);
        Assert.Equal(2, fixture.Sources.Count);
        AssertRecovering(fixture.Status, 1);
        fixture.Tick(76_000);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        AssertRecovering(fixture.Status, 1);
        fixture.Tick(90_999);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        fixture.Tick(91_000);
        fixture.Tick(91_000);
        Assert.Equal(3, fixture.Sources.Count);
        AssertRecovering(fixture.Status, 2);
        fixture.Tick(120_999);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        fixture.Tick(121_000);
        fixture.Tick(121_000);
        Assert.Equal(4, fixture.Sources.Count);
        AssertRecovering(fixture.Status, 3);
        fixture.Tick(181_000);
        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        fixture.Notify(200_000);
        fixture.Tick(202_000);
        Assert.Equal(4, fixture.Sources.Count);
    }

    [Fact]
    public void AnExplicitSelectionNeverExpandsOrStartsAutomaticRecovery()
    {
        using var fixture = new Fixture();
        fixture.Start("wifi");
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Tcp.With(Fixture.FirstPid, "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(61_000);
        fixture.Tick(200_000);

        Assert.Single(fixture.Sources);
        Assert.Equal("wifi", Assert.Single(fixture.Plan.Bindings).AdapterId);
        Assert.False(fixture.Plan.Automatic);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        Assert.NotNull(fixture.Status.Hint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownAdapterOrTcpQueriesDoNotInventAnEmptyTopology(bool failTcp)
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Source.Inner.PushOpcode(0x142);
        if (failTcp) fixture.Tcp.Failure = new InvalidOperationException("synthetic unavailable TCP table");
        else fixture.Adapters.Failure = true;
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(15_000);
        fixture.Tick(30_000);
        fixture.Tick(61_000);
        fixture.Tick(200_000);

        Assert.Single(fixture.Sources);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        Assert.Equal(0, fixture.Source.Inner.StopCount);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
    }

    [Fact]
    public void SubscriptionFailureLeavesPeriodicDiscoveryAvailable()
    {
        using var fixture = new Fixture(subscriptionFails: true);
        fixture.Start();
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Tick(15_000);
        fixture.Tick(15_000);

        Assert.Equal(1, fixture.SubscriptionCount);
        Assert.Equal(2, fixture.Sources.Count);
        Assert.Contains(fixture.Plan.Bindings, binding => binding.AdapterId == "virtual");
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("select")]
    [InlineData("dispose")]
    [InlineData("exit")]
    [InlineData("same-pid-restart")]
    public async Task LateDiscoveryCannotMutateAStoppedDisposedOrReplacedGame(string mutation)
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Adapters.PauseNextRead();
        var polling = Task.Run(() => fixture.Tick(15_000));
        try
        {
            Assert.True(fixture.Adapters.Entered.Wait(TimeSpan.FromSeconds(5)), "discovery did not reach the synthetic boundary");
            if (mutation == "stop") fixture.Controller.Stop();
            else if (mutation == "dispose") fixture.Controller.Dispose();
            else if (mutation == "exit") fixture.Processes.Clear();
            else if (mutation == "same-pid-restart")
            {
                fixture.Processes.Clear();
                fixture.Processes.Add(GameProcessLocator.Dx11ProcessName, Fixture.FirstPid,
                    DateTimeOffset.UnixEpoch.AddMinutes(1), @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
            }
            else
            {
                fixture.Processes.Add(GameProcessLocator.Dx11ProcessName, Fixture.SecondPid,
                    DateTimeOffset.UnixEpoch.AddMinutes(1), Fixture.SecondExecutable);
                var choice = Assert.Single(fixture.Controller.RescanGame().Processes,
                    process => process.ProcessId == Fixture.SecondPid);
                fixture.Controller.SelectGameProcess(choice.ProcessId, choice.Token);
                fixture.Controller.Start();
            }
        }
        finally
        {
            fixture.Adapters.Release();
            await polling.WaitAsync(TimeSpan.FromSeconds(10));
        }

        if (mutation == "select")
        {
            Assert.Equal(2, fixture.Sources.Count);
            Assert.Equal(Fixture.SecondPid, fixture.Source.Inner.LastOptions!.ProcessId);
            Assert.Equal(0, fixture.Source.Inner.StopCount);
            Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        }
        else
        {
            Assert.Single(fixture.Sources);
            if (mutation is "exit" or "same-pid-restart") Assert.Equal(0, original.Inner.StopCount);
            else Assert.Equal(1, original.Inner.StopCount);
            if (mutation == "same-pid-restart")
            {
                // Only a fresh follow poll may end the old incarnation. The result that
                // queried its predecessor must never apply the predecessor's plan.
                fixture.Tick(15_000);
                Assert.Equal(1, original.Inner.StopCount);
                Assert.Single(fixture.Sources);
            }
        }
    }

    [Fact]
    public void SourceReleaseFailurePreventsReplacementUntilItsResourcesActuallyExit()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        original.ReleaseFails = true;
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        fixture.Tick(2_100);
        fixture.Tick(3_000);

        Assert.Single(fixture.Sources);
        Assert.Equal(CaptureControllerState.Faulted, fixture.Controller.State);
        Assert.True(original.IsRunning);
        Assert.True(original.DisposeAttempts > 0);
        original.ReleaseFails = false;
        fixture.Tick(40_000);
        fixture.Tick(80_000);
        Assert.Equal(1, original.Inner.StopCount);
        Assert.Equal(2, fixture.Sources.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureToCreateOrStartAReplacementCannotRecoverForever(bool failFactory)
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.FactoryFails = failFactory;
        fixture.NewSourceStartFails = !failFactory;
        fixture.Notify(100);
        fixture.Tick(2_100);
        for (var second = 3; second <= 240; second++) fixture.Tick(second * 1_000L);

        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(3, fixture.Status.PathRecovery.Attempts);
        Assert.InRange(fixture.FactoryAttempts, 2, 4);
        var attempts = fixture.FactoryAttempts;
        for (var second = 241; second <= 360; second++) fixture.Tick(second * 1_000L);
        Assert.Equal(attempts, fixture.FactoryAttempts);
    }

    [Fact]
    public void FinalOldReaderIpcDuringCommittedReplacementCannotRenewAFactoryFailureBudget()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var original = fixture.Source;
        original.OpenFailures = 1;
        original.Inner.IngressCounters = new CaptureIngressCounters(RawPackets: 900, UnconfirmedTuples: 4);
        var finalIpcCallbacks = 0;
        original.BeforeStop = () =>
        {
            finalIpcCallbacks++;
            original.Inner.PushOpcode(0x142);
        };
        fixture.FactoryFails = true;

        fixture.Tick(61_000);

        Assert.Equal(1, finalIpcCallbacks);
        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        AssertRecovering(fixture.Status, 1);
        for (var second = 62; second <= 240; second++) fixture.Tick(second * 1_000L);

        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(3, fixture.Status.PathRecovery.Attempts);
        Assert.InRange(fixture.FactoryAttempts, 2, 4);
        var attempts = fixture.FactoryAttempts;
        fixture.Tick(300_000);
        Assert.Equal(attempts, fixture.FactoryAttempts);
        Assert.Single(fixture.Sources);
    }

    [Fact]
    public void SamePidNewIncarnationBetweenReleaseAndReplacementStartsWithAnIndependentBudget()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var originalSession = fixture.Source.Inner.LastOptions!.CaptureSessionId;
        fixture.Adapters.Add("virtual", "10.24.0.2");
        fixture.Notify(100);
        fixture.Tick(2_100);
        Assert.Equal(CaptureControllerState.Idle, fixture.Controller.State);
        AssertRecovering(fixture.Status, 1);
        var restartedAt = DateTimeOffset.UnixEpoch.AddMinutes(1);
        fixture.Processes.Clear();
        fixture.Processes.Add(GameProcessLocator.Dx11ProcessName, Fixture.FirstPid,
            restartedAt, @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");

        fixture.Tick(2_100);

        Assert.Equal(2, fixture.Sources.Count);
        Assert.Equal(CaptureControllerState.Running, fixture.Controller.State);
        Assert.NotEqual(originalSession, fixture.Status.CaptureSessionId);
        Assert.Equal(restartedAt, fixture.Status.Game.StartedAtUtc);
        Assert.Equal(PathRecoverySnapshot.None, fixture.Status.PathRecovery);
        Assert.Contains(fixture.Plan.Bindings, binding => binding.AdapterId == "virtual");
    }

    [Fact]
    public void UnavailableNativeOpenFactsDoNotPreventBoundedRecoveryFailure()
    {
        using var fixture = new Fixture();
        fixture.Start();
        fixture.Source.PathStatusFails = true;
        fixture.ExhaustSilentRecovery();

        Assert.Equal(PathRecoveryState.Failed, fixture.Status.PathRecovery.State);
        Assert.Equal(3, fixture.Status.PathRecovery.Attempts);
        Assert.Single(fixture.Sources);
    }

    [Fact]
    public void DisposalUnsubscribesOnceAndLateNotificationsCannotStartCapture()
    {
        using var fixture = new Fixture();
        fixture.Start();
        var notify = Assert.IsType<Action>(fixture.NetworkChanged);
        fixture.Controller.Dispose();
        fixture.Controller.Dispose();
        fixture.Adapters.Add("virtual", "10.24.0.2");
        notify();
        fixture.Tick(100_000);

        Assert.Equal(1, fixture.SubscriptionCount);
        Assert.Equal(1, fixture.UnsubscriptionCount);
        Assert.Single(fixture.Sources);
        Assert.Equal(1, fixture.Source.Inner.StopCount);
    }

    private static void AssertRecovering(CaptureDiagnosticsSnapshot snapshot, int attempts)
    {
        Assert.Equal(PathRecoveryState.Recovering, snapshot.PathRecovery.State);
        Assert.Equal(attempts, snapshot.PathRecovery.Attempts);
        Assert.Equal(CollectorTexts.CaptureHintPathRecovering(), snapshot.Hint);
        Assert.DoesNotContain(snapshot.Warnings, warning => warning.Chinese.Contains("重新登录", StringComparison.Ordinal));
    }

    private sealed class Fixture : IDisposable
    {
        internal const int FirstPid = 4321, SecondPid = 9876;
        internal const string SecondExecutable = @"D:\SdoA\Other\game\ffxiv_dx11.exe";
        private readonly TestDatabase _database = new();
        private long _now;
        internal readonly MutableAdapters Adapters = new();
        internal readonly FakeProcessTcpTable Tcp = new();
        internal readonly FakeGameProcessProvider Processes = new();
        internal readonly List<SourceDouble> Sources = new();
        internal readonly CaptureController Controller;
        internal Action? NetworkChanged;
        internal int SubscriptionCount, UnsubscriptionCount, FactoryAttempts, NewSourceOpenFailures;
        internal int? NewSourcePreexisting = 0;
        internal bool FactoryFails, NewSourceStartFails;
        internal SourceDouble Source => Sources[^1];
        internal CapturePathPlan Plan => Assert.IsType<CapturePathPlan>(Source.Inner.LastOptions!.PathPlan);
        internal CaptureDiagnosticsSnapshot Status => Controller.Snapshot();

        internal Fixture(bool subscriptionFails = false)
        {
            var settings = new SettingsRepository(_database.Database, _database.Clock);
            settings.EnsureDefaults();
            Adapters.Add("wifi", "192.0.2.10");
            Tcp.With(FirstPid, "192.0.2.10").With(SecondPid, "192.0.2.10");
            Processes.Add(GameProcessLocator.Dx11ProcessName, FirstPid, DateTimeOffset.UnixEpoch,
                @"D:\SdoA\FFXIV\game\ffxiv_dx11.exe");
            Controller = new CaptureController(new CaptureServices
            {
                Npcap = new NpcapDetector(FakeNpcapEnvironment.Healthy()),
                Game = new GameProcessLocator(Processes, new FakeGameFileReader()
                    .With(@"D:\SdoA\FFXIV\game\ffxivgame.ver", "2024.06.18.0000.0000")
                    .With(@"D:\SdoA\Other\game\ffxivgame.ver", "2024.06.18.0000.0000")),
                Adapters = new AdapterEnumerator(Adapters, Tcp),
                // Keep diagnostics independent of the pausable discovery boundary.
                NetworkPaths = new NetworkPathInspector(new FakeAdapterProvider(), new EmptyConnections()),
                SubscribeNetworkChanges = callback =>
                {
                    SubscriptionCount++;
                    if (subscriptionFails) throw new InvalidOperationException("synthetic subscription unavailable");
                    NetworkChanged = callback;
                    return new CallbackSubscription(() => UnsubscriptionCount++);
                },
                SourceFactory = () =>
                {
                    FactoryAttempts++;
                    if (FactoryFails) throw new InvalidOperationException("synthetic source factory unavailable");
                    var source = new SourceDouble { OpenFailures = NewSourceOpenFailures };
                    source.Inner.PreexistingTcpConnections = NewSourcePreexisting;
                    if (NewSourceStartFails) source.Inner.StartFailure = new CollectorException(
                        ErrorCodes.Internal, "synthetic source start unavailable");
                    Sources.Add(source);
                    return source;
                },
                Profile = new FakeProfileStatusProvider(ProfileStatus.Verified),
                Clock = _database.Clock,
                Uptime = () => TimeSpan.FromMilliseconds(Interlocked.Read(ref _now)),
                ProcessUptime = () => TimeSpan.FromMilliseconds(Interlocked.Read(ref _now)),
                Settings = settings,
                Sessions = new CaptureSessionRepository(_database.Database),
                Database = _database.Database,
                CollectorVersion = "0.1.0",
                DetectionTtl = TimeSpan.Zero,
                EnableFollowTimer = false,
            });
        }

        internal void Start(string? adapterId = null) => Controller.Start(adapterId);

        internal void Tick(long now)
        {
            Interlocked.Exchange(ref _now, now);
            Controller.Poll();
        }

        internal void Notify(long now)
        {
            Assert.IsType<Action>(NetworkChanged)();
            Tick(now);
        }

        internal void ExhaustSilentRecovery()
        {
            Source.Inner.IngressCounters = new CaptureIngressCounters(RawPackets: 900, UnconfirmedTuples: 4);
            Tick(61_000);
            Tick(76_000);
            Tick(106_000);
            Tick(166_000);
        }

        public void Dispose()
        {
            Adapters.Release();
            foreach (var source in Sources) source.ReleaseFails = false;
            Controller.Dispose();
            foreach (var source in Sources) source.Dispose();
            Adapters.Dispose();
            _database.Dispose();
        }
    }

    private sealed class SourceDouble : ICaptureSource
    {
        internal readonly FakeCaptureSource Inner = new() { PreexistingTcpConnections = 0 };
        internal bool ReleaseFails, PathStatusFails;
        internal int DisposeAttempts, OpenFailures;
        internal Action? BeforeStop;
        public string Kind => Inner.Kind;
        public bool IsRunning => Inner.IsRunning;
        public bool ReadsGameExecutable => false;
        public int? PreexistingTcpConnections => Inner.PreexistingTcpConnections;
        public CaptureIngressCounters IngressCounters => Inner.IngressCounters;
        public CapturePathStatus PathStatus => PathStatusFails
            ? throw new InvalidOperationException("synthetic native facts unavailable")
            : new(1, 1, OpenFailures, false, false);
        public void Start(CaptureStartOptions options, ICaptureSourceObserver observer) => Inner.Start(options, observer);
        public void Stop()
        {
            if (ReleaseFails) throw new InvalidOperationException("synthetic reader has not exited");
            Interlocked.Exchange(ref BeforeStop, null)?.Invoke();
            Inner.Stop();
        }
        public void Dispose()
        {
            DisposeAttempts++;
            if (ReleaseFails) throw new InvalidOperationException("synthetic reader still owns resources");
            Inner.Dispose();
        }
    }

    private sealed class MutableAdapters : IAdapterProvider, IDisposable
    {
        private readonly object _gate = new();
        private readonly List<AdapterInfo> _values = new();
        private readonly ManualResetEventSlim _release = new(true);
        private int _pauseNext;
        internal readonly ManualResetEventSlim Entered = new();
        internal bool Failure;

        internal void Add(string id, params string[] addresses)
        {
            lock (_gate) _values.Add(new(id, id, "synthetic driver", true, false,
                addresses.Select(IPAddress.Parse).ToArray()));
        }

        internal void Remove(string id)
        {
            lock (_gate) _values.RemoveAll(value => value.Id == id);
        }

        internal void Replace(string id, params string[] addresses)
        {
            lock (_gate)
            {
                var index = _values.FindIndex(value => value.Id == id);
                _values[index] = _values[index] with { IPv4Addresses = addresses.Select(IPAddress.Parse).ToArray() };
            }
        }

        internal void SetUp(string id, bool isUp)
        {
            lock (_gate)
            {
                var index = _values.FindIndex(value => value.Id == id);
                _values[index] = _values[index] with { IsUp = isUp };
            }
        }

        internal void PauseNextRead()
        {
            Entered.Reset();
            _release.Reset();
            Interlocked.Exchange(ref _pauseNext, 1);
        }

        internal void Release() => _release.Set();

        public IReadOnlyList<AdapterInfo> List()
        {
            AdapterInfo[] result;
            lock (_gate) result = _values.ToArray();
            if (Interlocked.Exchange(ref _pauseNext, 0) == 1)
            {
                Entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("synthetic discovery boundary was not released");
            }
            return Failure ? throw new InvalidOperationException("synthetic adapter query unavailable") : result;
        }

        public void Dispose() { Entered.Dispose(); _release.Dispose(); }
    }

    private sealed class EmptyConnections : IProcessNetworkConnectionProvider
    {
        public ProcessNetworkConnectionTables Read(int processId) => new(
            new(NetworkQueryStatus.Success, Array.Empty<ProcessNetworkConnection>()),
            new(NetworkQueryStatus.Success, Array.Empty<ProcessNetworkConnection>()));
    }

    private sealed class CallbackSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
