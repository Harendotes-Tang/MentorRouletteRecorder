using System.Diagnostics;
using System.Text;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The poll-driven update check: what <c>GetStatus</c> reads, when a request is actually sent, and
/// what survives a restart. A real settings repository over a throwaway database and a clock the
/// test moves, so nothing here waits on wall time and nothing reaches a network.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    private const string Local = "1.0.0";
    private const string Published = "2.0.0";

    private static readonly DateTimeOffset T0 = new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _database = new();
    private readonly TestClock _clock;
    private readonly SettingsRepository _settings;

    public UpdateCheckServiceTests()
    {
        _clock = _database.Clock;
        _clock.UtcNow = T0;
        _settings = new SettingsRepository(_database.Database, _clock);
    }

    public void Dispose() => _database.Dispose();

    private UpdateCheckService Service(
        Transport transport, string local = Local, Func<TimeSpan>? jitter = null, Func<string, string?>? environment = null) =>
        new(
            _settings,
            new UpdateCheckClient(transport.Send, TimeSpan.FromSeconds(5), environment ?? (_ => null)),
            local,
            _clock,
            jitter ?? (() => TimeSpan.Zero));

    /// <summary>Waits for the background check <see cref="UpdateCheckService.Observe"/> scheduled, if any.</summary>
    private static async Task SettleAsync(UpdateCheckService service)
    {
        if (service.Pending is { } pending)
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------------- scheduling

    [Fact]
    public async Task TheFirstObserveSchedulesOneCheckAndStampsIt()
    {
        var transport = new Transport();
        var service = Service(transport);

        var observed = service.Observe();
        await SettleAsync(service);
        var settled = service.Snapshot();

        Assert.True(observed.Enabled);
        Assert.Equal(UpdateCheckClient.ReleaseUrl, observed.ReleaseUrl);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(T0, settled.LastCheckedAtUtc);
        Assert.Equal(Published, settled.LatestVersion);
        Assert.Equal("OK", settled.LastOutcome);
        Assert.True(settled.UpdateAvailable);
    }

    [Fact]
    public async Task ASecondObserveInsideTheWindowSendsNothing()
    {
        var transport = new Transport();
        var service = Service(transport);

        service.Observe();
        await SettleAsync(service);
        _clock.UtcNow = T0.AddHours(23).AddMinutes(59);
        service.Observe();
        await SettleAsync(service);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(T0, service.Snapshot().LastCheckedAtUtc);
    }

    [Fact]
    public async Task ADayLaterOneMoreCheckIsSent()
    {
        var transport = new Transport();
        var service = Service(transport);

        service.Observe();
        await SettleAsync(service);
        _clock.UtcNow = T0 + UpdateCheckService.CheckInterval;
        service.Observe();
        await SettleAsync(service);

        Assert.Equal(2, transport.Calls);
        Assert.Equal(T0 + UpdateCheckService.CheckInterval, service.Snapshot().LastCheckedAtUtc);
    }

    /// <summary>A crash loop must not become a request per restart: inside the grace the persisted stamp stands.</summary>
    [Fact]
    public async Task ARestartInsideTheGraceSendsNothing()
    {
        var transport = new Transport();
        var first = Service(transport);
        first.Observe();
        await SettleAsync(first);

        _clock.UtcNow = T0.AddMinutes(30);
        var restarted = Service(transport);
        var snapshot = restarted.Observe();
        await SettleAsync(restarted);

        Assert.Equal(1, transport.Calls);
        Assert.Equal(T0, snapshot.LastCheckedAtUtc);
        Assert.Equal(Published, snapshot.LatestVersion);
        Assert.Equal("OK", snapshot.LastOutcome);
    }

    /// <summary>A user who restarts the software expects it to look: once per process start, past the grace.</summary>
    [Fact]
    public async Task ARestartPastTheGraceChecksOnceAndThenWaitsForTheDay()
    {
        var transport = new Transport();
        var first = Service(transport);
        first.Observe();
        await SettleAsync(first);

        _clock.UtcNow = T0.AddHours(2);
        var restarted = Service(transport);
        restarted.Observe();
        await SettleAsync(restarted);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(T0.AddHours(2), restarted.Snapshot().LastCheckedAtUtc);

        // The same process, hours later: the startup check was the one for today.
        _clock.UtcNow = T0.AddHours(10);
        restarted.Observe();
        await SettleAsync(restarted);
        Assert.Equal(2, transport.Calls);
    }

    // ------------------------------------------------------------------------- 立即检查

    [Fact]
    public async Task AManualCheckSendsWhateverIsDueAndReportsChecked()
    {
        var transport = new Transport();
        var service = Service(transport);
        service.Observe();
        await SettleAsync(service);
        _clock.UtcNow = T0.AddMinutes(5);

        var outcome = await service.CheckNowIfAllowedAsync();

        Assert.Equal(UpdateCheckRequestOutcome.Checked, outcome);
        Assert.Equal(2, transport.Calls);
        Assert.Equal(T0.AddMinutes(5), service.Snapshot().LastCheckedAtUtc);
    }

    [Fact]
    public async Task AManualCheckWithTheSettingOffSendsNothingAndSaysDisabled()
    {
        var transport = new Transport();
        var service = Service(transport);
        service.ApplySetting(false);

        Assert.Equal(UpdateCheckRequestOutcome.Disabled, await service.CheckNowIfAllowedAsync());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task AManualCheckUnderTheKillSwitchSendsNothingAndSaysBlocked()
    {
        var transport = new Transport();
        var service = Service(transport, environment: name => name == UpdateCheckClient.DisableVariable ? "1" : null);

        Assert.Equal(UpdateCheckRequestOutcome.Blocked, await service.CheckNowIfAllowedAsync());
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task AManualCheckWhileOneIsInFlightWaitsForItInsteadOfSendingTwice()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Gate = gate };
        var service = Service(transport);
        service.Observe();
        await transport.Invoked.WaitAsync(TimeSpan.FromSeconds(10));

        var manual = service.CheckNowIfAllowedAsync();
        Assert.False(manual.IsCompleted);
        gate.SetResult();

        Assert.Equal(UpdateCheckRequestOutcome.Checked, await manual.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, transport.Calls);
        Assert.Equal(Published, service.Snapshot().LatestVersion);
    }

    [Fact]
    public async Task AManualCheckWhileAnotherManualOneIsInFlightWaitsForThatOne()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Gate = gate };
        var service = Service(transport);

        // Nothing scheduled this one: the first 立即检查 claimed it itself. While claiming the check
        // and publishing the task to wait on were two steps, a claim could be taken with Pending
        // still holding the previous check - null here, the first of this process - and the second
        // call then reported 已检查 without waiting for anything, leaving its caller to read the
        // state from before the check (2026-09-21 full audit, finding 22).
        var first = service.CheckNowIfAllowedAsync();
        await transport.Invoked.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(service.Pending);

        var second = service.CheckNowIfAllowedAsync();
        Assert.False(second.IsCompleted);
        gate.SetResult();

        Assert.Equal(UpdateCheckRequestOutcome.Checked, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Published, service.Snapshot().LatestVersion);
        Assert.Equal(UpdateCheckRequestOutcome.Checked, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task AClockMovedBackIsDue()
    {
        var transport = new Transport();
        var service = Service(transport);
        service.Observe();
        await SettleAsync(service);

        // A stamp in the future can only be a clock that was wrong or has been moved back;
        // waiting a further day for it to come round would leave the check dead until then.
        _clock.UtcNow = T0.AddHours(-3);
        service.Observe();
        await SettleAsync(service);

        Assert.Equal(2, transport.Calls);
        Assert.Equal(T0.AddHours(-3), service.Snapshot().LastCheckedAtUtc);
    }

    [Fact]
    public async Task TheJitterPushesTheNextCheckPastTheInterval()
    {
        var transport = new Transport();
        var service = Service(transport, jitter: () => TimeSpan.FromHours(1));
        service.Observe();
        await SettleAsync(service);

        _clock.UtcNow = T0.AddHours(24).AddMinutes(30);
        service.Observe();
        await SettleAsync(service);
        Assert.Equal(1, transport.Calls);

        _clock.UtcNow = T0.AddHours(25).AddMinutes(30);
        service.Observe();
        await SettleAsync(service);
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public void TheJitterIsBoundedWhateverItIsGiven()
    {
        var transport = new Transport();

        Assert.Equal(TimeSpan.Zero, Service(transport, jitter: () => TimeSpan.FromHours(-5)).Jitter);
        Assert.Equal(TimeSpan.FromMinutes(30), Service(transport, jitter: () => TimeSpan.FromMinutes(30)).Jitter);
        Assert.Equal(TimeSpan.FromHours(2), UpdateCheckService.MaxJitter);
        Assert.Equal(TimeSpan.FromHours(24), UpdateCheckService.CheckInterval);

        foreach (var jitter in new[]
                 {
                     Service(transport, jitter: () => TimeSpan.FromHours(9)).Jitter,
                     Service(transport, jitter: () => TimeSpan.MaxValue).Jitter,
                     new UpdateCheckService(
                         _settings,
                         new UpdateCheckClient(transport.Send, TimeSpan.FromSeconds(5), _ => null),
                         Local,
                         _clock).Jitter,
                 })
        {
            Assert.InRange(jitter, TimeSpan.Zero, UpdateCheckService.MaxJitter);
            Assert.NotEqual(UpdateCheckService.MaxJitter, jitter);
        }
    }

    [Fact]
    public async Task OnlyOneCheckIsEverInFlight()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Gate = gate };
        var service = Service(transport);

        service.Observe();
        await transport.Invoked.WaitAsync(TimeSpan.FromSeconds(10));
        service.Observe();
        service.Observe();
        gate.SetResult();
        await SettleAsync(service);

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task ObserveAnswersFromTheCacheWhileACheckIsStillRunning()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport { Gate = gate };
        var service = Service(transport);

        var stopwatch = Stopwatch.StartNew();
        var pending = service.Observe();
        var second = service.Observe();
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), "Observe 不得等待正在进行的检查。");
        Assert.Null(pending.LastCheckedAtUtc);
        Assert.Null(second.LatestVersion);

        gate.SetResult();
        await SettleAsync(service);
        Assert.Equal(Published, service.Snapshot().LatestVersion);
    }

    // ---------------------------------------------------------------------------- failure

    [Fact]
    public async Task AFailedCheckStillStampsTheTimeAndKeepsThePreviousVersion()
    {
        var transport = new Transport();
        var service = Service(transport);
        service.Observe();
        await SettleAsync(service);

        transport.Status = 500;
        _clock.UtcNow = T0.AddHours(25);
        service.Observe();
        await SettleAsync(service);
        var snapshot = service.Snapshot();

        Assert.Equal(2, transport.Calls);
        Assert.Equal(T0.AddHours(25), snapshot.LastCheckedAtUtc);
        Assert.Equal("HTTP_STATUS", snapshot.LastOutcome);
        Assert.Equal(Published, snapshot.LatestVersion);
        Assert.True(snapshot.UpdateAvailable);
    }

    [Fact]
    public async Task APrivateRepositoryIsRecordedQuietly()
    {
        var transport = new Transport { Status = 404 };
        var service = Service(transport);

        service.Observe();
        await SettleAsync(service);
        var snapshot = service.Snapshot();

        Assert.Equal("NOT_FOUND", snapshot.LastOutcome);
        Assert.Null(snapshot.LatestVersion);
        Assert.False(snapshot.UpdateAvailable);
        Assert.Equal(T0, snapshot.LastCheckedAtUtc);
    }

    // ---------------------------------------------------------------------------- the switch

    [Fact]
    public async Task TheSettingOffSendsNothingAndReportsDisabled()
    {
        var transport = new Transport();
        var service = Service(transport);

        service.ApplySetting(false);
        var snapshot = service.Observe();
        await SettleAsync(service);

        Assert.False(snapshot.Enabled);
        Assert.Equal(0, transport.Calls);
        Assert.Null(snapshot.LastCheckedAtUtc);
        Assert.False(service.Diagnostics().Enabled);
    }

    [Fact]
    public async Task TheSettingBackOnResumesChecking()
    {
        var transport = new Transport();
        var service = Service(transport);
        service.ApplySetting(false);
        service.Observe();

        service.ApplySetting(true);
        service.Observe();
        await SettleAsync(service);

        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public void TheStoredSettingIsReadAtStartupAndDefaultsToOn()
    {
        var transport = new Transport();
        Assert.True(Service(transport).Snapshot().Enabled);

        _settings.SetSetting(UpdateCheckService.EnabledSetting, "false");
        Assert.False(Service(transport).Snapshot().Enabled);

        _settings.SetSetting(UpdateCheckService.EnabledSetting, "true");
        Assert.True(Service(transport).Snapshot().Enabled);
    }

    [Fact]
    public async Task TheKillSwitchOverridesTheSetting()
    {
        var transport = new Transport();
        var service = Service(transport, environment: name =>
            name == UpdateCheckClient.DisableVariable ? "1" : null);

        var snapshot = service.Observe();
        await SettleAsync(service);
        var settled = service.Snapshot();

        Assert.Equal(0, transport.Calls);
        Assert.True(snapshot.Enabled);
        Assert.Equal("DISABLED", settled.LastOutcome);
        Assert.Equal(T0, settled.LastCheckedAtUtc);
        Assert.True(service.Diagnostics().KillSwitch);
    }

    // --------------------------------------------------------------------------- comparison

    [Theory]
    [InlineData("2.0.0", true)]
    [InlineData("1.0.1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("0.9.9", false)]
    public async Task OnlyAStrictlyNewerVersionIsAnUpdate(string published, bool available)
    {
        var transport = new Transport { Version = published };
        var service = Service(transport);

        service.Observe();
        await SettleAsync(service);
        var snapshot = service.Snapshot();

        Assert.Equal(published, snapshot.LatestVersion);
        Assert.Equal(available, snapshot.UpdateAvailable);
    }

    [Fact]
    public async Task ARunningPrereleaseIsOlderThanTheReleaseItWasCutFrom()
    {
        var transport = new Transport { Version = "1.0.0" };
        var service = Service(transport, local: "1.0.0-rc.1");

        service.Observe();
        await SettleAsync(service);

        Assert.True(service.Snapshot().UpdateAvailable);
    }

    [Fact]
    public async Task AVersionThatDoesNotParseIsNeverAnUpdate()
    {
        var transport = new Transport { Version = "1.0.1" };
        var service = Service(transport, local: "not-a-version");

        service.Observe();
        await SettleAsync(service);

        Assert.False(service.Snapshot().UpdateAvailable);
    }

    // ------------------------------------------------------------------------- persistence

    [Fact]
    public async Task EveryStoredKeyIsNamespacedUnderUpdate()
    {
        Assert.Equal("update.check_enabled", UpdateCheckService.EnabledSetting);
        Assert.Equal("update.last_checked_at_utc", UpdateCheckService.LastCheckedSetting);
        Assert.Equal("update.latest_version", UpdateCheckService.LatestVersionSetting);
        Assert.Equal("update.last_outcome", UpdateCheckService.LastOutcomeSetting);

        var transport = new Transport();
        var service = Service(transport);
        service.Observe();
        await SettleAsync(service);

        Assert.Equal("\"" + Published + "\"", _settings.GetSetting(UpdateCheckService.LatestVersionSetting));
        Assert.Equal("\"OK\"", _settings.GetSetting(UpdateCheckService.LastOutcomeSetting));
        Assert.NotNull(_settings.GetSetting(UpdateCheckService.LastCheckedSetting));
    }

    [Fact]
    public async Task AnUnreadableStoredStateIsTreatedAsNoStateAtAll()
    {
        _settings.SetSetting(UpdateCheckService.LastCheckedSetting, "\"not a timestamp\"");
        _settings.SetSetting(UpdateCheckService.LatestVersionSetting, "17");
        _settings.SetSetting(UpdateCheckService.LastOutcomeSetting, "[]");

        var transport = new Transport();
        var service = Service(transport);
        var snapshot = service.Observe();

        Assert.Null(snapshot.LastCheckedAtUtc);
        Assert.Null(snapshot.LatestVersion);
        Assert.Null(snapshot.LastOutcome);
        await SettleAsync(service);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task TheTestSeamRunsOneCheckAndReturnsItsOutcome()
    {
        var transport = new Transport();
        var service = Service(transport);

        var result = await service.CheckNowAsync();

        Assert.Equal(UpdateCheckOutcome.Ok, result.Outcome);
        Assert.Equal(Published, result.LatestVersion);
        Assert.Equal(1, transport.Calls);
        Assert.Equal(T0, service.Snapshot().LastCheckedAtUtc);
    }

    // ------------------------------------------------------------------------ test doubles

    /// <summary>Answers the one address with a metadata document, counting and optionally holding calls.</summary>
    private sealed class Transport
    {
        private readonly TaskCompletionSource _invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public string? Version { get; set; } = Published;

        public int Status { get; set; } = 200;

        /// <summary>Held until completed, so a check can be observed while it is still running.</summary>
        public TaskCompletionSource? Gate { get; init; }

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes on the first call.</summary>
        public Task Invoked => _invoked.Task;

        public async Task<UpdateTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _invoked.TrySetResult();
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var body = Encoding.UTF8.GetBytes(
                Version is null ? "{}" : "{\"version\": \"" + Version + "\"}");
            return new UpdateTransportResponse(Status, uri, null, body.Length, new MemoryStream(body));
        }
    }
}
