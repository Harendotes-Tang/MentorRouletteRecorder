namespace MentorRecorder.Collector.Update;

/// <summary>
/// Sends one GET to an address <see cref="UpdateCheckClient"/> built and returns the response head
/// with the body still unread.
///
/// The production transport is <see cref="UpdateCheckClient.HttpTransport"/>, in the only file of
/// this feature allowed to open a connection (docs/privacy-boundary.md §8.4). Tests and the service
/// pass their own. A transport must not follow redirects - the client follows them itself, one hop
/// at a time, checking each target - and should honour cancellation; it may throw, because the
/// client turns every failure into an outcome.
///
/// Kept separate from <see cref="Protocol.Sharing.SharedCalibrationTransport"/>: that one has no
/// redirect target to report, and the two request classes must stay independently auditable.
/// </summary>
/// <param name="uri">Address to fetch.</param>
/// <param name="cancellationToken">Cancelled by the caller or when the budget runs out.</param>
public delegate Task<UpdateTransportResponse> UpdateCheckTransport(Uri uri, CancellationToken cancellationToken);

/// <summary>What a transport got back. Disposing it releases the body and whatever produced it.</summary>
public sealed class UpdateTransportResponse : IDisposable
{
    private readonly IDisposable? _owner;

    /// <summary>Wraps a response head and its unread body.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="finalUri">The address that actually answered.</param>
    /// <param name="location">The <c>Location</c> header of a redirect, absolute or relative; null when there was none.</param>
    /// <param name="contentLength">Declared body length, when the answer declared one.</param>
    /// <param name="body">The body, read by the client with a byte cap.</param>
    /// <param name="owner">Disposed together with the body, for example the response object it came from.</param>
    public UpdateTransportResponse(
        int statusCode, Uri finalUri, Uri? location, long? contentLength, Stream body, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(finalUri);
        ArgumentNullException.ThrowIfNull(body);
        StatusCode = statusCode;
        FinalUri = finalUri;
        Location = location;
        ContentLength = contentLength;
        Body = body;
        _owner = owner;
    }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>The address that actually answered.</summary>
    public Uri FinalUri { get; }

    /// <summary>Where a redirect points, exactly as the answer stated it; null when it stated nothing.</summary>
    public Uri? Location { get; }

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
