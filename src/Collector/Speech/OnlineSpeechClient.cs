using System.Net;
using System.Net.Http.Headers;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Speech;

/// <summary>
/// Online speech synthesis: the second outbound request class the released software may make, and
/// only after the user turned it on (docs/privacy-boundary.md §8.3).
///
/// This file is deliberately the only speech file that names <c>HttpClient</c> (static rule NET-006)
/// and the only file anywhere that names the Azure speech host (NET-007). Everything else - the
/// service, the cache and every test - sees <see cref="OnlineSpeechTransport"/>, a delegate returning
/// a response head and an unread body; <see cref="HttpTransport"/> is the production one.
///
/// Enforced here whatever the transport is:
/// <list type="bullet">
/// <item><see cref="DisableVariable"/> is read before every request, before anything is sent.</item>
/// <item>Azure is asked at <c>https://{region}.tts.speech.microsoft.com/cognitiveservices/v1</c> with a
/// validated region; an OpenAI-compatible service at <c>{base_url}/audio/speech</c> with a validated
/// base address (https, or http to loopback only; no user info, query or fragment).</item>
/// <item>The body carries the sentence, the voice and the rate; the headers carry the key, the media
/// types and <see cref="UserAgent"/>; nothing else.</item>
/// <item>Each request has its own <see cref="DefaultRequestTimeout"/> covering connect, headers and body.</item>
/// <item>A status outside 2xx fails (401/403 auth, 429 quota); 3xx is a refused redirect; an answer from
/// any address but the requested one is refused.</item>
/// <item>The body is counted while it is read and abandoned past <see cref="MaxResponseBytes"/>; its
/// type must be <c>audio/*</c> or <c>application/octet-stream</c>; it must be 16-bit PCM WAV.</item>
/// </list>
/// Every failure becomes an outcome rather than an exception. Response bodies are never kept, logged
/// or passed on: a service's error page can quote the request, and the request carries the key.
/// </summary>
public sealed class OnlineSpeechClient
{
    /// <summary>Environment variable that stops every request before it is sent.</summary>
    public const string DisableVariable = "MR_DISABLE_ONLINE_SPEECH";

    /// <summary>The fixed User-Agent; deliberately without a version.</summary>
    public const string UserAgent = "MentorRecorder";

    /// <summary>Largest answer accepted, in bytes, counted while reading.</summary>
    public const int MaxResponseBytes = 5 * 1024 * 1024;

    /// <summary>The output format asked of Azure: 24 kHz, 16-bit, mono, with a RIFF header.</summary>
    public const string AzureOutputFormat = "riff-24khz-16bit-mono-pcm";

    /// <summary>Path of the Azure synthesis endpoint.</summary>
    public const string AzurePath = "/cognitiveservices/v1";

    /// <summary>Path appended to an OpenAI-compatible base address.</summary>
    public const string OpenAiPath = "/audio/speech";

    /// <summary>Header carrying the Azure key.</summary>
    public const string AzureKeyHeader = "Ocp-Apim-Subscription-Key";

    /// <summary>Header naming the Azure output format.</summary>
    public const string AzureFormatHeader = "X-Microsoft-OutputFormat";

    /// <summary>Budget of one request: connect, headers and body together.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// What every Azure host ends with; the region is the only thing in front of it. The one place
    /// in the repository this host name may be written (NET-007).
    /// </summary>
    public const string AzureHostSuffix = ".tts.speech.microsoft.com";

