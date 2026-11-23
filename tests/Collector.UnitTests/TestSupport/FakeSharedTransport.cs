using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// Answers shared-calibration requests from a table of absolute addresses and records every request; an
/// address nobody serves answers 404. A test can hold each request open or fail them all.
/// </summary>
internal sealed class FakeSharedTransport
{
    private readonly Dictionary<string, Func<Uri, CancellationToken, Task<SharedTransportResponse>>> _routes =
        new(StringComparer.Ordinal);

    private readonly List<Uri> _requests = new();

    /// <summary>Runs before each answer; a test holds a request open with it.</summary>
    public Func<Uri, CancellationToken, Task>? BeforeAnswer { get; set; }

    /// <summary>Every request fails the way an unreachable network does.</summary>
    public bool Unreachable { get; set; }

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

    /// <summary>Answers <paramref name="uri"/> with 200 and <paramref name="body"/>.</summary>
    public FakeSharedTransport Serve(Uri uri, byte[] body) =>
        Serve(uri, (requested, _) => Task.FromResult(
            new SharedTransportResponse(200, requested, body.Length, new MemoryStream(body))));

    /// <summary>Answers <paramref name="uri"/> however <paramref name="respond"/> does: a status, a stalled body, a failure.</summary>
    public FakeSharedTransport Serve(Uri uri, Func<Uri, CancellationToken, Task<SharedTransportResponse>> respond)
    {
        lock (_routes)
        {
            _routes[uri.AbsoluteUri] = respond;
        }

        return this;
    }

    public async Task<SharedTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(uri);
        }

        if (BeforeAnswer is { } before)
        {
            await before(uri, cancellationToken).ConfigureAwait(false);
        }

        if (Unreachable)
        {
            throw new IOException("the network is unreachable");
        }

        Func<Uri, CancellationToken, Task<SharedTransportResponse>>? respond;
        lock (_routes)
        {
            _routes.TryGetValue(uri.AbsoluteUri, out respond);
        }

        return respond is null
            ? new SharedTransportResponse(404, uri, 0, new MemoryStream())
            : await respond(uri, cancellationToken).ConfigureAwait(false);
    }
}
