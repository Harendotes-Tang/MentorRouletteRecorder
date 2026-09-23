using System.Text.Json;
using System.Text.Json.Serialization;

namespace MentorRecorder.Collector.Domain.Mutations;

/// <summary>
/// Keeps the complete duty identity in one internal audit value. Including content id
/// distinguishes new complete revisions from legacy content-only changes even when the
/// associated territory or provenance did not change. Display names remain separate fields.
/// </summary>
internal sealed record DutyIdentityAudit
{
    [JsonPropertyName("content_id")]
    public required int? ContentId { get; init; }

    [JsonPropertyName("territory_id")]
    public required int? TerritoryId { get; init; }

    [JsonPropertyName("duty_source")]
    public required string? Source { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Serializes observed values without consulting mutable reference catalogs.</summary>
    public static string Capture(MentorRun run) => JsonSerializer.Serialize(new DutyIdentityAudit
    {
        ContentId = run.ContentId,
        TerritoryId = run.TerritoryId,
        Source = run.DutySource is { } source ? EnumWire<DutySource>.Format(source) : null,
    });

    /// <summary>Restores a complete snapshot; missing, unknown or invalid fields are refused.</summary>
    public static MentorRun Restore(MentorRun run, string json)
    {
        var identity = JsonSerializer.Deserialize<DutyIdentityAudit>(json, Options)
            ?? throw new JsonException("Missing duty identity snapshot.");
        if (identity.ContentId is < 0 || identity.TerritoryId is < 0 ||
            (identity.Source is not null && !EnumWire<DutySource>.TryParse(identity.Source, out _)))
        {
            throw new JsonException("Invalid duty identity snapshot.");
        }

        return run with
        {
            ContentId = identity.ContentId,
            TerritoryId = identity.TerritoryId,
            DutySource = identity.Source is null ? null : EnumWire<DutySource>.Parse(identity.Source),
        };
    }
}
