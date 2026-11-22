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
    private readonly SemaphoreSlim _gate = new(1);
    private readonly string? _clientId = clientId ?? ConfiguredClientId();
    private readonly Func<HttpMethod, string, string?, HttpContent?, CancellationToken, Task<JsonObject>> _transport =
        transport ?? CommunityHttp.GithubAsync;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private int _deviceGeneration;
    private DeviceGrant? _grant;
    private JsonObject? _account;

    internal bool Configured => _clientId is not null;

    internal async Task<JsonObject> RequestAsync(string action, JsonObject payload, CancellationToken cancellationToken)
    {
        if (transport is null && Environment.GetEnvironmentVariable("MR_DISABLE_DUTY_COMMUNITY") == "1"
            && action is not ("cancel" or "sign_out"))
            return Result("offline");
        if (action is "cancel" or "sign_out")
            Interlocked.Increment(ref _deviceGeneration);
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result("auth_busy");
        try
        {
            if (action == "cancel")
            {
                _grant = null;
                return Result("cancelled");
            }
            if (action == "sign_out")
            {
                _grant = null;
                _account = null;
                _tokens.Clear();
                return Result("signed_out");
            }
            if (action == "issue_status")
                return await IssueStatusAsync(CommunityContent.Integer(payload, "issue"), cancellationToken).ConfigureAwait(false);
            if (_clientId is null)
                return Result("app_not_configured");

            return action switch
            {
                "sign_in" => await StartAsync(cancellationToken).ConfigureAwait(false),
                "poll" => await PollAsync(cancellationToken).ConfigureAwait(false),
                "account" => await AccountAsync(cancellationToken).ConfigureAwait(false),
                "submit" => await SubmitAsync(payload, cancellationToken).ConfigureAwait(false),
                _ => throw new FormatException("invalid_action"),
            };
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } && _clientId is not null)
            {
                _account = null;
                _tokens.Clear();
                return Result("signed_out");
            }
            return Result(action == "submit" ? "submit_unconfirmed" : "temporarily_unavailable");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonObject> StartAsync(CancellationToken token)
    {
        _grant = null;
        var generation = Volatile.Read(ref _deviceGeneration);
        var response = await FormAsync(DeviceUrl + "/code", token).ConfigureAwait(false);
        if (generation != Volatile.Read(ref _deviceGeneration))
            return Result("cancelled");
        var interval = Math.Clamp(CommunityContent.Integer(response, "interval"), 5, 120);
        var expiresIn = Math.Clamp(CommunityContent.Integer(response, "expires_in"), 1, 1800);
        var now = _clock.GetUtcNow();
        _grant = new(generation, CommunityContent.String(response, "device_code", 512),
            CommunityContent.String(response, "user_code", 32), now.AddSeconds(expiresIn), now.AddSeconds(interval), interval);
        return GrantState(_grant);
    }

    private async Task<JsonObject> PollAsync(CancellationToken token)
    {
        if (_grant is null)
            return Result("signed_out");
        var grant = _grant;
        if (grant.Generation != Volatile.Read(ref _deviceGeneration))
        {
            _grant = null;
            return Result("cancelled");
        }
        var now = _clock.GetUtcNow();
        if (now >= grant.Expires)
        {
            _grant = null;
            return Result("device_expired");
        }
        if (now < grant.NextPoll)
            return GrantState(grant);
        _grant = grant with { NextPoll = now.AddSeconds(grant.Interval) };
        var response = await FormAsync(TokenUrl, token, ("grant_type", Grant), ("device_code", grant.Code)).ConfigureAwait(false);
        if (grant.Generation != Volatile.Read(ref _deviceGeneration))
        {
            _grant = null;
            return Result("cancelled");
        }
        var error = response["error"]?.GetValue<string>();
        if (error == "authorization_pending")
            return GrantState(_grant);
        if (error == "slow_down")
        {
            _grant = grant with { Interval = grant.Interval + 5, NextPoll = now.AddSeconds(grant.Interval + 5) };
            return GrantState(_grant);
        }
        _grant = null;
        if (error is not null)
            return Result(error == "expired_token" ? "device_expired" : "authorization_denied");
        SaveToken(response);
        _account = null;
        return await AccountAsync(token).ConfigureAwait(false);
    }

    private async Task<JsonObject> AccountAsync(CancellationToken token)
    {
        var access = await AccessAsync(token).ConfigureAwait(false);
        if (access is null)
            return Result("signed_out");
        if (_account is null)
        {
            var user = await _transport(HttpMethod.Get, "https://api.github.com/user", access, null, token)
                .ConfigureAwait(false);
            _account = new JsonObject
            {
                ["login"] = CommunityContent.String(user, "login", 39),
                ["id"] = CommunityContent.Integer64(user, "id"),
            };
        }
        return new JsonObject { ["status"] = "signed_in", ["account"] = _account.DeepClone() };
    }

    private async Task<JsonObject> SubmitAsync(JsonObject payload, CancellationToken token)
    {
        var body = BuildIssueBody(payload);
        var access = await AccessAsync(token).ConfigureAwait(false);
        if (access is null || _account is null)
            return Result("signed_out");
        if (CommunityContent.Integer64(payload, "author_id") != CommunityContent.Integer64(_account, "id"))
            return Result("account_changed");
        var issue = await _transport(HttpMethod.Post, IssueUrl, access,
            CommunityHttp.Json(new JsonObject
            {
                ["title"] = "MentorRecorder community contribution",
                ["body"] = body,
            }), token).ConfigureAwait(false);
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

    private async Task<JsonObject> IssueStatusAsync(int issue, CancellationToken token)
    {
        if (issue < 1)
            throw new FormatException("invalid_issue");
        var record = await _transport(HttpMethod.Get,
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

    private async Task<string?> AccessAsync(CancellationToken token)
    {
        var stored = _tokens.Read(_clientId);
        if (stored is null || JsonNode.Parse(stored) is not JsonObject saved)
            return null;
        if (saved["expires"]!.GetValue<long>() > _clock.GetUtcNow().ToUnixTimeSeconds() + 60)
            return CommunityContent.String(saved, "access", 1024);
        if (saved["refresh"]?.GetValue<string>() is not { Length: > 0 } refresh)
            return null;
        var response = await FormAsync(TokenUrl, token, ("grant_type", "refresh_token"), ("refresh_token", refresh))
            .ConfigureAwait(false);
        if (response["error"] is not null)
        {
            _tokens.Clear();
            _account = null;
            return null;
        }
        return SaveToken(response);
    }

    private string SaveToken(JsonObject response)
    {
        var access = CommunityContent.String(response, "access_token", 1024);
        var expires = response["expires_in"]?.GetValue<int>() ?? int.MaxValue;
        var refresh = response["refresh_token"] is null ? "" : CommunityContent.String(response, "refresh_token", 1024);
        _tokens.Write(_clientId!, new JsonObject
        {
            ["access"] = access,
            ["refresh"] = refresh,
            ["expires"] = _clock.GetUtcNow().AddSeconds(expires).ToUnixTimeSeconds(),
        }.ToJsonString());
        return access;
    }

    private Task<JsonObject> FormAsync(string url, CancellationToken token, params (string Key, string Value)[] parts)
    {
        var fields = parts.Select(part => KeyValuePair.Create(part.Key, part.Value)).Prepend(KeyValuePair.Create("client_id", _clientId!));
        return _transport(HttpMethod.Post, url, null, new FormUrlEncodedContent(fields), token);
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

    private sealed record DeviceGrant(int Generation, string Code, string UserCode, DateTimeOffset Expires, DateTimeOffset NextPoll, int Interval);
}