    // One process-wide instance, created on first use only: constructing a client sends nothing.
    private static readonly Lazy<HttpClient> Http = new(
        () => new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan },
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly OnlineSpeechTransport _transport;
    private readonly TimeSpan _requestTimeout;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>Creates a client over a transport.</summary>
    /// <param name="transport">Sends one request; <see cref="HttpTransport"/> in production.</param>
    /// <param name="requestTimeout">Budget of one request; <see cref="DefaultRequestTimeout"/> when null.</param>
    /// <param name="readEnvironment">Reads an environment variable; the process environment when null.</param>
    public OnlineSpeechClient(
        OnlineSpeechTransport transport, TimeSpan? requestTimeout = null, Func<string, string?>? readEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var timeout = requestTimeout ?? DefaultRequestTimeout;
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), "the request timeout must be positive");
        }

        _transport = transport;
        _requestTimeout = timeout;
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Production transport: the process-wide client, no redirects, no cookies, no credentials.</summary>
    public static OnlineSpeechTransport HttpTransport { get; } = SendAsync;

    /// <summary>The production client. Constructing it sends nothing.</summary>
    public static OnlineSpeechClient CreateDefault() => new(HttpTransport);

    /// <summary>Budget of one request.</summary>
    public TimeSpan RequestTimeout => _requestTimeout;

    /// <summary>True when the kill switch is set in this process right now.</summary>
    public bool IsDisabledNow => IsDisabled(_readEnvironment(DisableVariable));

    /// <summary>
    /// True when a value of <see cref="DisableVariable"/> turns online speech off: anything except
    /// empty, <c>0</c> or <c>false</c>. The same rule as the shared-calibration kill switch.
    /// </summary>
    /// <param name="value">The variable's value, or null when unset.</param>
    public static bool IsDisabled(string? value) => SharedCalibrationClient.IsDisabled(value);

    /// <summary>The Azure host of a region.</summary>
    /// <param name="region">A region (<see cref="SpeechValidation.IsRegion"/>).</param>
    public static string AzureHost(string region) => SpeechValidation.IsRegion(region)
        ? region + AzureHostSuffix
        : throw new ArgumentException("not an Azure region", nameof(region));

    /// <summary>The Azure synthesis address of a region.</summary>
    /// <param name="region">A region.</param>
    public static Uri AzureEndpoint(string region) =>
        new("https://" + AzureHost(region) + AzurePath, UriKind.Absolute);

    /// <summary>The synthesis address under an OpenAI-compatible base address.</summary>
    /// <param name="baseUrl">Base address as the user typed it; normalised here.</param>
    public static Uri OpenAiEndpoint(string baseUrl) =>
        SpeechValidation.TryNormalizeBaseUrl(baseUrl, out var normalized, out _)
            ? new Uri(normalized + OpenAiPath, UriKind.Absolute)
            : throw new ArgumentException("not an acceptable base address", nameof(baseUrl));

    /// <summary>
    /// The host a sentence would be sent to under <paramref name="config"/>, or null when nothing would
    /// be sent. Shown to the user ("播报文字会发送到 …") and written to the log.
    /// </summary>
    /// <param name="config">Settings.</param>
    public static string? TargetHost(OnlineSpeechConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Target(config)?.Host;
    }

    /// <summary>
    /// What a key is bound to: the Azure host, or the whole normalised OpenAI-compatible base address.
    /// A key entered for one target is never sent to another (docs/privacy-boundary.md §8.3). Null when
    /// the settings name no target.
    /// </summary>
    /// <param name="config">Settings.</param>
    public static string? KeyBinding(OnlineSpeechConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Provider switch
        {
            SpeechProvider.Azure when SpeechValidation.IsRegion(config.AzureRegion) =>
                "azure:" + AzureHost(config.AzureRegion!),
            SpeechProvider.OpenAiCompatible when SpeechValidation.TryNormalizeBaseUrl(config.OpenAiBaseUrl, out var url, out _) =>
                "openai_compatible:" + url,
            _ => null,
        };
    }

    /// <summary>
    /// Builds the request for one sentence. The settings must be complete; the sentence must already be
    /// prepared (<see cref="SpeechValidation.TryNormalizeText"/>).
    /// </summary>
    /// <param name="config">Complete settings.</param>
    /// <param name="key">The key for the settings' target.</param>
    /// <param name="text">Prepared sentence.</param>
    /// <param name="ratePercent">Rate in percent of normal, 50-200.</param>
    public static SpeechHttpRequest BuildRequest(OnlineSpeechConfig config, string key, string text, int ratePercent)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(text);
        if (!config.IsComplete)
        {
            throw new ArgumentException("the speech settings are incomplete", nameof(config));
        }

        if (!SpeechValidation.TryNormalizeKey(key ?? string.Empty, out var trimmed))
        {
            throw new ArgumentException("not an acceptable key", nameof(key));
        }

        return config.Provider switch
        {
            SpeechProvider.Azure => new SpeechHttpRequest(
                AzureEndpoint(config.AzureRegion!),
                new[]
                {
                    new KeyValuePair<string, string>(AzureKeyHeader, trimmed),
                    new KeyValuePair<string, string>(AzureFormatHeader, AzureOutputFormat),
                    new KeyValuePair<string, string>("User-Agent", UserAgent),
                },
                "application/ssml+xml",
                SpeechMarkup.Encode(SpeechMarkup.AzureSsml(config.Voice!, text, ratePercent))),
            SpeechProvider.OpenAiCompatible => new SpeechHttpRequest(
                OpenAiEndpoint(config.OpenAiBaseUrl!),
                new[]
                {
                    new KeyValuePair<string, string>("Authorization", "Bearer " + trimmed),
                    new KeyValuePair<string, string>("User-Agent", UserAgent),
                },
                "application/json",
                SpeechMarkup.OpenAiBody(config.OpenAiModel!, config.Voice!, text, ratePercent)),
            _ => throw new ArgumentException("no speech service is selected", nameof(config)),
        };
    }

    /// <summary>
    /// Synthesises one sentence. Never throws once its arguments are valid: cancellation and every
    /// network failure end up on the result.
    /// </summary>
    /// <param name="config">Settings.</param>
    /// <param name="key">Key for the settings' target, or null when there is none.</param>
    /// <param name="text">Prepared sentence.</param>
    /// <param name="ratePercent">Rate in percent of normal, 50-200.</param>
    /// <param name="cancellationToken">Stops the request; the result then says Cancelled.</param>
    public async Task<SpeechSynthesisResult> SynthesizeAsync(
        OnlineSpeechConfig config, string? key, string text, int ratePercent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(text);
        if (IsDisabledNow)
        {
            return SpeechSynthesisResult.Disabled;
        }

        if (!config.IsComplete || key is null || !SpeechValidation.TryNormalizeKey(key, out _))
        {
            return SpeechSynthesisResult.NotConfigured;
        }

        var request = BuildRequest(config, key, text, ratePercent);
        var host = request.Uri.Host;
        if (cancellationToken.IsCancellationRequested)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Cancelled, null, host);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _transport(request, budget.Token).ConfigureAwait(false);
            return await ReadAsync(request, response, host, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Cancelled, null, host);
        }
        catch (OperationCanceledException)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Timeout, null, host);
        }
        catch (HttpRequestException ex)
        {
            return ex.InnerException is TimeoutException
                ? new SpeechSynthesisResult(SpeechOutcome.Timeout, null, host)
                : new SpeechSynthesisResult(SpeechOutcome.Network, null, host,
                    Reason: UpperSnakeCaseNamingPolicy.Instance.ConvertName(ex.HttpRequestError.ToString()));
        }
        catch (IOException)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Network, null, host, Reason: "READ_FAILED");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The type name is kept for diagnostics, the message is not: it can carry an address.
            return new SpeechSynthesisResult(SpeechOutcome.Network, null, host,
                Reason: UpperSnakeCaseNamingPolicy.Instance.ConvertName(ex.GetType().Name));
        }
    }

    private static async Task<SpeechSynthesisResult> ReadAsync(
        SpeechHttpRequest request, SpeechTransportResponse response, string host, CancellationToken cancellationToken)
    {
        var status = response.StatusCode;
        if (status is >= 300 and < 400)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Network, null, host, status, "REDIRECT_REFUSED");
        }

        if (status is 401 or 403)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Auth, null, host, status);
        }

        if (status == 429)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Quota, null, host, status);
        }

        if (status is < 200 or >= 300)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Network, null, host, status, "HTTP_STATUS");
        }

        if (response.FinalUri != request.Uri)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Network, null, host, status, "REDIRECT_REFUSED");
        }

        if (!IsAudioMediaType(response.MediaType))
        {
            return new SpeechSynthesisResult(SpeechOutcome.Format, null, host, status, "CONTENT_TYPE");
        }

        if (response.ContentLength > MaxResponseBytes)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Format, null, host, status, "TOO_LARGE");
        }

        var body = await ReadCappedAsync(response.Body, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return new SpeechSynthesisResult(SpeechOutcome.Format, null, host, status, "TOO_LARGE");
        }

        return WaveFile.TryNormalize(body, out var canonical, out _, out var refusal)
            ? new SpeechSynthesisResult(SpeechOutcome.Ok, canonical, host, status)
            : new SpeechSynthesisResult(SpeechOutcome.Format, null, host, status, refusal);
    }

    /// <summary>True for <c>audio/*</c> and <c>application/octet-stream</c>, parameters ignored.</summary>
    /// <param name="mediaType">Content-Type as received, or null.</param>
    public static bool IsAudioMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        var bare = mediaType.Split(';', 2)[0].Trim();
        return (bare.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && bare.Length > "audio/".Length) ||
               bare.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; null the moment one byte more arrives.</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static (string Host, Uri Uri)? Target(OnlineSpeechConfig config) => config.Provider switch
    {
        SpeechProvider.Azure when SpeechValidation.IsRegion(config.AzureRegion) =>
            (AzureHost(config.AzureRegion!), AzureEndpoint(config.AzureRegion!)),
        SpeechProvider.OpenAiCompatible when SpeechValidation.TryNormalizeBaseUrl(config.OpenAiBaseUrl, out var url, out var uri) =>
            (uri!.Host, new Uri(url + OpenAiPath, UriKind.Absolute)),
        _ => null,
    };

    /// <summary>
    /// True for an address the production transport may send to: an Azure endpoint of a valid region,
    /// or an https address (http for loopback) without user info, query or fragment.
    /// </summary>
    /// <param name="uri">Address.</param>
    public static bool IsSendable(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Host.EndsWith(AzureHostSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var region = uri.Host[..^AzureHostSuffix.Length];
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                   SpeechValidation.IsRegion(region) && uri.AbsolutePath == AzurePath;
        }

        return uri.AbsolutePath.EndsWith(OpenAiPath, StringComparison.Ordinal) &&
               (uri.Scheme == Uri.UriSchemeHttps ||
                (uri.Scheme == Uri.UriSchemeHttp && SpeechValidation.IsLoopbackHost(uri)));
    }

    // ------------------------------------------------------------ production transport

    /// <summary>
    /// The handler behind <see cref="HttpTransport"/>: redirects and cookies off, no credentials of any
    /// kind, no automatic decompression, the system proxy, and a connect timeout equal to the
    /// per-request budget. Idle connections are closed quickly.
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
        ConnectTimeout = DefaultRequestTimeout,
        MaxResponseHeadersLength = 64,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };

    /// <summary>The HTTP/1.1 POST for a built request, with exactly its headers.</summary>
    /// <param name="request">Request to send.</param>
    internal static HttpRequestMessage CreateMessage(SpeechHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var content = new ByteArrayContent(request.Body);
        content.Headers.ContentType = new MediaTypeHeaderValue(request.ContentType);
        var message = new HttpRequestMessage(HttpMethod.Post, request.Uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content,
        };
        foreach (var (name, value) in request.Headers)
        {
            message.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
    }

    private static async Task<SpeechTransportResponse> SendAsync(SpeechHttpRequest request, CancellationToken cancellationToken)
    {
        if (!IsSendable(request.Uri))
        {
            throw new ArgumentException("the speech transport only posts to validated speech addresses", nameof(request));
        }

        using var message = CreateMessage(request);
        var response = await Http.Value
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new SpeechTransportResponse(
                (int)response.StatusCode,
                response.RequestMessage?.RequestUri ?? request.Uri,
                response.Content.Headers.ContentType?.MediaType,
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
