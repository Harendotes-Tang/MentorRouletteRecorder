using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace MentorRecorder.Collector.Community;

internal sealed class CommunityHttp
{
    internal const string Repository = "Harendotes-Tang/MentorRecorder-Calibrations";
    internal const string Api = "https://api.github.com/repos/" + Repository;
    internal const string Github = "https://github.com";
    internal const string RawRoot = "https://raw.githubusercontent.com/" + Repository + "/";
    internal const string MirrorRoot = "https://api.gitcode.com/api/v5/repos/" + Repository + "/raw/";
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        DefaultProxyCredentials = null,
    }) { Timeout = TimeSpan.FromSeconds(12) };

    // GitHub that has not answered within this time no longer holds a refresh back: the GitCode
    // mirror is asked as well and whichever delivers the file first is used.
    private static readonly TimeSpan SlowAfter = TimeSpan.FromSeconds(3);

    internal static Task<byte[]?> GetPublicationAsync(string revision, string file, int maxBytes,
        CancellationToken cancellationToken) =>
        GetPublicationAsync(revision, file,
            (url, token) => SendAsync(HttpMethod.Get, url, null, null, maxBytes, token), SlowAfter, cancellationToken);

    /// <summary>Returns the file, or null only when both GitHub and the mirror report it missing.</summary>
    internal static async Task<byte[]?> GetPublicationAsync(string revision, string file,
        Func<string, CancellationToken, Task<byte[]?>> send, TimeSpan slowAfter, CancellationToken cancellationToken)
    {
        // Raw GitHub and GitCode receive only a fixed path and a ref. The same download path
        // never accepts an authorization token even if user authentication is active.
        var path = "community/v1/" + file;
        using var abandoned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var github = send($"{RawRoot}{revision}/{path}", abandoned.Token);
            Task<byte[]?>? mirror = null;
            Task<byte[]?> Mirror() => mirror ??= send($"{MirrorRoot}{path}?ref={revision}", abandoned.Token);

            var first = github;
            if (await Task.WhenAny(github, Task.Delay(slowAfter, abandoned.Token)).ConfigureAwait(false) != github)
            {
                cancellationToken.ThrowIfCancellationRequested();
                first = await Task.WhenAny(github, Mirror()).ConfigureAwait(false);
            }
            var (content, missing) = await Outcome(first, cancellationToken).ConfigureAwait(false);
            if (content is not null)
                return content;
            // The mirror when GitHub had nothing, GitHub when the mirror answered first without the file.
            var (other, otherMissing) = await Outcome(first == github ? Mirror() : github, cancellationToken)
                .ConfigureAwait(false);
            return other ?? (missing && otherMissing ? null : throw new HttpRequestException());
        }
        finally
        {
            // Stops the slower request and the pending delay.
            await abandoned.CancelAsync().ConfigureAwait(false);
        }
    }

    private static async Task<(byte[]? Content, bool Missing)> Outcome(Task<byte[]?> request,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = await request.ConfigureAwait(false);
            return (content, content is null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, false);
        }
    }

    internal static async Task<JsonObject> GithubAsync(HttpMethod method, string url, string? token,
        HttpContent? content, CancellationToken cancellationToken)
    {
        var bytes = await SendAsync(method, url, token, content, 96_000, cancellationToken).ConfigureAwait(false);
        return bytes is null ? throw new HttpRequestException(null, null, HttpStatusCode.NotFound)
            : CommunityContent.ParseObject(bytes);
    }

    private static async Task<byte[]?> SendAsync(HttpMethod method, string url, string? token, HttpContent? content,
        int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.UserAgent.ParseAdd("MentorRecorder-Community/1");
        request.Headers.Accept.ParseAdd("application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new FormatException("download_limit");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > maxBytes)
                throw new FormatException("download_limit");
            output.Write(buffer, 0, count);
        }
    }

    internal static StringContent Json(JsonObject data) => new(data.ToJsonString(), Encoding.UTF8, "application/json");
}
