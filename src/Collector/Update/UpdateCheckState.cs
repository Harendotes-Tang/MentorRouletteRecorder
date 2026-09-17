namespace MentorRecorder.Collector.Update;

/// <summary>
/// How one update check ended. Rendered on the wire and into the sanitized report by
/// <see cref="Domain.EnumWire{TEnum}"/>, so <c>NotFound</c> reads as <c>NOT_FOUND</c>.
/// </summary>
public enum UpdateCheckOutcome
{
    /// <summary>A published version was read.</summary>
    Ok,

    /// <summary>Nothing is published at that address. The ordinary answer while the repository is private.</summary>
    NotFound,

    /// <summary>The host refused to answer for now (403, 429).</summary>
    RateLimited,

    /// <summary>Any other status outside 2xx.</summary>
    HttpStatus,

    /// <summary>A redirect without a usable target, or one hop too many.</summary>
    RedirectRefused,

    /// <summary>A redirect target, or an answer, from an address the client does not accept.</summary>
    HostRefused,

    /// <summary>The answer declared, or produced, more bytes than the cap.</summary>
    TooLarge,

    /// <summary>The whole chain ran out of its budget.</summary>
    Timeout,

    /// <summary>The name did not resolve, or the connection was refused.</summary>
    DnsOrConnect,

    /// <summary>The TLS handshake failed.</summary>
    TlsFailed,

    /// <summary>The document was not a metadata document this build can read.</summary>
    Malformed,

    /// <summary>The transport failed in some other way; only the failure's type is kept.</summary>
    TransportFailed,

    /// <summary>The caller cancelled the check.</summary>
    Cancelled,

    /// <summary>The kill switch stopped the check before anything was sent.</summary>
    Disabled,
}

/// <summary>What one check produced.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="LatestVersion">The published version, when one was read.</param>
/// <param name="StatusCode">Status of the answer, when there was one.</param>
/// <param name="Detail">
/// A short non-sensitive token: a refusal reason, or the type name of a transport failure. Never a
/// message, an address or a host.
/// </param>
public sealed record UpdateCheckResult(
    UpdateCheckOutcome Outcome, string? LatestVersion = null, int? StatusCode = null, string? Detail = null);

/// <summary>
/// The update check as <c>GetStatus</c> reports it. Read from a cache, so asking for it never waits
/// on a request.
/// </summary>
/// <param name="Enabled">Whether <c>update.check_enabled</c> allows a check at all.</param>
/// <param name="UpdateAvailable">True when the published version is strictly newer than this build's.</param>
/// <param name="LatestVersion">Newest published version this process knows of, or null.</param>
/// <param name="LastCheckedAtUtc">When a check was last attempted, successfully or not.</param>
/// <param name="LastOutcome">How that attempt ended, as an <see cref="UpdateCheckOutcome"/> token.</param>
/// <param name="ReleaseUrl">The release page a human is sent to; nothing is ever downloaded or run.</param>
public sealed record UpdateCheckSnapshot(
    bool Enabled,
    bool UpdateAvailable,
    string? LatestVersion,
    DateTimeOffset? LastCheckedAtUtc,
    string? LastOutcome,
    string ReleaseUrl);

/// <summary>
/// What the sanitized diagnostics report may say about the update check
/// (docs/privacy-boundary.md §8.4): whether it is allowed, whether the kill switch forbids it, and
/// when this process last attempted one and how it ended. Never where to: no address, host or path.
/// </summary>
/// <param name="Enabled">Whether the setting allows a check.</param>
/// <param name="KillSwitch">True when <c>MR_DISABLE_UPDATE_CHECK</c> stops every check of this process.</param>
/// <param name="LastCheckedAtUtc">When a check was last attempted.</param>
/// <param name="LastOutcome">How that attempt ended.</param>
/// <param name="LatestVersion">Newest published version this process knows of.</param>
public sealed record UpdateCheckDiagnostics(
    bool Enabled, bool KillSwitch, DateTimeOffset? LastCheckedAtUtc, string? LastOutcome, string? LatestVersion)
{
    /// <summary>Nothing checked yet, with the check allowed: the state of a fresh install.</summary>
    public static UpdateCheckDiagnostics None { get; } = new(true, false, null, null, null);
}
