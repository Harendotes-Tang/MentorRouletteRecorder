using MentorRecorder.Collector.Update;
using Failure = MentorRecorder.Collector.Update.UpdateDownloadFailure;
using State = MentorRecorder.Collector.Update.UpdateDownloadState;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 下载并安装: every way a download can fail on the network side - the checksum, the two requests, the size cap and
/// the two time limits - and that none of them leaves a file behind.
/// </summary>
public sealed class UpdateDownloadFailureTests : UpdateDownloadTestBase
{
    // ------------------------------------------------------------------------------- checksum

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not Found</html>")]
    [InlineData("f258e2f34a883fa01d88b69fba3095ec80d9807c466a46d22027caa825917d8")]
    [InlineData("f258e2f34a883fa01d88b69fba3095ec80d9807c466a46d22027caa825917d8e0  x")]
    public async Task AMalformedChecksumStopsTheDownloadBeforeTheInstallerIsAsked(string checksum)
    {
        var transport = new Transport().Publish(Published, Installer, checksum: checksum);
        var service = Service(transport);

        service.Start();
        var failed = await SettleAsync(service);

        AssertFailed(failed, Failure.ChecksumUnavailable);
        Assert.Equal(Published, failed.Version);
        Assert.Equal(new[] { ChecksumUri(Published) }, transport.Requests);
        AssertNothingKept();
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task AChecksumThatIsNotPublishedIsUnavailable(int status)
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(ChecksumUri(Published), (uri, _) => Task.FromResult(Transport.Status(uri, status)));
        var service = Service(transport);

        service.Start();
        var failed = await SettleAsync(service);

        // 404 is the checksum that is not there; 410 is an answer of another kind, reported as such.
        AssertFailed(failed, status == 404 ? Failure.ChecksumUnavailable : Failure.HttpStatus);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task AChecksumLargerThanItsCapIsUnavailable()
    {
        var transport = new Transport().Publish(
            Published, Installer, checksum: Sha(Installer).PadRight(UpdateCheckClient.MaxChecksumBytes + 1, ' '));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.ChecksumUnavailable);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task AnInstallerThatDoesNotMatchItsChecksumIsDeleted()
    {
        var tampered = (byte[])Installer.Clone();
        tampered[^1] ^= 0xFF;
        var transport = new Transport().Publish(Published, tampered, checksum: Sha(Installer));
        var service = Service(transport);

        service.Start();
        var failed = await SettleAsync(service);

        AssertFailed(failed, Failure.ChecksumMismatch);
        Assert.Null(failed.ReceivedBytes);
        AssertNothingKept();
        Assert.Empty(Directory.EnumerateFileSystemEntries(Updates));
    }

    // --------------------------------------------------------------------------- the requests

    [Theory]
    [InlineData(404, Failure.NotFound)]
    [InlineData(403, Failure.RateLimited)]
    [InlineData(429, Failure.RateLimited)]
    [InlineData(500, Failure.HttpStatus)]
    [InlineData(204, Failure.HttpStatus)]
    public async Task AnInstallerRequestThatFailsIsReportedByItsCause(int status, Failure expected)
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(InstallerUri(Published), (uri, _) => Task.FromResult(Transport.Status(uri, status)));
        var service = Service(transport);

        service.Start();
        var failed = await SettleAsync(service);

        AssertFailed(failed, expected);
        Assert.Equal(Published, failed.Version);
        AssertNothingKept();
    }

