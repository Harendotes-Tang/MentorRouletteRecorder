using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Reference;

/// <summary>One job of the reference table.</summary>
/// <param name="JobId">Job id.</param>
/// <param name="Abbreviation">Three-letter abbreviation.</param>
/// <param name="NameEn">English name.</param>
/// <param name="NameZh">Simplified Chinese name, shown in the UI.</param>
/// <param name="RoleGroup">Display role group, one of the six values in data/jobs/jobs.json.</param>
/// <param name="Role">Statistics role bucket derived from the group.</param>
public sealed record JobInfo(
    int JobId,
    string Abbreviation,
    string NameEn,
    string NameZh,
    string RoleGroup,
    Role Role);

/// <summary>
/// Maps a job id to a display name and a role.
///
/// An id that is not in the table is not guessed at: it becomes the unknown job, which forms
/// its own statistics bucket and is never folded into a real job
/// (docs/statistics-definitions.md section 11).
/// </summary>
public sealed class JobCatalog
{
    /// <summary>Name shown for a job that is unknown or absent from the table.</summary>
    public const string UnknownJobName = "未知";

    /// <summary>Role group shown for an unknown job.</summary>
    public const string UnknownRoleGroup = "未知";

    private const string ResourceName = "MentorRecorder.Collector.Data.jobs.json";

    private readonly IReadOnlyDictionary<int, JobInfo> _byId;

    private JobCatalog(IReadOnlyDictionary<int, JobInfo> byId)
    {
        _byId = byId;
    }

    /// <summary>An empty catalog. Every lookup returns the unknown job.</summary>
    public static JobCatalog Empty { get; } = new(new Dictionary<int, JobInfo>());

    /// <summary>The catalog embedded in this assembly, loaded once.</summary>
    public static JobCatalog Default { get; } = LoadEmbedded();

    /// <summary>Number of known jobs.</summary>
    public int Count => _byId.Count;

    /// <summary>All known jobs, ordered by id.</summary>
    public IReadOnlyList<JobInfo> Jobs => _byId.Values.OrderBy(j => j.JobId).ToArray();

    /// <summary>Looks up a job, or null when the id is unknown or absent.</summary>
    /// <param name="jobId">Job id.</param>
    public JobInfo? Find(int? jobId) =>
        jobId is { } id && _byId.TryGetValue(id, out var info) ? info : null;

    /// <summary>Display name of a job id; the unknown marker when it cannot be resolved.</summary>
    /// <param name="jobId">Job id.</param>
    public string NameOf(int? jobId) => Find(jobId)?.NameZh ?? UnknownJobName;

    /// <summary>Statistics role of a job id; UNKNOWN when it cannot be resolved.</summary>
    /// <param name="jobId">Job id.</param>
    public Role RoleOf(int? jobId) => Find(jobId)?.Role ?? Role.Unknown;

    /// <summary>How the job of a run was determined.</summary>
    /// <param name="jobId">Job id.</param>
    /// <param name="source">Where the run came from.</param>
    public JobDetection DetectionOf(int? jobId, RunSource source)
    {
        if (Find(jobId) is null)
        {
            return JobDetection.Unknown;
        }

        return source == RunSource.AutoNetwork ? JobDetection.Network : JobDetection.Manual;
    }

    /// <summary>Loads a catalog from a JSON document.</summary>
    /// <param name="json">Document text.</param>
    public static JobCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var file = JsonSerializer.Deserialize<JobsFile>(json)
            ?? throw new InvalidDataException("job catalog is empty");
        if (file.Version != 1)
        {
            throw new InvalidDataException("job catalog version is unsupported");
        }

        var byId = new Dictionary<int, JobInfo>();
        foreach (var row in file.Jobs ?? Array.Empty<JobRow>())
        {
            if (row.JobId < 0 || !byId.TryAdd(row.JobId, null!))
            {
                throw new InvalidDataException($"invalid or duplicate job_id {row.JobId}");
            }

            var group = string.IsNullOrWhiteSpace(row.Role) ? UnknownRoleGroup : row.Role;
            byId[row.JobId] = new JobInfo(
                row.JobId,
                row.Abbreviation ?? string.Empty,
                row.NameEn ?? string.Empty,
                string.IsNullOrWhiteSpace(row.NameZh) ? UnknownJobName : row.NameZh,
                group,
                MapRole(group));
        }

        return new JobCatalog(byId);
    }

    /// <summary>
    /// Maps a display role group onto the four-value statistics role. The three damage groups
    /// collapse into DPS; anything unrecognised becomes UNKNOWN rather than a guess.
    /// </summary>
    /// <param name="roleGroup">Display role group.</param>
    public static Role MapRole(string? roleGroup) => roleGroup switch
    {
        "坦克" => Role.Tank,
        "治疗" => Role.Healer,
        "近战" or "远程物理" or "魔法" => Role.Dps,
        _ => Role.Unknown,
    };

    private static JobCatalog LoadEmbedded()
    {
        var assembly = typeof(JobCatalog).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // The software must work without the reference table; names stay unknown.
            return Empty;
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    private sealed record JobsFile
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("jobs")]
        public JobRow[]? Jobs { get; init; }
    }

    private sealed record JobRow
    {
        [JsonPropertyName("job_id")]
        public int JobId { get; init; }

        [JsonPropertyName("abbreviation")]
        public string? Abbreviation { get; init; }

        [JsonPropertyName("name_en")]
        public string? NameEn { get; init; }

        [JsonPropertyName("name_zh")]
        public string? NameZh { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }
    }
}
