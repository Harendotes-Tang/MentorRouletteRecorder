using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// Puts one audited field value back onto a run.
///
/// Undo walks the <c>changes_json</c> of the newest revision and re-applies each
/// <c>old_value</c>; automatic field protection also uses it to retain current human values.
/// Values come back from JSON as <c>long</c>, <c>string</c>,
/// <c>bool</c> or null, so each field converts explicitly. An unknown field name is refused
/// rather than ignored, because silently dropping half an undo would be worse than failing.
/// </summary>
public static class RunFieldWriter
{
    /// <summary>Returns a copy of <paramref name="run"/> with one field set.</summary>
    /// <param name="run">Run to copy.</param>
    /// <param name="field">Wire field name from the audit row.</param>
    /// <param name="value">Value from the audit row.</param>
    public static MentorRun Apply(MentorRun run, string field, object? value)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(field);

        return field switch
        {
            RunFields.ContentId => run with { ContentId = AsInt(field, value) },
            RunAuditFields.DutyIdentity => RestoreDutyIdentity(run, field, value),
            RunFields.DutyName => run with { DutyName = AsString(field, value) },
            RunFields.DutyCategory => run with { DutyCategory = AsString(field, value) },
            RunFields.JobId => run with { JobId = AsInt(field, value) },
            RunFields.JobName => run with { JobName = AsString(field, value) },
            RunFields.Role => run with { Role = AsEnum<Role>(field, value, Role.Unknown) },
            RunFields.MatchedAtUtc => run with { MatchedAtUtc = AsTime(field, value) },
            RunFields.EnteredAtUtc => run with { EnteredAtUtc = AsTime(field, value) },
            RunFields.EndedAtUtc => run with { EndedAtUtc = AsTime(field, value) },
            RunFields.DurationMs => run with { DurationMs = AsLong(field, value) },
            RunFields.Result => run with { Result = AsEnum<RunResult>(field, value, RunResult.Unknown) },
            // Recovery audits confidence even though clients cannot edit it directly.
            "detection_confidence" => run with
            {
                DetectionConfidence = AsEnum<DetectionConfidence>(field, value, DetectionConfidence.None),
            },
            RunFields.ContributesToGoal => run with { ContributesToGoal = AsBool(field, value) },
            RunFields.Note => run with { Note = AsString(field, value) },
            RunFields.SoftDeleted => run with { SoftDeleted = AsBool(field, value) },
            RunFields.PendingReview => run with { PendingReview = AsBool(field, value) },
            RunFields.ManuallyCorrected => run with { ManuallyCorrected = AsBool(field, value) },
            _ => throw new CollectorException(
                ErrorCodes.Internal,
                "修订记录包含无法还原的字段，已拒绝撤销以免部分还原。",
                new Dictionary<string, object?> { ["field"] = field }),
        };
    }

    private static MentorRun RestoreDutyIdentity(MentorRun run, string field, object? value)
    {
        if (value is not string json)
        {
            throw Refuse(field);
        }

        try
        {
            return DutyIdentityAudit.Restore(run, json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw Refuse(field);
        }
    }

    private static string? AsString(string field, object? value) => value switch
    {
        null => null,
        string text => text,
        _ => throw Refuse(field),
    };

    private static int? AsInt(string field, object? value) => value switch
    {
        null => null,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        int number => number,
        _ => throw Refuse(field),
    };

    private static long? AsLong(string field, object? value) => value switch
    {
        null => null,
        long number => number,
        int number => number,
        _ => throw Refuse(field),
    };

    private static bool AsBool(string field, object? value) => value switch
    {
        bool flag => flag,
        long number => number != 0,
        _ => throw Refuse(field),
    };

    private static DateTimeOffset? AsTime(string field, object? value) => value switch
    {
        null => null,
        string text when UtcTimestamp.TryParse(text, out var parsed) => parsed,
        _ => throw Refuse(field),
    };

    private static TEnum AsEnum<TEnum>(string field, object? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (value is null)
        {
            return fallback;
        }

        if (value is string text && EnumWire<TEnum>.TryParse(text, out var parsed))
        {
            return parsed;
        }

        throw Refuse(field);
    }

    private static CollectorException Refuse(string field) => new(
        ErrorCodes.Internal,
        "修订记录中的历史值类型异常，已拒绝撤销以免写入错误数据。",
        new Dictionary<string, object?> { ["field"] = field });
}
