using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Turns domain objects into the JSON shapes of contracts/ipc-v1.schema.json.
///
/// One direction only, and one place only: if a field name or an enum spelling is wrong it
/// is wrong here and nowhere else. The CSV and JSON exporters reuse these builders so an
/// exported file cannot drift from what the UI was shown.
/// </summary>
public static class Wire
{
    /// <summary>
    /// Options every JSON node in this process is written with.
    ///
    /// The explicit resolver is required, not cosmetic: a <c>JsonNode</c> built through the
    /// generic <c>Add&lt;T&gt;</c> / <c>JsonValue.Create&lt;T&gt;</c> overloads holds a boxed
    /// CLR value, and writing one without a type resolver throws at serialisation time --
    /// that is, on the wire, long after the mistake was made.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>Indented variant of <see cref="JsonOptions"/>, for files a human reads.</summary>
    public static JsonSerializerOptions IndentedJsonOptions { get; } = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Converts an audit or diagnostic value into a primitive JSON node.
    ///
    /// Audit rows and error details carry <c>object?</c> values that came back from JSON in
    /// the first place, so the accepted set is deliberately small and closed; anything else
    /// is rendered as its invariant string rather than reflected over.
    /// </summary>
    /// <param name="value">Value to render.</param>
    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>Builds an array of plain strings.</summary>
    /// <param name="values">Strings to include.</param>
    public static JsonArray Strings(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    /// <summary>Builds a <c>$defs/Run</c> object.</summary>
    /// <param name="run">Run to render.</param>
    public static JsonObject Run(MentorRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new JsonObject
        {
            ["run_id"] = run.RunId,
            ["revision"] = run.Revision,
            ["capture_session_id"] = run.CaptureSessionId,
            ["region"] = EnumWire<Region>.Format(run.Region),
            ["game_build"] = run.GameBuild,
            ["protocol_profile_id"] = run.ProtocolProfileId,
            ["mentor_roulette_id"] = run.MentorRouletteId,
            ["content_id"] = run.ContentId,
            ["territory_id"] = run.TerritoryId,
            ["duty_name"] = run.DutyName,
            ["duty_category"] = run.DutyCategory,
            ["job_id"] = run.JobId,
            ["job_name"] = run.JobName,
            ["role"] = EnumWire<Role>.Format(run.Role),
            ["matched_at_utc"] = UtcTimestamp.ToTextOrNull(run.MatchedAtUtc),
            ["entered_at_utc"] = UtcTimestamp.ToTextOrNull(run.EnteredAtUtc),
            ["ended_at_utc"] = UtcTimestamp.ToTextOrNull(run.EndedAtUtc),
            ["duration_ms"] = run.DurationMs,
            ["result"] = EnumWire<RunResult>.Format(run.Result),
            ["detection_confidence"] = EnumWire<DetectionConfidence>.Format(run.DetectionConfidence),
            ["source"] = EnumWire<RunSource>.Format(run.Source),
            ["contributes_to_goal"] = run.ContributesToGoal,
            ["manually_created"] = run.ManuallyCreated,
            ["manually_corrected"] = run.ManuallyCorrected,
            ["soft_deleted"] = run.SoftDeleted,

            // Proposed contract additions; see the change requests in docs/data-model.md.
            ["pending_review"] = run.PendingReview,
            ["note"] = run.Note,

            // Always present, null when the run has none: the contract promises the property
            // exists on every serialised Run.
            ["reflection"] = Reflection(run.Reflection),

            ["created_at_utc"] = UtcTimestamp.ToText(run.CreatedAtUtc),
            ["updated_at_utc"] = UtcTimestamp.ToText(run.UpdatedAtUtc),
        };
    }

    /// <summary>Builds a <c>$defs/RunReflection</c> object, or JSON null.</summary>
    /// <param name="reflection">Reflection to render; null renders as null.</param>
    public static JsonNode? Reflection(RunReflection? reflection) => reflection is null
        ? null
        : new JsonObject
        {
            ["mood"] = ReflectionText.Format(reflection.Mood),
            ["text"] = reflection.Text,
            ["created_at_utc"] = UtcTimestamp.ToText(reflection.CreatedAtUtc),
            ["updated_at_utc"] = UtcTimestamp.ToText(reflection.UpdatedAtUtc),
        };

    /// <summary>Builds a <c>$defs/ReflectionEntry</c> object.</summary>
    /// <param name="run">Run the reflection belongs to; its own reflection is rendered too.</param>
    /// <param name="reflection">Reflection to render.</param>
    public static JsonObject ReflectionEntry(MentorRun run, RunReflection reflection)
    {
        ArgumentNullException.ThrowIfNull(reflection);

        return new JsonObject
        {
            ["run"] = Run(run),
            ["reflection"] = Reflection(reflection),
        };
    }

    /// <summary>Builds a <c>$defs/RunRevision</c> object.</summary>
    /// <param name="revision">Revision to render.</param>
    public static JsonObject Revision(RunRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);

        var changes = new JsonArray();
        foreach (var change in revision.Changes)
        {
            changes.Add(new JsonObject
            {
                ["field"] = change.Field,
                ["old_value"] = Value(change.OldValue),
                ["new_value"] = Value(change.NewValue),
            });
        }

        return new JsonObject
        {
            ["revision_id"] = revision.RevisionId,
            ["run_id"] = revision.RunId,
            ["revision"] = revision.Revision,
            ["changed_at_utc"] = UtcTimestamp.ToText(revision.ChangedAtUtc),
            ["change_kind"] = EnumWire<ChangeKind>.Format(revision.ChangeKind),
            ["reason"] = revision.Reason,
            ["actor"] = EnumWire<RevisionActor>.Format(revision.Actor),
            ["changes"] = changes,
        };
    }

