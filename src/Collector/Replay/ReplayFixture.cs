using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Domain.Events;

namespace MentorRecorder.Collector.Replay;

/// <summary>A validated synthetic semantic-event fixture.</summary>
/// <param name="FixtureId">Stable identifier, matching the file name stem.</param>
/// <param name="Description">Human explanation of what the fixture exercises.</param>
/// <param name="Sha256">Hash of the file, verified against the sidecar.</param>
/// <param name="Profile">Synthetic profile the state machine is bound to.</param>
/// <param name="Session">Synthetic capture session the events belong to.</param>
/// <param name="Events">Ordered semantic events.</param>
public sealed record ReplayFixture(
    string FixtureId,
    string Description,
    string Sha256,
    ReplayProfile Profile,
    ReplaySession Session,
    IReadOnlyList<SemanticEvent> Events);

/// <summary>The synthetic profile a fixture declares.</summary>
/// <param name="ProfileId">Synthetic profile identifier, for example <c>synthetic/v1</c>.</param>
/// <param name="MentorRouletteId">Synthetic mentor roulette id.</param>
/// <param name="MatchWindowSeconds">Match acceptance window.</param>
/// <param name="Status">
/// Declared status. <c>SYNTHETIC</c> (the default) yields a usable offline binding; any other
/// value yields a fail-closed live binding, which is how a fixture exercises the refusal
/// path. <c>VERIFIED</c> is rejected outright: a fixture is never evidence about the real
/// protocol and must not be able to claim it is.
/// </param>
public sealed record ReplayProfile(
    string ProfileId,
    int MentorRouletteId,
    int MatchWindowSeconds,
    string Status = ReplayProfile.SyntheticStatus)
{
    /// <summary>Status token selecting a usable synthetic binding.</summary>
    public const string SyntheticStatus = "SYNTHETIC";

    /// <summary>True when this fixture drives a usable state machine.</summary>
    public bool IsSynthetic => string.Equals(Status, SyntheticStatus, StringComparison.Ordinal);
}

/// <summary>The synthetic capture session a fixture declares.</summary>
/// <param name="CaptureSessionId">Session UUID; also the namespace of every event key.</param>
/// <param name="StartedAtUtc">Session start time.</param>
public sealed record ReplaySession(string CaptureSessionId, DateTimeOffset StartedAtUtc);

/// <summary>
/// Loads fixed, privacy-safe fixture files and verifies their sidecar SHA-256.
///
/// Semantic fixture format v1: the file declares a synthetic profile, a synthetic session
/// and an ordered list of already-semantic events. Repeating the same
/// <c>(kind, event_key, monotonic_ms)</c> triple is how a fixture expresses a duplicated
/// observation, which is what the deduplication tests need.
/// </summary>
public static class ReplayFixtureLoader
{
    /// <summary>Loads and validates one fixture file.</summary>
    /// <param name="path">Path to the <c>.fixture.json</c> file.</param>
    public static ReplayFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sidecarPath = fullPath + ".sha256";
        if (!File.Exists(sidecarPath))
        {
            throw new InvalidDataException("fixture SHA-256 sidecar is missing: " + sidecarPath);
        }

        var expectedHash = File.ReadAllText(sidecarPath).Trim().ToLowerInvariant();
        if (expectedHash.Length != 64 || !string.Equals(expectedHash, actualHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("fixture SHA-256 mismatch");
        }

        var document = JsonSerializer.Deserialize<FixtureDocument>(bytes)
            ?? throw new InvalidDataException("fixture document is empty");
        Validate(document);
        var profile = document.Profile!;
        var session = document.Session!;

        var events = document.Events!.Select(row => ToSemantic(session.CaptureSessionId!, row)).ToArray();
        return new ReplayFixture(
            document.FixtureId!,
            document.Description ?? string.Empty,
            actualHash,
            new ReplayProfile(
                profile.ProfileId!,
                profile.MentorRouletteId,
                profile.MatchWindowSeconds,
                profile.Status ?? ReplayProfile.SyntheticStatus),
            new ReplaySession(
                session.CaptureSessionId!,
                UtcTimestamp.Parse(session.StartedAtUtc)),
            events);
    }

