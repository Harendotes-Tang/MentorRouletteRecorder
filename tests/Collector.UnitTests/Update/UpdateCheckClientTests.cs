using System.Net;
using System.Security.Authentication;
using System.Text;
using MentorRecorder.Collector.Update;
using static MentorRecorder.Collector.UnitTests.UpdateTransport;
using Outcome = MentorRecorder.Collector.Update.UpdateCheckOutcome;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The update-check download client (docs/privacy-boundary.md §8.4), driven entirely through its
/// injected transport: no test here opens a connection, names the production transport type or
/// writes a host name. Addresses are asserted against the client's own public constants, and
/// redirect targets are built from the address the client itself produced.
/// </summary>
public sealed class UpdateCheckClientTests
{
    private const string Metadata = "{\"version\": \"9.9.9\"}";

    private static UpdateCheckClient Client(UpdateTransport transport, TimeSpan? timeout = null) =>
        new(transport.Send, timeout ?? TimeSpan.FromSeconds(5), _ => null);

    /// <summary>A redirect target on the same host as the metadata address, with a query string.</summary>
    private static Uri SameHost(string path) =>
        new(UpdateCheckClient.MetadataUri().GetLeftPart(UriPartial.Authority) + path, UriKind.Absolute);

    /// <summary>The content host's domain, built from the client's own list rather than spelled here (NET-007).</summary>
    private static string ContentDomain => UpdateCheckClient.AllowedHosts.Single(name =>
        !string.Equals(name, UpdateCheckClient.MetadataUri().Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>A redirect target on the content host the release assets are actually served from.</summary>
    private static Uri ContentHost(string path) => new("https://objects." + ContentDomain + path, UriKind.Absolute);

    /// <summary>Sends the metadata request through <paramref name="hops"/> redirects on its own host; returns the hops.</summary>
    private static Uri[] RedirectChain(UpdateTransport transport, int hops)
    {
        var chain = Enumerable.Range(1, hops).Select(index => SameHost("/hop/" + index)).ToArray();
        transport.OnRedirect(UpdateCheckClient.MetadataUri(), chain[0]);
        for (var index = 0; index < chain.Length - 1; index++)
        {
            transport.OnRedirect(chain[index], chain[index + 1]);
        }

        return chain;
    }

    // ------------------------------------------------------------------------ addresses

    [Fact]
    public void TheOneAddressIsTheLatestReleaseAsset()
    {
        var uri = UpdateCheckClient.MetadataUri();

        Assert.Equal("Harendotes-Tang", UpdateCheckClient.Owner);
        Assert.Equal("MentorRouletteRecorder", UpdateCheckClient.Repository);
        Assert.Equal("BUILD-METADATA.json", UpdateCheckClient.AssetFileName);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
        Assert.True(uri.IsDefaultPort);
        Assert.Equal(string.Empty, uri.Query);
        Assert.Equal(string.Empty, uri.Fragment);
        Assert.Equal(string.Empty, uri.UserInfo);
        Assert.Contains(uri.Host, UpdateCheckClient.AllowedHosts, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            "/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository +
            "/releases/latest/download/" + UpdateCheckClient.AssetFileName,
            uri.AbsolutePath);

        // The page a human is sent to; the software never downloads or runs anything.
        var release = new Uri(UpdateCheckClient.ReleaseUrl, UriKind.Absolute);
        Assert.Equal(Uri.UriSchemeHttps, release.Scheme);
        Assert.Equal(uri.Host, release.Host);
        Assert.Equal(
            "/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository + "/releases/latest",
            release.AbsolutePath);
    }

    /// <summary>
    /// The installer of a published version, for the user's browser: the release asset of the release tagged
    /// with that version, on the same host and repository as everything else. This client never asks it.
    /// </summary>
    [Fact]
    public void TheInstallerAddressIsTheSetupAssetOfThatRelease()
    {
        var text = UpdateCheckClient.InstallerUrl("1.5.12");

        Assert.NotNull(text);
        var installer = new Uri(text, UriKind.Absolute);
        Assert.Equal(text, installer.AbsoluteUri);
        Assert.Equal(Uri.UriSchemeHttps, installer.Scheme);
        Assert.True(installer.IsDefaultPort);
        Assert.Equal(UpdateCheckClient.MetadataUri().Host, installer.Host);
        Assert.Equal(string.Empty, installer.UserInfo);
        Assert.Equal(string.Empty, installer.Query);
        Assert.Equal(string.Empty, installer.Fragment);
        Assert.Equal(
            "/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository +
            "/releases/download/v1.5.12/MentorRecorder-1.5.12-setup.exe",
            installer.AbsolutePath);
    }

    /// <summary>
    /// Only three runs of ASCII digits make an address: anything else a stored or published value could carry -
    /// a suffix, a path, a query, white space, other digits - gives none, neither the installer's nor its checksum's.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("1.5.1.0")]
    [InlineData("1.5.1-beta.2")]
    [InlineData("1.5.1/../../evil")]
    [InlineData("1.5.1?x=1")]
    [InlineData("1.5.1#x")]
    [InlineData(" 1.5.1")]
    [InlineData("1.5.1\n")]
    [InlineData("١.5.1")]
    [InlineData("v1.5.1")]
    public void AnAddressIsRefusedForAnythingButThreePlainNumbers(string? version)
    {
        Assert.Null(UpdateCheckClient.InstallerUrl(version));
        Assert.Null(UpdateCheckClient.ChecksumUrl(version));
    }

    [Fact]
    public void TheBudgetAndTheCapAreTheDeclaredOnes()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), UpdateCheckClient.DefaultTimeout);
        Assert.Equal(16 * 1024, UpdateCheckClient.MaxBodyBytes);
        Assert.Equal(3, UpdateCheckClient.MaxRedirects);
        Assert.Equal("MentorRecorder", UpdateCheckClient.UserAgent);
        Assert.Equal("MR_DISABLE_UPDATE_CHECK", UpdateCheckClient.DisableVariable);
        Assert.Equal(2, UpdateCheckClient.AllowedHosts.Count);
    }

    [Fact]
    public void ConstructingTheProductionClientSendsNothing()
    {
        var client = UpdateCheckClient.CreateDefault();

        Assert.NotNull(client);
    }

    // ---------------------------------------------------------------------- kill switch

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" yes ", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void TheKillSwitchIsOnForAnyValueButEmptyZeroOrFalse(string? value, bool disabled) =>
        Assert.Equal(disabled, UpdateCheckClient.IsDisabled(value));

    [Fact]
    public async Task TheKillSwitchStopsTheRequestBeforeAnythingIsSent()
    {
        var transport = new UpdateTransport();
        var client = new UpdateCheckClient(transport.Send, TimeSpan.FromSeconds(5), _ => "1");

        var result = await client.FetchAsync();

        Assert.Equal(Outcome.Disabled, result.Outcome);
        Assert.Null(result.LatestVersion);
        Assert.Empty(transport.Requests);
        Assert.True(client.IsDisabledNow);
    }

    // ------------------------------------------------------------------------- the fetch

    [Fact]
    public async Task APublishedVersionIsRead()
    {
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), Metadata);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri() }, transport.Requests);
    }

    [Fact]
    public async Task APrivateRepositoryAnswersNotFoundAndIsNotAnError()
    {
        var transport = new UpdateTransport();

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.NotFound, result.Outcome);
        Assert.Equal(404, result.StatusCode);
        Assert.Null(result.LatestVersion);
    }

    [Theory]
    [InlineData(403, Outcome.RateLimited)]
    [InlineData(429, Outcome.RateLimited)]
    [InlineData(401, Outcome.HttpStatus)]
    [InlineData(500, Outcome.HttpStatus)]
    [InlineData(503, Outcome.HttpStatus)]
    [InlineData(204, Outcome.HttpStatus)]
    public async Task EveryOtherStatusIsReported(int status, Outcome expected)
    {
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), status);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public async Task AMalformedDocumentIsRefusedWithoutItsContent()
    {
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), "{\"version\": \"PRIVATE\"}");

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Malformed, result.Outcome);
        Assert.Null(result.LatestVersion);
        Assert.Equal("INVALID:version", result.Detail);
    }

    // -------------------------------------------------------------------------- redirects

    [Fact]
    public async Task ARedirectIsFollowedOneHopAtATimeToTheContentHost()
    {
        var signed = ContentHost("/release/asset?token=abc&expires=1");
        var transport = new UpdateTransport()
            .OnRedirect(UpdateCheckClient.MetadataUri(), signed)
            .On(signed, Metadata);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.Equal("9.9.9", result.LatestVersion);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), signed }, transport.Requests);
    }

    [Fact]
    public async Task AtMostThreeHopsAreFollowed()
    {
        var transport = new UpdateTransport();
        RedirectChain(transport, UpdateCheckClient.MaxRedirects + 1);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.RedirectRefused, result.Outcome);
        Assert.Equal(UpdateCheckClient.MaxRedirects + 2, transport.Requests.Count);
    }

    [Fact]
    public async Task ThreeHopsStillReachTheDocument()
    {
        var transport = new UpdateTransport();
        var hops = RedirectChain(transport, UpdateCheckClient.MaxRedirects);
        transport.On(hops[^1], Metadata);

        Assert.Equal(Outcome.Ok, (await Client(transport).FetchAsync()).Outcome);
    }

    [Fact]
    public async Task ARedirectWithoutALocationIsRefused()
    {
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), 302);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.RedirectRefused, result.Outcome);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri() }, transport.Requests);
    }

    [Fact]
    public async Task ARedirectAnywhereElseIsRefused()
    {
        // Built from the client's own constants so no host name is written down here, and so a
        // look-alike is exactly the allowed name with something glued to it.
        var release = UpdateCheckClient.MetadataUri().Host;
        var content = ContentDomain;
        var locations = new[]
        {
            "http://" + release + "/owner/repo/asset",
            "https://" + release + ":8443/owner/repo/asset",
            "https://user:secret@" + release + "/owner/repo/asset",
            "https://www." + release + "/owner/repo/asset",
            "https://" + release + ".attacker.example/asset",
            "https://" + content + ".attacker.example/asset",
            "https://not" + content + "/asset",
            "ftp://" + release + "/owner/repo/asset",
            "https://example.invalid/asset",
        };

        foreach (var location in locations)
        {
            var transport = new UpdateTransport().OnRedirect(
                UpdateCheckClient.MetadataUri(), new Uri(location, UriKind.Absolute));

            var result = await Client(transport).FetchAsync();

            Assert.Equal(Outcome.HostRefused, result.Outcome);
            Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), UpdateCheckClient.GitCodeReleasesUri() }, transport.Requests);
        }
    }

    [Fact]
    public async Task AnAnswerFromAnAddressThatWasNotAskedIsRefused()
    {
        var transport = new UpdateTransport().On(
            UpdateCheckClient.MetadataUri(),
            (_, _) => Task.FromResult(
                Body(new Uri("https://example.invalid/asset", UriKind.Absolute), Encoding.UTF8.GetBytes(Metadata))));

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.HostRefused, result.Outcome);
    }

    [Fact]
    public async Task ARelativeLocationIsResolvedAgainstTheAddressThatSentIt()
    {
        var target = SameHost("/owner/repo/releases/download/v9.9.9/BUILD-METADATA.json");
        var transport = new UpdateTransport()
            .On(
                UpdateCheckClient.MetadataUri(),
                (uri, _) => Task.FromResult(new UpdateTransportResponse(
                    302, uri, new Uri(target.PathAndQuery, UriKind.Relative), 0, new MemoryStream())))
            .On(target, Metadata);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.Equal(new[] { UpdateCheckClient.MetadataUri(), target }, transport.Requests);
    }

    // ------------------------------------------------------------------------- body size

    [Fact]
    public async Task ADeclaredLengthOverTheCapIsRefusedBeforeTheBodyIsRead()
    {
        var served = false;
        var transport = new UpdateTransport().On(
            UpdateCheckClient.MetadataUri(),
            (uri, _) =>
            {
                served = true;
                return Task.FromResult(new UpdateTransportResponse(
                    200, uri, null, UpdateCheckClient.MaxBodyBytes + 1, new MemoryStream(new byte[8])));
            });

        var result = await Client(transport).FetchAsync();

        Assert.True(served);
        Assert.Equal(Outcome.TooLarge, result.Outcome);
    }

    [Fact]
    public async Task ABodyOverTheCapIsAbandonedEvenWithoutADeclaredLength()
    {
        var oversized = new byte[UpdateCheckClient.MaxBodyBytes + 1];
        var transport = new UpdateTransport().On(
            UpdateCheckClient.MetadataUri(),
            (uri, _) => Task.FromResult(new UpdateTransportResponse(200, uri, null, null, new MemoryStream(oversized))));

        Assert.Equal(Outcome.TooLarge, (await Client(transport).FetchAsync()).Outcome);
    }

    [Fact]
    public async Task ABodyExactlyAtTheCapIsStillRead()
    {
        var padded = Metadata.PadRight(UpdateCheckClient.MaxBodyBytes, ' ');
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), padded);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.Equal("9.9.9", result.LatestVersion);
    }

    // -------------------------------------------------------------------------- failures

    [Fact]
    public async Task TheWholeChainRunsUnderOneBudget()
    {
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), NeverAnswers);

        var result = await Client(transport, TimeSpan.FromMilliseconds(50)).FetchAsync();

        Assert.Equal(Outcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task CancellationByTheCallerIsNotATimeout()
    {
        using var cancelled = new CancellationTokenSource();
        var transport = new UpdateTransport().On(
            UpdateCheckClient.MetadataUri(),
            async (uri, token) =>
            {
                await cancelled.CancelAsync().ConfigureAwait(false);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return Status(uri, 200);
            });

        var result = await Client(transport).FetchAsync(cancelled.Token);

        Assert.Equal(Outcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task ACancelledTokenSendsNothingAtAll()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var transport = new UpdateTransport().On(UpdateCheckClient.MetadataUri(), Metadata);

        var result = await Client(transport).FetchAsync(cancelled.Token);

        Assert.Equal(Outcome.Cancelled, result.Outcome);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task ATlsFailureIsDistinguishedFromAConnectFailure()
    {
        var tls = await Client(Throwing(new HttpRequestException(
            HttpRequestError.SecureConnectionError, "refused", new AuthenticationException("bad certificate"))))
            .FetchAsync();
        var connect = await Client(Throwing(new HttpRequestException(
            HttpRequestError.ConnectionError, "refused"))).FetchAsync();

        Assert.Equal(Outcome.TlsFailed, tls.Outcome);
        Assert.Equal(Outcome.DnsOrConnect, connect.Outcome);
    }

    [Fact]
    public async Task AnyOtherFailureBecomesAnOutcomeAndNeverAnException()
    {
        var result = await Client(Throwing(new InvalidOperationException("proxy http://10.0.0.1:8080 refused")))
            .FetchAsync();

        Assert.Equal(Outcome.TransportFailed, result.Outcome);
        Assert.Equal(nameof(InvalidOperationException), result.Detail);
        Assert.DoesNotContain("10.0.0.1", result.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadFailureBecomesAnOutcome()
    {
        var transport = new UpdateTransport().On(
            UpdateCheckClient.MetadataUri(),
            (uri, _) => Task.FromResult(new UpdateTransportResponse(200, uri, null, null, new FailingStream())));

        Assert.Equal(Outcome.TransportFailed, (await Client(transport).FetchAsync()).Outcome);
    }

    private static UpdateTransport Throwing(Exception error)
    {
        var transport = new UpdateTransport();
        return transport.On(UpdateCheckClient.MetadataUri(), (_, _) => Task.FromException<UpdateTransportResponse>(error));
    }

    // ------------------------------------------------------------- the installer download

    private const string Digest = "f258e2f34a883fa01d88b69fba3095ec80d9807c466a46d22027caa825917d8e";

    /// <summary>The checksum is published beside the installer, under the installer's own name plus <c>.sha256</c>.</summary>
    [Fact]
    public void TheChecksumAddressIsTheInstallerAddressWithItsSuffix()
    {
        Assert.Equal(".sha256", UpdateCheckClient.ChecksumSuffix);
        Assert.Equal(UpdateCheckClient.InstallerUrl("1.5.12") + ".sha256", UpdateCheckClient.ChecksumUrl("1.5.12"));
        Assert.Equal(string.Empty, ChecksumUri("1.5.12").Query);
        Assert.Equal(1024, UpdateCheckClient.MaxChecksumBytes);
    }

    [Fact]
    public async Task APublishedChecksumIsReadThroughTheSameRedirectRules()
    {
        var signed = ContentHost("/release/asset?token=abc&expires=1");
        var transport = new UpdateTransport()
            .OnRedirect(ChecksumUri("9.9.9"), signed)
            .On(signed, Digest.ToUpperInvariant() + "  MentorRecorder-9.9.9-setup.exe\r\n");

        var result = await Client(transport).FetchChecksumAsync("9.9.9");

        Assert.Equal(Outcome.Ok, result.Outcome);
        Assert.Equal(Digest, result.Sha256);
        Assert.Equal(new[] { ChecksumUri("9.9.9"), signed }, transport.Requests);
    }

    [Fact]
    public async Task AChecksumThatIsNoChecksumIsMalformed()
    {
        var transport = new UpdateTransport().On(ChecksumUri("9.9.9"), "<html>not found</html>");

        var result = await Client(transport).FetchChecksumAsync("9.9.9");

        Assert.Equal(Outcome.Malformed, result.Outcome);
        Assert.Null(result.Sha256);
    }

    [Fact]
    public async Task AChecksumOverItsCapIsRefusedWhetherDeclaredOrNot()
    {
        var oversized = Encoding.ASCII.GetBytes(Digest.PadRight(UpdateCheckClient.MaxChecksumBytes + 1, ' '));
        var declared = new UpdateTransport().On(
            ChecksumUri("9.9.9"),
            (uri, _) => Task.FromResult(new UpdateTransportResponse(
                200, uri, null, UpdateCheckClient.MaxChecksumBytes + 1, new MemoryStream(new byte[8]))));
        var undeclared = new UpdateTransport().On(
            ChecksumUri("9.9.9"),
            (uri, _) => Task.FromResult(new UpdateTransportResponse(200, uri, null, null, new MemoryStream(oversized))));

        Assert.Equal(Outcome.TooLarge, (await Client(declared).FetchChecksumAsync("9.9.9")).Outcome);
        Assert.Equal(Outcome.TooLarge, (await Client(undeclared).FetchChecksumAsync("9.9.9")).Outcome);
    }

    [Fact]
    public async Task AChecksumRedirectAnywhereElseIsRefused()
    {
        var elsewhere = new Uri("https://" + UpdateCheckClient.MetadataUri().Host + ".attacker.example/x", UriKind.Absolute);
        var transport = new UpdateTransport().OnRedirect(ChecksumUri("9.9.9"), elsewhere);

        var result = await Client(transport).FetchChecksumAsync("9.9.9");

        Assert.Equal(Outcome.HostRefused, result.Outcome);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task TheChecksumRequestRunsUnderTheCheckBudget()
    {
        var transport = new UpdateTransport().On(ChecksumUri("9.9.9"), NeverAnswers);

        var result = await Client(transport, TimeSpan.FromMilliseconds(50)).FetchChecksumAsync("9.9.9");

        Assert.Equal(Outcome.Timeout, result.Outcome);
    }

    [Fact]
    public async Task TheKillSwitchStopsBothDownloadRequestsBeforeAnythingIsSent()
    {
        var transport = new UpdateTransport();
        var client = new UpdateCheckClient(transport.Send, TimeSpan.FromSeconds(5), _ => "1");

        var checksum = await client.FetchChecksumAsync("9.9.9");
        using var installer = await client.OpenInstallerAsync("9.9.9", 1024);

        Assert.Equal(Outcome.Disabled, checksum.Outcome);
        Assert.Equal(Outcome.Disabled, installer.Result.Outcome);
        Assert.Null(installer.Body);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task TheInstallerIsOpenedWithItsBodyUnreadAfterTheRedirect()
    {
        var signed = ContentHost("/release/installer?token=abc");
        var body = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var transport = new UpdateTransport()
            .OnRedirect(InstallerUri("9.9.9"), signed)
            .On(signed, (uri, _) => Task.FromResult(new UpdateTransportResponse(200, uri, null, 4, body)));

        using var installer = await Client(transport).OpenInstallerAsync("9.9.9", 1024);

        Assert.Equal(Outcome.Ok, installer.Result.Outcome);
        Assert.Equal(4, installer.ContentLength);
        Assert.Same(body, installer.Body);
        Assert.Equal(0, body.Position);
        Assert.Equal(new[] { InstallerUri("9.9.9"), signed }, transport.Requests);
    }

    [Fact]
    public async Task AnInstallerDeclaringMoreThanTheCapIsRefusedAndReleased()
    {
        var body = new MemoryStream(new byte[8]);
        var transport = new UpdateTransport().On(
            InstallerUri("9.9.9"),
            (uri, _) => Task.FromResult(new UpdateTransportResponse(200, uri, null, 1025, body)));

        using var installer = await Client(transport).OpenInstallerAsync("9.9.9", 1024);

        Assert.Equal(Outcome.TooLarge, installer.Result.Outcome);
        Assert.Null(installer.Body);
        Assert.False(body.CanRead);
    }

    [Theory]
    [InlineData(404, Outcome.NotFound)]
    [InlineData(429, Outcome.RateLimited)]
    [InlineData(500, Outcome.HttpStatus)]
    public async Task AnInstallerAnswerThatIsNoInstallerIsReported(int status, Outcome expected)
    {
        var transport = new UpdateTransport().On(InstallerUri("9.9.9"), status);

        using var installer = await Client(transport).OpenInstallerAsync("9.9.9", 1024);

        Assert.Equal(expected, installer.Result.Outcome);
        Assert.Equal(status, installer.Result.StatusCode);
        Assert.Null(installer.Body);
    }

    [Fact]
    public async Task OnlyAPublishedVersionCanBeAsked()
    {
        var client = Client(new UpdateTransport());

        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchChecksumAsync("1.0.0/../x"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenInstallerAsync("1.0.0-beta.1", 1024));
    }

    // ----------------------------------------------------- where a download may be redirected

    private static Uri OnReleaseHost(string path) =>
        new("https://" + UpdateCheckClient.MetadataUri().Host + path, UriKind.Absolute);

    private const string ReleaseFolder =
        "/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository + "/releases/download/v9.9.9/";

    /// <summary>Asks for the checksum and for the installer through one redirect to <paramref name="target"/>, which answers.</summary>
    private static async Task<(Outcome Checksum, Outcome Installer, int Requests)> RedirectedAsync(Uri target)
    {
        var checksum = new UpdateTransport()
            .OnRedirect(ChecksumUri("9.9.9"), target)
            .On(target, Digest + "  MentorRecorder-9.9.9-setup.exe\r\n");
        var installer = new UpdateTransport()
            .OnRedirect(InstallerUri("9.9.9"), target)
            .On(target, (uri, _) => Task.FromResult(
                new UpdateTransportResponse(200, uri, null, 4, new MemoryStream(new byte[] { 1, 2, 3, 4 }))));

        var fetched = await Client(checksum).FetchChecksumAsync("9.9.9");
        using var opened = await Client(installer).OpenInstallerAsync("9.9.9", 1024);
        return (fetched.Outcome, opened.Result.Outcome, checksum.Requests.Count + installer.Requests.Count);
    }

    /// <summary>
    /// The file a download fetches is run with administrator rights, so its content may come only from where GitHub
    /// serves this project's release assets - not from any other subdomain of the content host, several of which
    /// carry what any GitHub user publishes.
    /// </summary>
    [Theory]
    [InlineData("raw.")]
    [InlineData("gist.")]
    [InlineData("user-images.")]
    [InlineData("media.")]
    [InlineData("")]
    public async Task ADownloadIsNotFollowedToAnyOtherContentHost(string subdomain)
    {
        var target = new Uri("https://" + subdomain + ContentDomain + "/owner/repo/main/setup.exe", UriKind.Absolute);

        var (checksum, installer, requests) = await RedirectedAsync(target);

        Assert.Equal(Outcome.HostRefused, checksum);
        Assert.Equal(Outcome.HostRefused, installer);
        Assert.Equal(2, requests);
    }

    /// <summary>On the release host, a download's redirect may only stay inside that version's own release folder.</summary>
    [Theory]
    [InlineData("/Someone-Else/" + UpdateCheckClient.Repository + "/releases/download/v9.9.9/MentorRecorder-9.9.9-setup.exe")]
    [InlineData("/" + UpdateCheckClient.Owner + "/Another-Repository/releases/download/v9.9.9/MentorRecorder-9.9.9-setup.exe")]
    [InlineData("/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository + "/releases/download/v9.9.8/MentorRecorder-9.9.8-setup.exe")]
    [InlineData("/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository + "/releases/latest/download/MentorRecorder-9.9.9-setup.exe")]
    [InlineData("/" + UpdateCheckClient.Owner + "/" + UpdateCheckClient.Repository + "/releases/download/v9.9.9")]
    [InlineData(ReleaseFolder + "../../../../Someone-Else/x/releases/download/v9.9.9/setup.exe")]
    [InlineData(ReleaseFolder + "%2e%2e/%2e%2e/v1.0.0/MentorRecorder-1.0.0-setup.exe")]
    [InlineData(ReleaseFolder + "%2Fx")]
    public async Task ADownloadIsNotFollowedOutsideItsReleaseFolder(string path)
    {
        var (checksum, installer, requests) = await RedirectedAsync(OnReleaseHost(path));

        Assert.Equal(Outcome.HostRefused, checksum);
        Assert.Equal(Outcome.HostRefused, installer);
        Assert.Equal(2, requests);
    }

    /// <summary>The hosts GitHub serves release assets from are followed, as is a hop that stays in the release folder.</summary>
    [Theory]
    [InlineData("https://release-assets.")]
    [InlineData("https://objects.")]
    [InlineData("https://RELEASE-ASSETS.")]
    [InlineData(null)]
    public async Task ADownloadIsFollowedToWhereReleaseAssetsAreServed(string? scheme)
    {
        var target = scheme is null
            ? OnReleaseHost(ReleaseFolder + "MentorRecorder-9.9.9-setup.exe.mirror")
            : new Uri(scheme + ContentDomain + "/a1b2/asset?sp=r&sig=abc", UriKind.Absolute);

        var (checksum, installer, requests) = await RedirectedAsync(target);

        Assert.Equal(Outcome.Ok, checksum);
        Assert.Equal(Outcome.Ok, installer);
        Assert.Equal(4, requests);
    }

    [Fact]
    public void ReleaseAssetsAreServedFromTwoHostsOfTheContentDomain()
    {
        Assert.Equal(
            new[] { "release-assets." + ContentDomain, "objects." + ContentDomain },
            UpdateCheckClient.AssetHosts);
    }

    /// <summary>The metadata check keeps its own, wider rule: the narrower one is for the two download requests.</summary>
    [Fact]
    public async Task TheMetadataCheckKeepsItsOwnRedirectRule()
    {
        var target = new Uri("https://raw." + ContentDomain + "/owner/repo/main/BUILD-METADATA.json", UriKind.Absolute);
        var transport = new UpdateTransport()
            .OnRedirect(UpdateCheckClient.MetadataUri(), target)
            .On(target, Metadata);

        var result = await Client(transport).FetchAsync();

        Assert.Equal(Outcome.Ok, result.Outcome);
    }
}
