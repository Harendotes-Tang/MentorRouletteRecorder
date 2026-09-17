using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Sharing;
using Outcome = MentorRecorder.Collector.Protocol.Sharing.SharedFetchOutcome;
using Source = MentorRecorder.Collector.Protocol.Sharing.SharedCalibrationSource;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The shared-calibration download client (docs/privacy-boundary.md §8.2, plans/shared-calibration.md
/// §3.3), driven entirely through its injected transport: no test here opens a connection, names the
/// production transport type or writes a host name. Addresses are asserted against the client's own
/// public constants.
/// </summary>
public sealed class SharedCalibrationClientTests
{
    private const string Build = SharedCalibrationIndexTests.Build;

    private sealed record Published(string Sha, string Code, ShareCodePayload Payload);

    private static Published Code(int opcode, string build = Build)
    {
        var payload = ShareCodeTests.Payload(CalibrationMatchSource.Announcement, new ShareCodePop((ushort)opcode, Length: 64)) with
        {
            GameBuild = build,
        };
        return new Published(ShareCode.Sha256(payload), ShareCode.Encode(payload), payload);
    }

    private static byte[] Index(params JsonNode[] entries) => SharedCalibrationIndexTests.Index(entries);

    private static JsonObject Entry(string sha, int submitters = 1, bool revoked = false, string matchSource = "ANNOUNCEMENT") =>
        SharedCalibrationIndexTests.Entry(sha, submitters: submitters, revoked: revoked, matchSource: matchSource);

    private static string Commit => SharedCalibrationIndexTests.Commit();

    private static Uri CodeAt(Source source, string sha) =>
        SharedCalibrationClient.CodeUri(source, Commit, SharedCalibrationIndex.CodePath(Region.Cn, Build, sha));

    private static SharedTransportResponse Answer(Uri uri, byte[] body) => new(200, uri, body.Length, new MemoryStream(body));

    private static SharedTransportResponse Status(Uri uri, int status) => new(status, uri, 0, new MemoryStream());

    private static SharedCalibrationClient Client(FakeTransport transport, TimeSpan? timeout = null) =>
        new(transport.Send, timeout ?? TimeSpan.FromSeconds(5), _ => null);

    /// <summary>Answers the index and the given codes from one source.</summary>
    private static FakeTransport Serve(FakeTransport transport, Source source, byte[] index, params Published[] codes)
    {
        transport.On(SharedCalibrationClient.IndexUri(source), index);
        foreach (var code in codes)
        {
            transport.On(CodeAt(source, code.Sha), Encoding.UTF8.GetBytes(code.Code));
        }

        return transport;
    }

    // ------------------------------------------------------------------------ addresses

    [Fact]
    public void TheSourcesAreOneRepositoryOnThreeHostsTriedInAFixedOrder()
    {
        Assert.Equal("Harendotes-Tang", SharedCalibrationClient.Owner);
        Assert.Equal("MentorRecorder-Calibrations", SharedCalibrationClient.Repository);
        Assert.Equal(new[] { Source.GithubRaw, Source.CdnPrimary, Source.CdnFallback }, SharedCalibrationClient.SourceOrder);
        var hosts = SharedCalibrationClient.SourceOrder.Select(SharedCalibrationClient.HostFor).ToArray();
        Assert.Equal(3, hosts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(hosts.Order(), SharedCalibrationClient.AllowedHosts.Order());

        foreach (var source in SharedCalibrationClient.SourceOrder)
        {
            var uri = SharedCalibrationClient.IndexUri(source);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Equal(SharedCalibrationClient.HostFor(source), uri.Host);
            Assert.True(uri.IsDefaultPort);
            Assert.Equal(string.Empty, uri.Query);
            Assert.Equal(string.Empty, uri.Fragment);
            Assert.Equal(string.Empty, uri.UserInfo);
        }

        var repository = SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository;
        Assert.Equal("/" + repository + "/main/index.json", SharedCalibrationClient.IndexUri(Source.GithubRaw).AbsolutePath);
        Assert.Equal("/gh/" + repository + "@main/index.json", SharedCalibrationClient.IndexUri(Source.CdnPrimary).AbsolutePath);
        Assert.Equal("/gh/" + repository + "@main/index.json", SharedCalibrationClient.IndexUri(Source.CdnFallback).AbsolutePath);
    }

    [Fact]
    public void ACodeIsAddressedByCommitNotByBranch()
    {
        var path = SharedCalibrationIndex.CodePath(Region.Cn, Build, SharedCalibrationIndexTests.Sha('a'));
        var repository = SharedCalibrationClient.Owner + "/" + SharedCalibrationClient.Repository;

        Assert.Equal("/" + repository + "/" + Commit + "/" + path, SharedCalibrationClient.CodeUri(Source.GithubRaw, Commit, path).AbsolutePath);
        Assert.Equal("/gh/" + repository + "@" + Commit + "/" + path, SharedCalibrationClient.CodeUri(Source.CdnPrimary, Commit, path).AbsolutePath);
        Assert.Equal(string.Empty, SharedCalibrationClient.CodeUri(Source.CdnFallback, Commit, path).Query);
        Assert.Throws<ArgumentException>(() => SharedCalibrationClient.CodeUri(Source.GithubRaw, "main", path));
        Assert.Throws<ArgumentException>(() => SharedCalibrationClient.CodeUri(Source.GithubRaw, Commit, "../index.json"));
        Assert.Throws<ArgumentException>(() => SharedCalibrationClient.CodeUri(Source.GithubRaw, Commit, path + "?x=1"));
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
    public void TheKillSwitchIsOnForAnyValueButEmptyZeroOrFalse(string? value, bool disabled)
    {
        Assert.Equal(disabled, SharedCalibrationClient.IsDisabled(value));
    }

    [Fact]
    public async Task TheKillSwitchStopsTheFetchBeforeTheTransportIsTouched()
    {
        var transport = new FakeTransport();
        string? asked = null;
        var client = new SharedCalibrationClient(transport.Send, readEnvironment: name =>
        {
            asked = name;
            return "1";
        });

        var result = await client.FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.Disabled, result.Status);
        Assert.Equal("MR_DISABLE_SHARED_FETCH", SharedCalibrationClient.DisableVariable);
        Assert.Equal(SharedCalibrationClient.DisableVariable, asked);
        Assert.Empty(transport.Requests);
        Assert.Empty(result.IndexAttempts);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task OnlyAKnownRegionAndAValidBuildCanBeFetched()
    {
        var transport = new FakeTransport();
        var client = Client(transport);

        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(Region.Unknown, Build));
        await Assert.ThrowsAsync<ArgumentException>(() => client.FetchAsync(Region.Cn, "../x"));
        Assert.Empty(transport.Requests);
    }

    // ------------------------------------------------------------------------ happy path

    [Fact]
    public async Task TheFirstSourceThatAnswersSuppliesTheIndexAndTheCodes()
    {
        var few = Code(0xF001);
        var many = Code(0xF002);
        var transport = Serve(new FakeTransport(), Source.GithubRaw, Index(Entry(few.Sha, submitters: 1), Entry(many.Sha, submitters: 4)), few, many);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.Ok, result.Status);
        Assert.Equal(new[] { new SharedSourceAttempt(Source.GithubRaw, Outcome.Ok, 200) }, result.IndexAttempts);
        Assert.Equal(new[] { many.Sha, few.Sha }, result.Candidates.Select(candidate => candidate.CodeSha256));
        var first = result.Candidates[0];
        Assert.Equal(many.Code, first.Code);
        Assert.Equal(many.Payload, first.Payload);
        Assert.Equal(4, first.Submitters);
        Assert.Equal(Commit, first.Commit);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), first.FirstPublishedAtUtc);
        Assert.Empty(result.Discards);
        Assert.Empty(result.SkippedEntries);
        Assert.Equal(3, transport.Requests.Count);
        Assert.All(transport.Requests, uri => Assert.Equal(SharedCalibrationClient.HostFor(Source.GithubRaw), uri.Host));
    }

    [Fact]
    public async Task EachFailingSourceIsRecordedAndTheNextOneIsTriedInOrder()
    {
        var code = Code(0xF001);
        var transport = new FakeTransport()
            .On(SharedCalibrationClient.IndexUri(Source.GithubRaw),
                (_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host"))
            .On(SharedCalibrationClient.IndexUri(Source.CdnPrimary), (uri, _) => Task.FromResult(Status(uri, 503)));
        Serve(transport, Source.CdnFallback, Index(Entry(code.Sha)), code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(new[] { Outcome.DnsOrConnect, Outcome.HttpStatus, Outcome.Ok }, result.IndexAttempts.Select(attempt => attempt.Outcome));
        Assert.Equal(new[] { Source.GithubRaw, Source.CdnPrimary, Source.CdnFallback }, result.IndexAttempts.Select(attempt => attempt.Source));
        Assert.Equal("NAME_RESOLUTION_ERROR", result.IndexAttempts[0].Detail);
        Assert.Equal(503, result.IndexAttempts[1].HttpStatus);
        Assert.Equal(SharedFetchStatus.Ok, result.Status);
        Assert.Single(result.Candidates);
        // The code is requested first from the source that supplied the index: that source is known to answer.
        Assert.Equal(4, transport.Requests.Count);
        Assert.Equal(SharedCalibrationClient.HostFor(Source.CdnFallback), transport.Requests[3].Host);
    }

    [Fact]
    public async Task AtMostEightCodesAreDownloadedInTheIndexOrder()
    {
        var codes = Enumerable.Range(0, 10).Select(i => Code(0xF000 + i)).ToArray();
        var index = Index(codes.Select((code, i) => (JsonNode)Entry(code.Sha, submitters: 10 - i)).ToArray());
        var transport = Serve(new FakeTransport(), Source.GithubRaw, index, codes);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(codes.Take(SharedCalibrationIndex.MaxCandidates).Select(code => code.Sha), result.Candidates.Select(c => c.CodeSha256));
        Assert.Equal(1 + SharedCalibrationIndex.MaxCandidates, transport.Requests.Count);
    }

    [Fact]
    public async Task ARevokedCodeIsNeverDownloadedButIsReported()
    {
        var revoked = Code(0xF001);
        var kept = Code(0xF002);
        var transport = Serve(new FakeTransport(), Source.GithubRaw, Index(Entry(revoked.Sha, submitters: 9, revoked: true), Entry(kept.Sha)), revoked, kept);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(kept.Sha, Assert.Single(result.Candidates).CodeSha256);
        Assert.Equal(new[] { revoked.Sha }, result.RevokedCodeSha256s);
        Assert.DoesNotContain(transport.Requests, uri => uri.AbsolutePath.Contains(revoked.Sha[..12], StringComparison.Ordinal));
    }

    [Fact]
    public async Task EntriesTheIndexCouldNotReadAreReportedAndTheRestIsUsed()
    {
        var code = Code(0xF001);
        var broken = Entry(code.Sha);
        broken.Remove("commit");
        var transport = Serve(new FakeTransport(), Source.GithubRaw, Index(broken, Entry(code.Sha)), code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal("MISSING:commit", Assert.Single(result.SkippedEntries).Reason);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task NothingPublishedForThisBuildDownloadsNoCode()
    {
        var elsewhere = Code(0xF001, build: "2026.08.05.0000.0000");
        var transport = new FakeTransport().On(
            SharedCalibrationClient.IndexUri(Source.GithubRaw),
            Index(SharedCalibrationIndexTests.Entry(elsewhere.Sha, build: "2026.08.05.0000.0000")));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.NoneForBuild, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Single(transport.Requests);
    }

    [Fact]
    public async Task WhenEverySourceFailsTheIndexIsUnavailableAndEachSourceSaysWhy()
    {
        var transport = new FakeTransport();

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.IndexUnavailable, result.Status);
        Assert.Equal(
            SharedCalibrationClient.SourceOrder.Select(source => new SharedSourceAttempt(source, Outcome.HttpStatus, 404)),
            result.IndexAttempts);
        Assert.Empty(result.Candidates);
    }

    // ------------------------------------------------------------------ what is refused

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ARedirectIsRefusedAndNeverFollowed(int status)
    {
        var transport = new FakeTransport();
        foreach (var source in SharedCalibrationClient.SourceOrder)
        {
            transport.On(SharedCalibrationClient.IndexUri(source), (uri, _) => Task.FromResult(Status(uri, status)));
        }

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.IndexUnavailable, result.Status);
        Assert.All(result.IndexAttempts, attempt =>
        {
            Assert.Equal(Outcome.RedirectRefused, attempt.Outcome);
            Assert.Equal(status, attempt.HttpStatus);
        });
        Assert.Equal(3, transport.Requests.Count);
    }

    [Fact]
    public async Task AnAnswerFromAnywhereButTheRequestedAddressIsRefused()
    {
        var body = "{}"u8.ToArray();
        var transport = new FakeTransport()
            .On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (_, _) => Task.FromResult(
                new SharedTransportResponse(200, new Uri("https://example.invalid/index.json"), body.Length, new MemoryStream(body))))
            .On(SharedCalibrationClient.IndexUri(Source.CdnPrimary), (uri, _) => Task.FromResult(
                new SharedTransportResponse(200, new UriBuilder(uri) { Scheme = Uri.UriSchemeHttp, Port = -1 }.Uri, body.Length, new MemoryStream(body))))
            .On(SharedCalibrationClient.IndexUri(Source.CdnFallback), (uri, _) => Task.FromResult(
                new SharedTransportResponse(200, new Uri(uri, "/elsewhere.json"), body.Length, new MemoryStream(body))));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(
            new[] { Outcome.HostRefused, Outcome.HostRefused, Outcome.RedirectRefused },
            result.IndexAttempts.Select(attempt => attempt.Outcome));
    }

    [Fact]
    public async Task ABodyDeclaredLargerThanTheCapIsRefusedWithoutReadingIt()
    {
        var stream = new EndlessStream();
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (uri, _) => Task.FromResult(
            new SharedTransportResponse(200, uri, SharedCalibrationClient.MaxIndexBytes + 1, stream)));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(Outcome.TooLarge, result.IndexAttempts[0].Outcome);
        Assert.Equal(0, stream.Served);
    }

    [Fact]
    public async Task AnUndeclaredOversizedBodyIsAbandonedAsSoonAsTheCapIsPassed()
    {
        var stream = new EndlessStream();
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (uri, _) => Task.FromResult(
            new SharedTransportResponse(200, uri, null, stream)));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(Outcome.TooLarge, result.IndexAttempts[0].Outcome);
        Assert.InRange(stream.Served, SharedCalibrationClient.MaxIndexBytes + 1, SharedCalibrationClient.MaxIndexBytes + EndlessStream.Chunk);
    }

    [Fact]
    public async Task AnIndexOfExactlyTheCapIsAccepted()
    {
        var code = Code(0xF001);
        var index = Index(Entry(code.Sha));
        var padded = index.Concat(Enumerable.Repeat((byte)' ', SharedCalibrationClient.MaxIndexBytes - index.Length)).ToArray();
        var transport = Serve(new FakeTransport(), Source.GithubRaw, padded, code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(Outcome.Ok, result.IndexAttempts[0].Outcome);
        Assert.Equal(SharedFetchStatus.Ok, result.Status);
    }

    [Fact]
    public async Task ASourceThatHangsTimesOutAndTheNextSourceIsTried()
    {
        var code = Code(0xF001);
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("the delay never ends on its own");
        });
        Serve(transport, Source.CdnPrimary, Index(Entry(code.Sha)), code);

        var result = await Client(transport, TimeSpan.FromMilliseconds(100)).FetchAsync(Region.Cn, Build);

        Assert.Equal(new[] { Outcome.Timeout, Outcome.Ok }, result.IndexAttempts.Select(attempt => attempt.Outcome));
        Assert.Equal(SharedFetchStatus.Ok, result.Status);
    }

    [Fact]
    public async Task ABodyThatStopsArrivingTimesOutToo()
    {
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (uri, _) => Task.FromResult(
            new SharedTransportResponse(200, uri, null, new StallingStream())));

        var result = await Client(transport, TimeSpan.FromMilliseconds(100)).FetchAsync(Region.Cn, Build);

        Assert.Equal(Outcome.Timeout, result.IndexAttempts[0].Outcome);
    }

    [Fact]
    public async Task FailuresAreClassifiedAndNeverThrown()
    {
        var transport = new FakeTransport()
            .On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (_, _) =>
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake", new AuthenticationException()))
            .On(SharedCalibrationClient.IndexUri(Source.CdnPrimary), (uri, _) => Task.FromResult(
                new SharedTransportResponse(200, uri, null, new BrokenStream())))
            .On(SharedCalibrationClient.IndexUri(Source.CdnFallback), (_, _) => throw new InvalidOperationException("surprise"));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(new[] { Outcome.TlsFailed, Outcome.ReadFailed, Outcome.TransportFailed }, result.IndexAttempts.Select(attempt => attempt.Outcome));
        Assert.Equal("InvalidOperationException", result.IndexAttempts[2].Detail);
        Assert.Equal(SharedFetchStatus.IndexUnavailable, result.Status);
    }

    [Fact]
    public async Task AMirrorServingSomethingThatIsNotAnIndexFallsThroughToTheNext()
    {
        var code = Code(0xF001);
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), "<html>blocked</html>"u8.ToArray());
        Serve(transport, Source.CdnPrimary, Index(Entry(code.Sha)), code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(new SharedSourceAttempt(Source.GithubRaw, Outcome.Malformed, 200, "NOT_JSON"), result.IndexAttempts[0]);
        Assert.Equal(Outcome.Ok, result.IndexAttempts[1].Outcome);
        Assert.Equal(SharedFetchStatus.Ok, result.Status);
    }

    [Fact]
    public async Task AMirrorServingAnIndexWithALoneSurrogateKeyFallsThroughToTheNext()
    {
        var code = Code(0xF001);
        var damaged = Encoding.UTF8.GetString(Index(Entry(code.Sha)))
            .Replace("{\"region\"", "{\"\\ud800\":1,\"region\"", StringComparison.Ordinal);
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), Encoding.UTF8.GetBytes(damaged));
        Serve(transport, Source.CdnPrimary, Index(Entry(code.Sha)), code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(new SharedSourceAttempt(Source.GithubRaw, Outcome.Malformed, 200, "NOT_JSON"), result.IndexAttempts[0]);
        Assert.Equal(SharedFetchStatus.Ok, result.Status);
        Assert.Equal(code.Sha, Assert.Single(result.Candidates).CodeSha256);
    }

    [Fact]
    public async Task ACodeThatDoesNotHashToItsEntryIsRefusedAndAnotherMirrorIsTried()
    {
        var code = Code(0xF001);
        var impostor = Code(0xBAD0);
        var transport = new FakeTransport()
            .On(SharedCalibrationClient.IndexUri(Source.GithubRaw), Index(Entry(code.Sha)))
            .On(CodeAt(Source.GithubRaw, code.Sha), Encoding.UTF8.GetBytes(impostor.Code))
            .On(CodeAt(Source.CdnPrimary, code.Sha), Encoding.UTF8.GetBytes(code.Code));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(code.Sha, Assert.Single(result.Candidates).CodeSha256);
        Assert.Empty(result.Discards);
    }

    public static TheoryData<string, string, string> DiscardedCodes() => new()
    {
        { "hash", "MRC1.impostor", "HASH_MISMATCH" },
        { "undecodable", "MRC1.!!!", "UNDECODABLE:E_SHARE_CODE_CHARACTERS" },
        { "ill-formed text", ShareCodeTests.IllFormedCode(), "UNDECODABLE:E_SHARE_CODE_JSON" },
        { "unreachable", "", "UNREACHABLE" },
    };

    [Theory]
    [MemberData(nameof(DiscardedCodes))]
    public async Task ACodeNoMirrorServesCorrectlyIsDiscardedWithAReason(string why, string served, string reason)
    {
        var code = Code(0xF001);
        var body = served == "MRC1.impostor" ? Code(0xBAD0).Code : served;
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), Index(Entry(code.Sha)));
        if (body.Length > 0)
        {
            transport.On(CodeAt(Source.GithubRaw, code.Sha), Encoding.UTF8.GetBytes(body));
        }

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal(SharedFetchStatus.CodesUnavailable, result.Status);
        var discard = Assert.Single(result.Discards);
        Assert.Equal(code.Sha, discard.CodeSha256);
        Assert.True(reason == discard.Reason, why + ": " + discard.Reason);
        Assert.Equal(SharedCalibrationClient.SourceOrder, discard.Attempts.Select(attempt => attempt.Source));
    }

    [Theory]
    [InlineData("game_build")]
    [InlineData("match_source")]
    public async Task ACodeThatHashesCorrectlyButDescribesSomethingElseIsDiscarded(string field)
    {
        var code = field == "game_build" ? Code(0xF001, build: "2026.08.05.0000.0000") : Code(0xF001);
        var entry = Entry(code.Sha, matchSource: field == "match_source" ? "MARKER_OFFSET" : "ANNOUNCEMENT");
        var transport = new FakeTransport()
            .On(SharedCalibrationClient.IndexUri(Source.GithubRaw), Index(entry))
            .On(CodeAt(Source.GithubRaw, code.Sha), Encoding.UTF8.GetBytes(code.Code));

        var result = await Client(transport).FetchAsync(Region.Cn, Build);

        Assert.Equal("PAYLOAD_MISMATCH:" + field, Assert.Single(result.Discards).Reason);
        Assert.Empty(result.Candidates);
    }

    // ---------------------------------------------------------------------- cancellation

    [Fact]
    public async Task CancellingBeforeTheFetchStartsSendsNothing()
    {
        var transport = new FakeTransport();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = await Client(transport).FetchAsync(Region.Cn, Build, cancel.Token);

        Assert.Equal(SharedFetchStatus.Cancelled, result.Status);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task CancellingMidFetchStopsWithoutThrowing()
    {
        using var cancel = new CancellationTokenSource();
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), async (_, token) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("the delay never ends on its own");
        });

        var result = await Client(transport).FetchAsync(Region.Cn, Build, cancel.Token);

        Assert.Equal(SharedFetchStatus.Cancelled, result.Status);
        Assert.Equal(new[] { new SharedSourceAttempt(Source.GithubRaw, Outcome.Cancelled) }, result.IndexAttempts);
        Assert.Single(transport.Requests);
    }

    // ------------------------------------------------------------------ what is recorded

    [Fact]
    public async Task TheResultNamesSourcesNeverAddresses()
    {
        var code = Code(0xF001);
        var transport = new FakeTransport().On(SharedCalibrationClient.IndexUri(Source.GithubRaw), (uri, _) => Task.FromResult(Status(uri, 500)));
        Serve(transport, Source.CdnPrimary, Index(Entry(code.Sha), Entry(Code(0xF002).Sha)), code);

        var result = await Client(transport).FetchAsync(Region.Cn, Build);
        var text = JsonSerializer.Serialize(result);

        Assert.NotEmpty(result.Discards);
        Assert.DoesNotContain("://", text, StringComparison.Ordinal);
        Assert.All(SharedCalibrationClient.AllowedHosts, host => Assert.DoesNotContain(host, text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(SharedCalibrationClient.Repository, text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ production transport

    [Fact]
    public void TheProductionHandlerFollowsNoRedirectKeepsNoCookieAndSendsNoCredential()
    {
        using var handler = SharedCalibrationClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        Assert.Equal(SharedCalibrationClient.DefaultSourceTimeout, handler.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), SharedCalibrationClient.DefaultSourceTimeout);
        Assert.Equal(64 * 1024, SharedCalibrationClient.MaxIndexBytes);
        Assert.Equal(4 * 1024, SharedCalibrationClient.MaxCodeBytes);
    }

    [Fact]
    public void TheProductionRequestIsAPlainGetCarryingOnlyTheUserAgent()
    {
        using var request = SharedCalibrationClient.CreateRequest(SharedCalibrationClient.IndexUri(Source.GithubRaw));

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
        Assert.Equal(new Version(1, 1), request.Version);
        var header = Assert.Single(request.Headers);
        Assert.Equal("User-Agent", header.Key);
        Assert.Equal(new[] { "MentorRecorder" }, header.Value);
        Assert.Equal("MentorRecorder", SharedCalibrationClient.UserAgent);
        Assert.Equal(string.Empty, request.RequestUri!.Query);
    }

    // --------------------------------------------------------------------------- fakes

    private sealed class FakeTransport
    {
        private readonly Dictionary<string, Func<Uri, CancellationToken, Task<SharedTransportResponse>>> _routes = new(StringComparer.Ordinal);
        private readonly List<Uri> _requests = new();

        public IReadOnlyList<Uri> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        public FakeTransport On(Uri uri, Func<Uri, CancellationToken, Task<SharedTransportResponse>> respond)
        {
            _routes[uri.AbsoluteUri] = respond;
            return this;
        }

        public FakeTransport On(Uri uri, byte[] body) => On(uri, (requested, _) => Task.FromResult(Answer(requested, body)));

        public Task<SharedTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
        {
            lock (_requests)
            {
                _requests.Add(uri);
            }

            return _routes.TryGetValue(uri.AbsoluteUri, out var respond)
                ? respond(uri, cancellationToken)
                : Task.FromResult(Status(uri, 404));
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("read asynchronously");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Serves whitespace indefinitely, one chunk per read, counting the bytes served.</summary>
    private sealed class EndlessStream : ReadOnlyStream
    {
        public const int Chunk = 1024;

        public long Served { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, Chunk);
            buffer.Span[..count].Fill((byte)' ');
            Served += count;
            return ValueTask.FromResult(count);
        }
    }

    /// <summary>Delivers response headers but never any body.</summary>
    private sealed class StallingStream : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    /// <summary>Drops the connection partway through the body.</summary>
    private sealed class BrokenStream : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("connection reset"));
    }
}
