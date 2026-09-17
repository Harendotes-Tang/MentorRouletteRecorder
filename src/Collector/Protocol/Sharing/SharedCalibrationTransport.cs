namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Sends one GET to an address <see cref="SharedCalibrationClient"/> built and returns the response
/// head with the body still unread.
///
/// The production transport is <see cref="SharedCalibrationClient.HttpTransport"/>, in the only file
/// allowed to open a connection (docs/privacy-boundary.md §8.2). Tests and the pipeline pass their own.
/// A transport must not follow redirects and should honour cancellation; it may throw, because the
/// client turns every failure into an outcome.
/// </summary>
/// <param name="uri">Address to fetch.</param>
/// <param name="cancellationToken">Cancelled by the caller or when the per-source budget runs out.</param>
public delegate Task<SharedTransportResponse> SharedCalibrationTransport(Uri uri, CancellationToken cancellationToken);

/// <summary>What a transport got back. Disposing it releases the body and whatever produced it.</summary>
public sealed class SharedTransportResponse : IDisposable
{
    private readonly IDisposable? _owner;

    /// <summary>Wraps a response head and its unread body.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="finalUri">The address that actually answered.</param>
    /// <param name="contentLength">Declared body length, when the answer declared one.</param>
    /// <param name="body">The body, read by the client with a byte cap.</param>
    /// <param name="owner">Disposed together with the body, for example the response object it came from.</param>
    public SharedTransportResponse(int statusCode, Uri finalUri, long? contentLength, Stream body, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(finalUri);
        ArgumentNullException.ThrowIfNull(body);
        StatusCode = statusCode;
        FinalUri = finalUri;
        ContentLength = contentLength;
        Body = body;
        _owner = owner;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The address that actually answered.</summary>
    public Uri FinalUri { get; }

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
