using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>What the index said about a stored code when it was fetched; used only to order candidates.</summary>
/// <param name="CodeSha256">Identity of the code.</param>
/// <param name="Submitters">Distinct submitters.</param>
/// <param name="FirstPublishedAtUtc">First publication.</param>
/// <param name="Commit">Commit it was downloaded at.</param>
internal sealed record SharedCodeHints(string CodeSha256, int Submitters, DateTimeOffset? FirstPublishedAtUtc, string? Commit);

/// <summary>Capture sessions whose traffic contradicted a code under one template.</summary>
/// <param name="TemplateSha256">Template in force.</param>
/// <param name="CodeSha256">The code.</param>
/// <param name="Sessions">Distinct session ids, at most <see cref="SharedCalibrationState.MaxSessions"/>.</param>
/// <param name="FirstAtUtc">First contradiction.</param>
/// <param name="LastAtUtc">Latest contradiction.</param>
internal sealed record SharedRejection(
    string TemplateSha256, string CodeSha256, IReadOnlyList<string> Sessions, DateTimeOffset FirstAtUtc, DateTimeOffset LastAtUtc);

/// <summary>
/// The content of <c>state.json</c> in one region/build directory of <see cref="SharedCalibrationStore"/>.
///
/// Immutable: every change returns a new state. Reading is tolerant on purpose - the file is
/// bookkeeping, not evidence - so anything that is not what this build writes reads as absent: a
/// wrong layout reads as an empty state, and each record that fails a check is dropped on its own.
/// Every list is bounded, so a damaged file cannot make the next one grow.
/// </summary>
/// <param name="Fetches">Last attempt per template.</param>
/// <param name="Codes">Index hints per stored code.</param>
/// <param name="Revoked">Codes the last readable index revoked.</param>
/// <param name="Rejections">Contradiction records per template and code.</param>
/// <param name="UserRejectedAtUtc">
/// When the player chose 不用共享的，我自己校准 for this build; null while they have not. Written as the optional
/// <c>user_rejected_at</c>, so a file from before it existed reads as "not refused" without a schema bump.
/// </param>
internal sealed record SharedCalibrationState(
    IReadOnlyList<SharedFetchRecord> Fetches,
    IReadOnlyList<SharedCodeHints> Codes,
    IReadOnlyList<string> Revoked,
    IReadOnlyList<SharedRejection> Rejections,
    DateTimeOffset? UserRejectedAtUtc = null)
{
    internal const int SchemaVersion = 1;
    internal const int MaxFetches = 8;
    internal const int MaxCodes = 256;
    internal const int MaxRejections = 64;
    internal const int MaxSessions = 8;
    internal const int MaxSessionIdLength = 128;
    internal const long MaxBytes = 1024 * 1024;

    private const int MaxAttempts = 8;
    private const string StampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly Regex Token = new(@"^[A-Za-z0-9_:.-]{1,128}\z", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 16 };

    /// <summary>No fetch, no hints, nothing revoked, nothing rejected.</summary>
    internal static SharedCalibrationState Empty { get; } = new(
        Array.Empty<SharedFetchRecord>(), Array.Empty<SharedCodeHints>(), Array.Empty<string>(), Array.Empty<SharedRejection>());

    internal static bool IsSessionId([NotNullWhen(true)] string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= MaxSessionIdLength;

    internal SharedFetchRecord? FetchFor(string templateSha256) =>
        Fetches.FirstOrDefault(record => string.Equals(record.TemplateSha256, templateSha256, StringComparison.Ordinal));

    internal SharedCodeHints? HintsFor(string codeSha256) =>
        Codes.FirstOrDefault(hints => string.Equals(hints.CodeSha256, codeSha256, StringComparison.Ordinal));

    internal SharedRejection? RejectionFor(string templateSha256, string codeSha256) =>
        Rejections.FirstOrDefault(rejection => SameKey(rejection, templateSha256, codeSha256));

    internal SharedCalibrationState WithFetch(SharedFetchRecord record) => this with
    {
        Fetches = Fetches
            .Where(existing => !string.Equals(existing.TemplateSha256, record.TemplateSha256, StringComparison.Ordinal))
            .Prepend(record)
            .OrderByDescending(existing => existing.LastAttemptAtUtc)
            .Take(MaxFetches)
            .ToArray(),
    };

    internal SharedCalibrationState WithHints(IEnumerable<SharedCodeHints> hints) => this with
    {
        Codes = hints.Concat(Codes).DistinctBy(entry => entry.CodeSha256, StringComparer.Ordinal).Take(MaxCodes).ToArray(),
    };

    internal SharedCalibrationState WithRevoked(IEnumerable<string> revoked) => this with
    {
        Revoked = revoked
            .Where(sha => SharedCalibrationIndex.IsSha256(sha))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(SharedCalibrationIndex.MaxEntries)
            .ToArray(),
    };

    internal SharedCalibrationState WithRejection(SharedRejection rejection) => this with
    {
        Rejections = Rejections
            .Where(existing => !SameKey(existing, rejection.TemplateSha256, rejection.CodeSha256))
            .Prepend(rejection)
            .OrderByDescending(existing => existing.LastAtUtc)
            .Take(MaxRejections)
            .ToArray(),
    };

    /// <summary>The player refused shared calibration for this build; an earlier refusal keeps its time.</summary>
    internal SharedCalibrationState WithUserRejection(DateTimeOffset atUtc) => this with { UserRejectedAtUtc = UserRejectedAtUtc ?? atUtc };

    /// <summary>重新观察: every contradiction record and the player's refusal are forgotten.</summary>
    internal SharedCalibrationState WithoutRejections() => this with { Rejections = Array.Empty<SharedRejection>(), UserRejectedAtUtc = null };

    // ------------------------------------------------------------------------ reading

    /// <summary>Reads a state file's text; anything not understood reads as absent. Never throws.</summary>
    internal static SharedCalibrationState Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, ReadOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "schema_version") != SchemaVersion)
            {
                return Empty;
            }

            return new SharedCalibrationState(
                Items(root, "fetches").Select(ReadFetch).OfType<SharedFetchRecord>()
                    .DistinctBy(record => record.TemplateSha256, StringComparer.Ordinal).Take(MaxFetches).ToArray(),
                Items(root, "codes").Select(ReadHints).OfType<SharedCodeHints>()
                    .DistinctBy(hints => hints.CodeSha256, StringComparer.Ordinal).Take(MaxCodes).ToArray(),
                Empty.WithRevoked(Items(root, "revoked").Select(StringOf).OfType<string>()).Revoked,
                Items(root, "rejections").Select(ReadRejection).OfType<SharedRejection>()
                    .DistinctBy(rejection => (rejection.TemplateSha256, rejection.CodeSha256)).Take(MaxRejections).ToArray(),
                ReadStamp(root, "user_rejected_at"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Empty;
        }
    }

    private static SharedFetchRecord? ReadFetch(JsonElement item)
    {
        var template = StringOf(item, "template_sha256");
        if (!SharedCalibrationIndex.IsSha256(template) ||
            ReadStamp(item, "last_attempt_at") is not { } attempted ||
            !EnumWire<SharedFetchStatus>.TryParse(StringOf(item, "status"), out var status))
        {
            return null;
        }

        return new SharedFetchRecord(
            template,
            attempted,
            ReadStamp(item, "last_success_at"),
            status,
            ReadAttempts(item, "index_sources"),
            Items(item, "discards").Select(ReadDiscard).OfType<SharedCodeDiscard>()
                .Take(SharedCalibrationIndex.MaxCandidates).ToArray());
    }

    private static SharedSourceAttempt[] ReadAttempts(JsonElement node, string name) =>
        Items(node, name).Select(ReadAttempt).OfType<SharedSourceAttempt>().Take(MaxAttempts).ToArray();

    private static SharedSourceAttempt? ReadAttempt(JsonElement item)
    {
        if (!EnumWire<SharedCalibrationSource>.TryParse(StringOf(item, "source"), out var source) ||
            !EnumWire<SharedFetchOutcome>.TryParse(StringOf(item, "outcome"), out var outcome))
        {
            return null;
        }

        int? status = Int(item, "http_status") is int code and >= 100 and <= 599 ? code : null;
        return new SharedSourceAttempt(source, outcome, status, TokenOrNull(StringOf(item, "detail")));
    }

    private static SharedCodeDiscard? ReadDiscard(JsonElement item)
    {
        var sha = StringOf(item, "code_sha256");
        var reason = TokenOrNull(StringOf(item, "reason"));
        return SharedCalibrationIndex.IsSha256(sha) && reason is not null
            ? new SharedCodeDiscard(sha, reason, ReadAttempts(item, "sources"))
            : null;
    }

    private static SharedCodeHints? ReadHints(JsonElement item)
    {
        var sha = StringOf(item, "code_sha256");
        if (!SharedCalibrationIndex.IsSha256(sha))
        {
            return null;
        }

        var commit = StringOf(item, "commit");
        return new SharedCodeHints(
            sha,
            Int(item, "submitters") is int count and >= 0 ? count : 0,
            ReadStamp(item, "first_published_at"),
            SharedCalibrationIndex.IsCommit(commit) ? commit : null);
    }

    private static SharedRejection? ReadRejection(JsonElement item)
    {
        var template = StringOf(item, "template_sha256");
        var code = StringOf(item, "code_sha256");
        var sessions = Items(item, "sessions").Select(StringOf).Where(IsSessionId).OfType<string>()
            .Distinct(StringComparer.Ordinal).Take(MaxSessions).ToArray();
        if (!SharedCalibrationIndex.IsSha256(template) || !SharedCalibrationIndex.IsSha256(code) || sessions.Length == 0 ||
            ReadStamp(item, "first_at") is not { } first || ReadStamp(item, "last_at") is not { } last)
        {
            return null;
        }

        return new SharedRejection(template, code, sessions, first, last);
    }

    private static bool SameKey(SharedRejection rejection, string templateSha256, string codeSha256) =>
        string.Equals(rejection.TemplateSha256, templateSha256, StringComparison.Ordinal) &&
        string.Equals(rejection.CodeSha256, codeSha256, StringComparison.Ordinal);

    private static string? TokenOrNull(string? text) => text is not null && Token.IsMatch(text) ? text : null;

    private static IEnumerable<JsonElement> Items(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? StringOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? StringOf(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? StringOf(value) : null;

    private static int? Int(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static DateTimeOffset? ReadStamp(JsonElement node, string name) =>
        StringOf(node, name) is { } text &&
        DateTimeOffset.TryParseExact(text, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : null;

    // ------------------------------------------------------------------------ writing

    /// <summary>The state file's text for a region and build.</summary>
    internal string Serialize(Region region, string gameBuild)
    {
        var node = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["region"] = EnumWire<Region>.Format(region),
            ["game_build"] = gameBuild,
            ["fetches"] = Nodes(Fetches, WriteFetch),
            ["codes"] = Nodes(Codes, WriteHints),
            ["revoked"] = new JsonArray(Revoked.Select(sha => (JsonNode?)JsonValue.Create(sha)).ToArray()),
            ["rejections"] = Nodes(Rejections, rejection => new JsonObject
            {
                ["template_sha256"] = rejection.TemplateSha256,
                ["code_sha256"] = rejection.CodeSha256,
                ["sessions"] = new JsonArray(rejection.Sessions.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                ["first_at"] = Stamp(rejection.FirstAtUtc),
                ["last_at"] = Stamp(rejection.LastAtUtc),
            }),
        };
        if (UserRejectedAtUtc is { } refused)
        {
            node["user_rejected_at"] = Stamp(refused);
        }

        return node.ToJsonString(WriteOptions);
    }

    private static JsonObject WriteFetch(SharedFetchRecord record)
    {
        var node = new JsonObject
        {
            ["template_sha256"] = record.TemplateSha256,
            ["last_attempt_at"] = Stamp(record.LastAttemptAtUtc),
            ["status"] = EnumWire<SharedFetchStatus>.Format(record.Status),
            ["index_sources"] = Nodes(record.IndexAttempts, WriteAttempt),
            ["discards"] = Nodes(record.Discards, discard => new JsonObject
            {
                ["code_sha256"] = discard.CodeSha256,
                ["reason"] = discard.Reason,
                ["sources"] = Nodes(discard.Attempts, WriteAttempt),
            }),
        };
        if (record.LastSuccessAtUtc is { } success)
        {
            node["last_success_at"] = Stamp(success);
        }

        return node;
    }

    private static JsonObject WriteAttempt(SharedSourceAttempt attempt)
    {
        var node = new JsonObject
        {
            ["source"] = EnumWire<SharedCalibrationSource>.Format(attempt.Source),
            ["outcome"] = EnumWire<SharedFetchOutcome>.Format(attempt.Outcome),
        };
        if (attempt.HttpStatus is { } status)
        {
            node["http_status"] = status;
        }

        if (attempt.Detail is { } detail)
        {
            node["detail"] = detail;
        }

        return node;
    }

    private static JsonObject WriteHints(SharedCodeHints hints)
    {
        var node = new JsonObject
        {
            ["code_sha256"] = hints.CodeSha256,
            ["submitters"] = hints.Submitters,
        };
        if (hints.FirstPublishedAtUtc is { } published)
        {
            node["first_published_at"] = Stamp(published);
        }

        if (hints.Commit is { } commit)
        {
            node["commit"] = commit;
        }

        return node;
    }

    private static JsonArray Nodes<T>(IEnumerable<T> items, Func<T, JsonObject> write) =>
        new(items.Select(item => (JsonNode?)write(item)).ToArray());

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture);
}
