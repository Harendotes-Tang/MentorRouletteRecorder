using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Mutations;

/// <summary>
/// What the idempotency table stores for one applied mutation.
///
/// The row keeps a fingerprint of the request next to the outcome, so replaying a request id
/// with a <em>different</em> body is detectable instead of silently returning somebody
/// else's result (docs/manual-correction.md section 4).
/// </summary>
/// <param name="Fingerprint">Hash of the canonical request text.</param>
/// <param name="RunId">Run the mutation applied to, when any.</param>
/// <param name="Revision">Revision after the mutation.</param>
/// <param name="AuditEventId">Identifier of the audit record.</param>
/// <param name="Run">Run as it stood right after the mutation.</param>
/// <param name="Settings">Achievement settings as they stood right after the mutation.</param>
public sealed record MutationSnapshot(
    [property: JsonPropertyName("fingerprint")] string Fingerprint,
    [property: JsonPropertyName("run_id")] string? RunId,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("audit_event_id")] string AuditEventId,
    [property: JsonPropertyName("run")] MentorRun? Run,
    [property: JsonPropertyName("settings")] AchievementSettings? Settings);

/// <summary>Serialisation of stored mutation outcomes and of request fingerprints.</summary>
public static class MutationSnapshotCodec
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serialises a snapshot for the idempotency table.</summary>
    /// <param name="snapshot">Snapshot to store.</param>
    public static string Serialize(MutationSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Options);

    /// <summary>Reads a snapshot back, or null when the stored text is not one.</summary>
    /// <param name="json">Stored text.</param>
    public static MutationSnapshot? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MutationSnapshot>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serialises any other stored outcome with the same options.
    ///
    /// <c>SetRunReflection</c> shares the idempotency table but not the shape of a mutation:
    /// it has no revision and no audit row, so it stores its own snapshot type here rather
    /// than pretending to be one.
    /// </summary>
    /// <typeparam name="T">Snapshot type.</typeparam>
    /// <param name="snapshot">Snapshot to store.</param>
    public static string SerializeOther<T>(T snapshot) => JsonSerializer.Serialize(snapshot, Options);

    /// <summary>Reads such a snapshot back, or null when the stored text is not one.</summary>
    /// <typeparam name="T">Snapshot type.</typeparam>
    /// <param name="json">Stored text.</param>
    public static T? DeserializeOther<T>(string json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lower-case hex SHA-256 of the canonical request text.
    ///
    /// Each part is <em>length-prefixed</em> rather than separator-joined: a separator is only
    /// unambiguous while no value contains it, and JSON strings may contain any character. An
    /// ambiguous encoding would let two different requests sharing one <c>request_id</c> hash
    /// alike, so the second would be dropped as a replay instead of refused. A missing part is
    /// encoded distinctly from an empty one for the same reason.
    /// </summary>
    /// <param name="parts">Canonical parts, in a fixed order.</param>
    public static string Fingerprint(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder(256);
        foreach (var part in parts)
        {
            Append(builder, part);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    /// <summary>Appends one length-prefixed, unambiguous part.</summary>
    /// <param name="builder">Buffer being built.</param>
    /// <param name="part">Part to append; null is encoded distinctly from empty.</param>
    private static void Append(StringBuilder builder, string? part)
    {
        if (part is null)
        {
            builder.Append("~:");
            return;
        }

        builder.Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
    }

    /// <summary>Canonical text of a change set, so an equal patch hashes equally.</summary>
    /// <param name="changes">Change set from the request.</param>
    public static string Canonical(RunChangeSet changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var builder = new StringBuilder(128);
        foreach (var field in changes.Specified.OrderBy(f => f, StringComparer.Ordinal))
        {
            // Length-prefixed for the same reason as Fingerprint: a duty name or note that
            // happens to contain the separator must not be able to imitate another field.
            Append(builder, field);
            Append(builder, Value(changes, field));
        }

        return builder.ToString();
    }

    private static string? Value(RunChangeSet changes, string field) => field switch
    {
        RunFields.ContentId => Text(changes.ContentId),
        RunFields.DutyName => changes.DutyName,
        RunFields.DutyCategory => changes.DutyCategory,
        RunFields.JobId => Text(changes.JobId),
        RunFields.MatchedAtUtc => UtcTimestamp.ToTextOrNull(changes.MatchedAtUtc),
        RunFields.EnteredAtUtc => UtcTimestamp.ToTextOrNull(changes.EnteredAtUtc),
        RunFields.EndedAtUtc => UtcTimestamp.ToTextOrNull(changes.EndedAtUtc),
        RunFields.DurationMs => Text(changes.DurationMs),
        RunFields.Result => changes.Result is { } result ? EnumWire<RunResult>.Format(result) : null,
        RunFields.ContributesToGoal => Text(changes.ContributesToGoal),
        RunFields.Note => changes.Note,
        RunFields.PendingReview => Text(changes.PendingReview),
        _ => null,
    };

    private static string? Text<T>(T? value)
        where T : struct =>
        value is null ? null : Convert.ToString(value.Value, CultureInfo.InvariantCulture);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(UpperSnakeCaseNamingPolicy.Instance));
        return options;
    }
}
