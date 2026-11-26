using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Speech;

namespace MentorRecorder.Collector.Community;

internal sealed class CommunityGithub(string dataDirectory, string? clientId = null,
    Func<HttpMethod, string, string?, HttpContent?, CancellationToken, Task<JsonObject>>? transport = null,
    TimeProvider? timeProvider = null, ISecretProtector? protector = null)
{
    private const string DeviceUrl = CommunityHttp.Github + "/login/device";
    private const string TokenUrl = CommunityHttp.Github + "/login/oauth/access_token";
    private const string Grant = "urn:ietf:params:oauth:grant-type:device_code";
    private const string IssueUrl = CommunityHttp.Api + "/issues";
    internal const string IssueMarker = "<!-- mentor-recorder:community-content:v1 -->";
    private readonly SpeechKeyStore _tokens = new(dataDirectory,
        protector ?? DpapiProtector.ForPurpose("MentorRecorder.community-github.v1"), "community-github.bin");
    private readonly object _gate = new();
    private readonly string? _clientId = clientId ?? ConfiguredClientId();
    private readonly Func<HttpMethod, string, string?, HttpContent?, CancellationToken, Task<JsonObject>> _transport =
        transport ?? CommunityHttp.GithubAsync;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private ActiveRequest? _active;
    private DeviceGrant? _grant;
    private JsonObject? _account;
    private CommunityLibrary.RequestRecord? _lastRequest;

    internal bool Configured => _clientId is not null;

    internal async Task<JsonObject> RequestAsync(string action, JsonObject payload, CancellationToken cancellationToken)
    {
        if (transport is null && Environment.GetEnvironmentVariable("MR_DISABLE_DUTY_COMMUNITY") == "1"
            && action is not ("cancel" or "sign_out"))
            return Result("offline");
        cancellationToken.ThrowIfCancellationRequested();
        ActiveRequest request;
        lock (_gate)
        {
            if (action is "cancel" or "sign_out")
                return EndAuthorization(action == "sign_out");
            if (_active is not null)
                return Result("auth_busy");
            request = _active = new(action);
            if (action == "sign_in")
                _grant = null;
        }
        string? outcome = null;
        try
        {
            var result = await RunAsync(action, payload, request, cancellationToken).ConfigureAwait(false);
            outcome = result["status"]?.GetValue<string>();
            return result;
        }
        catch (FormatException error)
        {
            outcome = error.Message;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_active, request))
                    _active = null;
            }
            // Only a request that reached the network is reported (docs/privacy-boundary.md §8.7).
            if (request.SentAt is { } at)
                Volatile.Write(ref _lastRequest, new CommunityLibrary.RequestRecord(at,
                    outcome ?? (cancellationToken.IsCancellationRequested ? "cancelled" : "failed")));
        }
    }

    /// <summary>The most recent request of this kind that was sent, for the diagnostics report.</summary>
    internal CommunityLibrary.RequestRecord? LastRequest => Volatile.Read(ref _lastRequest);

    private async Task<JsonObject> RunAsync(string action, JsonObject payload, ActiveRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (action == "issue_status")
                return await IssueStatusAsync(request, CommunityContent.Integer(payload, "issue"), cancellationToken)
                    .ConfigureAwait(false);
            if (_clientId is null)
                return Result("app_not_configured");

            return action switch
            {
                "sign_in" => await StartAsync(request, cancellationToken).ConfigureAwait(false),
                "poll" => await PollAsync(request, cancellationToken).ConfigureAwait(false),
                "account" => await AccountAsync(request, cancellationToken).ConfigureAwait(false),
                "submit" => await SubmitAsync(payload, request, cancellationToken).ConfigureAwait(false),
                _ => throw new FormatException("invalid_action"),
            };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (request.Superseded && action is ("sign_in" or "poll" or "account"))
                    return Result("cancelled");
                if (!request.Superseded && error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
                {
                    if (action is "account" or "submit")
                        return ClearAccount();
                    if (action == "poll")
                    {
                        _grant = null;
                        return Result("authorization_denied");
                    }
                }
            }
            // A submission that failed before its issue was posted (the token refresh or the
            // account lookup) created nothing on GitHub; one that failed after may have.
            if (action == "submit")
                return Result(request.Posted ? "submit_unconfirmed" : "submit_not_sent");
            return Result("temporarily_unavailable");
        }
    }

    // Called under _gate. A superseded auth request can finish its network call independently
    // of a new authorization, but only the current request may publish its result.
    private JsonObject EndAuthorization(bool signOut)
    {
        _grant = null;
        if (_active is { } request && (signOut || request.Action is ("sign_in" or "poll")))
        {
            request.Superseded = true;
            if (request.Action is "sign_in" or "poll" or "account")
                _active = null;
        }
        return signOut ? ClearAccount() : Result("cancelled");
    }

    private JsonObject ClearAccount()
    {
        _account = null;
        try
        {
            _tokens.Clear();
            return Result("signed_out");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Result("temporarily_unavailable");
        }
    }

    private async Task<JsonObject> StartAsync(ActiveRequest request, CancellationToken token)
    {
        var response = await FormAsync(request, DeviceUrl + "/code", token).ConfigureAwait(false);
        lock (_gate)
        {
            if (request.Superseded)
                return Result("cancelled");
            token.ThrowIfCancellationRequested();
            var interval = Math.Clamp(CommunityContent.Integer(response, "interval"), 5, 120);
            var expiresIn = Math.Clamp(CommunityContent.Integer(response, "expires_in"), 1, 1800);
            var now = _clock.GetUtcNow();
            _grant = new(CommunityContent.String(response, "device_code", 512),
                CommunityContent.String(response, "user_code", 32), now.AddSeconds(expiresIn), now.AddSeconds(interval), interval);
            return GrantState(_grant);
        }
    }

    private async Task<JsonObject> PollAsync(ActiveRequest request, CancellationToken token)
    {
        DeviceGrant grant;
        DateTimeOffset now;
        lock (_gate)
        {
            if (request.Superseded)
                return Result("cancelled");
            if (_grant is null)
                return Result("signed_out");
            now = _clock.GetUtcNow();
            var expired = _grant.PendingCredentials is { } pending
                ? pending.Expires <= now.ToUnixTimeSeconds()
                : now >= _grant.Expires;
            if (expired)
            {
                _grant = null;
                return Result("device_expired");
            }
            if (now < _grant.NextPoll)
                return GrantState(_grant);
            grant = _grant = _grant with { NextPoll = now.AddSeconds(_grant.Interval) };
        }

        var credentials = grant.PendingCredentials;
        if (credentials is null)
        {
            var response = await FormAsync(request, TokenUrl, token, ("grant_type", Grant), ("device_code", grant.Code))
                .ConfigureAwait(false);
            lock (_gate)
            {
                if (request.Superseded)
                    return Result("cancelled");
                token.ThrowIfCancellationRequested();
                var error = response["error"]?.GetValue<string>();
                if (error == "authorization_pending")
                    return GrantState(grant);
                if (error == "slow_down")
                {
                    _grant = grant with { Interval = grant.Interval + 5, NextPoll = now.AddSeconds(grant.Interval + 5) };
                    return GrantState(_grant);
                }
                if (error is not null)
                {
                    _grant = null;
                    return Result(error == "expired_token" ? "device_expired" : "authorization_denied");
                }
                // GitHub has consumed the device code. A retry can read /user again using this
                // provisional in-memory token, as long as this authorization is still current.
                credentials = ParseToken(response);
                _grant = grant with { PendingCredentials = credentials };
            }
        }

        var account = await FetchAccountAsync(request, credentials.Access, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (request.Superseded)
                return Result("cancelled");
            token.ThrowIfCancellationRequested();
            SaveToken(credentials);
            _account = account;
            _grant = null;
            return SignedIn(account);
        }
    }

    private async Task<JsonObject> AccountAsync(ActiveRequest request, CancellationToken token)
    {
        var access = await AccessAsync(request, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (request.Superseded)
                return Result("cancelled");
            if (access is null)
                return Result("signed_out");
            if (_account is not null)
                return SignedIn(_account);
        }

        var account = await FetchAccountAsync(request, access, token).ConfigureAwait(false);
        lock (_gate)
        {
            if (request.Superseded)
                return Result("cancelled");
            _account = account;
            return SignedIn(account);
        }
    }

    private async Task<JsonObject> FetchAccountAsync(ActiveRequest request, string access, CancellationToken token)
    {
        var user = await Send(request, HttpMethod.Get, "https://api.github.com/user", access, null, token)
            .ConfigureAwait(false);
        return new JsonObject
        {
            ["login"] = CommunityContent.String(user, "login", 39),
            ["id"] = CommunityContent.Integer64(user, "id"),
        };
    }

    private async Task<JsonObject> SubmitAsync(JsonObject payload, ActiveRequest request, CancellationToken token)
    {
        var body = BuildIssueBody(payload);
        var access = await AccessAsync(request, token).ConfigureAwait(false);
        bool known;
        lock (_gate)
        {
            if (request.Superseded || access is null)
                return Result("signed_out");
            known = _account is not null;
        }
        // After a Collector restart the stored token is valid but the account is not yet known.
        var fetched = known ? null : await FetchAccountAsync(request, access, token).ConfigureAwait(false);
        Task<JsonObject> submission;
        lock (_gate)
        {
            if (request.Superseded)
                return Result("signed_out");
            _account ??= fetched;
            if (_account is null)
                return Result("signed_out");
            if (CommunityContent.Integer64(payload, "author_id") != CommunityContent.Integer64(_account, "id"))
                return Result("account_changed");
            request.Posted = true;
            submission = Send(request, HttpMethod.Post, IssueUrl, access, CommunityHttp.Json(new JsonObject
            {
                ["title"] = "MentorRecorder community contribution",
                ["body"] = body,
            }), token);
        }
        var issue = await submission.ConfigureAwait(false);
        return new JsonObject { ["status"] = "submitted", ["issue"] = CommunityContent.Integer(issue, "number") };
    }

    internal static string BuildIssueBody(JsonObject payload)
    {
        var license = CommunityContent.String(payload, "license", 20);
        if (!CommunityContent.IsLicense(license) || !CommunityContent.Boolean(payload, "consent") ||
            payload["items"] is not JsonArray items || items.Count is < 1 or > CommunityContent.MaxBatch)
            throw new FormatException("invalid_submission");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is not JsonObject draft)
                throw new FormatException("invalid_content");
            CommunityContent.Validate(draft);
            if (!seen.Add(draft["id"]!.GetValue<string>()))
                throw new FormatException("duplicate_id");
        }
        var bases = CommunityWorkspace.Object(payload, "base_revisions");
        foreach (var basis in bases)
        {
            if (!seen.Contains(basis.Key) || CommunityContent.Integer(bases, basis.Key) < 1)
                throw new FormatException("invalid_source_revision");
        }
        // Compact JSON with an ASCII-escaped representation prevents embedded newlines/fences
        // from altering the structural framing. GitHub takes the issue author from its session.
        var body = IssueMarker + "\n\n```json\n" + new JsonObject
        {
            ["schema_version"] = 1,
            ["license"] = license,
            ["consent"] = true,
            ["items"] = items.DeepClone(),
            ["base_revisions"] = bases.DeepClone(),
        }.ToJsonString() + "\n```";
        if (body.Length > CommunityContent.MaxSubmissionBytes)
            throw new FormatException("submission_size");
        return body;
    }

    private async Task<JsonObject> IssueStatusAsync(ActiveRequest request, int issue, CancellationToken token)
    {
        if (issue < 1)
            throw new FormatException("invalid_issue");
        var record = await Send(request, HttpMethod.Get,
            IssueUrl + "/" + issue.ToString(CultureInfo.InvariantCulture), null, null, token).ConfigureAwait(false);
        if (record["body"] is not JsonValue body || !body.TryGetValue<string>(out var issueBody) ||
            !issueBody.Contains(IssueMarker, StringComparison.Ordinal))
            return Result("invalid_issue");
        var labels = record["labels"]?.AsArray().Select(label => label?["name"]?.GetValue<string>()).ToHashSet();
        var status = labels switch
        {
            not null when labels.Contains("community-failed") || labels.Contains("community-classification") => "processing_failed",
            // Every item repeated text already in the library, so nothing new was published.
            not null when labels.Contains("community-duplicate") => "duplicate",
            not null when labels.Contains("community-published") => "published",
            _ when record["state"]?.GetValue<string>() == "closed" => "closed",
            _ => "processing",
        };
        return new JsonObject { ["status"] = status, ["issue"] = issue };
    }

    private async Task<string?> AccessAsync(ActiveRequest request, CancellationToken token)
    {
        string? stored;
        lock (_gate)
        {
            if (request.Superseded)
                return null;
            stored = _tokens.Read(_clientId);
        }
        if (stored is null || JsonNode.Parse(stored) is not JsonObject saved)
            return null;
        if (saved["expires"]!.GetValue<long>() > _clock.GetUtcNow().ToUnixTimeSeconds() + 60)
            return CommunityContent.String(saved, "access", 1024);
        if (saved["refresh"]?.GetValue<string>() is not { Length: > 0 } refresh)
            return null;
        var response = await FormAsync(request, TokenUrl, token, ("grant_type", "refresh_token"), ("refresh_token", refresh))
            .ConfigureAwait(false);
        lock (_gate)
        {
            if (request.Superseded)
                return null;
            if (response["error"] is not null)
            {
                _tokens.Clear();
                _account = null;
                return null;
            }
            var credentials = ParseToken(response);
            SaveToken(credentials);
            return credentials.Access;
        }
    }

    private Credentials ParseToken(JsonObject response)
    {
        var access = CommunityContent.String(response, "access_token", 1024);
        var expires = response["expires_in"]?.GetValue<int>() ?? int.MaxValue;
        var refresh = response["refresh_token"] is null ? "" : CommunityContent.String(response, "refresh_token", 1024);
        return new(access, refresh, _clock.GetUtcNow().AddSeconds(expires).ToUnixTimeSeconds());
    }

    private void SaveToken(Credentials credentials) => _tokens.Write(_clientId!, new JsonObject
    {
        ["access"] = credentials.Access,
        ["refresh"] = credentials.Refresh,
        ["expires"] = credentials.Expires,
    }.ToJsonString());

    private Task<JsonObject> FormAsync(ActiveRequest request, string url, CancellationToken token,
        params (string Key, string Value)[] parts)
    {
        var fields = parts.Select(part => KeyValuePair.Create(part.Key, part.Value)).Prepend(KeyValuePair.Create("client_id", _clientId!));
        return Send(request, HttpMethod.Post, url, null, new FormUrlEncodedContent(fields), token);
    }

    // Every GitHub request goes through here, so the request knows that and when it was sent.
    private Task<JsonObject> Send(ActiveRequest request, HttpMethod method, string url, string? access,
        HttpContent? content, CancellationToken token)
    {
        request.SentAt = _clock.GetUtcNow();
        return _transport(method, url, access, content, token);
    }

    private static string? ConfiguredClientId()
    {
        var value = Environment.GetEnvironmentVariable("MENTORRECORDER_COMMUNITY_CLIENT_ID");
        return value is { Length: >= 10 and <= 64 } && value.All(char.IsAsciiLetterOrDigit) ? value : null;
    }

    private static JsonObject GrantState(DeviceGrant grant) => new()
    {
        ["status"] = "authorizing",
        ["user_code"] = grant.UserCode,
        ["interval"] = grant.Interval,
    };

    private static JsonObject Result(string status) => new() { ["status"] = status };

    private static JsonObject SignedIn(JsonObject account) => new()
    {
        ["status"] = "signed_in",
        ["account"] = account.DeepClone(),
    };

    private sealed class ActiveRequest(string action)
    {
        internal string Action { get; } = action;
        internal bool Superseded { get; set; }
        // When this request last reached the network, and whether its issue POST has started.
        internal DateTimeOffset? SentAt { get; set; }
        internal bool Posted { get; set; }
    }

    private sealed record Credentials(string Access, string Refresh, long Expires);
    private sealed record DeviceGrant(string Code, string UserCode, DateTimeOffset Expires, DateTimeOffset NextPoll, int Interval)
    {
        internal Credentials? PendingCredentials { get; init; }
    }
}
