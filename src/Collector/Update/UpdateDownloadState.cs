namespace MentorRecorder.Collector.Update;

/// <summary>
/// Where the download of a newer version's installer stands (<c>$defs/UpdateDownload</c>, field <c>state</c>).
/// Rendered by <see cref="Domain.EnumWire{TEnum}"/>, so <c>Downloading</c> reads as <c>DOWNLOADING</c>.
/// </summary>
public enum UpdateDownloadState
{
    /// <summary>Nothing asked, or the last download was cancelled.</summary>
    Idle,

    /// <summary>Bytes are arriving.</summary>
    Downloading,

    /// <summary>The file is complete and its SHA-256 is being compared.</summary>
    Verifying,

    /// <summary>The verified installer is in place.</summary>
    Ready,

    /// <summary>Nothing usable was kept; <see cref="UpdateDownloadFailure"/> says why.</summary>
    Failed,
}

/// <summary>
/// Why a download ended without an installer (<c>$defs/UpdateDownload</c>, field <c>failure</c>). The request
/// failures carry the same names as the update check's outcomes.
/// </summary>
public enum UpdateDownloadFailure
{
    /// <summary>No version newer than this build is known (and no reinstall was asked for).</summary>
    NoUpdate,

    /// <summary>The update check is switched off, or the kill switch stops it; nothing was sent.</summary>
    Disabled,

    /// <summary>An answer, or a redirect target, from an address the client does not accept.</summary>
    HostRefused,

    /// <summary>A redirect without a usable target, or one hop too many.</summary>
    RedirectRefused,

    /// <summary>The installer is not published at its address.</summary>
    NotFound,

    /// <summary>The host refused to answer for now (403, 429).</summary>
    RateLimited,

    /// <summary>Any other status outside 2xx.</summary>
    HttpStatus,

    /// <summary>The installer declared, or produced, more bytes than <see cref="UpdateDownloadLimits.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>No byte for the idle limit, or the whole download past its overall limit.</summary>
    Timeout,

    /// <summary>The name did not resolve, or the connection was refused.</summary>
    DnsOrConnect,

    /// <summary>The TLS handshake failed.</summary>
    TlsFailed,

    /// <summary>The connection failed in some other way, or ended before the declared length.</summary>
    TransportFailed,

    /// <summary>The published SHA-256 could not be read: not there, or not a checksum.</summary>
    ChecksumUnavailable,

    /// <summary>The file downloaded is not the one the published SHA-256 names; it was deleted.</summary>
    ChecksumMismatch,

    /// <summary>The installer could not be stored: no space, no access, or <c>updates</c> is not a plain folder.</summary>
    DiskFailed,
}

/// <summary>
/// The limits one download runs under. <see cref="Default"/> is the shipping set; tests pass small ones so that no
/// test waits for real seconds or streams hundreds of megabytes.
/// </summary>
/// <param name="MaxBytes">Largest installer accepted: a larger declared length is refused, a body passing it abandoned.</param>
/// <param name="IdleTimeout">Longest wait for the next byte, the answer to the installer request included.</param>
/// <param name="TotalTimeout">Longest the whole download may take, checksum included.</param>
/// <param name="StartInterval">
/// Shortest time between two downloads started, and how long an installer verified a moment ago is reported READY
/// again without being hashed once more; zero for no limit.
/// </param>
public sealed record UpdateDownloadLimits(long MaxBytes, TimeSpan IdleTimeout, TimeSpan TotalTimeout, TimeSpan StartInterval)
{
    /// <summary>300 MiB, 30 seconds without a byte, 30 minutes in all, a new download at most every 5 seconds.</summary>
    public static UpdateDownloadLimits Default { get; } =
        new(300L * 1024 * 1024, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(5));
}

