using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;

namespace MentorRecorder.Collector.Ipc;

/// <summary>Paging arguments read from a request.</summary>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Page size, already capped at the contract maximum.</param>
public sealed record PageRequest(int Page, int PageSize);

/// <summary>
/// Turns validated payloads into domain requests.
///
/// Nothing here touches storage. A payload that gets past these functions is known to be
/// structurally valid, within every declared bound, and free of unknown fields, so the
/// storage layer only ever has to enforce business rules.
/// </summary>
public static class RequestParsers
{
    /// <summary>Reads a <c>$defs/RunFilter</c>.</summary>
    /// <param name="reader">Payload containing the filter, or null.</param>
    public static RunFilter? Filter(PayloadReader? reader)
    {
        if (reader is null)
        {
            return null;
        }

        reader.RejectUnknown(
            "from_utc", "to_utc", "date_field", "content_id", "duty_category", "job_id",
            "result", "source", "corrected_only", "include_deleted", "with_reflection",
            "pending_review", "text");

        var filter = new RunFilter
        {
            FromUtc = reader.Timestamp("from_utc"),
            ToUtc = reader.Timestamp("to_utc"),
            DateField = DateField(reader.String("date_field", 32)),
            ContentIds = reader.IntArray("content_id", 500, minimum: 0),
            DutyCategories = reader.StringArray("duty_category", 100),
            JobIds = reader.IntArray("job_id", 100, minimum: 0),
            Results = reader.EnumArray<RunResult>("result", 6),
            Sources = reader.EnumArray<RunSource>("source", 3),
            CorrectedOnly = reader.Bool("corrected_only") ?? false,
            IncludeDeleted = reader.Bool("include_deleted") ?? false,
            WithReflection = reader.Bool("with_reflection") ?? false,
            PendingReview = reader.Bool("pending_review"),
            Text = reader.String("text", 200),
        };

        if (filter.FromUtc is { } from && filter.ToUtc is { } to && from > to)
        {
            throw CollectorException.BadRequest("from_utc 不能晚于 to_utc。", "filter.from_utc");
        }

        return filter;
    }

    /// <summary>Reads a <c>$defs/RunSort</c>.</summary>
    /// <param name="reader">Payload containing the sort, or null.</param>
    public static RunSort Sort(PayloadReader? reader)
    {
        if (reader is null)
        {
            return RunSort.Default;
        }

        reader.RejectUnknown("field", "direction");
        return new RunSort(SortField(reader.String("field", 32)), Direction(reader.String("direction", 8)));
    }

    /// <summary>Reads paging arguments and applies the contract cap.</summary>
    /// <param name="reader">Payload containing page and page_size.</param>
    public static PageRequest Paging(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new PageRequest(
            reader.Int("page", 1, int.MaxValue) ?? 1,
            reader.Int("page_size", 1, Domain.Queries.Paging.MaxPageSize)
                ?? Domain.Queries.Paging.DefaultPageSize);
    }

