using MentorRecorder.Collector.Domain;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Where downloaded share codes and the bookkeeping around them are kept. Codes are candidates,
/// never profiles: no profile directory reads this store.
/// Implementations never throw for anything found on disk;
/// arguments that cannot name a region, build, template or code throw <see cref="ArgumentException"/>.
/// </summary>
public interface ISharedCalibrationStore
{
    /// <summary>
    /// Records what a fetch produced: writes every candidate that decodes and hashes to its claim and
    /// describes this region and build, and remembers the attempt. A fetch that sent nothing records
    /// nothing. Never deletes or overwrites a valid stored code.
    /// </summary>
    /// <param name="region">Region the fetch was for.</param>
    /// <param name="gameBuild">Build the fetch was for.</param>
    /// <param name="templateSha256">Template in force; the attempt is remembered per template.</param>
    /// <param name="result">What the fetch produced.</param>
    /// <param name="nowUtc">When it finished.</param>
    SharedStoreWriteResult RecordFetch(
        Region region, string gameBuild, string templateSha256, SharedCalibrationFetchResult result, DateTimeOffset nowUtc);

    /// <summary>
    /// Stored codes for this region, build and template that are neither revoked by the last readable
    /// index nor rejected, best first, at most <see cref="SharedCalibrationIndex.MaxCandidates"/>.
    /// </summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Hash of the template the codes must have been made under.</param>
    IReadOnlyList<SharedStoredCandidate> LoadCandidates(Region region, string gameBuild, string templateSha256);

    /// <summary>The last fetch attempt for this region, build and template, or null when there was none.</summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Template in force.</param>
    SharedFetchRecord? LastFetch(Region region, string gameBuild, string templateSha256);

    /// <summary>
    /// Counts one capture session whose traffic contradicted a code. Idempotent per session id. The
    /// caller only reports healthy sessions (plan §4.2).
    /// </summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Template in force.</param>
    /// <param name="codeSha256">The contradicted code.</param>
    /// <param name="sessionId">Capture session that contradicted it.</param>
    /// <param name="nowUtc">When.</param>
    SharedContradictionResult RecordContradiction(
        Region region, string gameBuild, string templateSha256, string codeSha256, string sessionId, DateTimeOffset nowUtc);

    /// <summary>True once enough distinct sessions contradicted this code under this template.</summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="templateSha256">Template in force.</param>
    /// <param name="codeSha256">The code.</param>
    bool IsRejected(Region region, string gameBuild, string templateSha256, string codeSha256);

    /// <summary>
    /// Remembers that the player chose not to use shared calibrations for this region and build
    /// (不用共享的，我自己校准). Kept apart from the contradiction records, whatever the template; cleared only
    /// by <see cref="ClearRejections"/>. True when it reached the disk.
    /// </summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    /// <param name="nowUtc">When the player said so.</param>
    bool RecordUserRejection(Region region, string gameBuild, DateTimeOffset nowUtc);

    /// <summary>True while the player's refusal of shared calibrations stands for this region and build.</summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    bool IsUserRejected(Region region, string gameBuild);

    /// <summary>Forgets every rejection for a build (重新观察), the player's own refusal included. Codes and fetch records stay.</summary>
    /// <param name="region">Region of the running client.</param>
    /// <param name="gameBuild">Build of the running client.</param>
    bool ClearRejections(Region region, string gameBuild);
}

/// <summary>What recording a fetch did.</summary>
/// <param name="Recorded">True when the attempt reached the state file.</param>
/// <param name="CodesWritten">Code files written; a code already stored intact is not rewritten.</param>
/// <param name="Refused">
/// Candidates not written, as <c>&lt;first 12 hex digits&gt;:&lt;reason&gt;</c> (HASH_MISMATCH, UNDECODABLE:..., PAYLOAD_MISMATCH:...,
/// NAME_TAKEN, UNREADABLE, UNWRITABLE).
/// </param>
public sealed record SharedStoreWriteResult(bool Recorded, int CodesWritten, IReadOnlyList<string> Refused);

/// <summary>A stored code offered for verification against local traffic.</summary>
/// <param name="CodeSha256">Identity of the code.</param>
/// <param name="Code">Code text.</param>
/// <param name="Payload">What it says.</param>
/// <param name="Submitters">Submitters according to the index it was fetched from; 0 when unknown.</param>
/// <param name="FirstPublishedAtUtc">First publication according to that index, when known.</param>
public sealed record SharedStoredCandidate(
    string CodeSha256, string Code, ShareCodePayload Payload, int Submitters, DateTimeOffset? FirstPublishedAtUtc);

/// <summary>The last fetch attempt for one region, build and template.</summary>
/// <param name="TemplateSha256">Template in force.</param>
/// <param name="LastAttemptAtUtc">When the last attempt that sent anything finished; what the six-hour throttle reads.</param>
/// <param name="LastSuccessAtUtc">When a fetch last got a complete answer (OK or NONE_FOR_BUILD).</param>
/// <param name="Status">How the last attempt ended.</param>
/// <param name="IndexAttempts">Its index requests, per source.</param>
/// <param name="Discards">Its codes not obtained, with per-source attempts.</param>
public sealed record SharedFetchRecord(
    string TemplateSha256,
    DateTimeOffset LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    SharedFetchStatus Status,
    IReadOnlyList<SharedSourceAttempt> IndexAttempts,
    IReadOnlyList<SharedCodeDiscard> Discards);

/// <summary>What counting a contradiction came to.</summary>
/// <param name="Sessions">Distinct contradicting sessions now on record.</param>
/// <param name="Rejected">True when that reaches <see cref="SharedCalibrationStore.RejectionThreshold"/>.</param>
/// <param name="Persisted">True when the record is on disk.</param>
public sealed record SharedContradictionResult(int Sessions, bool Rejected, bool Persisted);

/// <summary>The default store: remembers nothing and never touches the disk.</summary>
internal sealed class InertSharedCalibrationStore : ISharedCalibrationStore
{
    internal static readonly InertSharedCalibrationStore Instance = new();

    private InertSharedCalibrationStore()
    {
    }

    /// <inheritdoc />
    public SharedStoreWriteResult RecordFetch(
        Region region, string gameBuild, string templateSha256, SharedCalibrationFetchResult result, DateTimeOffset nowUtc) =>
        new(false, 0, Array.Empty<string>());

    /// <inheritdoc />
    public IReadOnlyList<SharedStoredCandidate> LoadCandidates(Region region, string gameBuild, string templateSha256) =>
        Array.Empty<SharedStoredCandidate>();

    /// <inheritdoc />
    public SharedFetchRecord? LastFetch(Region region, string gameBuild, string templateSha256) => null;

    /// <inheritdoc />
    public SharedContradictionResult RecordContradiction(
        Region region, string gameBuild, string templateSha256, string codeSha256, string sessionId, DateTimeOffset nowUtc) =>
        new(0, false, false);

    /// <inheritdoc />
    public bool IsRejected(Region region, string gameBuild, string templateSha256, string codeSha256) => false;

    /// <inheritdoc />
    public bool RecordUserRejection(Region region, string gameBuild, DateTimeOffset nowUtc) => false;

    /// <inheritdoc />
    public bool IsUserRejected(Region region, string gameBuild) => false;

    /// <inheritdoc />
    public bool ClearRejections(Region region, string gameBuild) => false;
}
