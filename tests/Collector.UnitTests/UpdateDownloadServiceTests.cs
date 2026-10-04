using MentorRecorder.Collector.Update;
using Failure = MentorRecorder.Collector.Update.UpdateDownloadFailure;
using State = MentorRecorder.Collector.Update.UpdateDownloadState;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 下载并安装, the Collector's half: the state machine - the happy path, cancel, start again, nothing to download,
/// stopping - and what it writes to the log and says to the user.
/// </summary>
public sealed class UpdateDownloadServiceTests : UpdateDownloadTestBase
{
    // --------------------------------------------------------------------------- the happy path

    [Fact]
    public async Task ADownloadIsAnsweredAtOnceAndEndsReadyWithTheVerifiedInstaller()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport().Publish(Published, Installer, gate: gate);
        var service = Service(transport);

        var answer = service.Start();

        Assert.Equal(State.Downloading, answer.State);
        Assert.Equal(Published, answer.Version);
        Assert.Equal(0, answer.ReceivedBytes);
        gate.SetResult();
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Published, ready.Version);
        Assert.Equal(Installer.Length, ready.ReceivedBytes);
        Assert.Equal(Installer.Length, ready.TotalBytes);
        Assert.Equal(InstallerPath(), ready.FilePath);
        Assert.True(Path.IsPathFullyQualified(ready.FilePath!));
        Assert.Equal(Sha(Installer), ready.Sha256);
        Assert.Null(ready.Failure);
        Assert.Null(ready.Message);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.False(File.Exists(PartPath()));

        // The checksum first, then the installer; nothing else.
        Assert.Equal(new[] { ChecksumUri(Published), InstallerUri(Published) }, transport.Requests);
    }

    [Fact]
    public async Task AnInstallerWithoutADeclaredLengthIsKeptAndHasNoTotal()
    {
        var transport = new Transport().Publish(Published, Installer, declareLength: false);
        var service = Service(transport);

        service.Start();
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Null(ready.TotalBytes);
        Assert.Equal(Installer.Length, ready.ReceivedBytes);
    }

    [Fact]
    public async Task ReceivedBytesGrowWhileDownloading()
    {
        var body = new StallingStream(Installer[..50_000]);
        var transport = new Transport().Publish(Published, Installer, body: body);
        var service = Service(transport);

        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes == 50_000, "progress must be visible while downloading");

        var snapshot = service.Snapshot();
        Assert.Equal(State.Downloading, snapshot.State);
        Assert.Equal(Installer.Length, snapshot.TotalBytes);
        Assert.True(File.Exists(PartPath()));
        Assert.False(File.Exists(InstallerPath()));
        service.Cancel();
        await SettleAsync(service);
    }

    // ---------------------------------------------------------------------------------- cancel

    [Fact]
    public async Task CancelStopsARunningDownloadAndRemovesThePart()
    {
        var body = new StallingStream(Installer[..20_000]);
        var transport = new Transport().Publish(Published, Installer, body: body);
        var service = Service(transport);
        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes > 0, "the download must be under way");
        Assert.True(File.Exists(PartPath()));

        var answer = service.Cancel();

        Assert.Equal(State.Idle, answer.State);
        Assert.Null(answer.Version);
        Assert.Null(answer.ReceivedBytes);
        var settled = await SettleAsync(service);
        Assert.Equal(State.Idle, settled.State);
        Assert.True(body.Stalled.IsCompleted, "the read in flight must have been cancelled");
        AssertNothingKept();
    }

    /// <summary>
    /// A cancel that lands after the verified file got its final name but before READY was published: the answer
    /// was IDLE, so the installer must not stay on disk behind it.
    /// </summary>
    [Fact]
    public async Task ACancelBetweenTheRenameAndReadyLeavesNoInstaller()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.BeforeOutcome = () =>
        {
            Assert.True(File.Exists(InstallerPath()), "the seam must run after the rename");
            service.Cancel();
        };

        service.Start();
        var settled = await SettleAsync(service);

        Assert.Equal(State.Idle, settled.State);
        AssertNothingKept();
        Assert.Equal("CANCELLED", UpdateLines()[^1]["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelInAnyOtherStateChangesNothing()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        Assert.Equal(UpdateDownloadSnapshot.Idle, service.Cancel());

        service.Start();
        var ready = await SettleAsync(service);
        Assert.Equal(ready, service.Cancel());
        Assert.True(File.Exists(InstallerPath()));

        var failing = Service(new Transport(), enabled: false);
        var failed = failing.Start();
        Assert.Equal(State.Failed, failed.State);
        Assert.Equal(failed, failing.Cancel());
    }

    [Fact]
    public async Task ACancelledDownloadCanBeStartedAgain()
    {
        var first = new StallingStream(Installer[..20_000]);
        var transport = new Transport().Publish(Published, Installer, body: first);
        var service = Service(transport);
        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes > 0, "the download must be under way");
        service.Cancel();

        // Once the cancelled one has finished (NothingStartsWhileACancelledDownloadIsStillFinishing).
        await SettleAsync(service);
        transport.Publish(Published, Installer);
        Assert.Equal(State.Downloading, service.Start().State);
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
    }

    // ------------------------------------------------------------------ one download at a time

    /// <summary>
    /// A cancelled download can take a while to finish: a step that cannot be cancelled - flushing a large file, an
    /// antivirus scan of it - outlives any wait for it. Until it has finished nothing new starts, because the two
    /// would otherwise meet on the same partial file.
    /// </summary>
    [Fact]
    public async Task NothingStartsWhileACancelledDownloadIsStillFinishing()
    {
        var stuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport().Publish(Published, Installer);

        // This installer request ignores its cancellation and answers only when the test lets it.
        transport.On(InstallerUri(Published), async (uri, _) =>
        {
            await stuck.Task.ConfigureAwait(false);
            return new UpdateTransportResponse(200, uri, null, Installer.Length, new MemoryStream(Installer));
        });
        var service = Service(transport);
        service.Start();
        await EventuallyAsync(() => transport.Requests.Count == 2, "the installer must have been asked");
        var first = service.Running!;
        service.Cancel();

        var answer = service.Start();

        Assert.Equal(State.Idle, answer.State);
        Assert.Same(first, service.Running);
        Assert.False(first.IsCompleted);
        Assert.Equal(2, transport.Requests.Count);

        transport.Publish(Published, Installer);
        stuck.SetResult();
        await first.WaitAsync(Patience);
        Assert.False(File.Exists(PartPath()), "the cancelled download removes its own .part");

        Assert.Equal(State.Downloading, service.Start().State);
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
    }

    // ---------------------------------------------------------------------------------- pace

    /// <summary>
    /// However often it is asked, at most one new download starts per interval; within it the answer is the state as
    /// it stands and nothing is sent. A failed download can be retried once the interval has passed.
    /// </summary>
    [Fact]
    public async Task ANewDownloadStartsAtMostOncePerInterval()
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(InstallerUri(Published), (uri, _) => Task.FromResult(Transport.Status(uri, 503)));
        var service = Service(transport, limits: Limits(startInterval: TimeSpan.FromSeconds(10)));
        service.Start();
        var failed = await SettleAsync(service);
        AssertFailed(failed, Failure.HttpStatus);

        Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(failed, service.Start());
        Assert.Equal(2, transport.Requests.Count);

        Advance(TimeSpan.FromSeconds(1));
        transport.Publish(Published, Installer);
        Assert.Equal(State.Downloading, service.Start().State);
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        Assert.Equal(4, transport.Requests.Count);
    }

    /// <summary>Start and cancel in a loop - a client gone wrong - starts one download per interval, not one per call.</summary>
    [Fact]
    public async Task StartAndCancelInALoopStartsOneDownloadPerInterval()
    {
        var service = Service(
            new Transport().Publish(Published, Installer), limits: Limits(startInterval: TimeSpan.FromSeconds(10)));
        var launched = new HashSet<Task>();

        for (var round = 0; round < 5; round++)
        {
            service.Start();
            launched.Add(service.Running!);
            service.Cancel();
            await SettleAsync(service);
        }

        _ = Assert.Single(launched);
        Assert.Equal(State.Idle, service.Snapshot().State);

        Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(State.Downloading, service.Start().State);
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
    }

    /// <summary>An installer verified a moment ago is reported READY again at once, without hashing it once more.</summary>
    [Fact]
    public async Task AReadyInstallerVerifiedAMomentAgoIsNotHashedAgain()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, limits: Limits(startInterval: TimeSpan.FromSeconds(10)));
        service.Start();
        var ready = await SettleAsync(service);
        var download = service.Running;

        Assert.Equal(ready, service.Start());
        Assert.Same(download, service.Running);

        Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(State.Verifying, service.Start().State);
        Assert.NotSame(download, service.Running);
        Assert.Equal(ready, await SettleAsync(service));

        // The re-check verified it too: asked again at once, it is answered at once.
        var recheck = service.Running;
        Assert.Equal(ready, service.Start());
        Assert.Same(recheck, service.Running);
        Assert.Equal(2, transport.Requests.Count);
    }

    /// <summary>A READY whose file has gone is not reported again, even while no new download may start yet.</summary>
    [Fact]
    public async Task AReadyWhoseFileHasGoneIsNotReportedWhileNothingMayStart()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, limits: Limits(startInterval: TimeSpan.FromSeconds(10)));
        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        File.Delete(InstallerPath());

        Assert.Equal(UpdateDownloadSnapshot.Idle, service.Start());
        Assert.Equal(2, transport.Requests.Count);
    }

    // ------------------------------------------------------------------ the check switched off

    /// <summary>
    /// Switched off, nothing is offered (docs/privacy-boundary.md §8.4). A download in flight stops at once, its
    /// partial file goes, and the status says why rather than going quiet.
    /// </summary>
    [Fact]
    public async Task WithdrawStopsARunningDownloadKeepsNothingAndSaysWhy()
    {
        var body = new StallingStream(Installer[..20_000]);
        var service = Service(new Transport().Publish(Published, Installer, body: body));
        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes > 0, "the download must be under way");

        var answer = service.Withdraw();

        AssertWithdrawn(answer);
        Assert.Equal(answer, await SettleAsync(service));
        Assert.True(body.Stalled.IsCompleted, "the read in flight must have been cancelled");
        AssertNothingKept();
    }

    [Fact]
    public async Task WithdrawGivesUpAReadyInstaller()
    {
        var service = Service(new Transport().Publish(Published, Installer));
        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);

        AssertWithdrawn(service.Withdraw());

        AssertWithdrawn(service.Snapshot());
        AssertNothingKept();
    }

    /// <summary>Switched off while a READY installer is being checked again: the check stops and the installer goes.</summary>
    [Fact]
    public async Task WithdrawDuringARecheckGivesUpTheInstaller()
    {
        var service = Service(new Transport().Publish(Published, Installer));
        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        service.BeforeOutcome = () => service.Withdraw();

        Assert.Equal(State.Verifying, service.Start().State);

        AssertWithdrawn(await SettleAsync(service));
        AssertNothingKept();
    }

    [Fact]
    public void WithdrawChangesNothingWhenNoDownloadIsRunningOrReady()
    {
        // First: the services share one settings store, and the second one stores a newer version in it.
        var nothingNewer = Service(new Transport(), latest: null);
        var failed = nothingNewer.Start();
        AssertFailed(failed, Failure.NoUpdate);
        Assert.Equal(failed, nothingNewer.Withdraw());

        var idle = Service(new Transport().Publish(Published, Installer));
        Assert.Equal(UpdateDownloadSnapshot.Idle, idle.Withdraw());
    }

    private static void AssertWithdrawn(UpdateDownloadSnapshot snapshot)
    {
        AssertFailed(snapshot, Failure.Disabled);
        Assert.Equal(Published, snapshot.Version);
        Assert.Equal(UpdateDownloadMessages.Withdrawn, snapshot.Message);
    }

    // ------------------------------------------------------------------------------ restarting

    [Fact]
    public async Task StartWhileRunningReportsTheRunningDownloadAndStartsNothing()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new Transport().Publish(Published, Installer, gate: gate);
        var service = Service(transport);

        var first = service.Start();
        var running = service.Running;
        await EventuallyAsync(() => transport.Requests.Count == 2, "the installer must have been asked");
        var second = service.Start(reinstall: true);

        Assert.Equal(State.Downloading, first.State);
        Assert.Equal(State.Downloading, second.State);
        Assert.Same(running, service.Running);
        gate.SetResult();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task StartWhenReadyAndIntactDownloadsNothing()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.Start();
        var ready = await SettleAsync(service);

        var answer = service.Start();
        var settled = await SettleAsync(service);

        // The file is hashed again off the read loop before it is called ready a second time.
        Assert.Equal(State.Verifying, answer.State);
        Assert.Equal(Published, answer.Version);
        Assert.Equal(ready, settled);
        Assert.Equal(2, transport.Requests.Count);
    }

    [Fact]
    public async Task StartWhenReadyAndTamperedWithDownloadsAgain()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.Start();
        await SettleAsync(service);

        // Same length, other bytes: only the hash can tell.
        var tampered = (byte[])Installer.Clone();
        tampered[100] ^= 0xFF;
        await File.WriteAllBytesAsync(InstallerPath(), tampered);
        var answer = service.Start();
        var settled = await SettleAsync(service);

        Assert.Equal(State.Verifying, answer.State);
        Assert.Equal(State.Ready, settled.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task StartWhenReadyAndDeletedDownloadsAgain()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.Start();
        await SettleAsync(service);
        File.Delete(InstallerPath());

        var answer = service.Start();
        var settled = await SettleAsync(service);

        Assert.Equal(State.Downloading, answer.State);
        Assert.Equal(State.Ready, settled.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal(4, transport.Requests.Count);
    }

    [Fact]
    public async Task AFailedDownloadCanBeRetried()
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(InstallerUri(Published), (uri, _) => Task.FromResult(Transport.Status(uri, 503)));
        var service = Service(transport);
        service.Start();
        AssertFailed(await SettleAsync(service), Failure.HttpStatus);

        transport.Publish(Published, Installer);
        service.Start();

        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
    }

    // ------------------------------------------------------------------- nothing to download

    [Theory]
    [InlineData(null, false)]
    [InlineData(Local, false)]
    [InlineData("0.9.0", false)]
    [InlineData(null, true)]
    public void NothingIsSentWhenThereIsNoNewerVersion(string? latest, bool reinstall)
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, latest: latest);

        var answer = service.Start(reinstall);

        AssertFailed(answer, Failure.NoUpdate);
        Assert.Null(answer.Version);
        Assert.Null(service.Running);
        Assert.Empty(transport.Requests);
        Assert.False(Directory.Exists(Updates));
    }

    [Fact]
    public void TheSettingOffSendsNothing()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, enabled: false);

        var answer = service.Start(reinstall: true);

        AssertFailed(answer, Failure.Disabled);
        Assert.Contains("设置", answer.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public void TheKillSwitchSendsNothing()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(
            transport, environment: name => name == UpdateCheckClient.DisableVariable ? "1" : null);

        var answer = service.Start(reinstall: true);

        AssertFailed(answer, Failure.Disabled);
        Assert.DoesNotContain("设置 · 通用", answer.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Requests);
    }

    [Theory]
    [InlineData(Published)]
    [InlineData("2.1.0")]
    public async Task ReinstallDownloadsThePublishedVersionWhateverThisBuildIs(string local)
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, local: local);

        Assert.Equal(Failure.NoUpdate, service.Start().Failure);
        Assert.Equal(State.Downloading, service.Start(reinstall: true).State);
        var ready = await SettleAsync(service);

        Assert.Equal(State.Ready, ready.State);
        Assert.Equal(Published, ready.Version);
    }

    /// <summary>
    /// A FAILED answer keeps nothing usable (the contract's own words), so a verified installer that is no longer
    /// offered - here because the check was switched off - goes with the READY it belonged to.
    /// </summary>
    [Fact]
    public async Task ReadyIsGivenUpWhenThereIsNoLongerAnythingToOffer()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.Start();
        Assert.Equal(State.Ready, (await SettleAsync(service)).State);

        LastCheck.ApplySetting(false);
        var answer = service.Start();

        AssertFailed(answer, Failure.Disabled);
        Assert.False(File.Exists(InstallerPath()));
        Assert.Equal(2, transport.Requests.Count);
    }

    // ----------------------------------------------------------------------------------- stop

    [Fact]
    public async Task StoppingTheServiceCancelsARunningDownloadAndRemovesThePart()
    {
        var body = new StallingStream(Installer[..20_000]);
        var transport = new Transport().Publish(Published, Installer, body: body);
        var service = Service(transport);
        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes > 0, "the download must be under way");
        var running = service.Running!;

        service.Dispose();

        Assert.True(running.IsCompleted, "Dispose must wait for the download to unwind");
        Assert.True(body.Stalled.IsCompleted);
        AssertNothingKept();
        Assert.Equal(State.Idle, service.Snapshot().State);

        // Nothing starts after that.
        var requests = transport.Requests.Count;
        Assert.Equal(State.Idle, service.Start().State);
        Assert.Same(running, service.Running);
        Assert.Equal(requests, transport.Requests.Count);
    }

    // ------------------------------------------------------------------------------------ log

    [Fact]
    public async Task TheLogStatesStartAndEndWithoutAnyAddress()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport);
        service.Start();
        await SettleAsync(service);

        transport.On(InstallerUri(Published), (uri, _) => Task.FromResult(Transport.Status(uri, 404)));
        File.Delete(InstallerPath());
        service.Start();
        await SettleAsync(service);

        var lines = UpdateLines();
        Assert.Equal(
            new[] { "update_download_started", "update_download_finished", "update_download_started", "update_download_finished" },
            lines.Select(line => line["event"]!.GetValue<string>()));
        Assert.All(lines, line => Assert.Equal(Published, line["version"]!.GetValue<string>()));
        Assert.Equal("READY", lines[1]["outcome"]!.GetValue<string>());
        Assert.Equal(Installer.Length, lines[1]["bytes"]!.GetValue<long>());
        Assert.True(lines[1].ContainsKey("duration_ms"));
        Assert.Equal("NOT_FOUND", lines[3]["outcome"]!.GetValue<string>());

        var text = string.Join("\n", lines.Select(line => line.ToJsonString()));
        Assert.DoesNotContain("https", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UpdateCheckClient.Repository, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UpdateCheckClient.MetadataUri().Host, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Root, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACancelledDownloadIsLoggedAsCancelled()
    {
        var transport = new Transport().Publish(Published, Installer, body: new StallingStream(Installer[..1000]));
        var service = Service(transport);
        service.Start();
        await EventuallyAsync(() => service.Snapshot().ReceivedBytes > 0, "the download must be under way");

        service.Cancel();
        await SettleAsync(service);

        Assert.Equal("CANCELLED", UpdateLines()[^1]["outcome"]!.GetValue<string>());
    }

    // ------------------------------------------------------------------------------- messages

    /// <summary>
    /// Every failure has one formal sentence for the user: what happened and what they can do, with no internal
    /// name, token or address in it.
    /// </summary>
    [Fact]
    public void EveryFailureHasASentenceWithoutInternalNames()
    {
        var sentences = Enum.GetValues<Failure>().Select(UpdateDownloadMessages.For)
            .Append(UpdateDownloadMessages.SettingOff)
            .Append(UpdateDownloadMessages.Withdrawn)
            .ToArray();

        Assert.Equal(sentences.Length, sentences.Distinct(StringComparer.Ordinal).Count());
        Assert.All(sentences, sentence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(sentence));
            Assert.DoesNotMatch("[A-Za-z_]", sentence);
            Assert.EndsWith("。", sentence, StringComparison.Ordinal);
        });
    }

    // ----------------------------------------------------------------------------------- names

    [Fact]
    public void TheFileNameIsBuiltFromTheVersionAlone()
    {
        Assert.Equal("updates", UpdateDownloadFiles.FolderName);
        Assert.Equal("MentorRecorder-2.0.0-setup.exe", UpdateDownloadFiles.FileName("2.0.0"));
        Assert.Equal(InstallerPath(), new UpdateDownloadFiles(DataDirectory).InstallerPath("2.0.0"));
    }

    [Theory]
    [InlineData("2.0.0/../../evil")]
    [InlineData("..\\2.0.0")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("2.0.0 ")]
    [InlineData("")]
    public void AnythingButAPlainVersionNamesNoFile(string version) =>
        Assert.Throws<ArgumentException>(() => UpdateDownloadFiles.FileName(version));
}
