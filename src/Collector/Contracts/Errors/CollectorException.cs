namespace MentorRecorder.Collector.Contracts.Errors;

/// <summary>
/// The error codes of contracts/error-codes.md, verbatim. That file is the normative
/// source; this class exists so that no code path can invent a code that the contract
/// does not declare.
/// </summary>
public static class ErrorCodes
{
    public const string CandidateObservationNotFound = "ERR_CANDIDATE_OBSERVATION_NOT_FOUND";
    /// <summary>Envelope protocol version is not 1, or the frame could not be decoded.</summary>
    public const string ProtocolVersion = "ERR_PROTOCOL_VERSION";

    /// <summary>Malformed JSON, missing or mistyped field, page size above the cap, bad timestamp.</summary>
    public const string BadRequest = "ERR_BAD_REQUEST";

    /// <summary>No such run.</summary>
    public const string NotFound = "ERR_NOT_FOUND";

    /// <summary>A mutation that requires a reason did not carry one.</summary>
    public const string ReasonRequired = "ERR_REASON_REQUIRED";

    /// <summary>matched &gt; entered, or entered &gt; ended.</summary>
    public const string TimeOrder = "ERR_TIME_ORDER";

    /// <summary>Explicit or derived duration is negative.</summary>
    public const string NegativeDuration = "ERR_NEGATIVE_DURATION";

    /// <summary>The requested change would not change anything.</summary>
    public const string NoChanges = "ERR_NO_CHANGES";

    /// <summary>Optimistic concurrency check failed.</summary>
    public const string RevisionConflict = "ERR_REVISION_CONFLICT";

    /// <summary>Soft delete of an already soft-deleted run.</summary>
    public const string AlreadyDeleted = "ERR_ALREADY_DELETED";

    /// <summary>Restore of a run that is not deleted.</summary>
    public const string NotDeleted = "ERR_NOT_DELETED";

    /// <summary>
    /// The named revision cannot be undone. Revision 1 is the creation of the run itself:
    /// undoing it would mean deleting the run, which is a different operation with different
    /// consequences, so it is refused rather than reinterpreted.
    /// </summary>
    public const string UndoNotAllowed = "ERR_UNDO_NOT_ALLOWED";

    /// <summary>Npcap is not installed. This software never bundles or downloads it.</summary>
    public const string NpcapMissing = "ERR_NPCAP_MISSING";

    /// <summary>The game process was not found.</summary>
    public const string FfxivNotRunning = "ERR_FFXIV_NOT_RUNNING";

    /// <summary>Capture is already starting or running.</summary>
    public const string CaptureAlreadyRunning = "ERR_CAPTURE_ALREADY_RUNNING";

    /// <summary>Capture is stopped or failed.</summary>
    public const string CaptureNotRunning = "ERR_CAPTURE_NOT_RUNNING";

    /// <summary>No usable protocol profile: fail-closed, nothing is parsed and nothing recorded.</summary>
    public const string ProfileUnsupported = "ERR_PROFILE_UNSUPPORTED";

    /// <summary>SQLite stayed busy past the retry budget.</summary>
    public const string DbBusy = "ERR_DB_BUSY";

    /// <summary>Integrity check, migration validation or schema version check failed.</summary>
    public const string DbIntegrity = "ERR_DB_INTEGRITY";

    /// <summary>Export or backup target could not be written.</summary>
    public const string ExportFailed = "ERR_EXPORT_FAILED";

    /// <summary>
    /// Another Collector is already serving this machine's pipe, so this one refuses to
    /// start. It is not a fault: the answer is to talk to the instance that is running. The
    /// process exits with <see cref="Program.ExitCodeAlreadyRunning"/> rather than the general
    /// failure code so a launcher can tell the two apart without reading text.
    /// </summary>
    public const string AlreadyRunning = "ERR_ALREADY_RUNNING";

    /// <summary>Anything else.</summary>
    public const string Internal = "ERR_INTERNAL";

    /// <summary>Calibration has no draft for the user to confirm yet.</summary>
    public const string CalibrationNotReady = "ERR_CALIBRATION_NOT_READY";

