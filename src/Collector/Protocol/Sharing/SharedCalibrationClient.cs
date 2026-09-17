using System.Net;
using System.Security.Authentication;
using System.Text;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Downloads shared calibrations: the one outbound request the released software is allowed to make
/// (docs/privacy-boundary.md §8.2).
///
/// This file is deliberately the only place in the repository that names <c>HttpClient</c> (static
/// rule NET-006) and the source host names (NET-007). Everything else - the pipeline, the store and
/// every test - sees <see cref="SharedCalibrationTransport"/>, a delegate returning a response head
/// and an unread body; <see cref="HttpTransport"/> is the production one.
///
/// Enforced here whatever the transport is:
/// <list type="bullet">
/// <item><see cref="DisableVariable"/> is read on every fetch, before anything is sent.</item>
/// <item>Only <c>raw.githubusercontent.com</c>, <c>cdn.jsdelivr.net</c> and <c>fastly.jsdelivr.net</c> are
/// asked, in <see cref="SourceOrder"/>, for one fixed repository; no query string, no fragment.</item>
/// <item>Each request has its own <see cref="DefaultSourceTimeout"/> covering connect, headers and body.</item>
/// <item>A status outside 2xx fails; 3xx is recorded as a refused redirect and never followed; an
/// answer from any address but the requested one is refused.</item>
/// <item>The body is counted while it is read and abandoned the moment it passes
/// <see cref="MaxIndexBytes"/> or <see cref="MaxCodeBytes"/>.</item>
/// <item>The index is untrusted input (<see cref="SharedCalibrationIndex"/>); a code must decode and
/// hash to its entry and describe the same region, build and match source.</item>
/// </list>
/// Every failure becomes an outcome on the result rather than an exception, and the result names
/// sources, never addresses.
/// </summary>
public sealed class SharedCalibrationClient
{
    /// <summary>Owner of the public data repository (working name, plan §1).</summary>
    public const string Owner = "Harendotes-Tang";

    /// <summary>Name of the public data repository (working name, plan §1).</summary>
    public const string Repository = "MentorRecorder-Calibrations";

    /// <summary>Branch the index is read from. Codes are read by commit instead.</summary>
    public const string Branch = "main";

    /// <summary>File name of the index at the repository root.</summary>
    public const string IndexFileName = "index.json";

    /// <summary>The one request header this client sets; deliberately without a version.</summary>
    public const string UserAgent = "MentorRecorder";

    /// <summary>Largest index accepted, in bytes, counted while reading.</summary>
    public const int MaxIndexBytes = 64 * 1024;

    /// <summary>Largest code file accepted, in bytes, counted while reading; the code's own inflate cap applies after.</summary>
    public const int MaxCodeBytes = 4 * 1024;

    /// <summary>Environment variable that stops every fetch before anything is sent (<see cref="IsDisabled"/>).</summary>
    public const string DisableVariable = "MR_DISABLE_SHARED_FETCH";

    /// <summary>Budget of one request to one source: connect, headers and body together.</summary>
    public static readonly TimeSpan DefaultSourceTimeout = TimeSpan.FromSeconds(10);

