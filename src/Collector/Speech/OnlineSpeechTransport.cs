namespace MentorRecorder.Collector.Speech;

/// <summary>
/// Sends one POST that <see cref="OnlineSpeechClient"/> built and returns the response head with the
/// body still unread.
///
/// The production transport is <see cref="OnlineSpeechClient.HttpTransport"/>, in the only speech
/// file allowed to open a connection (docs/privacy-boundary.md §8.3). Tests pass their own. A
/// transport must not follow redirects, must not add credentials or cookies, and should honour
/// cancellation; it may throw, because the client turns every failure into an outcome.
/// </summary>
/// <param name="request">What to send.</param>
/// <param name="cancellationToken">Cancelled by the caller or when the per-request budget runs out.</param>
public delegate Task<SpeechTransportResponse> OnlineSpeechTransport(
    SpeechHttpRequest request, CancellationToken cancellationToken);

/// <summary>
/// One outgoing speech request. Deliberately a class with its own <see cref="ToString"/> rather than
/// a record: a record would print every header, and one header is the user's key.
/// </summary>
public sealed class SpeechHttpRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="uri">Absolute address.</param>
    /// <param name="headers">Request headers other than Content-Type, in sending order.</param>
    /// <param name="contentType">Media type of the body.</param>
    /// <param name="body">Body bytes.</param>
    public SpeechHttpRequest(
        Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, string contentType, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentNullException.ThrowIfNull(body);
        Uri = uri;
        Headers = headers;
        ContentType = contentType;
        Body = body;
    }

    /// <summary>Always POST.</summary>
    public string Method => "POST";

    /// <summary>Absolute address.</summary>
    public Uri Uri { get; }

    /// <summary>Headers other than Content-Type; one of them carries the key.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Media type of the body.</summary>
    public string ContentType { get; }

    /// <summary>Body bytes: SSML or JSON, UTF-8.</summary>
    public byte[] Body { get; }

    /// <summary>The value of a header, or null. Header names compare case-insensitively.</summary>
    /// <param name="name">Header name.</param>
    public string? Header(string name) =>
        Headers.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Method, host and path only: never a header, never the body.</summary>
    public override string ToString() => Method + " " + Uri.Host + Uri.AbsolutePath;
}

/// <summary>What a transport got back. Disposing it releases the body and whatever produced it.</summary>
public sealed class SpeechTransportResponse : IDisposable
{
    private readonly IDisposable? _owner;

    /// <summary>Wraps a response head and its unread body.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="finalUri">The address that answered.</param>
    /// <param name="mediaType">Content-Type without parameters, or null when absent.</param>
    /// <param name="contentLength">Declared body length, when the answer declared one.</param>
    /// <param name="body">The body, read by the client with a byte cap.</param>
    /// <param name="owner">Disposed together with the body.</param>
    public SpeechTransportResponse(
        int statusCode, Uri finalUri, string? mediaType, long? contentLength, Stream body, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(finalUri);
        ArgumentNullException.ThrowIfNull(body);
        StatusCode = statusCode;
        FinalUri = finalUri;
        MediaType = mediaType;
        ContentLength = contentLength;
        Body = body;
        _owner = owner;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The address that answered.</summary>
    public Uri FinalUri { get; }

    /// <summary>Content-Type without parameters, or null.</summary>
    public string? MediaType { get; }

    /// <summary>Declared body length, if any.</summary>
    public long? ContentLength { get; }

    /// <summary>The unread body.</summary>
    public Stream Body { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        Body.Dispose();
        _owner?.Dispose();
    }
}

/// <summary>How one synthesis attempt ended.</summary>
public enum SpeechOutcome
{
    /// <summary>A valid 16-bit PCM WAV came back.</summary>
    Ok,

    /// <summary><c>MR_DISABLE_ONLINE_SPEECH</c> stopped it before anything was sent.</summary>
    Disabled,

    /// <summary>No service, an incomplete one, or no key for it.</summary>
    NotConfigured,

    /// <summary>401 or 403: the key was refused.</summary>
    Auth,

    /// <summary>429: the service's quota or rate limit.</summary>
    Quota,

    /// <summary>Any other status, a refused redirect, or a connection that failed.</summary>
    Network,

    /// <summary>The per-request budget ran out, or the sentence waited too long for its turn.</summary>
    Timeout,

    /// <summary>The answer was not an acceptable audio file: type, size or content.</summary>
    Format,

    /// <summary>The caller gave up (the connection closed or the Collector is stopping).</summary>
    Cancelled,
}

/// <summary>
/// The result of one synthesis attempt. It names the host that was asked, never the address, the
/// key or anything the service wrote back.
/// </summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Audio">Canonical WAV bytes when <see cref="SpeechOutcome.Ok"/>.</param>
/// <param name="Host">Host name that was asked, or null when nothing was sent.</param>
/// <param name="HttpStatus">Status code, when one came back.</param>
/// <param name="Reason">Upper-case detail token, e.g. <c>REDIRECT_REFUSED</c> or <c>NOT_16_BIT</c>.</param>
public sealed record SpeechSynthesisResult(
    SpeechOutcome Outcome, byte[]? Audio, string? Host, int? HttpStatus = null, string? Reason = null)
{
    /// <summary>Stopped by the kill switch.</summary>
    public static SpeechSynthesisResult Disabled { get; } = new(SpeechOutcome.Disabled, null, null);

    /// <summary>Nothing to send it to.</summary>
    public static SpeechSynthesisResult NotConfigured { get; } = new(SpeechOutcome.NotConfigured, null, null);

    /// <summary>Record printing without the audio bytes.</summary>
    /// <param name="builder">Target.</param>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Outcome = ").Append(Outcome)
            .Append(", AudioBytes = ").Append(Audio?.Length ?? 0)
            .Append(", Host = ").Append(Host)
            .Append(", HttpStatus = ").Append(HttpStatus)
            .Append(", Reason = ").Append(Reason);
        return true;
    }
}
