using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Diagnostics;

/// <summary>Severity of a log line.</summary>
public enum LogLevel
{
    /// <summary>Ordinary progress.</summary>
    Info,

    /// <summary>Something degraded but the process continues.</summary>
    Warn,

    /// <summary>An operation failed.</summary>
    Error,
}

/// <summary>
/// The local diagnostic log: one JSON object per line, size-rotated, never leaving this
/// machine.
///
/// What may be written is a short, closed list: timestamps, levels, component names, event
/// names, counts and durations. What may never be written is everything that could identify
/// a person or reconstruct traffic -- packet bytes, hex dumps, chat text, character names,
/// IP or MAC addresses, SIDs and other users' paths. <see cref="Sanitize"/> is the last line
/// of defence: any user's profile path, any IPv4 or IPv6 literal, any Windows SID and any
/// hexadecimal run of sixteen characters or more is replaced with a placeholder even if a
/// caller passes one in by accident. The install path ending in
/// <c>game\ffxiv_dx11.exe</c> is likewise collapsed to <c>[game-dir]</c>, including when it
/// lives outside a user profile (docs/capture-diagnostics.md section 6,
/// docs/privacy-boundary.md section 5).
/// </summary>
public sealed partial class RotatingFileLogger : IDisposable
{
    /// <summary>Bytes a single log file may reach before it is rotated.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Number of rotated files kept per day, including the active one.</summary>
    public const int MaxFiles = 5;

    /// <summary>Days of history kept when the user has not chosen a value.</summary>
    public const int DefaultRetentionDays = 7;

    /// <summary>Smallest retention the contract permits.</summary>
    public const int MinRetentionDays = 1;

    /// <summary>Largest retention the contract permits.</summary>
    public const int MaxRetentionDays = 90;

    /// <summary>Matches a drive-rooted user profile folder such as <c>C:\Users\someone</c>.</summary>
    private const string UserProfileRegex = @"[A-Za-z]:\\Users\\[^\\/:*?""<>|\r\n]+";

    /// <summary>Matches a Windows security identifier such as <c>S-1-5-21-…-1013</c>.</summary>
    private const string SidRegex = @"\bS-1-\d+(?:-\d+)+\b";

    /// <summary>Matches a dotted-quad IPv4 literal.</summary>
    private const string IPv4Regex = @"\b\d{1,3}(?:\.\d{1,3}){3}\b";

    /// <summary>
    /// Matches an IPv6 literal, in the full eight-group form and in every <c>::</c>-compressed
    /// form. The lookaround guards stop a match from starting or ending in the middle of a
    /// longer hex run, so a 32-character digest is never mistaken for an address.
    /// </summary>
    private const string IPv6Regex =
        @"(?<![0-9A-Fa-f:.])(?:" +
        @"(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}" +
        @"|(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4}" +
        @"|(?:[0-9A-Fa-f]{1,4}:){1,7}:" +
        @"|::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4}" +
        @"|::" +
        @")(?![0-9A-Fa-f:.])";

    /// <summary>
    /// Matches a hexadecimal run of sixteen characters or more: a packet dump, a full digest,
    /// or anything else long enough to reconstruct traffic from.
    ///
    /// The threshold is sixteen rather than eight because short SHA-256 <em>prefixes</em> are
    /// used as non-identifying ids -- the twelve-character adapter fingerprint of
    /// <c>CaptureDiagnostics.Fingerprint</c> reaches the log -- and redacting them would cost
    /// diagnostic value without removing a secret. UUID groups are eight characters or fewer
    /// and are likewise untouched.
    /// </summary>
    private const string HexBlobRegex = @"\b[0-9a-fA-F]{16,}\b";

    /// <summary>
    /// Matches a drive-rooted or UNC FF14 executable path. The path may contain spaces or
    /// non-ASCII directory names; only the stable <c>game\ffxiv_dx11.exe</c> suffix matters.
    /// </summary>
    private const string GameExecutablePathRegex =
        @"(?:(?:[A-Za-z]:)|(?:\\\\[^\\/\r\n""]+[\\/][^\\/\r\n""]+))[\\/]" +
        @"(?:[^\\/\r\n""]+[\\/])*game[\\/]ffxiv_dx11\.exe";

    /// <summary>Attempts made for one line before it is given up on.</summary>
    public const int AppendAttempts = 4;