    private const string RawHost = "raw.githubusercontent.com";
    private const string CdnHost = "cdn.jsdelivr.net";
    private const string FastlyHost = "fastly.jsdelivr.net";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // One process-wide instance: sockets are pooled per handler, and a client per fetch would leave
    // connections behind. Created on first use only, so merely constructing a client sends nothing.
    private static readonly Lazy<HttpClient> Http = new(
        () => new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly SharedCalibrationTransport _transport;
    private readonly TimeSpan _sourceTimeout;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates a client over a transport.</summary>
    /// <param name="transport">Sends one request; <see cref="HttpTransport"/> in production.</param>
    /// <param name="sourceTimeout">Budget of one request; <see cref="DefaultSourceTimeout"/> when null.</param>
    /// <param name="readEnvironment">Reads an environment variable; the process environment when null.</param>
    public SharedCalibrationClient(
        SharedCalibrationTransport transport, TimeSpan? sourceTimeout = null, Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var timeout = sourceTimeout ?? DefaultSourceTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTimeout), "the per-source timeout must be positive");
        }

        _transport = transport;
        _sourceTimeout = timeout;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The sources, in the order every request tries them.</summary>
    public static IReadOnlyList<SharedCalibrationSource> SourceOrder { get; } = Array.AsReadOnly(new[]
    {
        SharedCalibrationSource.GithubRaw, SharedCalibrationSource.CdnPrimary, SharedCalibrationSource.CdnFallback,
    });

    /// <summary>Every host an answer may come from.</summary>
    public static IReadOnlyCollection<string> AllowedHosts { get; } = Array.AsReadOnly(new[] { RawHost, CdnHost, FastlyHost });

    /// <summary>Production transport: the process-wide client, no redirects, no cookies, the system proxy.</summary>
    public static SharedCalibrationTransport HttpTransport { get; } = SendAsync;

    /// <summary>The production client. Constructing it sends nothing.</summary>
    public static SharedCalibrationClient CreateDefault() => new(HttpTransport);

    /// <summary>Host name of a source.</summary>
    /// <param name="source">A source.</param>
    public static string HostFor(SharedCalibrationSource source) => source switch
    {
        SharedCalibrationSource.GithubRaw => RawHost,
        SharedCalibrationSource.CdnPrimary => CdnHost,
        SharedCalibrationSource.CdnFallback => FastlyHost,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Address of the index on a source, read from <see cref="Branch"/>.</summary>
    /// <param name="source">A source.</param>
    public static Uri IndexUri(SharedCalibrationSource source) => Address(source, Branch, IndexFileName);

    /// <summary>Address of a code file, pinned to the commit that added it so no cache can serve other content.</summary>
    /// <param name="source">A source.</param>
    /// <param name="commit">Full commit id from the index.</param>
    /// <param name="path">Code path from the index (<see cref="SharedCalibrationIndex.CodePath"/>).</param>
    public static Uri CodeUri(SharedCalibrationSource source, string commit, string path)
    {
        if (!SharedCalibrationIndex.IsCommit(commit))
        {
            throw new ArgumentException("not a commit id", nameof(commit));
        }

        if (!SharedCalibrationIndex.IsCodePath(path))
        {
            throw new ArgumentException("not a code path", nameof(path));
        }

        return Address(source, commit, path);
    }

    /// <summary>
    /// True when a value of <see cref="DisableVariable"/> turns fetching off: anything except empty,
    /// <c>0</c> or <c>false</c>. A kill switch errs towards off.
    /// </summary>
    /// <param name="value">The variable's value, or null when unset.</param>
    public static bool IsDisabled(string? value)
    {
        var trimmed = value?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed != "0" &&
               !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fetches the index and the codes it lists for a region and build. Never throws once its
    /// arguments are valid: cancellation and every network failure end up on the result.
    /// </summary>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build (<see cref="SharedCalibrationIndex.IsBuild"/>).</param>
    /// <param name="cancellationToken">Stops the fetch; the result then says CANCELLED.</param>
    public async Task<SharedCalibrationFetchResult> FetchAsync(
        Region region, string gameBuild, CancellationToken cancellationToken = default)
    {
        if (region is not (Region.Cn or Region.Global))
        {
            throw new ArgumentException("only CN and GLOBAL have shared calibrations", nameof(region));
        }

        if (!SharedCalibrationIndex.IsBuild(gameBuild))
        {
            throw new ArgumentException("not a client build", nameof(gameBuild));
        }

        if (IsDisabled(_readEnvironment(DisableVariable)))
        {
            return SharedCalibrationFetchResult.Disabled;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return SharedCalibrationFetchResult.Empty(SharedFetchStatus.Cancelled);
        }

        var attempts = new List<SharedSourceAttempt>();
        foreach (var source in SourceOrder)
        {
            var (attempt, body) = await GetAsync(source, IndexUri(source), MaxIndexBytes, cancellationToken).ConfigureAwait(false);
            if (attempt.Outcome == SharedFetchOutcome.Ok)
            {
                var index = SharedCalibrationIndex.Read(body);
                if (index.IsReadable)
                {
                    attempts.Add(attempt);
                    return await FetchCodesAsync(region, gameBuild, source, index, attempts, cancellationToken).ConfigureAwait(false);
                }

                attempt = attempt with { Outcome = SharedFetchOutcome.Malformed, Detail = index.Refusal };
            }

            attempts.Add(attempt);
            if (attempt.Outcome == SharedFetchOutcome.Cancelled)
            {
                return SharedCalibrationFetchResult.Empty(SharedFetchStatus.Cancelled, attempts);
            }
        }

        return SharedCalibrationFetchResult.Empty(SharedFetchStatus.IndexUnavailable, attempts);
    }

    private async Task<SharedCalibrationFetchResult> FetchCodesAsync(
        Region region,
        string gameBuild,
        SharedCalibrationSource indexSource,
        SharedIndexReadResult index,
        IReadOnlyList<SharedSourceAttempt> indexAttempts,
        CancellationToken cancellationToken)
    {
        var revoked = SharedCalibrationIndex.Revoked(index.Entries, region, gameBuild);
        var chosen = SharedCalibrationIndex.Select(index.Entries, region, gameBuild);
        if (chosen.Count == 0)
        {
            return new SharedCalibrationFetchResult(SharedFetchStatus.NoneForBuild, indexAttempts,
                Array.Empty<SharedCalibrationCandidate>(), Array.Empty<SharedCodeDiscard>(), index.Skipped, revoked);
        }

        // The source that just served the index is known to answer, so codes are asked there first.
        var order = SourceOrder.Where(source => source == indexSource)
            .Concat(SourceOrder.Where(source => source != indexSource))
            .ToArray();
        var candidates = new List<SharedCalibrationCandidate>();
        var discards = new List<SharedCodeDiscard>();
        foreach (var entry in chosen)
        {
            var (candidate, discard) = await FetchCodeAsync(entry, order, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }

            if (discard is not null)
            {
                discards.Add(discard);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new SharedCalibrationFetchResult(SharedFetchStatus.Cancelled, indexAttempts, candidates, discards,
                    index.Skipped, revoked);
            }
        }

        var status = candidates.Count > 0 ? SharedFetchStatus.Ok : SharedFetchStatus.CodesUnavailable;
        return new SharedCalibrationFetchResult(status, indexAttempts, candidates, discards, index.Skipped, revoked);
    }

    private async Task<(SharedCalibrationCandidate? Candidate, SharedCodeDiscard? Discard)> FetchCodeAsync(
        SharedIndexEntry entry, IReadOnlyList<SharedCalibrationSource> order, CancellationToken cancellationToken)
    {
        var attempts = new List<SharedSourceAttempt>();
        var reason = "UNREACHABLE";
        foreach (var source in order)
        {
            var uri = CodeUri(source, entry.Commit, entry.Path);
            var (attempt, body) = await GetAsync(source, uri, MaxCodeBytes, cancellationToken).ConfigureAwait(false);
            if (attempt.Outcome == SharedFetchOutcome.Ok)
            {
                var (candidate, refusal) = Check(entry, body.Span);
                if (candidate is not null)
                {
                    return (candidate, null);
                }

                // Content is pinned to a commit, so a wrong answer is one damaged mirror: try the next.
                reason = refusal!;
                attempt = attempt with { Outcome = SharedFetchOutcome.Malformed, Detail = refusal };
            }

            attempts.Add(attempt);
            if (attempt.Outcome == SharedFetchOutcome.Cancelled)
            {
                reason = reason == "UNREACHABLE" ? "CANCELLED" : reason;
                break;
            }
        }

        return (null, new SharedCodeDiscard(entry.CodeSha256, reason, attempts));
    }

    private static (SharedCalibrationCandidate? Candidate, string? Refusal) Check(SharedIndexEntry entry, ReadOnlySpan<byte> body)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(SharedCalibrationIndex.WithoutBom(body));
        }
        catch (DecoderFallbackException)
        {
            return (null, "NOT_UTF8");
        }

        var decoded = ShareCode.Decode(text);
        if (decoded.Payload is not { } payload)
        {
            return (null, "UNDECODABLE:" + decoded.Rejection?.Code);
        }

        if (!string.Equals(decoded.CodeSha256, entry.CodeSha256, StringComparison.Ordinal))
        {
            return (null, "HASH_MISMATCH");
        }

        if (payload.Region != entry.Region)
        {
            return (null, "PAYLOAD_MISMATCH:region");
        }

        if (!string.Equals(payload.GameBuild, entry.GameBuild, StringComparison.Ordinal))
        {
            return (null, "PAYLOAD_MISMATCH:game_build");
        }

        if (payload.MatchSource != entry.MatchSource)
        {
            return (null, "PAYLOAD_MISMATCH:match_source");
        }

        return (new SharedCalibrationCandidate(
            entry.CodeSha256, text.Trim(), payload, entry.Submitters, entry.FirstPublishedAtUtc, entry.Commit, entry.Conflicting), null);
    }

    private async Task<(SharedSourceAttempt Attempt, ReadOnlyMemory<byte> Body)> GetAsync(
        SharedCalibrationSource source, Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_sourceTimeout);
        try
        {
            using var response = await _transport(uri, budget.Token).ConfigureAwait(false);
            var status = response.StatusCode;
            if (status is >= 300 and < 400)
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.RedirectRefused, status), default);
            }

            if (status is < 200 or >= 300)
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.HttpStatus, status), default);
            }

            if (!IsAllowed(response.FinalUri))
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.HostRefused, status), default);
            }

            if (response.FinalUri != uri)
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.RedirectRefused, status), default);
            }

            if (response.ContentLength > maxBytes)
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.TooLarge, status), default);
            }

            var body = await ReadCappedAsync(response.Body, maxBytes, budget.Token).ConfigureAwait(false);
            if (body is null)
            {
                return (new SharedSourceAttempt(source, SharedFetchOutcome.TooLarge, status), default);
            }

            return (new SharedSourceAttempt(source, SharedFetchOutcome.Ok, status), body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (new SharedSourceAttempt(source, SharedFetchOutcome.Cancelled), default);
        }
        catch (OperationCanceledException)
        {
            return (new SharedSourceAttempt(source, SharedFetchOutcome.Timeout), default);
        }
        catch (HttpRequestException ex)
        {
            return (Classify(source, ex), default);
        }
        catch (IOException)
        {
            return (new SharedSourceAttempt(source, SharedFetchOutcome.ReadFailed), default);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A fetch runs in the background and must never take the Collector down; the type name
            // is kept for diagnostics, the message is not (it can carry a proxy address).
            return (new SharedSourceAttempt(source, SharedFetchOutcome.TransportFailed, null, ex.GetType().Name), default);
        }
    }

    private static SharedSourceAttempt Classify(SharedCalibrationSource source, HttpRequestException ex)
    {
        var detail = UpperSnakeCaseNamingPolicy.Instance.ConvertName(ex.HttpRequestError.ToString());
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException)
        {
            return new SharedSourceAttempt(source, SharedFetchOutcome.TlsFailed, null, detail);
        }

        return ex.InnerException is TimeoutException
            ? new SharedSourceAttempt(source, SharedFetchOutcome.Timeout)
            : new SharedSourceAttempt(source, SharedFetchOutcome.DnsOrConnect, null, detail);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; null the moment one byte more arrives.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        return null;
    }

    private static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static Uri Address(SharedCalibrationSource source, string reference, string path) => new(source switch
    {
        SharedCalibrationSource.GithubRaw => "https://" + RawHost + "/" + Owner + "/" + Repository + "/" + reference + "/" + path,
        SharedCalibrationSource.CdnPrimary => "https://" + CdnHost + "/gh/" + Owner + "/" + Repository + "@" + reference + "/" + path,
        SharedCalibrationSource.CdnFallback => "https://" + FastlyHost + "/gh/" + Owner + "/" + Repository + "@" + reference + "/" + path,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    }, UriKind.Absolute);

    // ------------------------------------------------------------ production transport

    /// <summary>
    /// The handler behind <see cref="HttpTransport"/>: redirects and cookies off, no credentials of
    /// any kind, no automatic decompression (so no Accept-Encoding header), the system proxy, and a
    /// connect timeout equal to the per-source budget. Idle connections are closed quickly so a
    /// fetch does not linger in <c>netstat</c>.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = true,
        Proxy = null,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = DefaultSourceTimeout,
        MaxResponseHeadersLength = 64,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>A plain HTTP/1.1 GET whose only header is <see cref="UserAgent"/>.</summary>
    /// <param name="uri">Address to fetch.</param>
    internal static HttpRequestMessage CreateRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        return request;
    }

    private static async Task<SharedTransportResponse> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowed(uri) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("the shared-calibration transport only fetches its own fixed addresses", nameof(uri));
        }

        using var request = CreateRequest(uri);
        var response = await Http.Value
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new SharedTransportResponse(
                (int)response.StatusCode,
                response.RequestMessage?.RequestUri ?? uri,
                response.Content.Headers.ContentLength,
                body,
                response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
