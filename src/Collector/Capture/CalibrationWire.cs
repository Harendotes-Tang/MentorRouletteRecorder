using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Calibration;
using MentorRecorder.Collector.Protocol.Profiles;
using MentorRecorder.Collector.Protocol.Sharing;

namespace MentorRecorder.Collector.Capture;

/// <summary>Renders calibration status for <c>CaptureStatus</c> and the diagnostics report.</summary>
public static class CalibrationWire
{
    /// <summary>Longest refusal token put on the wire.</summary>
    public const int MaxRefusalTokenLength = 64;

    /// <summary>Contract token for a state.</summary>
    /// <param name="state">State to render.</param>
    public static string State(CalibrationState state) => state switch
    {
        CalibrationState.Waiting => "WAITING",
        CalibrationState.Observing => "OBSERVING",
        CalibrationState.Ready => "READY",
        CalibrationState.Blocked => "BLOCKED",
        CalibrationState.Done => "DONE",
        _ => "IDLE",
    };

    /// <summary>Contract token for where the profile in force came from.</summary>
    /// <param name="origin">Origin, or null when no profile is in force.</param>
    public static string? Origin(ProfileOrigin? origin) => origin switch
    {
        ProfileOrigin.Local => "LOCAL_CALIBRATION",
        ProfileOrigin.Shipped => "SHIPPED",
        ProfileOrigin.Shared => "SHARED_CALIBRATION",
        _ => null,
    };

    /// <summary>Builds the <c>calibration</c> object of <c>CaptureStatus</c>.</summary>
    /// <param name="snapshot">Snapshot to render.</param>
    public static JsonObject Status(CalibrationStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var node = Report(snapshot);
        node.Remove("evidence");
        node["events"] = new JsonArray(snapshot.Events.Select(item => (JsonNode?)Event(item)).ToArray());
        return node;
    }

    /// <summary>
    /// The part of the status that belongs in the sanitized diagnostics report: no timeline,
    /// because the timeline names duties and roulettes the user played.
    /// </summary>
    /// <param name="snapshot">Snapshot to render.</param>
    public static JsonObject Report(CalibrationStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var node = new JsonObject
        {
            ["state"] = State(snapshot.State),
            ["game_build"] = snapshot.GameBuild,
            ["template_profile_id"] = snapshot.TemplateProfileId,
            ["local_profile_id"] = snapshot.LocalProfileId,
            ["bound_at_utc"] = UtcTimestamp.ToTextOrNull(snapshot.BoundAtUtc),
            ["blockers"] = new JsonArray(snapshot.Blockers.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray()),
        };
        node["evidence"] = snapshot.Evidence is { } evidence ? Evidence(evidence, snapshot.CarriedSource) : null;
        node["progress"] = snapshot.Progress is { } progress
            ? new JsonObject
            {
                ["finder_request_seen"] = progress.FinderRequestSeen,
                ["pop_seen"] = progress.PopSeen,
                ["zone_clusters"] = progress.ZoneClusters,
                ["duty_entry_seen"] = progress.DutyEntrySeen,
                ["duty_exit_seen"] = progress.DutyExitSeen,
                ["pop_shape_seen"] = progress.PopShapeSeen,
                ["duty_zone_seen"] = progress.DutyZoneSeen,
                ["job_seen"] = progress.JobSeen,
            }
            : null;
        node["shared"] = Shared(snapshot.Shared);
        return node;
    }

    /// <summary>
    /// Builds <c>$defs/SharedCalibrationStatus</c>, the same object on <c>CaptureStatus.calibration</c> and in the
    /// diagnostics report (docs/capture-diagnostics.md §9.6). A whitelist: tokens, twelve-digit code prefixes,
    /// plain-language criteria, counts and the shared profile id. Never an address, a host, a code, an opcode or
    /// payload bytes.
    /// </summary>
    /// <param name="shared">Shared calibration as it stands.</param>
    public static JsonObject Shared(SharedCalibrationSnapshot shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        return new JsonObject
        {
            ["phase"] = EnumWire<SharedCalibrationPhase>.Format(shared.Phase),
            ["candidates"] = new JsonArray(shared.Candidates.Select(item => (JsonNode?)Candidate(item)).ToArray()),
            ["last_fetch_status"] = shared.LastFetchStatus is { } status ? EnumWire<SharedFetchStatus>.Format(status) : null,
            ["last_index_attempts"] = new JsonArray(shared.LastIndexAttempts.Select(attempt => (JsonNode?)new JsonObject
            {
                ["source"] = EnumWire<SharedCalibrationSource>.Format(attempt.Source),
                ["outcome"] = EnumWire<SharedFetchOutcome>.Format(attempt.Outcome),
            }).ToArray()),
            ["profile_id"] = shared.ProfileId,
            ["bound_at_utc"] = UtcTimestamp.ToTextOrNull(shared.BoundAtUtc),
            ["last_refusal"] = RefusalToken(shared.LastRefusal),
            ["rejected_candidates"] = shared.Candidates.Count(item => item.Status == SharedCandidateStatus.Rejected),
            ["user_rejected"] = shared.UserRejected,
            ["audit_pending"] = shared.AuditPending,
        };
    }