    /// <summary>Builds a <c>$defs/PageInfo</c> object.</summary>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size in effect.</param>
    /// <param name="total">Total matching rows.</param>
    public static JsonObject PageInfo(int page, int pageSize, int total) => new()
    {
        ["page"] = page,
        ["page_size"] = pageSize,
        ["total"] = total,
    };

    /// <summary>Builds a <c>$defs/ResultStats</c> object.</summary>
    /// <param name="statistics">Result distribution.</param>
    public static JsonObject ResultStats(ResultStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        var buckets = new JsonArray();
        foreach (var bucket in statistics.Buckets)
        {
            buckets.Add(new JsonObject
            {
                ["result"] = EnumWire<RunResult>.Format(bucket.Result),
                ["count"] = bucket.Count,
                ["share"] = bucket.Share,
            });
        }

        return new JsonObject
        {
            ["attempt_count"] = statistics.AttemptCount,
            ["buckets"] = buckets,
        };
    }

    /// <summary>Builds a <c>$defs/DashboardStats</c> object.</summary>
    /// <param name="statistics">Dashboard statistics.</param>
    public static JsonObject Dashboard(DashboardStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        return new JsonObject
        {
            ["attempt_count"] = statistics.AttemptCount,
            ["completed_count"] = statistics.CompletedCount,
            ["baseline_completed_count"] = statistics.BaselineCompletedCount,
            ["achievement_progress"] = statistics.AchievementProgress,
            ["goal_count"] = statistics.GoalCount,
            ["remaining"] = statistics.Remaining,
            ["completion_rate"] = statistics.CompletionRate,
            ["leave_rate"] = statistics.LeaveRate,
            ["avg_duration_ms"] = statistics.AverageDurationMs,
            ["result_breakdown"] = ResultStats(statistics.ResultBreakdown),
            ["unfinished_pending_review"] = statistics.UnfinishedPendingReview,
            ["trend"] = Trend(statistics.Trend),
        };
    }

    /// <summary>Builds a <c>$defs/TrendSeries</c> object.</summary>
    /// <param name="trend">Completion trend to render.</param>
    public static JsonObject Trend(TrendSeries trend)
    {
        ArgumentNullException.ThrowIfNull(trend);

        var buckets = new JsonArray();
        foreach (var bucket in trend.Buckets)
        {
            buckets.Add(new JsonObject
            {
                ["start_utc"] = UtcTimestamp.ToText(bucket.StartUtc),
                ["completed_count"] = bucket.CompletedCount,
            });
        }

        return new JsonObject
        {
            ["granularity"] = TrendGranularityToken(trend.Granularity),
            ["buckets"] = buckets,
        };
    }

    /// <summary>
    /// Wire token of a trend granularity.
    ///
    /// Spelled out rather than derived from <c>EnumWire</c>: these three are lower case on the
    /// wire because they are request parameters the client types, not domain enum values.
    /// </summary>
    /// <param name="granularity">Granularity to render.</param>
    public static string TrendGranularityToken(TrendGranularity granularity) => granularity switch
    {
        TrendGranularity.Week => "week",
        TrendGranularity.Month => "month",
        _ => "day",
    };

    /// <summary>Builds a <c>$defs/DungeonStatsRow</c> object.</summary>
    /// <param name="row">Aggregated row.</param>
    public static JsonObject DungeonRow(DungeonStatisticsRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new JsonObject
        {
            ["content_id"] = row.ContentId,
            ["duty_name"] = row.DutyName,
            ["duty_category"] = row.DutyCategory,
            ["attempt_count"] = row.AttemptCount,
            ["completed_count"] = row.CompletedCount,
            ["completion_rate"] = row.CompletionRate,
            ["avg_duration_ms"] = row.AverageDurationMs,
            ["last_seen_utc"] = UtcTimestamp.ToTextOrNull(row.LastSeenUtc),
        };
    }

    /// <summary>Builds a <c>$defs/JobStatsRow</c> object.</summary>
    /// <param name="row">Aggregated row.</param>
    public static JsonObject JobRow(JobStatisticsRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new JsonObject
        {
            ["job_id"] = row.JobId,
            ["job_name"] = row.JobName,
            ["role"] = EnumWire<Role>.Format(row.Role),
            ["attempt_count"] = row.AttemptCount,
            ["completed_count"] = row.CompletedCount,
            ["completion_rate"] = row.CompletionRate,
            ["avg_duration_ms"] = row.AverageDurationMs,
        };
    }