    /// <summary>The user marked part of the calibration timeline wrong; the draft was voided.</summary>
    public const string CalibrationRejected = "ERR_CALIBRATION_REJECTED";

    /// <summary>
    /// The profile in force gives no share code: none in force, shipped, shared by another player, or a local
    /// profile that cannot be read back or rebuilt into a code. <c>details.reason</c> says which.
    /// </summary>
    public const string ShareCodeUnavailable = "ERR_SHARE_CODE_UNAVAILABLE";

    /// <summary><c>MR_DISABLE_ONLINE_SPEECH</c> is set: online speech is off, cache included.</summary>
    public const string SpeechDisabled = "ERR_SPEECH_DISABLED";

    /// <summary>No online speech service, an incomplete one, or no key for it.</summary>
    public const string SpeechNotConfigured = "ERR_SPEECH_NOT_CONFIGURED";

    /// <summary>The speech service answered 401 or 403.</summary>
    public const string SpeechAuth = "ERR_SPEECH_AUTH";

    /// <summary>The speech service answered 429.</summary>
    public const string SpeechQuota = "ERR_SPEECH_QUOTA";

    /// <summary>Any other status, a refused redirect, or a connection that failed.</summary>
    public const string SpeechNetwork = "ERR_SPEECH_NETWORK";

    /// <summary>
    /// The request ran past its budget, or the sentence could not get a turn: more than three waiting
    /// (<c>details.reason = QUEUE_FULL</c>) or waiting past one request's budget (<c>QUEUE_WAIT</c>).
    /// </summary>
    public const string SpeechTimeout = "ERR_SPEECH_TIMEOUT";

    /// <summary>The answer was not acceptable audio: its type, its size or its content.</summary>
    public const string SpeechFormat = "ERR_SPEECH_FORMAT";

    /// <summary>
    /// The same <c>request_id</c> was reused for a request with a different body. Replaying
    /// the stored result would tell the client its new change was applied when it was not.
    /// </summary>
    public const string IdempotencyConflict = "ERR_IDEMPOTENCY_CONFLICT";
}

/// <summary>
/// An error that maps directly onto an entry of contracts/error-codes.md. Application
/// services and adapters use it to refuse an operation; Domain rules raise typed business
/// failures which the application boundary translates first. The IPC dispatcher serializes
/// this contract error without adding interpretation of its own.
/// </summary>
public sealed class CollectorException : Exception
{
    /// <summary>Creates an error carrying a contract error code.</summary>
    /// <param name="code">One of the constants on <see cref="ErrorCodes"/>.</param>
    /// <param name="message">User-facing message, Simplified Chinese by default.</param>
    /// <param name="details">Structured, non-sensitive context. Never contains payload bytes.</param>
    /// <param name="field">Offending field path, for a bad request.</param>
    /// <param name="retryable">True when resending the same request id is safe and may succeed.</param>
    /// <param name="inner">Underlying exception, if any.</param>
    public CollectorException(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? details = null,
        string? field = null,
        bool retryable = false,
        Exception? inner = null)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
        Details = details;
        Field = field;
        Retryable = retryable;
    }

    /// <summary>Contract error code.</summary>
    public string Code { get; }

    /// <summary>Structured, non-sensitive context.</summary>
    public IReadOnlyDictionary<string, object?>? Details { get; }

    /// <summary>Offending field path, for a bad request.</summary>
    public string? Field { get; }

    /// <summary>True when resending the same request id is safe.</summary>
    public bool Retryable { get; }

    /// <summary>Convenience factory for a bad request.</summary>
    /// <param name="message">User-facing message.</param>
    /// <param name="field">Offending field path.</param>
    public static CollectorException BadRequest(string message, string? field = null) =>
        new(ErrorCodes.BadRequest, message, field: field);

    /// <summary>Convenience factory for a missing run.</summary>
    /// <param name="runId">Run that was not found.</param>
    public static CollectorException NotFound(string runId) =>
        new(
            ErrorCodes.NotFound,
            "找不到该记录，请刷新列表后重试。",
            new Dictionary<string, object?> { ["run_id"] = runId });
}