    [Fact]
    public async Task ARedirectToAHostThatIsNotAllowedIsRefused()
    {
        var elsewhere = new Uri(
            "https://" + UpdateCheckClient.MetadataUri().Host + ".attacker.example/installer.exe", UriKind.Absolute);
        var transport = new Transport().Publish(Published, Installer);
        transport.On(InstallerUri(Published), (uri, _) => Task.FromResult(Transport.Redirect(uri, elsewhere)));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.HostRefused);
        Assert.DoesNotContain(elsewhere, transport.Requests);
        AssertNothingKept();
    }

    [Fact]
    public async Task ARedirectLoopIsRefused()
    {
        var transport = new Transport().Publish(Published, Installer);
        var installer = InstallerUri(Published);

        // Inside the release's own folder, the one place on the release host a download's redirect may lead.
        var loop = new Uri(installer, "loop");
        transport.On(installer, (uri, _) => Task.FromResult(Transport.Redirect(uri, loop)));
        transport.On(loop, (uri, _) => Task.FromResult(Transport.Redirect(uri, installer)));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.RedirectRefused);
        Assert.Equal(1 + UpdateCheckClient.MaxRedirects + 1, transport.Requests.Count);
        AssertNothingKept();
    }

    [Theory]
    [InlineData(HttpRequestError.ConnectionError, Failure.DnsOrConnect)]
    [InlineData(HttpRequestError.NameResolutionError, Failure.DnsOrConnect)]
    [InlineData(HttpRequestError.SecureConnectionError, Failure.TlsFailed)]
    public async Task AConnectionThatCannotBeMadeIsReportedByItsCause(HttpRequestError error, Failure expected)
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(
            InstallerUri(Published),
            (_, _) => Task.FromException<UpdateTransportResponse>(new HttpRequestException(error, "refused")));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), expected);
    }

    [Fact]
    public async Task AConnectionDroppedMidBodyIsATransportFailureAndNothingIsKept()
    {
        var transport = new Transport().Publish(Published, Installer, body: new FailingStream(Installer[..10_000]));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.TransportFailed);
        AssertNothingKept();
    }

    [Fact]
    public async Task ABodyShorterThanItsDeclaredLengthIsNotKept()
    {
        var transport = new Transport().Publish(
            Published, Installer, body: new MemoryStream(Installer[..1000]), checksum: Sha(Installer[..1000]));
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.TransportFailed);
        AssertNothingKept();
    }

    // --------------------------------------------------------------------------------- limits

    [Fact]
    public void TheDefaultLimitsAreTheDesignedOnes()
    {
        Assert.Equal(300L * 1024 * 1024, UpdateDownloadLimits.Default.MaxBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateDownloadLimits.Default.IdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(30), UpdateDownloadLimits.Default.TotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), UpdateDownloadLimits.Default.StartInterval);
    }

    [Fact]
    public async Task ADeclaredLengthOverTheCapIsRefusedBeforeAByteIsStored()
    {
        var body = new CountingStream(Installer);
        var transport = new Transport().Publish(
            Published, Installer, body: body, declaredLength: UpdateDownloadLimits.Default.MaxBytes + 1);
        var service = Service(transport);

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.TooLarge);
        Assert.Equal(0, body.BytesRead);
        AssertNothingKept();
    }

    [Fact]
    public async Task ABodyGrowingPastTheCapIsAbandonedTheMomentItPassesIt()
    {
        var body = new CountingStream(Installer);
        var transport = new Transport().Publish(Published, Installer, body: body, declareLength: false);
        var service = Service(transport, limits: Limits(maxBytes: 100_000));

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.TooLarge);
        Assert.InRange(body.BytesRead, 100_001, 100_000 + CountingStream.Chunk);
        AssertNothingKept();
    }

    [Fact]
    public async Task ABodyExactlyAtTheCapIsKept()
    {
        var transport = new Transport().Publish(Published, Installer);
        var service = Service(transport, limits: Limits(maxBytes: Installer.Length));

        service.Start();

        Assert.Equal(State.Ready, (await SettleAsync(service)).State);
    }

    [Fact]
    public async Task NoBytesForTheIdleTimeoutIsATimeoutAndThePartIsRemoved()
    {
        var body = new StallingStream(Installer[..1000]);
        var transport = new Transport().Publish(Published, Installer, body: body);
        var service = Service(transport, limits: Limits(idle: TimeSpan.FromMilliseconds(150)));

        service.Start();
        var failed = await SettleAsync(service);

        AssertFailed(failed, Failure.Timeout);
        Assert.True(body.Stalled.IsCompleted);
        AssertNothingKept();
    }

    [Fact]
    public async Task AnInstallerRequestThatNeverAnswersTimesOutOnTheIdleLimit()
    {
        var transport = new Transport().Publish(Published, Installer);
        transport.On(
            InstallerUri(Published),
            async (uri, token) =>
            {
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Transport.Status(uri, 200);
            });
        var service = Service(transport, limits: Limits(idle: TimeSpan.FromMilliseconds(150)));

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.Timeout);
    }

    [Fact]
    public async Task TheWholeDownloadRunsUnderOneOverallLimit()
    {
        // A byte every few milliseconds never trips the idle limit; the overall one stops it.
        var transport = new Transport().Publish(Published, Installer, body: new TricklingStream(), declareLength: false);
        var service = Service(
            transport, limits: Limits(idle: TimeSpan.FromSeconds(10), total: TimeSpan.FromMilliseconds(300)));

        service.Start();

        AssertFailed(await SettleAsync(service), Failure.Timeout);
        AssertNothingKept();
    }
}
