using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

public sealed class UpdateMirrorTests : UpdateDownloadTestBase
{
    private static UpdateCheckClient Client(UpdateTransport transport, TimeSpan? timeout = null) =>
        new(transport.Send, timeout, readEnvironment: _ => null);

    private static string Release(string version, bool complete = true, string type = "attach") => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        prerelease = false,
        assets = (complete ? new[] { UpdateCheckClient.AssetFileName, "MentorRecorder-" + version + "-setup.exe", "MentorRecorder-" + version + "-setup.exe.sha256" } : new[] { "MentorRecorder-" + version + "-setup.exe" })
            .Select(name => new { name, type, browser_download_url = "https://attacker.invalid/file" }).ToArray()
    });
    private static readonly string PublishedListing = "[" + Release(Published) + "]";
    private static UpdateTransport MirrorInstaller(UpdateTransport transport, string? checksum = null) => transport
        .On(MirrorUri(InstallerFile + ".sha256"), Encoding.ASCII.GetBytes(checksum ?? Sha(Installer)))
        .On(MirrorUri(InstallerFile), Installer);
    private static UpdateTransport MirrorMetadata(string listing, string metadata = "{\"version\":\"2.0.0\"}") => new UpdateTransport()
        .On(UpdateCheckClient.GitCodeReleasesUri(), listing)
        .On(MirrorUri(UpdateCheckClient.AssetFileName), metadata);
    private static UpdateDownloadLimits SlowLimits() => Limits() with { SlowWindow = TimeSpan.FromMilliseconds(80) };

    [Fact]
    public async Task IncompleteNewestIsIgnoredAndMetadataVersionMustMatchTag()
    {
        var transport = MirrorMetadata("[" + Release("3.0.0", false) + "," + Release(Published) + "]");
        var result = await Client(transport).FetchAsync();
        Assert.Equal(Published, result.LatestVersion);
        Assert.Equal(UpdateSource.GitCode, result.Source);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri(), MirrorUri(UpdateCheckClient.AssetFileName) }, transport.Requests);
        var mismatchedTransport = MirrorMetadata(PublishedListing, "{\"version\":\"8.0.0\"}");
        var mismatched = await Client(mismatchedTransport).FetchAsync();
        Assert.Contains(MirrorUri(UpdateCheckClient.AssetFileName), mismatchedTransport.Requests);
        Assert.Equal(UpdateCheckOutcome.NotFound, mismatched.Outcome);
        Assert.Null(mismatched.LatestVersion);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[{\"tag_name\":\"v2.0.0\",\"tag_name\":\"v3.0.0\",\"assets\":[]}]")]
    [InlineData("[{\"tag_name\":\"v2.0.0\",\"assets\":[{\"name\":\"BUILD-METADATA.json\",\"type\":\"attach\"},{\"name\":\"BUILD-METADATA.json\",\"type\":\"attach\"}]}]")]
    public async Task MalformedAmbiguousOrEmptyListingDoesNotInventAnUpdate(string listing)
    {
        var transport = MirrorMetadata(listing);
        var result = await Client(transport).FetchAsync();
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri() }, transport.Requests);
        Assert.Equal(UpdateCheckOutcome.NotFound, result.Outcome);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task SourceArchivesCannotStandInForAttachments()
    {
        var transport = MirrorMetadata("[" + Release(Published, type: "source") + "]");
        var result = await Client(transport).FetchAsync();
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri() }, transport.Requests);
        Assert.Equal(UpdateCheckOutcome.NotFound, result.Outcome);
        Assert.Null(result.LatestVersion);
    }

    [Fact]
    public async Task PrimaryTimeoutLeavesBudgetForMirrorAndCancellationDoesNot()
    {
        var transport = MirrorMetadata(PublishedListing)
            .On(UpdateCheckClient.MetadataUri(), UpdateTransport.NeverAnswers);
        // The mirror has what the primary leaves of the budget. 400 ms left it too little on a
        // loaded build machine; the primary never answers, so a larger budget only costs time.
        var result = await Client(transport, TimeSpan.FromSeconds(3)).FetchAsync();
        Assert.Equal(UpdateSource.GitCode, result.Source);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var previous = transport.Requests.Count;
        Assert.Equal(UpdateCheckOutcome.Cancelled, (await Client(transport).FetchAsync(cancelled.Token)).Outcome);
        Assert.Equal(previous, transport.Requests.Count);
    }

    [Fact]
    public async Task StaleMirrorCannotLowerPersistedKnownVersion()
    {
        Settings.SetSetting(UpdateCheckService.LatestVersionSetting, "\"3.0.0\"");
        var transport = MirrorMetadata(PublishedListing);
        using var service = CheckService(Client(transport));
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Ok, result.Outcome);
        Assert.Equal(UpdateSource.GitCode, result.Source);
        Assert.Contains(MirrorUri(UpdateCheckClient.AssetFileName), transport.Requests);
        Assert.Equal("3.0.0", service.Snapshot().LatestVersion);
        using var restarted = CheckService(Client(new UpdateTransport()));
        Assert.Equal("3.0.0", restarted.Snapshot().LatestVersion);
    }

    /// <summary>
    /// Audit 2026-10-20, F-1: where the version came from is kept with it. A player who could only reach the mirror is
    /// sent to the mirror's release page and installer after a restart too, until a check learns a version from GitHub.
    /// </summary>
    [Fact]
    public async Task AVersionLearnedFromTheMirrorKeepsTheMirrorLinksAcrossARestart()
    {
        var transport = MirrorMetadata(PublishedListing)
            .On(UpdateCheckClient.MetadataUri(), 503);
        using (var service = CheckService(Client(transport)))
        {
            Assert.Equal(UpdateSource.GitCode, (await service.CheckNowAsync()).Source);
        }

        using var restarted = CheckService(Client(new UpdateTransport()));
        var snapshot = restarted.Snapshot();
        Assert.Equal(Published, snapshot.LatestVersion);
        Assert.Equal(UpdateCheckClient.GitCodeReleaseUrl(Published), snapshot.ReleaseUrl);
        Assert.Equal(UpdateCheckClient.GitCodeInstallerUrl(Published), snapshot.InstallerUrl);

        var github = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), "{\"version\":\"" + Published + "\"}");
        using (var service = CheckService(Client(github)))
        {
            Assert.Equal(UpdateSource.GitHub, (await service.CheckNowAsync()).Source);
        }

        using var again = CheckService(Client(new UpdateTransport()));
        Assert.Equal(UpdateCheckClient.ReleaseUrl, again.Snapshot().ReleaseUrl);
        Assert.Equal(UpdateCheckClient.InstallerUrl(Published), again.Snapshot().InstallerUrl);
    }

    /// <summary>
    /// Audit 2026-10-20, F-2: switching 检查新版本并提示 off stops the check in flight. GitHub answering only afterwards must
    /// not send the mirror requests, and a check stopped that way is no attempt: nothing is stamped.
    /// </summary>
    [Fact]
    public async Task SwitchingTheCheckOffWhileGitHubStallsAsksNoMirrorAndStampsNothing()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = MirrorMetadata(PublishedListing)
            .On(UpdateCheckClient.MetadataUri(), async (uri, _) =>
            {
                asked.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return UpdateTransport.Status(uri, 503);
            });
        using var service = CheckService(Client(transport));

        service.Observe();
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.ApplySetting(false);
        release.SetResult();
        await service.Pending!.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { UpdateCheckClient.MetadataUri() }, transport.Requests);
        Assert.Null(service.Snapshot().LastCheckedAtUtc);
        Assert.Null(Settings.GetSetting(UpdateCheckService.LastCheckedSetting));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrokenPrimaryBodyIsDiscardedAndCompleteMirrorIsVerified(bool throws)
    {
        Stream broken = throws ? new FailingStream(Installer[..100]) : new MemoryStream(Installer[..100]);
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer, body: broken));
        var service = Service(transport);
        var result = await StartAndSettleAsync(service);
        Assert.Equal(UpdateDownloadState.Ready, result.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal(Installer.Length, result.ReceivedBytes);
        Assert.False(File.Exists(PartPath()));
        Assert.Equal(new[] { ChecksumUri(Published), InstallerUri(Published), MirrorUri(InstallerFile + ".sha256"), MirrorUri(InstallerFile) }, transport.Requests);
    }

    [Fact]
    public async Task MirrorCannotReplaceAlreadyReadPrimaryHash()
    {
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer, body: new FailingStream(Installer[..100])), checksum: new string('0', 64));
        var service = Service(transport);
        AssertFailed(await StartAndSettleAsync(service), UpdateDownloadFailure.ChecksumMismatch);
        Assert.DoesNotContain(MirrorUri(InstallerFile), transport.Requests);
        AssertNothingKept();
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("finish")]
    [InlineData("cancel")]
    [InlineData("mirror unavailable after accept")]
    [InlineData("primary ends during re-open")]
    [InlineData("decline, then GitHub fails")]
    public async Task SlowInstallerOfferAppliesOnlyToItsLiveAttempt(string choice)
    {
        const int prefix = 1024;
        var github = new PausedInstaller(prefix, failAfterResume: choice == "decline, then GitHub fails");
        var mirrorOpens = 0;
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer, body: github))
            .On(MirrorUri(InstallerFile), async (uri, token) =>
            {
                var reopen = Interlocked.Increment(ref mirrorOpens) == 2;
                if (reopen && choice == "primary ends during re-open")
                {
                    // Answers only once GitHub has finished and the re-open is called off.
                    await Task.Delay(Timeout.Infinite, token);
                }

                return reopen && choice == "mirror unavailable after accept"
                    ? UpdateTransport.Status(uri, 404)
                    : UpdateTransport.Body(uri, Installer);
            });
        var service = Service(transport, limits: SlowLimits());
        service.Start();
        await Poll.UntilAsync(() => service.Snapshot().MirrorOfferId is not null, "slow GitHub should prompt after sampling");

        var offer = service.Snapshot();
        Assert.Equal(prefix, offer.ReceivedBytes);
        Assert.Equal(UpdateDownloadState.Downloading, offer.State);
        Assert.Single(transport.Requests, request => request == MirrorUri(InstallerFile));
        Assert.Equal(offer, service.ResolveMirrorOffer(Guid.NewGuid().ToString(), useGitCode: true));
        if (choice is "accept" or "mirror unavailable after accept" or "primary ends during re-open")
        {
            Assert.Null(service.ResolveMirrorOffer(offer.MirrorOfferId!.ToUpperInvariant(), useGitCode: true).MirrorOfferId);
            if (choice != "accept")
            {
                await Poll.UntilAsync(() => Volatile.Read(ref mirrorOpens) == 2, "mirror re-open should be attempted");
                github.Resume();
            }
        }
        else if (choice == "cancel")
        {
            service.Cancel();
        }
        else
        {
            if (choice is "decline" or "decline, then GitHub fails")
                Assert.Null(service.ResolveMirrorOffer(offer.MirrorOfferId!, useGitCode: false).MirrorOfferId);
            github.Resume();
        }

        var final = await SettleAsync(service);
        Assert.Equal(final, service.ResolveMirrorOffer(offer.MirrorOfferId!, useGitCode: true));
        Assert.Null(final.MirrorOfferId);
        if (choice == "cancel")
        {
            Assert.Equal(UpdateDownloadState.Idle, final.State);
            AssertNothingKept();
        }
        else
        {
            Assert.Equal(UpdateDownloadState.Ready, final.State);
            Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        }
        Assert.Equal(choice is "accept" or "cancel" or "decline, then GitHub fails" ? prefix : Installer.Length,
            github.BytesRead);
        Assert.Equal(choice is "accept" or "mirror unavailable after accept" or "primary ends during re-open"
            or "decline, then GitHub fails" ? 2 : 1, mirrorOpens);

        // The log says what became of the offer, and "switched" only when GitCode served the file (audit 2026-11-20, F-2).
        // GitHub failing after the answer sends the download to GitCode by itself: no answer describes that, so
        // nothing is said rather than a stale "declined" on a file that came from GitCode (re-review 2026-11-20, R2-1).
        var expected = choice switch
        {
            "accept" => "switched",
            "decline" => "declined",
            "mirror unavailable after accept" => "not_switched_mirror_unavailable",
            "primary ends during re-open" => "not_switched_primary_ended",
            _ => null,
        };
        var finished = UpdateLines().Last(line => line["event"]!.GetValue<string>() == "update_download_finished");
        Assert.Equal(expected, finished["mirror_offer"]?.GetValue<string>());

        // The player is told too: the final state carries the same value, and nothing when the download was cancelled.
        Assert.Equal(expected, final.MirrorOutcome);
        var wire = UpdateWire.Download(final);
        Assert.Equal(expected, wire["mirror_outcome"]?.GetValue<string>());
        Assert.Equal(expected is not null, wire.ContainsKey("mirror_outcome"));
    }

    [Fact]
    public async Task PrimaryThatCatchesUpDuringAWorkingMirrorReopenIsReportedAsFaster()
    {
        var github = new PausedInstaller(1024, secondPause: Installer.Length - 1);
        var reopenStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishReopen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mirrorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mirrorOpens = 0;
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer, body: github))
            .On(MirrorUri(InstallerFile), async (uri, token) =>
            {
                if (Interlocked.Increment(ref mirrorOpens) == 1)
                    return UpdateTransport.Body(uri, Installer);
                reopenStarted.TrySetResult();
                await finishReopen.Task.WaitAsync(token);
                return new UpdateTransportResponse(200, uri, null, Installer.Length,
                    new MirrorBody(mirrorClosed));
            });
        var service = Service(transport, limits: SlowLimits());
        service.Start();
        await Poll.UntilAsync(() => service.Snapshot().MirrorOfferId is not null, "slow GitHub should prompt");
        service.ResolveMirrorOffer(service.Snapshot().MirrorOfferId!, useGitCode: true);
        await reopenStarted.Task.WaitAsync(Patience);
        github.Resume();
        await Poll.UntilAsync(() => service.Snapshot().ReceivedBytes == Installer.Length - 1,
            "GitHub should catch up before GitCode replies");
        finishReopen.TrySetResult();
        await mirrorClosed.Task.WaitAsync(Patience);
        github.Finish();

        var result = await SettleAsync(service);
        Assert.Equal(UpdateDownloadState.Ready, result.State);
        Assert.Equal("not_switched_faster_primary", result.MirrorOutcome);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal(2, mirrorOpens);
    }

    private sealed class MirrorBody(TaskCompletionSource closed) : MemoryStream(Installer, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) closed.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowInstallerDoesNotOfferAnUnavailableOrMismatchedMirror(bool mismatched)
    {
        var github = new PausedInstaller(1024);
        var transport = new UpdateTransport().Publish(Published, Installer, body: github);
        if (mismatched)
            MirrorInstaller(transport, new string('0', 64));

        var service = Service(transport, limits: SlowLimits());
        service.Start();
        await Poll.UntilAsync(() => transport.Requests.Contains(MirrorUri(InstallerFile + ".sha256")), "slow GitHub should probe once");
        github.Resume();

        var final = await SettleAsync(service);
        Assert.Equal(UpdateDownloadState.Ready, final.State);
        Assert.Null(final.MirrorOfferId);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.DoesNotContain(MirrorUri(InstallerFile), transport.Requests);
        Assert.Single(transport.Requests, request => request == MirrorUri(InstallerFile + ".sha256"));
    }

    [Theory]
    [InlineData(100, 100, 400, 1, true)]
    [InlineData(760, 100, 400, 0, false)]
    [InlineData(400, 100, 400, 4, false)]
    [InlineData(400, 100, 100, 0, false)]
    [InlineData(950, 0, 400, 4, true)]
    [InlineData(1000, 0, 400, 0, false)]
    public void MirrorMustBeatRemainingPrimaryTimeIncludingRestart(
        long received, double primaryRate, double mirrorRate, double setupSeconds, bool expected)
    {
        Assert.Equal(expected,
            UpdateDownloadTransfer.MirrorFinishesSooner(1000, received, primaryRate, mirrorRate, setupSeconds));
    }

    private sealed class PausedInstaller(int prefixLength, bool failAfterResume = false, int? secondPause = null) : AsyncOnlyStream
    {
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public int BytesRead => Volatile.Read(ref _position);
        public void Resume() => _resume.TrySetResult();
        public void Finish() => _finish.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var end = prefixLength;
            if (_position >= prefixLength)
            {
                await _resume.Task.WaitAsync(cancellationToken);
                if (failAfterResume) throw new IOException("GitHub dropped the connection");
                end = secondPause ?? Installer.Length;
                if (secondPause is { } pause && _position >= pause)
                {
                    await _finish.Task.WaitAsync(cancellationToken);
                    end = Installer.Length;
                }
            }
            var read = Math.Min(buffer.Length, end - _position);
            Installer.AsMemory(_position, read).CopyTo(buffer);
            Volatile.Write(ref _position, _position + read);
            return read;
        }
    }

    [Fact]
    public async Task MirrorRedirectCannotChangeRepositoryOrVersion()
    {
        var start = MirrorUri(InstallerFile);
        var target = new Uri(start.AbsoluteUri.Replace("/v2.0.0/", "/v1.0.0/", StringComparison.Ordinal));
        var transport = new UpdateTransport().OnRedirect(start, target);
        using var result = await Client(transport).OpenInstallerAsync(Published, Installer.Length, UpdateSource.GitCode, default);
        Assert.Equal(UpdateCheckOutcome.HostRefused, result.Result.Outcome);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MirrorCdnRedirectMustBelongToThePinnedProject(bool correctProject)
    {
        var start = MirrorUri(InstallerFile);
        var mirrorHost = new Uri(UpdateCheckClient.GitCodeInstallerUrl(Published)!).Host;
        var project = correctProject ? UpdateCheckClient.GitCodeProjectId : "11065765";
        var target = new Uri("https://file-cdn." + mirrorHost + "/" + project + "/releases/untagger_opaque/" + InstallerFile + "?signature=abc");
        var transport = new UpdateTransport()
            .OnRedirect(start, target)
            .On(target, Installer);
        using var result = await Client(transport).OpenInstallerAsync(Published, Installer.Length, UpdateSource.GitCode, default);
        Assert.Equal(correctProject ? UpdateCheckOutcome.Ok : UpdateCheckOutcome.HostRefused, result.Result.Outcome);
        Assert.Equal(correctProject ? 2 : 1, transport.Requests.Count);
    }

    [Fact]
    public async Task OverallBudgetIsNotRenewedForMirror()
    {
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer, body: new StallingStream(Installer[..100])));
        var service = Service(transport, limits: Limits(total: TimeSpan.FromMilliseconds(100), idle: TimeSpan.FromSeconds(5)));
        AssertFailed(await StartAndSettleAsync(service), UpdateDownloadFailure.Timeout);
        Assert.DoesNotContain(MirrorUri(InstallerFile + ".sha256"), transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task DisablingAfterPrimaryFailureStopsBeforeMirror()
    {
        var disabled = false;
        var transport = MirrorInstaller(new UpdateTransport().Publish(Published, Installer))
            .On(InstallerUri(Published), (uri, _) =>
            {
                disabled = true;
                return Task.FromResult(UpdateTransport.Status(uri, 503));
            });
        var service = Service(transport, environment: _ => disabled ? "1" : null);
        AssertFailed(await StartAndSettleAsync(service), UpdateDownloadFailure.Disabled);
        Assert.DoesNotContain(MirrorUri(InstallerFile + ".sha256"), transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task MirrorSuccessReportsVersionBoundBrowserLinks()
    {
        using var service = CheckService(Client(MirrorMetadata(PublishedListing)));
        await service.CheckNowAsync();
        Assert.Equal(UpdateCheckClient.GitCodeReleaseUrl(Published), service.Snapshot().ReleaseUrl);
        Assert.Equal(UpdateCheckClient.GitCodeInstallerUrl(Published), service.Snapshot().InstallerUrl);
    }
}
