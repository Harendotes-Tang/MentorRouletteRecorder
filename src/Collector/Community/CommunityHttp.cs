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
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false,
        DefaultProxyCredentials = null,
    }) { Timeout = TimeSpan.FromSeconds(12) };

    internal static async Task<byte[]?> GetPublicationAsync(string revision, string file, int maxBytes,
        CancellationToken cancellationToken)
    {
        // Raw GitHub and GitCode receive only a fixed path and a ref. The same download path
        // never accepts an authorization token even if user authentication is active.
        string[] roots =
        [
            $"https://raw.githubusercontent.com/{Repository}/{revision}/",
            $"https://api.gitcode.com/api/v5/repos/{Repository}/raw/",
        ];
        var primaryMissing = false;
        var lastMissing = false;
        for (var i = 0; i < roots.Length; i++)
        {
            try
            {
                var url = roots[i] + "community/v1/" + file + (i == 1 ? "?ref=" + revision : "");
                var response = await SendAsync(HttpMethod.Get, url,
                    null, null, maxBytes, cancellationToken).ConfigureAwait(false);
                if (response is not null)
                    return response;
                if (i == 0)
                    primaryMissing = true;
                lastMissing = true;
            }
            catch (Exception error) when (error is HttpRequestException or IOException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Try the mirror when the primary is unavailable.
            }
        }
        return primaryMissing && lastMissing ? null : throw new HttpRequestException();
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
