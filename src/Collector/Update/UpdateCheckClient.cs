using System.Net;
using System.Security.Authentication;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Update;

/// <summary>
/// Downloads the metadata published beside the newest release, so the software can say that a newer
/// version exists (docs/privacy-boundary.md §8.4).
///
/// The check is notification only: this one small JSON document is read, nothing is written to disk
/// from it, and nothing is ever executed. Only when the user asks for it (下载并安装,
/// <see cref="UpdateDownloadService"/>) does this client also send the two requests of a download -
/// the checksum published beside a version's installer (<see cref="FetchChecksumAsync"/>) and the
/// installer itself (<see cref="OpenInstallerAsync"/>) - and even then it never runs what it fetched.
///
/// This file is deliberately the only place in this feature that names <c>HttpClient</c> (static rule
/// NET-006) and the source host names (NET-007). Everything else - the services, the wire and every
/// test - sees <see cref="UpdateCheckTransport"/>, a delegate returning a response head and an
/// unread body; <see cref="HttpTransport"/> is the production one.
///
/// Enforced here whatever the transport is, for every request:
/// <list type="bullet">
/// <item><see cref="DisableVariable"/> is read before anything is sent.</item>
/// <item>Only fixed addresses built from constants and a version of three plain numbers are asked
/// (<see cref="MetadataUri"/>, <see cref="ChecksumUrl"/>, <see cref="InstallerUrl"/>): no query, no fragment.</item>
/// <item>Redirects are followed explicitly, at most <see cref="MaxRedirects"/> hops, never by the
/// transport. Each target must be https on its default port, carry no user information and sit on
/// an allowed host; a query string is accepted on a redirect target only, because a signed asset
/// address needs one. The two requests of a download are held to a narrower rule still, because
/// what they fetch is run with administrator rights: on the release host every hop stays inside
/// that version's release folder, and anywhere else it is one of <see cref="AssetHosts"/>.</item>
/// <item>A body is refused on a declared length over its cap (<see cref="MaxBodyBytes"/>,
/// <see cref="MaxChecksumBytes"/>, the installer's cap from the caller); the two small documents are
/// also abandoned the moment they pass it while being read.</item>
/// <item>The metadata and the checksum each run under one <see cref="DefaultTimeout"/>; the installer
/// runs under the caller's limits.</item>
/// <item>What comes back is untrusted input (<see cref="UpdateMetadata"/>, <see cref="UpdateChecksum"/>).</item>
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

    /// <summary>Largest checksum document accepted, in bytes, counted while reading.</summary>
    public const int MaxChecksumBytes = 1024;

    /// <summary>Appended to an installer's address to name the checksum published beside it.</summary>
    public const string ChecksumSuffix = ".sha256";

    /// <summary>Most redirects followed before the check is given up.</summary>
    public const int MaxRedirects = 3;

    /// <summary>Environment variable that stops every check before anything is sent (<see cref="IsDisabled"/>).</summary>
    public const string DisableVariable = "MR_DISABLE_UPDATE_CHECK";

    /// <summary>Budget of one whole check: connect, headers, body and every redirect together.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const string ReleaseHost = "github.com";
    private const string ContentHost = "githubusercontent.com";

    // Where GitHub serves this project's release assets from (it redirects them to the first today).
    private const string ReleaseAssetHost = "release-assets." + ContentHost;
    private const string ObjectHost = "objects." + ContentHost;

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

    /// <summary>
    /// The only hosts the content of a download - the checksum and the installer - may come from: where GitHub serves
    /// this project's release assets. Much narrower than the content host's subdomains, under several of which any
    /// GitHub user can publish.
    /// </summary>
    public static IReadOnlyCollection<string> AssetHosts { get; } = Array.AsReadOnly(new[] { ReleaseAssetHost, ObjectHost });

    /// <summary>The release page the user is sent to. Opened by a human, never by this software.</summary>
    public static string ReleaseUrl { get; } = "https://" + ReleaseHost + "/" + Owner + "/" + Repository + "/releases/latest";

    /// <summary>
    /// Where the installer of a published version is: the asset <c>MentorRecorder-&lt;version&gt;-setup.exe</c> of
    /// the release tagged <c>v&lt;version&gt;</c>. Reported for the user's browser to open, and fetched by
    /// <see cref="OpenInstallerAsync"/> only when the user asks this software to download it. Null for anything
    /// but three plain numbers (<see cref="UpdateMetadata.IsVersion"/>), so nothing else can end up in the address.
    /// </summary>
    /// <param name="version">A published version.</param>
    public static string? InstallerUrl(string? version) =>
        UpdateMetadata.IsVersion(version)
            ? "https://" + ReleaseHost + "/" + Owner + "/" + Repository + "/releases/download/v" + version +
              "/MentorRecorder-" + version + "-setup.exe"
            : null;

    /// <summary>
    /// Where the SHA-256 of a published version's installer is: the installer's address plus
    /// <see cref="ChecksumSuffix"/>, an asset published beside it. Null exactly when <see cref="InstallerUrl"/> is.
    /// </summary>
    /// <param name="version">A published version.</param>
    public static string? ChecksumUrl(string? version) =>
        InstallerUrl(version) is { } installer ? installer + ChecksumSuffix : null;

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
    public Task<UpdateCheckResult> FetchAsync(CancellationToken cancellationToken = default) =>
        GuardAsync(GetMetadataAsync, _timeout, static refusal => refusal, cancellationToken);

    /// <summary>
    /// Fetches the SHA-256 published beside a version's installer (<see cref="ChecksumUrl"/>): at most
    /// <see cref="MaxChecksumBytes"/>, under one <see cref="DefaultTimeout"/>, read strictly
    /// (<see cref="UpdateChecksum"/>). Never throws for a failure of the request; anything that is no
    /// checksum is <see cref="UpdateCheckOutcome.Malformed"/>.
    /// </summary>
    /// <param name="version">A published version: three plain numbers.</param>
    /// <param name="cancellationToken">Stops the request; the result then says CANCELLED.</param>
    /// <exception cref="ArgumentException">The version is not three plain numbers.</exception>
    public Task<UpdateChecksumResult> FetchChecksumAsync(string version, CancellationToken cancellationToken = default)
    {
        var uri = Published(ChecksumUrl(version), nameof(version));
        var allowed = ReleaseAsset(version);
        return GuardAsync(
            token => GetChecksumAsync(uri, allowed, token), _timeout, UpdateChecksumResult.From, cancellationToken);
    }

    /// <summary>
    /// Asks for a version's installer (<see cref="InstallerUrl"/>) and returns the answer with its body
    /// unread, for the caller to stream; a declared length over <paramref name="maxBytes"/> is refused
    /// and released at once. No budget of its own: the caller's token carries the download's limits.
    /// Never throws for a failure of the request.
    /// </summary>
    /// <param name="version">A published version: three plain numbers.</param>
    /// <param name="maxBytes">Largest installer the caller accepts.</param>
    /// <param name="cancellationToken">Stops the request; the result then says CANCELLED.</param>
    /// <exception cref="ArgumentException">The version is not three plain numbers.</exception>
    public Task<UpdateInstallerResponse> OpenInstallerAsync(
        string version, long maxBytes, CancellationToken cancellationToken = default)
    {
        var uri = Published(InstallerUrl(version), nameof(version));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        var allowed = ReleaseAsset(version);
        return GuardAsync(
            token => OpenAsync(uri, maxBytes, allowed, token), null, UpdateInstallerResponse.Refused, cancellationToken);
    }

    /// <summary>
    /// What a failure of a request comes to. A cancellation is CANCELLED when the caller asked for it and
    /// TIMEOUT otherwise; the type name of anything unexpected is kept for diagnostics, its message is not
    /// (it can carry a proxy address).
    /// </summary>
    /// <param name="error">What the transport, or the body being read, threw.</param>
    /// <param name="callerToken">The caller's own token.</param>
    internal static UpdateCheckResult Describe(Exception error, CancellationToken callerToken) => error switch
    {
        OperationCanceledException when callerToken.IsCancellationRequested => new(UpdateCheckOutcome.Cancelled),
        OperationCanceledException => new(UpdateCheckOutcome.Timeout),
        HttpRequestException http => Classify(http),
        _ => new(UpdateCheckOutcome.TransportFailed, Detail: error.GetType().Name),
    };

    // The kill switch, a token already cancelled, the budget and every failure, for all three requests alike: a
    // request runs in the background and must never take the Collector down.
    private async Task<T> GuardAsync<T>(
        Func<CancellationToken, Task<T>> request,
        TimeSpan? budget,
        Func<UpdateCheckResult, T> refuse,
        CancellationToken cancellationToken)
    {
        if (IsDisabledNow)
        {
            return refuse(new UpdateCheckResult(UpdateCheckOutcome.Disabled));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return refuse(new UpdateCheckResult(UpdateCheckOutcome.Cancelled));
        }

        using var scope = budget.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        if (budget is { } limit)
        {
            scope!.CancelAfter(limit);
        }

        try
        {
            return await request(scope?.Token ?? cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return refuse(Describe(ex, cancellationToken));
        }
    }

    private async Task<UpdateCheckResult> GetMetadataAsync(CancellationToken cancellationToken)
    {
        var followed = await FollowAsync(MetadataUri(), IsAllowed, cancellationToken).ConfigureAwait(false);
        if (followed.Answer is not { } answer)
        {
            return followed.Refusal!;
        }

        using (answer)
        {
            var status = answer.StatusCode;
            var body = await ReadBodyAsync(answer, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return new UpdateCheckResult(UpdateCheckOutcome.TooLarge, StatusCode: status);
            }

            var metadata = UpdateMetadata.Read(body);
            return metadata.Version is { } version
                ? new UpdateCheckResult(UpdateCheckOutcome.Ok, version, status)
                : new UpdateCheckResult(UpdateCheckOutcome.Malformed, StatusCode: status, Detail: metadata.Refusal);
        }
    }

    private async Task<UpdateChecksumResult> GetChecksumAsync(
        Uri uri, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        var followed = await FollowAsync(uri, allowed, cancellationToken).ConfigureAwait(false);
        if (followed.Answer is not { } answer)
        {
            return UpdateChecksumResult.From(followed.Refusal!);
        }

        using (answer)
        {
            var status = answer.StatusCode;
            var body = await ReadBodyAsync(answer, MaxChecksumBytes, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return new UpdateChecksumResult(UpdateCheckOutcome.TooLarge, StatusCode: status);
            }

            return UpdateChecksum.TryRead(body, out var sha256)
                ? new UpdateChecksumResult(UpdateCheckOutcome.Ok, sha256, status)
                : new UpdateChecksumResult(UpdateCheckOutcome.Malformed, StatusCode: status);
        }
    }

    private async Task<UpdateInstallerResponse> OpenAsync(
        Uri uri, long maxBytes, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        var followed = await FollowAsync(uri, allowed, cancellationToken).ConfigureAwait(false);
        if (followed.Answer is not { } answer)
        {
            return UpdateInstallerResponse.Refused(followed.Refusal!);
        }

        if (answer.ContentLength > maxBytes)
        {
            answer.Dispose();
            return UpdateInstallerResponse.Refused(
                new UpdateCheckResult(UpdateCheckOutcome.TooLarge, StatusCode: answer.StatusCode));
        }

        return UpdateInstallerResponse.Opened(answer);
    }

    /// <summary>
    /// Asks <paramref name="start"/> and follows redirects one hop at a time, checking every target. Ends
    /// with the first 200 from an allowed address, its body unread and owned by the caller, or with why
    /// there is none; every other answer on the way is released here.
    /// </summary>
    /// <param name="start">The fixed address of the request.</param>
    /// <param name="allowed">The rule every address on the way must pass.</param>
    /// <param name="cancellationToken">The request's token.</param>
    private async Task<Followed> FollowAsync(Uri start, Func<Uri, bool> allowed, CancellationToken cancellationToken)
    {
        var uri = start;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await _transport(uri, cancellationToken).ConfigureAwait(false);
            var refusal = Judge(response, uri, hop, allowed, out var next);
            if (refusal is null && next is null)
            {
                return new Followed(response, null);
            }

            response.Dispose();
            if (refusal is not null)
            {
                return new Followed(null, refusal);
            }

            uri = next!;
        }

        return new Followed(null, new UpdateCheckResult(UpdateCheckOutcome.RedirectRefused));
    }

    /// <summary>
    /// Null with no <paramref name="next"/> for a 200 from an allowed address; null with the target for a
    /// redirect that may be followed; otherwise why the chain ends here.
    /// </summary>
    private static UpdateCheckResult? Judge(
        UpdateTransportResponse response, Uri asked, int hop, Func<Uri, bool> allowed, out Uri? next)
    {
        next = null;
        var status = response.StatusCode;
        if (status is >= 300 and < 400)
        {
            if (hop == MaxRedirects || response.Location is not { } location ||
                !Uri.TryCreate(asked, location, out var target))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.RedirectRefused, StatusCode: status);
            }

            if (!allowed(target))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.HostRefused, StatusCode: status);
            }

            next = target;
            return null;
        }

        if (!allowed(response.FinalUri))
        {
            return new UpdateCheckResult(UpdateCheckOutcome.HostRefused, StatusCode: status);
        }

        return status switch
        {
            403 or 429 => new UpdateCheckResult(UpdateCheckOutcome.RateLimited, StatusCode: status),
            404 => new UpdateCheckResult(UpdateCheckOutcome.NotFound, StatusCode: status),
            200 => null,
            _ => new UpdateCheckResult(UpdateCheckOutcome.HttpStatus, StatusCode: status),
        };
    }

    private static Uri Published(string? address, string parameter) =>
        address is null
            ? throw new ArgumentException("not a published version", parameter)
            : new Uri(address, UriKind.Absolute);

    /// <summary>The body of a small document: null when its declared length, or what arrives, passes the cap.</summary>
    private static async Task<byte[]?> ReadBodyAsync(
        UpdateTransportResponse answer, int maxBytes, CancellationToken cancellationToken) =>
        answer.ContentLength > maxBytes
            ? null
            : await ReadCappedAsync(answer.Body, maxBytes, cancellationToken).ConfigureAwait(false);

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

    /// <summary>Where a chain of redirects ended: an answer to read, or why there is none.</summary>
    private readonly record struct Followed(UpdateTransportResponse? Answer, UpdateCheckResult? Refusal);

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

    /// <summary>
    /// The rule of the two download requests, on top of <see cref="IsAllowed"/>. On the release host an
    /// address must stay inside this repository's folder of that one release,
    /// <c>/&lt;owner&gt;/&lt;repository&gt;/releases/download/v&lt;version&gt;/</c>, with no escaped
    /// character a server could read as another path; anywhere else it must be on one of
    /// <see cref="AssetHosts"/>. The metadata check keeps the wider rule.
    /// </summary>
    /// <param name="version">A published version: three plain numbers.</param>
    private static Func<Uri, bool> ReleaseAsset(string version)
    {
        var folder = "/" + Owner + "/" + Repository + "/releases/download/v" + version + "/";
        return uri => IsAllowed(uri) &&
                      (string.Equals(uri.Host, ReleaseHost, StringComparison.OrdinalIgnoreCase)
                          ? uri.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) &&
                            !uri.AbsolutePath.Contains('%')
                          : AssetHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase));
    }

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