    private static void Validate(FixtureDocument document)
    {
        if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.FixtureId) ||
            document.ContainsPersonalData || document.Profile is null || document.Session is null ||
            document.Events is null || document.Events.Length == 0)
        {
            throw new InvalidDataException("fixture header is invalid or declares personal data");
        }

        if (string.IsNullOrWhiteSpace(document.Profile.ProfileId) ||
            document.Profile.MentorRouletteId < 0 || document.Profile.MatchWindowSeconds <= 0)
        {
            throw new InvalidDataException("fixture synthetic profile is invalid");
        }

        // A fixture may declare a fail-closed status so the refusal path can be replayed,
        // but it may never declare itself VERIFIED: only real, documented evidence does that.
        if (string.Equals(document.Profile.Status, "VERIFIED", StringComparison.Ordinal))
        {
            throw new InvalidDataException("a fixture may not declare a VERIFIED protocol profile");
        }

        if (!Guid.TryParseExact(document.Session.CaptureSessionId, "D", out _) ||
            !UtcTimestamp.TryParse(document.Session.StartedAtUtc, out _))
        {
            throw new InvalidDataException("fixture session id or timestamp is invalid");
        }

        long previous = -1;
        foreach (var row in document.Events)
        {
            if (string.IsNullOrWhiteSpace(row.Kind) || string.IsNullOrWhiteSpace(row.EventKey) ||
                row.MonotonicMs < previous || !UtcTimestamp.TryParse(row.ObservedAtUtc, out _))
            {
                throw new InvalidDataException(
                    "fixture events must be monotonically ordered, keyed and UTC stamped");
            }

            previous = row.MonotonicMs;
        }
    }

    private static SemanticEvent ToSemantic(string sessionId, EventRow row)
    {
        var key = new EventKey(
            sessionId,
            PacketDirection.None,
            row.Kind!,
            row.MonotonicMs,
            null,
            row.EventKey!);
        var observed = UtcTimestamp.Parse(row.ObservedAtUtc);
        var mono = TimeSpan.FromMilliseconds(row.MonotonicMs);

        return row.Kind switch
        {
            "CONTENT_FINDER_POP" when row.RouletteId is { } rouletteId => new ContentFinderPop
            {
                Key = key, ObservedAtUtc = observed, Mono = mono,
                RouletteId = rouletteId, ContentId = row.ContentId,
            },
            "ZONE_INITIALIZATION" => new ZoneInitialization
            {
                Key = key, ObservedAtUtc = observed, Mono = mono,
                ContentId = row.ContentId, TerritoryId = row.TerritoryId,
                IsDutyInstance = row.IsDutyInstance ?? true,
            },
            "PLAYER_JOB" when row.JobId is { } jobId => new PlayerJob
            {
                Key = key, ObservedAtUtc = observed, Mono = mono, JobId = jobId,
            },
            "DUTY_RESULT" when row.Victory is { } victory => new DutyResult
            {
                Key = key, ObservedAtUtc = observed, Mono = mono, Victory = victory,
            },
            "MATCH_CANCELLED" => new MatchCancelled { Key = key, ObservedAtUtc = observed, Mono = mono },
            "MATCH_ANNOUNCED" => new MatchAnnounced { Key = key, ObservedAtUtc = observed, Mono = mono },
            "ZONE_LEFT" => new ZoneLeft
            {
                Key = key, ObservedAtUtc = observed, Mono = mono, TerritoryId = row.TerritoryId,
            },
            "INSTANCE_LEFT" => new InstanceLeft { Key = key, ObservedAtUtc = observed, Mono = mono },
            "CONNECTION_LOST" => new ConnectionLost { Key = key, ObservedAtUtc = observed, Mono = mono },
            "CAPTURE_STOPPED" => new CaptureStopped { Key = key, ObservedAtUtc = observed, Mono = mono },
            "EVENT_SEQUENCE_GAP" when row.DroppedCount is > 0 => new EventSequenceGap
            {
                Key = key, ObservedAtUtc = observed, Mono = mono, DroppedCount = row.DroppedCount.Value,
            },
            "PROFILE_LOST" => new ProfileLost
            {
                Key = key, ObservedAtUtc = observed, Mono = mono, Detail = "synthetic fixture marker",
            },
            "TIMEOUT_TICK" => new TimeoutTick { Key = key, ObservedAtUtc = observed, Mono = mono },
            _ => throw new InvalidDataException("fixture event has unsupported kind or missing fields: " + row.Kind),
        };
    }

    private sealed record FixtureDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
        [JsonPropertyName("fixture_id")] public string? FixtureId { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("contains_personal_data")] public bool ContainsPersonalData { get; init; }
        [JsonPropertyName("profile")] public ProfileRow? Profile { get; init; }
        [JsonPropertyName("session")] public SessionRow? Session { get; init; }
        [JsonPropertyName("events")] public EventRow[]? Events { get; init; }
    }

    private sealed record ProfileRow
    {
        [JsonPropertyName("profile_id")] public string? ProfileId { get; init; }
        [JsonPropertyName("mentor_roulette_id")] public int MentorRouletteId { get; init; }
        [JsonPropertyName("match_window_seconds")] public int MatchWindowSeconds { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
    }

    private sealed record SessionRow
    {
        [JsonPropertyName("capture_session_id")] public string? CaptureSessionId { get; init; }
        [JsonPropertyName("started_at_utc")] public string? StartedAtUtc { get; init; }
    }

    private sealed record EventRow
    {
        [JsonPropertyName("kind")] public string? Kind { get; init; }
        [JsonPropertyName("event_key")] public string? EventKey { get; init; }
        [JsonPropertyName("observed_at_utc")] public string? ObservedAtUtc { get; init; }
        [JsonPropertyName("monotonic_ms")] public long MonotonicMs { get; init; }
        [JsonPropertyName("roulette_id")] public int? RouletteId { get; init; }
        [JsonPropertyName("content_id")] public int? ContentId { get; init; }
        [JsonPropertyName("territory_id")] public int? TerritoryId { get; init; }
        [JsonPropertyName("job_id")] public int? JobId { get; init; }
        [JsonPropertyName("is_duty_instance")] public bool? IsDutyInstance { get; init; }
        [JsonPropertyName("victory")] public bool? Victory { get; init; }
        [JsonPropertyName("dropped_count")] public long? DroppedCount { get; init; }
    }
}
