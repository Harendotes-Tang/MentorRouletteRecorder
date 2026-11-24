using System.Security.Cryptography;
using System.Text;
using MentorRecorder.Collector.Update;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// The update tests' transport: answers by address, 404 for anything it was not told about, and records every
/// request. Addresses come from the client's own builders, so no host name is written here (NET-007).
/// </summary>
public sealed class UpdateTransport
{
    private readonly Dictionary<string, Func<Uri, CancellationToken, Task<UpdateTransportResponse>>> _routes =
        new(StringComparer.Ordinal);

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

    public static UpdateTransportResponse Status(Uri uri, int status) => new(status, uri, null, 0, new MemoryStream());

    public static UpdateTransportResponse Redirect(Uri uri, Uri location) =>
        new(302, uri, location, 0, new MemoryStream());

    /// <summary>A 200 answer carrying <paramref name="bytes"/>, with its length declared.</summary>
    public static UpdateTransportResponse Body(Uri uri, byte[] bytes) =>
        new(200, uri, null, bytes.Length, new MemoryStream(bytes));

    /// <summary>A request that never answers: it waits until the caller gives up on it.</summary>
    public static async Task<UpdateTransportResponse> NeverAnswers(Uri uri, CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        return Status(uri, 200);
    }

    public static Uri ChecksumUri(string version) => new(UpdateCheckClient.ChecksumUrl(version)!, UriKind.Absolute);

    public static Uri InstallerUri(string version) => new(UpdateCheckClient.InstallerUrl(version)!, UriKind.Absolute);

    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public UpdateTransport On(Uri uri, Func<Uri, CancellationToken, Task<UpdateTransportResponse>> respond)
    {
        lock (_routes)
        {
            _routes[uri.AbsoluteUri] = respond;
        }

        return this;
    }

    public UpdateTransport On(Uri uri, byte[] body) => On(uri, (requested, _) => Task.FromResult(Body(requested, body)));

    /// <summary>Answers with <paramref name="body"/> in UTF-8.</summary>
    public UpdateTransport On(Uri uri, string body) => On(uri, Encoding.UTF8.GetBytes(body));

    public UpdateTransport On(Uri uri, int status) => On(uri, (requested, _) => Task.FromResult(Status(requested, status)));

    public UpdateTransport OnRedirect(Uri from, Uri to) =>
        On(from, (requested, _) => Task.FromResult(Redirect(requested, to)));

    /// <summary>
    /// Publishes a version: its checksum and its installer. <paramref name="onChecksum"/> runs while the checksum
    /// is being asked for - after <c>updates</c> was prepared and cleared, before anything is written into it.
    /// </summary>
    public UpdateTransport Publish(
        string version,
        byte[] installer,
        string? checksum = null,
        Stream? body = null,
        bool declareLength = true,
        long? declaredLength = null,
        TaskCompletionSource? gate = null,
        Func<Task>? onChecksum = null)
    {
        var sidecar = Encoding.ASCII.GetBytes(
            (checksum ?? Sha(installer)) + (checksum is null ? "  MentorRecorder-" + version + "-setup.exe\r\n" : string.Empty));
        On(ChecksumUri(version), async (uri, _) =>
        {
            if (onChecksum is not null)
            {
                await onChecksum().ConfigureAwait(false);
            }

            return new UpdateTransportResponse(200, uri, null, sidecar.Length, new MemoryStream(sidecar));
        });
        On(InstallerUri(version), async (uri, token) =>
        {
            if (gate is not null)
            {
                await gate.Task.WaitAsync(token).ConfigureAwait(false);
            }

            return new UpdateTransportResponse(
                200,
                uri,
                null,
                declaredLength ?? (declareLength ? installer.Length : null),
                body ?? new MemoryStream(installer));
        });
        return this;
    }

    public Task<UpdateTransportResponse> Send(Uri uri, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(uri);
        }

        Func<Uri, CancellationToken, Task<UpdateTransportResponse>>? respond;
        lock (_routes)
        {
            _routes.TryGetValue(uri.AbsoluteUri, out respond);
        }

        return respond is null ? Task.FromResult(Status(uri, 404)) : respond(uri, cancellationToken);
    }
}