    /// <summary>Builds a page envelope of already-rendered items.</summary>
    /// <param name="items">Rendered items.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size in effect.</param>
    /// <param name="total">Total matching rows.</param>
    public static JsonObject PagedItems(JsonArray items, int page, int pageSize, int total) => new()
    {
        ["items"] = items,
        ["page_info"] = PageInfo(page, pageSize, total),
    };

    /// <summary>Renders one page of runs.</summary>
    /// <param name="page">Page produced by the repository.</param>
    public static JsonObject Runs(Page<MentorRun> page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var items = new JsonArray();
        foreach (var run in page.Items)
        {
            items.Add(Run(run));
        }

        return PagedItems(items, page.PageNumber, page.PageSize, page.Total);
    }

    /// <summary>
    /// Builds a <c>$defs/RunEventEntry</c>.
    ///
    /// Direction, opcode and the payload digest are recovered from
    /// <c>run_events.event_key</c>, which is the observation's own identity and already holds
    /// exactly those three things (<see cref="Domain.Events.EventKey"/>). Nothing is read from
    /// a packet here, and the digest is cut to twelve hex characters -- the width the forensic
    /// trace and the adapter fingerprint use -- because a comparison handle is all a reader
    /// needs.
    /// </summary>
    /// <param name="runEvent">Stored event row.</param>
    /// <param name="protocolProfileId">Profile the owning run was recorded under, or null.</param>
    public static JsonObject RunEventEntry(RunEvent runEvent, string? protocolProfileId)
    {
        ArgumentNullException.ThrowIfNull(runEvent);

        var identity = EventIdentity.Parse(runEvent.EventKey);
        return new JsonObject
        {
            ["event_id"] = runEvent.EventId,
            ["event_type"] = runEvent.EventType,
            ["observed_at_utc"] = UtcTimestamp.ToText(runEvent.OccurredAtUtc),
            ["direction"] = identity.Direction,
            ["opcode"] = identity.Opcode,
            ["payload_hash"] = identity.PayloadHash,
            ["parser_status"] = identity.ParserStatus,
            ["protocol_profile_id"] = protocolProfileId,
            ["parsed"] = ParsedDetail(runEvent.DetailJson),
        };
    }

    private static JsonObject? ParsedDetail(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return null;
        }

        try
        {
            // Stored by this process, but parsed defensively all the same: a hand-edited
            // database must not be able to break a read.
            return JsonNode.Parse(detailJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The observation facts a stored <c>run_events.event_key</c> carries, reduced to the wire
/// shape of <c>$defs/RunEventEntry</c>.
/// </summary>
/// <param name="Direction">S2C, C2S, or null.</param>
/// <param name="Opcode">Fixed-width hex opcode, or null.</param>
/// <param name="PayloadHash">Twelve hex characters, or null.</param>
/// <param name="ParserStatus">PARSED, SYNTHETIC or UNKNOWN.</param>
public sealed record EventIdentity(
    string? Direction, string? Opcode, string? PayloadHash, string ParserStatus)
{
    /// <summary>Hex characters kept from a stored payload digest.</summary>
    public const int PayloadHashLength = 12;

    /// <summary>The answer for a row with no observation identity at all.</summary>
    public static EventIdentity Unknown { get; } = new(null, null, null, "UNKNOWN");

    /// <summary>
    /// Parses the canonical event key written by
    /// <c>Domain.Events.EventKey.ToCanonicalString</c>. Anything that does not have the
    /// expected six parts is reported as <see cref="Unknown"/> rather than guessed at.
    /// </summary>
    /// <param name="eventKey">Stored canonical key, or null.</param>
    public static EventIdentity Parse(string? eventKey)
    {
        if (string.IsNullOrEmpty(eventKey))
        {
            return Unknown;
        }

        var parts = eventKey.Split('|');
        if (parts.Length < 6)
        {
            return Unknown;
        }

        var direction = parts[1] switch
        {
            nameof(Domain.Events.PacketDirection.ServerToClient) => "S2C",
            nameof(Domain.Events.PacketDirection.ClientToServer) => "C2S",
            _ => null,
        };

        // A numeric third part is an opcode the parser read off a real message; a symbolic
        // one is a lifecycle kind the Collector produced itself. That distinction is the
        // whole of parser_status, and it is derived rather than stored so it cannot drift.
        var parsed = int.TryParse(
            parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var opcode) &&
            opcode is >= 0 and <= 0xFFFF;

        var payloadHash = parts[4].Length >= PayloadHashLength
            ? parts[4][..PayloadHashLength].ToLowerInvariant()
            : null;
        if (payloadHash is not null && !IsHex(payloadHash))
        {
            payloadHash = null;
        }

        return new EventIdentity(
            direction,
            parsed ? "0x" + opcode.ToString("X4", CultureInfo.InvariantCulture) : null,
            payloadHash,
            parsed ? "PARSED" : "SYNTHETIC");
    }

    private static bool IsHex(string text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
