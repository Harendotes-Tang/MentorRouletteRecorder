using System.Net;
using System.Security.Authentication;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// Downloads the metadata published beside the newest release, so the software can say that a newer
/// version exists (docs/privacy-boundary.md §8.4).
///
/// Notification only. Nothing is downloaded but this one small JSON document, nothing is written to
/// disk from it, and nothing is ever executed: the user is shown a version and a link and decides
/// for themselves.
///
/// This file is deliberately the only place in this feature that names <c>HttpClient</c> (static rule
/// NET-006) and the source host names (NET-007). Everything else - the service, the wire and every
/// test - sees <see cref="UpdateCheckTransport"/>, a delegate returning a response head and an
/// unread body; <see cref="HttpTransport"/> is the production one.
///
/// Enforced here whatever the transport is:
/// <list type="bullet">
/// <item><see cref="DisableVariable"/> is read on every check, before anything is sent.</item>
/// <item>One fixed address is asked (<see cref="MetadataUri"/>): no query, no fragment.</item>
/// <item>Redirects are followed explicitly, at most <see cref="MaxRedirects"/> hops, never by the
/// transport. Each target must be https on its default port, carry no user information and sit on
/// an allowed host; a query string is accepted on a redirect target only, because a signed asset
/// address needs one.</item>
/// <item>The body is refused on a declared length over <see cref="MaxBodyBytes"/> and abandoned the
/// moment it passes it while being read.</item>
/// <item>The whole chain runs under one <see cref="DefaultTimeout"/>.</item>
/// <item>The document is untrusted input (<see cref="UpdateMetadata"/>).</item>
/// </list>
/// Every failure becomes an outcome on the result rather than an exception, and the result names no
/// address.
/// </summary>
public sealed class UpdateCheckClient
{
    /// <summary>Owner of the release repository.</summary>
    public const string Owner = "Harendotes-Tang";

    /// <summary>Name of the release repository.</summary>
    public const string Repository = "MentorRouletteRecorder";

    /// <summary>The release asset read; published beside every release as a standalone file.</summary>
    public const string AssetFileName = "BUILD-METADATA.json";

    /// <summary>The one request header this client sets; deliberately without a version.</summary>
    public const string UserAgent = "MentorRecorder";

    /// <summary>Largest document accepted, in bytes, counted while reading.</summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Most redirects followed before the check is given up.</summary>
    public const int MaxRedirects = 3;

    /// <summary>Environment variable that stops every check before anything is sent (<see cref="IsDisabled"/>).</summary>
    public const string DisableVariable = "MR_DISABLE_UPDATE_CHECK";

    /// <summary>Budget of one whole check: connect, headers, body and every redirect together.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string ReleaseHost = "github.com";
    private const string ContentHost = "githubusercontent.com";