    /// <summary>
    /// The leading upper-case token of a refusal (<c>NOT_SELECTED</c>, <c>WRITE_FAILED</c>, <c>REVOKED</c>...). What
    /// may follow it - an exception type, a selector's reason - is this process's own diagnostics and can name a
    /// file, so it never leaves the machine.
    /// </summary>
    /// <param name="refusal">Refusal as the session recorded it, or null.</param>
    public static string? RefusalToken(string? refusal)
    {
        if (string.IsNullOrEmpty(refusal))
        {
            return null;
        }

        var length = 0;
        while (length < refusal.Length && length < MaxRefusalTokenLength &&
               (char.IsAsciiLetterUpper(refusal[length]) || char.IsAsciiDigit(refusal[length]) || refusal[length] == '_'))
        {
            length++;
        }

        return length == 0 ? null : refusal[..length];
    }

    private static JsonObject Candidate(SharedCandidateSummary candidate) => new()
    {
        ["sha12"] = candidate.Sha12,
        ["source"] = candidate.Source is { } source ? EnumWire<SharedCandidateSource>.Format(source) : null,
        ["match_source"] = candidate.MatchSource is { } match ? EnumWire<CalibrationMatchSource>.Format(match) : null,
        ["status"] = EnumWire<SharedCandidateStatus>.Format(candidate.Status),
        ["verdict"] = EnumWire<SharedVerdict>.Format(candidate.Verdict),
        ["criteria"] = new JsonArray(candidate.Criteria.Select(criterion => (JsonNode?)new JsonObject
        {
            ["message"] = criterion.Message,
            ["verdict"] = EnumWire<SharedVerdict>.Format(criterion.Verdict),
            ["reason"] = criterion.Reason,
            ["contradicting_sessions"] = criterion.ContradictingSessions,
            ["gate"] = EnumWire<SharedGate>.Format(criterion.Gate),
        }).ToArray()),
        ["staging_overflowed"] = candidate.StagingOverflowed,
        ["provenance"] = candidate.Provenance is { } provenance ? EnumWire<SharedCandidateProvenance>.Format(provenance) : null,
        ["audit_pending"] = candidate.AuditPending,
    };

    private static JsonObject Evidence(CalibrationEvidenceSummary evidence, string? carriedSource) => new()
    {
        ["sessions"] = evidence.Sessions,
        ["messages_seen"] = evidence.MessagesSeen,
        ["pairs"] = Texts(evidence.Pairs),
        ["pops"] = Texts(evidence.Pops),
        ["clusters"] = evidence.Clusters,
        ["duty_zones"] = evidence.DutyZones,
        ["zone_candidates"] = evidence.ZoneCandidates,
        ["territory_candidates"] = evidence.TerritoryCandidates,
        ["outside_keys"] = evidence.OutsideKeys,
        ["overflow"] = evidence.Overflow,
        ["pop_shapes"] = evidence.PopShapes,
        ["pop_refusals"] = Texts(evidence.PopRefusals),
        ["finder_lengths"] = Texts(evidence.FinderLengths),
        ["roulette_echoes"] = Texts(evidence.RouletteEchoes ?? Array.Empty<string>()),
        ["match_echoes"] = Texts(evidence.MatchEchoes ?? Array.Empty<string>()),
        ["markers"] = Texts(evidence.Markers ?? Array.Empty<string>()),
        ["marker_overflow"] = evidence.MarkerOverflow,
        ["carried"] = evidence.Carried,
        ["carried_source"] = carriedSource,
        ["watched_seconds"] = evidence.WatchedSeconds,
        ["quiet_seconds"] = evidence.QuietSeconds,
        ["zone_outside"] = evidence.ZoneOutside,
        ["zone_shapes"] = Texts(evidence.ZoneShapes ?? Array.Empty<string>()),
        ["clusters_at"] = Numbers(evidence.ClustersAt ?? Array.Empty<long>()),
        ["pairs_at"] = Numbers(evidence.PairsAt ?? Array.Empty<long>()),
        ["finder_states"] = Texts(evidence.FinderStates),
        ["zone_once_only"] = Texts(evidence.ZoneOnceOnly),
        ["job_shapes"] = Texts(evidence.JobShapes ?? Array.Empty<string>()),
        ["diagnostics_overflow"] = evidence.DiagnosticsOverflow,
    };

    private static JsonArray Texts(IReadOnlyList<string> values) =>
        new(values.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray());

    /// <summary>Seconds-ago values. Relative on purpose: no wall clock leaves the machine here.</summary>
    /// <param name="values">Ages in seconds.</param>
    private static JsonArray Numbers(IReadOnlyList<long> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());

    private static JsonObject Event(CalibrationEvent item) => new()
    {
        ["event_id"] = item.EventId,
        ["kind"] = item.Kind,
        ["at_utc"] = UtcTimestamp.ToText(item.AtUtc),
        ["t_ms"] = item.TMs,
        ["label"] = item.Label,
        ["roulette_id"] = item.RouletteId,
        ["territory_id"] = item.TerritoryId,
        ["duty_name"] = item.DutyName,
        ["requires_confirmation"] = item.RequiresConfirmation,
    };
}