    /// <summary>Reads a <c>$defs/CreateManualRunRequest</c>.</summary>
    /// <param name="requestId">Envelope request id, used as the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static CreateManualRunCommand CreateManualRun(string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown(
            "content_id", "duty_name", "duty_category", "job_id", "matched_at_utc",
            "entered_at_utc", "ended_at_utc", "duration_ms", "result", "contributes_to_goal",
            "reason", "note");

        return new CreateManualRunCommand
        {
            RequestId = requestId,
            Reason = RequireReason(reader),
            Result = reader.RequiredEnum<RunResult>("result"),
            ContentId = reader.Int("content_id", 0),
            DutyName = reader.String("duty_name", 200),
            DutyCategory = reader.String("duty_category", 100),
            JobId = reader.Int("job_id", 0),
            MatchedAtUtc = reader.Timestamp("matched_at_utc"),
            EnteredAtUtc = reader.Timestamp("entered_at_utc"),
            EndedAtUtc = reader.Timestamp("ended_at_utc"),
            DurationMs = reader.Long("duration_ms"),
            DurationSpecified = reader.Has("duration_ms"),
            ContributesToGoal = reader.Bool("contributes_to_goal") ?? true,
            Note = reader.String("note", 1000),
        };
    }

    /// <summary>Reads a <c>$defs/CorrectRunRequest</c>.</summary>
    /// <param name="requestId">Envelope request id, used as the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static CorrectRunCommand CorrectRun(string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown("run_id", "expected_revision", "reason", "changes");
        var runId = reader.RequiredUuid("run_id");
        var expectedRevision = reader.RequiredInt("expected_revision", 1);
        var reason = RequireReason(reader);
        var changes = reader.Object("changes")
            ?? throw CollectorException.BadRequest("缺少必填字段 changes。", "payload.changes");

        return new CorrectRunCommand(requestId, runId, expectedRevision, reason, Changes(changes));
    }

    /// <summary>Reads the <c>changes</c> object of a correction.</summary>
    /// <param name="reader">Changes object.</param>
    public static RunChangeSet Changes(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown(RunFields.Correctable.ToArray());
        var specified = new HashSet<string>(reader.Names, StringComparer.Ordinal);

        var duration = reader.Long(RunFields.DurationMs);
        if (duration is < 0)
        {
            throw new CollectorException(
                ErrorCodes.NegativeDuration, "时长不能为负数。", field: "changes.duration_ms");
        }

        // A correction may confirm a 待复核 record (false) but never send one back into review:
        // pending_review is set only by the machine, when it could not observe an ending.
        var pendingReview = reader.Bool(RunFields.PendingReview);
        if (pendingReview is true)
        {
            throw new CollectorException(
                ErrorCodes.BadRequest, "pending_review 只能改为 false（确认复核）。",
                field: "changes.pending_review");
        }

        return new RunChangeSet
        {
            Specified = specified,
            ContentId = reader.Int(RunFields.ContentId, 0),
            DutyName = reader.String(RunFields.DutyName, 200),
            DutyCategory = reader.String(RunFields.DutyCategory, 100),
            JobId = reader.Int(RunFields.JobId, 0),
            MatchedAtUtc = reader.Timestamp(RunFields.MatchedAtUtc),
            EnteredAtUtc = reader.Timestamp(RunFields.EnteredAtUtc),
            EndedAtUtc = reader.Timestamp(RunFields.EndedAtUtc),
            DurationMs = duration,
            Result = reader.Enum<RunResult>(RunFields.Result),
            ContributesToGoal = reader.Bool(RunFields.ContributesToGoal),
            Note = reader.String(RunFields.Note, 1000),
            PendingReview = pendingReview,
        };
    }

    /// <summary>Reads a <c>$defs/RunReasonRequest</c>.</summary>
    /// <param name="requestId">Envelope request id, used as the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static RunReasonCommand RunReason(string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown("run_id", "expected_revision", "reason");
        return new RunReasonCommand(
            requestId,
            reader.RequiredUuid("run_id"),
            reader.RequiredInt("expected_revision", 1),
            RequireReason(reader));
    }

    /// <summary>Reads a <c>$defs/UpdateAchievementBaselineRequest</c>.</summary>
    /// <param name="requestId">Envelope request id, used as the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static UpdateAchievementBaselineCommand UpdateBaseline(string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown(
            "goal_count", "baseline_completed_count", "baseline_effective_at", "reason");
        return new UpdateAchievementBaselineCommand(
            requestId,
            reader.Int("goal_count", 1) ?? AchievementSettings.DefaultGoalCount,
            reader.RequiredInt("baseline_completed_count", 0),
            reader.RequiredTimestamp("baseline_effective_at"),
            RequireReason(reader));
    }

    /// <summary>Reads a <c>$defs/SetRunReflectionRequest</c>.</summary>
    /// <param name="requestId">Envelope request id, used as the idempotency key.</param>
    /// <param name="reader">Request payload.</param>
    public static SetRunReflectionCommand SetRunReflection(string requestId, PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown("run_id", "mood", "text");
        var runId = reader.RequiredUuid("run_id");

        if (!ReflectionText.TryParse(reader.RequiredString("mood", 16), out var mood))
        {
            throw CollectorException.BadRequest("mood 只能取 good / ok / bad。", "payload.mood");
        }

        // Present but empty is legal and clears the reflection, so required-ness is checked
        // here rather than through RequiredString, which refuses the empty string.
        var text = reader.String("text", ReflectionText.MaxLength)
            ?? throw CollectorException.BadRequest("缺少必填字段 text。", "payload.text");

        return new SetRunReflectionCommand(requestId, runId, mood, text);
    }

    /// <summary>Reads the optional <c>recent_limit</c> of a <c>$defs/GetReflectionSummaryRequest</c>.</summary>
    /// <param name="reader">Request payload.</param>
    public static int ReflectionRecentLimit(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown("recent_limit");
        return reader.Int("recent_limit", 0, 20) ?? 3;
    }

    /// <summary>
    /// Reads the optional <c>trend_granularity</c> of a <c>$defs/StatsRequest</c>.
    /// </summary>
    /// <param name="reader">Request payload.</param>
    public static TrendGranularity TrendGranularity(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return reader.String("trend_granularity", 16) switch
        {
            null or "day" => Domain.Statistics.TrendGranularity.Day,
            "week" => Domain.Statistics.TrendGranularity.Week,
            "month" => Domain.Statistics.TrendGranularity.Month,
            _ => throw CollectorException.BadRequest(
                "trend_granularity 只能取 day / week / month。", "trend_granularity"),
        };
    }

    /// <summary>
    /// Reads a <c>$defs/ExportDiagnosticsReportRequest</c>: an optional destination and an
    /// optional permission to replace a file that is already there. The destination is
    /// validated by the export layer, not here: where a path lands is a filesystem fact
    /// rather than a payload shape.
    /// </summary>
    /// <param name="reader">Request payload.</param>
    public static DiagnosticsReportRequest DiagnosticsReport(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown("target_path", "overwrite");
        return new DiagnosticsReportRequest(
            reader.String("target_path", 32000),
            reader.Bool("overwrite") ?? false);
    }

    /// <summary>Reads a <c>$defs/UpdateCaptureSettingsRequest</c>.</summary>
    /// <param name="reader">Request payload.</param>
    public static Capture.CaptureSettingsUpdate CaptureSettings(PayloadReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        reader.RejectUnknown(
            "follow_game", "autostart", "adapter_id", "log_retention_days",
            "allow_without_profile", "region_override", "candidate_validation_enabled", "research_payload_opcodes",
            "auto_calibration_enabled", "shared_calibration_enabled");

        // adapter_id and region_override distinguish "not named" from "named as null": for
        // both, null is a request to clear the value, not a request to leave it alone.
        var adapterSpecified = reader.Has("adapter_id");
        if (reader.IsNull("research_payload_opcodes"))
            throw CollectorException.BadRequest("research_payload_opcodes 必须是数组，清空请传 []。", "payload.research_payload_opcodes");
        if (reader.Has("candidate_validation_enabled") && reader.Bool("candidate_validation_enabled") is null)
            throw CollectorException.BadRequest("candidate_validation_enabled 必须是布尔值。", "payload.candidate_validation_enabled");
        var regionSpecified = reader.Has("region_override");
        var regionText = reader.String("region_override", 16);
        if (regionSpecified && regionText is not (null or "CN" or "GLOBAL"))
        {
            throw CollectorException.BadRequest(
                "region_override 只能取 CN / GLOBAL 或 null。", "payload.region_override");
        }

        return new Capture.CaptureSettingsUpdate
        {
            CandidateValidationEnabled = reader.Bool("candidate_validation_enabled"),
            ResearchPayloadOpcodes = reader.Has("research_payload_opcodes")
                ? Capture.ResearchPayloadPolicy.Normalize(reader.StringArray("research_payload_opcodes", Capture.ResearchPayloadPolicy.MaxOpcodes)) : null,
            AutoCalibrationEnabled = reader.Bool("auto_calibration_enabled"),
            SharedCalibrationEnabled = reader.Bool("shared_calibration_enabled"),
            FollowGame = reader.Bool("follow_game"),
            Autostart = reader.Bool("autostart"),
            AdapterId = reader.String("adapter_id", 400),
            AdapterSpecified = adapterSpecified,
            LogRetentionDays = reader.Int(
                "log_retention_days",
                Diagnostics.RotatingFileLogger.MinRetentionDays,
                Diagnostics.RotatingFileLogger.MaxRetentionDays),
            AllowWithoutProfile = reader.Bool("allow_without_profile"),
            RegionOverride = regionText switch
            {
                "CN" => Domain.Region.Cn,
                "GLOBAL" => Domain.Region.Global,
                _ => null,
            },
            RegionOverrideSpecified = regionSpecified,
        };
    }

    private static string RequireReason(PayloadReader reader)
    {
        var reason = reader.String("reason", MutationText.MaxReasonLength);
        return MutationText.IsUsableReason(reason)
            ? reason!.Trim()
            : throw new CollectorException(
                ErrorCodes.ReasonRequired,
                "该操作必须填写 1 到 500 字的修改理由。",
                field: "reason");
    }

    private static RunDateField DateField(string? text) => text switch
    {
        null or "entered_at_utc" => RunDateField.EnteredAtUtc,
        "matched_at_utc" => RunDateField.MatchedAtUtc,
        "ended_at_utc" => RunDateField.EndedAtUtc,
        _ => throw CollectorException.BadRequest(
            "date_field 只能取 matched_at_utc / entered_at_utc / ended_at_utc。", "filter.date_field"),
    };

    private static RunSortField SortField(string? text) => text switch
    {
        null or "entered_at_utc" => RunSortField.EnteredAtUtc,
        "matched_at_utc" => RunSortField.MatchedAtUtc,
        "ended_at_utc" => RunSortField.EndedAtUtc,
        "duration_ms" => RunSortField.DurationMs,
        "duty_name" => RunSortField.DutyName,
        _ => throw CollectorException.BadRequest("sort.field 不是契约声明的取值。", "sort.field"),
    };

    private static SortDirection Direction(string? text) => text switch
    {
        null or "desc" => SortDirection.Desc,
        "asc" => SortDirection.Asc,
        _ => throw CollectorException.BadRequest("sort.direction 只能取 asc 或 desc。", "sort.direction"),
    };
}

/// <summary>
/// One <c>ExportDiagnosticsReport</c> request.
/// </summary>
/// <param name="TargetPath">Destination, or null for the generated name under the database folder.</param>
/// <param name="Overwrite">
/// Whether a file the exporter did not name may be replaced. Defaults to false, so a mistyped
/// path is refused; the exporter's own generated name is replaced regardless.
/// </param>
public sealed record DiagnosticsReportRequest(string? TargetPath, bool Overwrite);