    private static readonly TimeSpan AppendRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Longest wait for the cross-process append lock before the line is written anyway.
    ///
    /// A logger must never park the thread that is recording a run, so the wait is bounded.
    /// The bound sits far above any real append (a few hundred microseconds) and far below
    /// anything a user would notice as a stall.
    /// </summary>
    private static readonly TimeSpan AppendLockTimeout = TimeSpan.FromSeconds(2);

    private readonly string _directory;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly bool _enabled;
    private Mutex? _appendLock;
    private int _retentionDays = DefaultRetentionDays;
    private long _suppressedWrites;
    private bool _disposed;

    /// <summary>Creates a logger writing into a directory.</summary>
    /// <param name="directory">Log folder; the default location when null.</param>
    /// <param name="clock">Clock used to stamp and name files.</param>
    /// <param name="enabled">False turns every write into a no-op.</param>
    public RotatingFileLogger(string? directory = null, IClock? clock = null, bool enabled = true)
    {
        _directory = string.IsNullOrWhiteSpace(directory) ? DatabasePaths.LogDirectory : directory;
        _clock = clock ?? SystemClock.Instance;
        _enabled = enabled;
    }

    /// <summary>A logger that writes nothing, for tests and for <c>--json</c> output modes.</summary>
    public static RotatingFileLogger Disabled { get; } = new(enabled: false);

    /// <summary>
    /// Lines this logger could not write. Anything above zero means the diagnostic log is
    /// incomplete, which matters more than the lines themselves: a log that is trusted and
    /// silently lossy sends its reader hunting a fault that was recorded and thrown away.
    /// <c>GetStatus</c> turns a non-zero count into a warning the user can see.
    /// </summary>
    public long SuppressedWrites => Interlocked.Read(ref _suppressedWrites);

    /// <summary>
    /// Days of history kept. Older files are deleted regardless of size.
    ///
    /// The value the user chose in 设置 → 诊断日志保留天数, pushed down by
    /// <c>UpdateCaptureSettings</c> and at startup. It is a live setting rather than a constant
    /// so that the control in the UI is not a dead one (spec gap P1-5).
    /// </summary>
    public int RetentionDays
    {
        get => Volatile.Read(ref _retentionDays);
        set => Volatile.Write(
            ref _retentionDays, Math.Clamp(value, MinRetentionDays, MaxRetentionDays));
    }