    // One process-wide instance: sockets are pooled per handler, and a client per check would leave
    // connections behind. Created on first use only, so merely constructing a client sends nothing.
    private static readonly Lazy<HttpClient> Http = new(
        () => new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly UpdateCheckTransport _transport;
    private readonly TimeSpan _timeout;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates a client over a transport.</summary>
    /// <param name="transport">Sends one request; <see cref="HttpTransport"/> in production.</param>
    /// <param name="timeout">Budget of the whole check; <see cref="DefaultTimeout"/> when null.</param>
    /// <param name="readEnvironment">Reads an environment variable; the process environment when null.</param>
    public UpdateCheckClient(
        UpdateCheckTransport transport, TimeSpan? timeout = null, Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var budget = timeout ?? DefaultTimeout;
        if (budget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "the check timeout must be positive");
        }

        _transport = transport;
        _timeout = budget;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Every host an answer or a redirect target may come from; subdomains of the content host included.</summary>
    public static IReadOnlyCollection<string> AllowedHosts { get; } = Array.AsReadOnly(new[] { ReleaseHost, ContentHost });

    /// <summary>The release page the user is sent to. Opened by a human, never by this software.</summary>
    public static string ReleaseUrl { get; } = "https://" + ReleaseHost + "/" + Owner + "/" + Repository + "/releases/latest";

    /// <summary>Production transport: the process-wide client, no redirects, no cookies, the system proxy.</summary>
    public static UpdateCheckTransport HttpTransport { get; } = SendAsync;

    /// <summary>True when the kill switch stops every check of this process right now.</summary>
    public bool IsDisabledNow => IsDisabled(_readEnvironment(DisableVariable));

    /// <summary>The production client. Constructing it sends nothing.</summary>
    public static UpdateCheckClient CreateDefault() => new(HttpTransport);

    /// <summary>The one address this client asks: the newest release's metadata asset.</summary>
    public static Uri MetadataUri() => new(
        "https://" + ReleaseHost + "/" + Owner + "/" + Repository + "/releases/latest/download/" + AssetFileName,
        UriKind.Absolute);

    /// <summary>
    /// True when a value of <see cref="DisableVariable"/> turns the check off: anything except empty,
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
    /// Fetches the published version. Never throws: cancellation and every network failure end up on
    /// the result.
    /// </summary>
    /// <param name="cancellationToken">Stops the check; the result then says CANCELLED.</param>
    public async Task<UpdateCheckResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisabledNow)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Disabled);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Cancelled);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            return await GetAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Timeout);
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
        catch (IOException ex)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.TransportFailed, Detail: ex.GetType().Name);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // A check runs in the background and must never take the Collector down; the type name
            // is kept for diagnostics, the message is not (it can carry a proxy address).
            return new UpdateCheckResult(UpdateCheckOutcome.TransportFailed, Detail: ex.GetType().Name);
        }
    }

    private async Task<UpdateCheckResult> GetAsync(CancellationToken cancellationToken)
    {
        var uri = MetadataUri();
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await _transport(uri, cancellationToken).ConfigureAwait(false);
            var status = response.StatusCode;
            if (status is >= 300 and < 400)
            {
                if (hop == MaxRedirects || response.Location is not { } location)
                {
                    return new UpdateCheckResult(UpdateCheckOutcome.RedirectRefused, StatusCode: status);
                }

                if (!Uri.TryCreate(uri, location, out var target))
                {
                    return new UpdateCheckResult(UpdateCheckOutcome.RedirectRefused, StatusCode: status);
                }

                if (!IsAllowed(target))
                {
                    return new UpdateCheckResult(UpdateCheckOutcome.HostRefused, StatusCode: status);
                }

                uri = target;
                continue;
            }

            if (!IsAllowed(response.FinalUri))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.HostRefused, StatusCode: status);
            }

            if (status is 403 or 429)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.RateLimited, StatusCode: status);
            }

            if (status == 404)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.NotFound, StatusCode: status);
            }

            if (status != 200)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.HttpStatus, StatusCode: status);
            }

            if (response.ContentLength > MaxBodyBytes)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.TooLarge, StatusCode: status);
            }

            var body = await ReadCappedAsync(response.Body, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.TooLarge, StatusCode: status);
            }

            var metadata = UpdateMetadata.Read(body);
            return metadata.Version is { } version
                ? new UpdateCheckResult(UpdateCheckOutcome.Ok, version, status)
                : new UpdateCheckResult(UpdateCheckOutcome.Malformed, StatusCode: status, Detail: metadata.Refusal);
        }

        return new UpdateCheckResult(UpdateCheckOutcome.RedirectRefused);
    }

    private static UpdateCheckResult Classify(HttpRequestException ex)
    {
        var detail = UpperSnakeCaseNamingPolicy.Instance.ConvertName(ex.HttpRequestError.ToString());
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError || ex.InnerException is AuthenticationException)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.TlsFailed, Detail: detail);
        }

        return ex.InnerException is TimeoutException
            ? new UpdateCheckResult(UpdateCheckOutcome.Timeout)
            : new UpdateCheckResult(UpdateCheckOutcome.DnsOrConnect, Detail: detail);
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

    /// <summary>
    /// True for an address this client will ask: https, default port, no user information, and a
    /// host that is exactly one of <see cref="AllowedHosts"/> or a subdomain of the content host.
    /// A look-alike such as <c>&lt;host&gt;.example.com</c> fails both tests.
    /// </summary>
    /// <param name="uri">Candidate address.</param>
    private static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
         uri.Host.EndsWith("." + ContentHost, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ production transport

    /// <summary>
    /// The handler behind <see cref="HttpTransport"/>: redirects and cookies off, no credentials of
    /// any kind, no automatic decompression (so no Accept-Encoding header), the system proxy, and a
    /// connect timeout equal to the whole budget. Idle connections are closed quickly so a check does
    /// not linger in <c>netstat</c>.
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
        ConnectTimeout = DefaultTimeout,
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

    private static async Task<UpdateTransportResponse> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowed(uri) || uri.Fragment.Length > 0)
        {
            throw new ArgumentException("the update-check transport only fetches its own fixed addresses", nameof(uri));
        }

        using var request = CreateRequest(uri);
        var response = await Http.Value
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new UpdateTransportResponse(
                (int)response.StatusCode,
                response.RequestMessage?.RequestUri ?? uri,
                response.Headers.Location,
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