/// <summary>
/// The download as <c>UpdateStatus.download</c> reports it. Built only through the factories below, so every state
/// carries exactly the fields the contract gives it: a version from DOWNLOADING on, bytes while DOWNLOADING,
/// VERIFYING and READY, the file and its hash when READY, a failure and its sentence when FAILED.
/// </summary>
/// <param name="State">Where the download stands.</param>
/// <param name="Version">The version being downloaded or downloaded.</param>
/// <param name="ReceivedBytes">Bytes written so far.</param>
/// <param name="TotalBytes">The size the server declared, when it declared one.</param>
/// <param name="FilePath">READY only: absolute path of the verified installer.</param>
/// <param name="Sha256">READY only: lower-case hex SHA-256 of that file, equal to the published one.</param>
/// <param name="Failure">FAILED only: why.</param>
/// <param name="Message">FAILED only: one sentence for the user.</param>
public sealed record UpdateDownloadSnapshot(
    UpdateDownloadState State,
    string? Version = null,
    long? ReceivedBytes = null,
    long? TotalBytes = null,
    string? FilePath = null,
    string? Sha256 = null,
    UpdateDownloadFailure? Failure = null,
    string? Message = null)
{
    /// <summary>Nothing asked.</summary>
    public static UpdateDownloadSnapshot Idle { get; } = new(UpdateDownloadState.Idle);

    /// <summary>True while a background task owns the download: DOWNLOADING or VERIFYING.</summary>
    public bool IsRunning => State is UpdateDownloadState.Downloading or UpdateDownloadState.Verifying;

    /// <summary>Bytes are arriving.</summary>
    public static UpdateDownloadSnapshot Downloading(string version, long received, long? total) =>
        new(UpdateDownloadState.Downloading, version, received, total);

    /// <summary>The file is complete and is being compared.</summary>
    public static UpdateDownloadSnapshot Verifying(string version, long received, long? total) =>
        new(UpdateDownloadState.Verifying, version, received, total);

    /// <summary>The verified installer is in place.</summary>
    public static UpdateDownloadSnapshot Ready(string version, long received, long? total, string filePath, string sha256) =>
        new(UpdateDownloadState.Ready, version, received, total, filePath, sha256);

    /// <summary>Nothing usable was kept.</summary>
    /// <param name="version">The version that was being downloaded; null when nothing was.</param>
    /// <param name="failure">Why.</param>
    /// <param name="message">The sentence for the user; <see cref="UpdateDownloadMessages.For"/> when null.</param>
    public static UpdateDownloadSnapshot Failed(string? version, UpdateDownloadFailure failure, string? message = null) =>
        new(UpdateDownloadState.Failed, version, Failure: failure, Message: message ?? UpdateDownloadMessages.For(failure));
}

/// <summary>
/// The sentence <c>download.message</c> carries for each failure: what happened and what the user can do - try again,
/// or let the browser download the installer (「在浏览器中下载」) - in formal Chinese and without any internal name.
/// </summary>
public static class UpdateDownloadMessages
{
    /// <summary>DISABLED because the user switched the update check off in the settings.</summary>
    public const string SettingOff =
        "更新检查已在设置中关闭，因此没有下载任何内容；如需下载，请先在「设置 · 通用」中开启更新检查。";

    /// <summary>DISABLED because the user switched the update check off while a download ran or was ready.</summary>
    public const string Withdrawn =
        "更新检查已在设置中关闭，因此下载已停止，已下载的安装程序也不再提供；如需更新，请先在「设置 · 通用」中重新开启更新检查，再点「重试」。";

