namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Where a file was asked for. Only names: the addresses behind them live in
/// <see cref="SharedCalibrationClient"/> alone, and nothing recorded or reported carries one.
/// </summary>
public enum SharedCalibrationSource
{
    /// <summary>The repository host's raw file service; sees a new index within minutes.</summary>
    GithubRaw,

    /// <summary>The primary CDN mirror of the repository; may serve a branch file up to 12 hours old.</summary>
    CdnPrimary,

    /// <summary>The same CDN mirror through its second edge network.</summary>
    CdnFallback,
}

/// <summary>How one request to one source ended (diagnostics tokens: UPPER_SNAKE_CASE of the member).</summary>
public enum SharedFetchOutcome
{
    /// <summary>A 2xx answer from the requested address, read in full within the cap.</summary>
    Ok,

    /// <summary>Connect, headers and body did not fit in the per-source budget.</summary>
    Timeout,

    /// <summary>A status outside 2xx and 3xx.</summary>
    HttpStatus,

    /// <summary>A 3xx status, or an answer that came from another address on an allowed host: redirects are never followed.</summary>
    RedirectRefused,

    /// <summary>The answer came from a host outside the allowlist, or not over HTTPS on the default port.</summary>
    HostRefused,

    /// <summary>The body was declared or turned out larger than the cap; reading stopped at once.</summary>
    TooLarge,

    /// <summary>The body arrived but was not a readable index, or the code in it did not check out.</summary>
    Malformed,

    /// <summary>Name resolution, connection or proxy failure.</summary>
    DnsOrConnect,

    /// <summary>The TLS handshake or certificate check failed.</summary>
    TlsFailed,

    /// <summary>The connection broke while the body was being read.</summary>
    ReadFailed,

    /// <summary>The transport failed in a way none of the above describes.</summary>
    TransportFailed,

    /// <summary>The caller cancelled.</summary>
    Cancelled,
}

/// <summary>How a whole fetch for one region and build ended.</summary>
public enum SharedFetchStatus
{
    /// <summary>At least one code was downloaded and checked against its index entry.</summary>
    Ok,

    /// <summary>Nothing was sent: the kill switch is on, or the fetch seam is not wired.</summary>
    Disabled,

    /// <summary>The caller cancelled; whatever was obtained before that is still reported.</summary>
    Cancelled,

    /// <summary>No source produced a readable index.</summary>
    IndexUnavailable,

    /// <summary>The index was read and lists no usable code for this region and build.</summary>
    NoneForBuild,

    /// <summary>The index listed codes, but none could be downloaded and checked.</summary>
    CodesUnavailable,
}

/// <summary>One request to one source.</summary>
/// <param name="Source">Which source.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="HttpStatus">Status code when an answer arrived.</param>
/// <param name="Detail">
/// Short fixed token (for example <c>NAME_RESOLUTION_ERROR</c>, <c>NOT_JSON</c>, <c>HASH_MISMATCH</c>, or an
/// exception type name); never an address, a message text or content from the answer.
/// </param>
public sealed record SharedSourceAttempt(
    SharedCalibrationSource Source,
    SharedFetchOutcome Outcome,
    int? HttpStatus = null,
    string? Detail = null);

/// <summary>A code the index listed that was not obtained.</summary>
/// <param name="CodeSha256">The code's identity as the index gave it.</param>
/// <param name="Reason">
/// <c>UNREACHABLE</c>, <c>CANCELLED</c>, <c>NOT_UTF8</c>, <c>UNDECODABLE:&lt;share code rejection&gt;</c>,
/// <c>HASH_MISMATCH</c> or <c>PAYLOAD_MISMATCH:&lt;field&gt;</c>.
/// </param>
/// <param name="Attempts">Every source tried for it, in order.</param>
public sealed record SharedCodeDiscard(string CodeSha256, string Reason, IReadOnlyList<SharedSourceAttempt> Attempts);

/// <summary>A downloaded code that decoded and hashed to its index entry. Not yet verified against local traffic.</summary>
/// <param name="CodeSha256">Identity of the code.</param>
/// <param name="Code">The code text, surrounding whitespace removed.</param>
/// <param name="Payload">What it says.</param>
/// <param name="Submitters">Distinct submitters according to the index.</param>
/// <param name="FirstPublishedAtUtc">First publication according to the index.</param>
/// <param name="Commit">Commit it was downloaded at.</param>
public sealed record SharedCalibrationCandidate(
    string CodeSha256,
    string Code,
    ShareCodePayload Payload,
    int Submitters,
    DateTimeOffset FirstPublishedAtUtc,
    string Commit);

/// <summary>Everything one fetch for a region and build produced, for the pipeline and for diagnostics.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="IndexAttempts">Every source asked for the index, in order; empty when nothing was sent.</param>
/// <param name="Candidates">Codes obtained, in the order they should be tried.</param>
/// <param name="Discards">Codes the index listed that were not obtained, and why.</param>
/// <param name="SkippedEntries">Index entries that could not be read.</param>
/// <param name="RevokedCodeSha256s">Codes the index revokes for this region and build.</param>
public sealed record SharedCalibrationFetchResult(
    SharedFetchStatus Status,
    IReadOnlyList<SharedSourceAttempt> IndexAttempts,
    IReadOnlyList<SharedCalibrationCandidate> Candidates,
    IReadOnlyList<SharedCodeDiscard> Discards,
    IReadOnlyList<SharedIndexSkip> SkippedEntries,
    IReadOnlyList<string> RevokedCodeSha256s)
{
    /// <summary>A fetch that sent nothing.</summary>
    public static SharedCalibrationFetchResult Disabled { get; } = Empty(SharedFetchStatus.Disabled);

    /// <summary>True when an index was read to the end, so <see cref="RevokedCodeSha256s"/> is an answer, not an absence of one.</summary>
    public bool IndexWasRead =>
        Status is SharedFetchStatus.Ok or SharedFetchStatus.NoneForBuild or SharedFetchStatus.CodesUnavailable;

    internal static SharedCalibrationFetchResult Empty(
        SharedFetchStatus status, IReadOnlyList<SharedSourceAttempt>? attempts = null) => new(
        status,
        attempts ?? Array.Empty<SharedSourceAttempt>(),
        Array.Empty<SharedCalibrationCandidate>(),
        Array.Empty<SharedCodeDiscard>(),
        Array.Empty<SharedIndexSkip>(),
        Array.Empty<string>());
}
