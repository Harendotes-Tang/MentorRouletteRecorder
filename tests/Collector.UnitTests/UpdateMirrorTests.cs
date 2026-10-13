using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Storage.Repositories;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

public sealed class UpdateMirrorTests : UpdateDownloadTestBase
{
    private static UpdateCheckClient Client(Transport transport, TimeSpan? timeout = null) =>
        new(transport.Send, timeout, readEnvironment: _ => null);

    private static Uri Mirror(string file) => UpdateCheckClient.GitCodeAssetUri(Published, file);
    private static string Exe => "MentorRecorder-" + Published + "-setup.exe";
    private static UpdateTransportResponse Bytes(Uri uri, byte[] bytes) => new(200, uri, null, bytes.Length, new MemoryStream(bytes));
    private static string Release(string version, bool complete = true, string type = "attach") => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version,
        prerelease = false,
        assets = (complete ? new[] { UpdateCheckClient.AssetFileName, "MentorRecorder-" + version + "-setup.exe", "MentorRecorder-" + version + "-setup.exe.sha256" } : new[] { "MentorRecorder-" + version + "-setup.exe" })
            .Select(name => new { name, type, browser_download_url = "https://attacker.invalid/file" }).ToArray()
    });
    private static Transport MirrorInstaller(Transport transport, string? checksum = null) => transport
        .On(Mirror(Exe + ".sha256"), (uri, _) => Task.FromResult(Bytes(uri, Encoding.ASCII.GetBytes(checksum ?? Sha(Installer)))))
        .On(Mirror(Exe), (uri, _) => Task.FromResult(Bytes(uri, Installer)));
    private static Transport MirrorMetadata(string listing, string metadata = "{\"version\":\"2.0.0\"}") => new Transport()
        .On(UpdateCheckClient.GitCodeReleasesUri(), (uri, _) => Task.FromResult(Bytes(uri, Encoding.UTF8.GetBytes(listing))))
        .On(Mirror(UpdateCheckClient.AssetFileName), (uri, _) => Task.FromResult(Bytes(uri, Encoding.UTF8.GetBytes(metadata))));

    [Fact]
    public async Task IncompleteNewestIsIgnoredAndMetadataVersionMustMatchTag()
    {
        var transport = MirrorMetadata("[" + Release("3.0.0", false) + "," + Release(Published) + "]");
        var result = await Client(transport).FetchAsync();
        Assert.Equal(Published, result.LatestVersion);
        Assert.Equal(UpdateSource.GitCode, result.Source);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri(), Mirror(UpdateCheckClient.AssetFileName) }, transport.Requests);
        var mismatchedTransport = MirrorMetadata("[" + Release(Published) + "]", "{\"version\":\"8.0.0\"}");
        var mismatched = await Client(mismatchedTransport).FetchAsync();
        Assert.Contains(Mirror(UpdateCheckClient.AssetFileName), mismatchedTransport.Requests);
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
        var transport = MirrorMetadata("[" + Release(Published) + "]")
            .On(UpdateCheckClient.MetadataUri(), async (uri, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Transport.Status(uri, 500);
            });
        var result = await Client(transport, TimeSpan.FromMilliseconds(400)).FetchAsync();
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
        using var database = new TestDatabase();
        var settings = new SettingsRepository(database.Database, database.Clock);
        settings.SetSetting(UpdateCheckService.LatestVersionSetting, "\"3.0.0\"");
        var transport = MirrorMetadata("[" + Release(Published) + "]");
        using var service = new UpdateCheckService(settings, Client(transport), Local, database.Clock, () => TimeSpan.Zero);
        var result = await service.CheckNowAsync();
        Assert.Equal(UpdateCheckOutcome.Ok, result.Outcome);
        Assert.Equal(UpdateSource.GitCode, result.Source);
        Assert.Contains(Mirror(UpdateCheckClient.AssetFileName), transport.Requests);
        Assert.Equal("3.0.0", service.Snapshot().LatestVersion);
        using var restarted = new UpdateCheckService(settings, Client(new Transport()), Local, database.Clock, () => TimeSpan.Zero);
        Assert.Equal("3.0.0", restarted.Snapshot().LatestVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BrokenPrimaryBodyIsDiscardedAndCompleteMirrorIsVerified(bool throws)
    {
        Stream broken = throws ? new FailingStream(Installer[..100]) : new MemoryStream(Installer[..100]);
        var transport = MirrorInstaller(new Transport().Publish(Published, Installer, body: broken));
        var service = Service(transport);
        service.Start();
        var result = await SettleAsync(service);
        Assert.Equal(UpdateDownloadState.Ready, result.State);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(InstallerPath()));
        Assert.Equal(Installer.Length, result.ReceivedBytes);
        Assert.False(File.Exists(PartPath()));
        Assert.Equal(new[] { ChecksumUri(Published), InstallerUri(Published), Mirror(Exe + ".sha256"), Mirror(Exe) }, transport.Requests);
    }

    [Fact]
    public async Task MirrorCannotReplaceAlreadyReadPrimaryHash()
    {
        var transport = MirrorInstaller(new Transport().Publish(Published, Installer, body: new FailingStream(Installer[..100])), checksum: new string('0', 64));
        var service = Service(transport);
        service.Start();
        AssertFailed(await SettleAsync(service), UpdateDownloadFailure.ChecksumMismatch);
        Assert.DoesNotContain(Mirror(Exe), transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task AValidPrimaryCheckDoesNotContactMirror()
    {
        var transport = new Transport().On(UpdateCheckClient.MetadataUri(), (uri, _) => Task.FromResult(Bytes(uri, Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\"}"))));
        Assert.Equal("1.0.0", (await Client(transport).FetchAsync()).LatestVersion);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task MirrorRedirectCannotChangeRepositoryOrVersion()
    {
        var start = Mirror(Exe);
        var target = new Uri(start.AbsoluteUri.Replace("/v2.0.0/", "/v1.0.0/", StringComparison.Ordinal));
        var transport = new Transport().On(start, (uri, _) => Task.FromResult(Transport.Redirect(uri, target)));
        using var result = await Client(transport).OpenInstallerAsync(Published, Installer.Length, UpdateSource.GitCode, default);
        Assert.Equal(UpdateCheckOutcome.HostRefused, result.Result.Outcome);
        Assert.Single(transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MirrorCdnRedirectMustBelongToThePinnedProject(bool correctProject)
    {
        var start = Mirror(Exe);
        var mirrorHost = new Uri(UpdateCheckClient.GitCodeInstallerUrl(Published)!).Host;
        var project = correctProject ? UpdateCheckClient.GitCodeProjectId : "11065765";
        var target = new Uri("https://file-cdn." + mirrorHost + "/" + project + "/releases/untagger_opaque/" + Exe + "?signature=abc");
        var transport = new Transport()
            .On(start, (uri, _) => Task.FromResult(Transport.Redirect(uri, target)))
            .On(target, (uri, _) => Task.FromResult(Bytes(uri, Installer)));
        using var result = await Client(transport).OpenInstallerAsync(Published, Installer.Length, UpdateSource.GitCode, default);
        Assert.Equal(correctProject ? UpdateCheckOutcome.Ok : UpdateCheckOutcome.HostRefused, result.Result.Outcome);
        Assert.Equal(correctProject ? 2 : 1, transport.Requests.Count);
    }

    [Fact]
    public async Task OverallBudgetIsNotRenewedForMirror()
    {
        var transport = MirrorInstaller(new Transport().Publish(Published, Installer, body: new StallingStream(Installer[..100])));
        var service = Service(transport, limits: Limits(total: TimeSpan.FromMilliseconds(100), idle: TimeSpan.FromSeconds(5)));
        service.Start();
        AssertFailed(await SettleAsync(service), UpdateDownloadFailure.Timeout);
        Assert.DoesNotContain(Mirror(Exe + ".sha256"), transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task DisablingAfterPrimaryFailureStopsBeforeMirror()
    {
        var disabled = false;
        var transport = MirrorInstaller(new Transport().Publish(Published, Installer))
            .On(InstallerUri(Published), (uri, _) =>
            {
                disabled = true;
                return Task.FromResult(Transport.Status(uri, 503));
            });
        var service = Service(transport, environment: _ => disabled ? "1" : null);
        service.Start();
        AssertFailed(await SettleAsync(service), UpdateDownloadFailure.Disabled);
        Assert.DoesNotContain(Mirror(Exe + ".sha256"), transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task MirrorSuccessReportsVersionBoundBrowserLinks()
    {
        using var database = new TestDatabase();
        var settings = new SettingsRepository(database.Database, database.Clock);
        using var service = new UpdateCheckService(settings, Client(MirrorMetadata("[" + Release(Published) + "]")), Local, database.Clock, () => TimeSpan.Zero);
        await service.CheckNowAsync();
        Assert.Equal(UpdateCheckClient.GitCodeReleaseUrl(Published), service.Snapshot().ReleaseUrl);
        Assert.Equal(UpdateCheckClient.GitCodeInstallerUrl(Published), service.Snapshot().InstallerUrl);
    }
}