    /// <summary>The sentence for a failure. DISABLED here is the kill switch; the setting has <see cref="SettingOff"/>.</summary>
    /// <param name="failure">Why the download ended.</param>
    public static string For(UpdateDownloadFailure failure) => failure switch
    {
        UpdateDownloadFailure.NoUpdate =>
            "目前没有比正在使用的版本更新的正式版可供下载；如需确认，请先点击「检查更新」。",
        UpdateDownloadFailure.Disabled =>
            "本机已禁止本软件检查和下载更新，因此没有下载任何内容；如需更新，请点击「在浏览器中下载」。",
        UpdateDownloadFailure.HostRefused =>
            "下载地址指向了本软件不信任的服务器，下载已停止；请稍后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.RedirectRefused =>
            "下载服务器的转向无效或次数过多，下载已停止；请稍后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.NotFound =>
            "发布页面上暂时找不到该版本的安装程序，可能仍在上传；请稍后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.RateLimited =>
            "下载服务器暂时拒绝了请求，可能是短时间内访问过多；请过一段时间后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.HttpStatus =>
            "下载服务器返回了错误，未能下载安装程序；请稍后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.TooLarge =>
            "安装程序的大小超出了合理范围，为安全起见已停止下载；请点击「在浏览器中下载」。",
        UpdateDownloadFailure.Timeout =>
            "下载长时间没有进展或耗时过长，已经停止；请检查网络连接后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.DnsOrConnect =>
            "无法连接到下载服务器；请检查网络连接后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.TlsFailed =>
            "无法与下载服务器建立安全连接；请检查系统时间和网络环境后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.TransportFailed =>
            "下载过程中网络连接中断，安装程序不完整，已将其删除；请重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.ChecksumUnavailable =>
            "无法读取官方发布的安装程序校验值，因此没有下载安装程序；请稍后重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.ChecksumMismatch =>
            "下载的安装程序与官方发布的校验值不一致，已将其删除；请重试，或点击「在浏览器中下载」。",
        UpdateDownloadFailure.DiskFailed =>
            "无法在本机保存安装程序；请确认磁盘空间充足、数据文件夹可以写入后重试，或点击「在浏览器中下载」。",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "not a declared failure"),
    };
}

/// <summary>What fetching the published checksum produced.</summary>
/// <param name="Outcome">How the request ended; <see cref="UpdateCheckOutcome.Malformed"/> when it was no checksum.</param>
/// <param name="Sha256">The lower-case hex SHA-256 read, when the outcome is OK.</param>
/// <param name="StatusCode">Status of the answer, when there was one.</param>
/// <param name="Detail">A short non-sensitive token, as on <see cref="UpdateCheckResult"/>.</param>
public sealed record UpdateChecksumResult(
    UpdateCheckOutcome Outcome, string? Sha256 = null, int? StatusCode = null, string? Detail = null)
{
    internal static UpdateChecksumResult From(UpdateCheckResult refusal) =>
        new(refusal.Outcome, null, refusal.StatusCode, refusal.Detail);
}

/// <summary>
/// The answer to the installer request: on OK, the body still unread, for the caller to stream and then dispose;
/// otherwise only why there is none. Disposing releases the body and whatever produced it.
/// </summary>
public sealed class UpdateInstallerResponse : IDisposable
{
    private readonly UpdateTransportResponse? _answer;

    private UpdateInstallerResponse(UpdateCheckResult result, UpdateTransportResponse? answer)
    {
        Result = result;
        _answer = answer;
    }

    /// <summary>How the request ended; OK exactly when <see cref="Body"/> is there.</summary>
    public UpdateCheckResult Result { get; }

    /// <summary>The length the server declared, if any.</summary>
    public long? ContentLength => _answer?.ContentLength;

    /// <summary>The unread installer; null unless the request succeeded.</summary>
    public Stream? Body => _answer?.Body;

    /// <inheritdoc />
    public void Dispose() => _answer?.Dispose();

    internal static UpdateInstallerResponse Opened(UpdateTransportResponse answer) =>
        new(new UpdateCheckResult(UpdateCheckOutcome.Ok, StatusCode: answer.StatusCode), answer);

    internal static UpdateInstallerResponse Refused(UpdateCheckResult result) => new(result, null);
}

/// <summary>Why a download ended without an installer; thrown inside the download and turned into FAILED.</summary>
internal sealed class UpdateDownloadFailedException : Exception
{
    public UpdateDownloadFailedException(UpdateDownloadFailure failure, string? errorType = null, Exception? inner = null)
        : base("the download failed: " + failure, inner)
    {
        Failure = failure;
        ErrorType = errorType ?? inner?.GetType().Name;
    }

    /// <summary>Why.</summary>
    public UpdateDownloadFailure Failure { get; }

    /// <summary>For the log: the type of the failure that caused it, or a short token. Never a message or a path.</summary>
    public string? ErrorType { get; }
}