    /// <summary>
    /// Applies a retention setting and immediately deletes whatever it has just aged out, so
    /// that shortening the window takes effect now rather than on the first write of the next
    /// day.
    /// </summary>
    /// <param name="days">Days to keep; clamped to the contract range.</param>
    public void ApplyRetention(int days)
    {
        RetentionDays = days;
        if (!_enabled || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    PruneOldDays();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retention is best-effort: a file we may not delete is not a reason to fail
                // a settings write the user asked for.
            }
        }
    }

    /// <summary>Path of the file currently being appended to.</summary>
    public string CurrentPath =>
        Path.Combine(
            _directory,
            "collector-" + _clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log");

    /// <summary>Writes one structured line.</summary>
    /// <param name="level">Severity.</param>
    /// <param name="component">Subsystem name, for example <c>ipc</c>.</param>
    /// <param name="eventName">Short machine-readable event name.</param>
    /// <param name="fields">Extra non-sensitive fields.</param>
    public void Write(
        LogLevel level,
        string component,
        string eventName,
        IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!_enabled || _disposed)
        {
            return;
        }

        var line = new JsonObject
        {
            ["ts"] = UtcTimestamp.ToText(UtcTimestamp.Truncate(_clock.UtcNow)),
            ["level"] = level.ToString().ToUpperInvariant(),
            ["component"] = component,
            ["event"] = eventName,
        };

        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                line[key] = Ipc.Wire.Value(value);
            }
        }

        // The assembled line is sanitized rather than the string-typed fields, so that values
        // which only become text when rendered -- a DirectoryInfo, an IPAddress, an enum with
        // a revealing ToString -- cannot walk past the redaction pass merely because they are
        // not of type string at that point (review finding L2).
        SanitizeInPlace(line);
        Append(line.ToJsonString(Ipc.Wire.JsonOptions));
    }

    /// <summary>Writes an error line describing an exception by type and message only.</summary>
    /// <param name="component">Subsystem name.</param>
    /// <param name="eventName">Short machine-readable event name.</param>
    /// <param name="error">Exception; only its type name and message are recorded.</param>
    public void WriteError(string component, string eventName, Exception? error) =>
        WriteError(component, eventName, error, null);

    /// <summary>
    /// Writes an error line describing an exception, plus the caller's own account of what
    /// happened.
    ///
    /// The account matters when there is no exception at all. Machina reports its thread's
    /// failures through a trace callback rather than by throwing, so the capture fault path
    /// passes a reason and a null exception; recording only the exception would log
    /// "error_type: null, error_message: null" and say nothing about why capture stopped.
    /// </summary>
    /// <param name="component">Subsystem name.</param>
    /// <param name="eventName">Short machine-readable event name.</param>
    /// <param name="error">Exception; only its type name and message are recorded.</param>
    /// <param name="reason">The caller's own description; sanitized like everything else.</param>
    public void WriteError(string component, string eventName, Exception? error, string? reason) =>
        Write(LogLevel.Error, component, eventName, new Dictionary<string, object?>
        {
            ["error_type"] = error?.GetType().Name,
            ["error_message"] = error is null ? null : Sanitize(error.Message),
            ["reason"] = string.IsNullOrWhiteSpace(reason) ? null : Sanitize(reason),
        });

    /// <summary>
    /// Strips from a log line every category of secret docs/privacy-boundary.md section 5
    /// forbids: the FF14 install path, user profile folders, IPv4 and IPv6 literals, Windows
    /// security identifiers and long hexadecimal runs.
    ///
    /// It redacts <em>every</em> profile prefix, not only the current user's own: on a shared
    /// machine an exception message can mention somebody else's path, which the boundary
    /// forbids writing. The same holds for the other categories: none is needed to diagnose a
    /// fault, and each either names a person or lets traffic be reconstructed.
    ///
    /// The passes run in a fixed order, widest first, so a narrower pattern can never carve a
    /// fragment out of something a wider one would have removed whole. Over-redaction is the
    /// deliberate failure mode.
    /// </summary>
    /// <param name="text">Text about to be logged.</param>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var redacted = GameExecutablePathPattern().Replace(text, "[game-dir]");
        redacted = UserProfilePattern().Replace(redacted, "%USERPROFILE%");
        redacted = SidPattern().Replace(redacted, "[sid]");
        redacted = IPv6Pattern().Replace(redacted, "[ip6]");
        redacted = IPv4Pattern().Replace(redacted, "[ip]");
        return HexBlobPattern().Replace(redacted, "[hex]");
    }

    /// <summary>
    /// Replaces every string anywhere in a rendered line with its sanitized form, in place.
    ///
    /// It walks the tree rather than the serialized text on purpose: once the line is JSON,
    /// <c>C:\Users\someone</c> has become <c>C:\\Users\\someone</c> and the profile pattern no
    /// longer matches it. Redaction has to happen while the values are still values.
    /// </summary>
    /// <param name="node">Node to rewrite; objects and arrays are walked.</param>
    private static void SanitizeInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[key] = Sanitize(text);
                    }
                    else
                    {
                        SanitizeInPlace(obj[key]);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue item && item.TryGetValue<string>(out var element))
                    {
                        array[i] = element is null ? null : Sanitize(element);
                    }
                    else
                    {
                        SanitizeInPlace(array[i]);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(
        UserProfileRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePattern();

    [GeneratedRegex(SidRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    [GeneratedRegex(IPv4Regex, RegexOptions.CultureInvariant)]
    private static partial Regex IPv4Pattern();

    [GeneratedRegex(IPv6Regex, RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Pattern();

    [GeneratedRegex(HexBlobRegex, RegexOptions.CultureInvariant)]
    private static partial Regex HexBlobPattern();

    [GeneratedRegex(
        GameExecutablePathRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameExecutablePathPattern();

    /// <summary>
    /// Appends one rendered line, retrying briefly and counting what it could not write.
    ///
    /// The Collector is not always the only writer of the day's file: <c>--capture-trace</c>,
    /// an instance on its way out and the serving one can all hold it at once. The file is
    /// therefore opened with <see cref="FileShare.ReadWrite"/>, which <c>File.AppendAllText</c>
    /// does not do: it asks for read-only sharing and the second writer loses its line. A
    /// collision that survives the retries is still swallowed, because diagnostics must never
    /// take the process down, but it is counted (review finding M7).
    /// </summary>
    /// <param name="line">Sanitized JSON line, without its terminator.</param>
    private void Append(string line)
    {
        lock (_gate)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    AppendOnce(line);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= AppendAttempts)
                    {
                        Interlocked.Increment(ref _suppressedWrites);
                        return;
                    }

                    // Another writer is mid-append or mid-rotate. Waiting a moment is the
                    // whole fix; the window is microseconds wide.
                    Thread.Sleep(AppendRetryDelay);
                }
            }
        }
    }

    /// <summary>
    /// Makes one attempt at the whole append -- rotate, prune, write -- with every other
    /// writer of this folder held off.
    ///
    /// The lock is what makes the widened sharing safe. <see cref="FileShare.ReadWrite"/> lets
    /// a second writer in, and <see cref="FileMode.Append"/> on Windows is not an atomic
    /// append: the end-of-file offset is taken when the handle is opened and the write goes to
    /// that offset, so two writers that opened at the same length overwrite each other and a
    /// line disappears with no exception for the retry to catch. It is a named
    /// <see cref="Mutex"/> rather than <c>_gate</c> because the competing writers are usually
    /// other processes, which an in-process lock cannot see.
    ///
    /// Failing to take the lock is not failing to log: if the mutex cannot be created, or is
    /// not free within <see cref="AppendLockTimeout"/>, the line is written anyway, because
    /// diagnostics must never park a caller.
    /// </summary>
    /// <param name="line">Sanitized JSON line, without its terminator.</param>
    private void AppendOnce(string line)
    {
        var appendLock = AppendLock();
        var held = false;
        try
        {
            if (appendLock is not null)
            {
                try
                {
                    held = appendLock.WaitOne(AppendLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died mid-append. The wait still succeeded and this
                    // thread now owns the mutex; the file is append-only, so there is no
                    // half-finished state to repair.
                    held = true;
                }
            }

            Directory.CreateDirectory(_directory);
            var path = CurrentPath;
            if (!File.Exists(path))
            {
                // First write of a new day. Size-based rotation only bounds one day's
                // files, so this is where days that have aged out get removed.
                PruneOldDays();
            }
            else if (new FileInfo(path).Length >= MaxFileBytes)
            {
                Rotate(path);
            }

            AppendShared(path, line);
        }
        finally
        {
            if (held)
            {
                appendLock!.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// The cross-process append lock for this folder, created on first use, or null when the
    /// operating system will not give us one.
    ///
    /// Only ever called under <c>_gate</c>, so the field needs no synchronisation of its own.
    /// The name carries a hash of the folder rather than the folder itself: object names are
    /// visible machine-wide and a log path contains the user's account name.
    /// </summary>
    private Mutex? AppendLock()
    {
        if (_appendLock is not null)
        {
            return _appendLock;
        }

        try
        {
            _appendLock = new Mutex(initiallyOwned: false, AppendLockName(_directory));
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or IOException or
                  WaitHandleCannotBeOpenedException or NotSupportedException)
        {
            // Logging degrades to unlocked appends; it never fails the caller.
        }

        return _appendLock;
    }

    /// <summary>Logon-session-local mutex name for one log folder.</summary>
    /// <param name="directory">Log folder.</param>
    private static string AppendLockName(string directory)
    {
        var key = directory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "Local\\MentorRecorder.Log." + Convert.ToHexString(digest.AsSpan(0, 8));
    }

    /// <summary>
    /// Appends one line with sharing wide enough for a second writer.
    ///
    /// The whole line goes out in a single write on a file opened for append, so no writer can
    /// interleave half a line with another's half; ordering between writers is what the lock
    /// in <see cref="AppendOnce"/> is for.
    /// </summary>
    /// <param name="path">Active log file.</param>
    /// <param name="line">Sanitized JSON line, without its terminator.</param>
    private static void AppendShared(string path, string line)
    {
        var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
        using var stream = new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Deletes every log file whose day is older than <see cref="RetentionDays"/>.</summary>
    private void PruneOldDays()
    {
        const int dateOffset = 10;
        const int dateLength = 8;
        var cutoff = _clock.UtcNow.AddDays(-RetentionDays).UtcDateTime.Date;

        foreach (var file in Directory.GetFiles(_directory, "collector-*.log"))
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.Length < dateOffset + dateLength)
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    stem.Substring(dateOffset, dateLength),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day) &&
                day < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private void Rotate(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var index = MaxFiles - 1; index >= 1; index--)
        {
            var older = Path.Combine(_directory, $"{stem}.{index}.log");
            var newer = index == 1 ? path : Path.Combine(_directory, $"{stem}.{index - 1}.log");
            if (!File.Exists(newer))
            {
                continue;
            }

            if (File.Exists(older))
            {
                File.Delete(older);
            }

            File.Move(newer, older);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _appendLock?.Dispose();
            _appendLock = null;
        }
    }
}
