using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Profiles;

/// <summary>Why a profile was refused, or a non-fatal remark about it.</summary>
/// <param name="Code">Short machine-readable code.</param>
/// <param name="Path">JSON path or file the issue concerns.</param>
/// <param name="Message">Non-sensitive explanation.</param>
public sealed record ProfileIssue(string Code, string Path, string Message);

/// <summary>Outcome of validating one profile file.</summary>
/// <param name="Path">File that was checked.</param>
/// <param name="ProfileId">Identifier read from the file, when it could be read.</param>
/// <param name="Region">Region token read from the file, when it could be read.</param>
/// <param name="GameBuild">Build read from the file, when it could be read.</param>
/// <param name="Status">Status the file declares, when it could be read.</param>
/// <param name="MessageCount">Number of declared messages.</param>
/// <param name="FixtureVerified">True when every referenced fixture was found and matched.</param>
/// <param name="Errors">Fatal problems; a non-empty list means the profile was refused.</param>
/// <param name="Warnings">Non-fatal remarks.</param>
/// <param name="Profile">The loaded profile, when there were no errors.</param>
public sealed record ProfileValidationReport(
    string Path,
    string? ProfileId,
    string? Region,
    string? GameBuild,
    string? Status,
    int MessageCount,
    bool FixtureVerified,
    IReadOnlyList<ProfileIssue> Errors,
    IReadOnlyList<ProfileIssue> Warnings,
    [property: JsonIgnore] ProtocolProfile? Profile)
{
    /// <summary>True when the profile loaded cleanly.</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>Builds a report for a file that could not be read or parsed at all.</summary>
    /// <param name="path">File that failed.</param>
    /// <param name="code">Error code.</param>
    /// <param name="message">Non-sensitive explanation.</param>
    public static ProfileValidationReport Failure(string path, string code, string message) =>
        new(path, null, null, null, null, 0, false,
            new[] { new ProfileIssue(code, "$", message) }, Array.Empty<ProfileIssue>(), null);

    /// <summary>Serialises the report as indented snake_case JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ReportJsonOptions);

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(UpperSnakeCaseNamingPolicy.Instance) },
    };
}
