using System.Text;
using System.Text.RegularExpressions;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Protocol.Sharing;

/// <summary>
/// Keeps downloaded share codes and the bookkeeping around them on disk:
/// <c>&lt;root&gt;/&lt;cn|global&gt;/&lt;build&gt;/</c> holds one <c>&lt;first 12 hex digits&gt;.mrc</c> per code and a
/// <c>state.json</c> (docs/privacy-boundary.md §9).
///
/// Monotonic about codes. A code file is written only when the code decodes, hashes to the identity it
/// was downloaded under and describes this region and build. Nothing a fetch reports - a failure, an
/// empty index, an index that no longer lists a code - deletes or overwrites a valid code file; a
/// revoked or rejected code is hidden from <see cref="LoadCandidates"/>, never deleted.
///
/// Robust about state. <c>state.json</c> is bookkeeping, not evidence. A file that cannot be understood
/// reads as empty and is replaced on the next write; a file that cannot be opened (locked, denied) reads
/// as empty and is left alone, so a transient lock never costs the rejection records. Every write is a
/// temp file moved into place, and nothing here throws for anything found on disk.
///
/// Throttling is keyed by region, build and template hash: a software update that brings a new
/// template may fetch at once instead of waiting out the previous template's six hours.
/// </summary>
public sealed class SharedCalibrationStore : ISharedCalibrationStore
{
    /// <summary>Directory under the managed data root.</summary>
    public const string DirectoryName = "shared-calibrations";

    /// <summary>Bookkeeping file in each region/build directory.</summary>
    public const string StateFileName = "state.json";

    /// <summary>Distinct healthy sessions that must contradict a code before it stops being tried (plan §4.1 step 6).</summary>
    public const int RejectionThreshold = 2;

    /// <summary>At most one automatic fetch per region, build and template in this interval (plan §2.1).</summary>
    public static readonly TimeSpan AutoFetchInterval = TimeSpan.FromHours(6);

    private static readonly object Gate = new();
    private static readonly Regex CodeFileName = new(@"^[0-9a-f]{12}\.mrc\z", RegexOptions.CultureInvariant);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A store rooted at a directory; nothing is created until something is written.</summary>
    /// <param name="root">Directory holding the region directories; production uses <see cref="RootPath"/>.</param>
    public SharedCalibrationStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>The directory this store keeps its files under.</summary>
    public string Root { get; }

    /// <summary>Production root: <c>shared-calibrations</c> under the managed data root.</summary>
    public static string RootPath => Path.Combine(DatabasePaths.RootDirectory, DirectoryName);

    /// <summary>A store that remembers nothing and never touches the disk; the default seam.</summary>
    public static ISharedCalibrationStore Inert => InertSharedCalibrationStore.Instance;

    /// <summary>
    /// Directory of a region and build: the build lowercased, characters other than letters, digits,
    /// <c>.</c> and <c>-</c> replaced by <c>-</c>, and trailing dots replaced too (Windows drops them).
    /// Codes carry their own build, so two builds sharing a directory name cannot be confused.
    /// </summary>
    /// <param name="root">Store root.</param>
    /// <param name="region">CN or GLOBAL.</param>
    /// <param name="gameBuild">Client build (<see cref="SharedCalibrationIndex.IsBuild"/>).</param>
    public static string DirectoryFor(string root, Region region, string gameBuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var regionDirectory = SharedCalibrationIndex.RegionDirectory(region);
        if (!SharedCalibrationIndex.IsBuild(gameBuild))
        {
            throw new ArgumentException("not a client build", nameof(gameBuild));
        }

        var name = new StringBuilder(gameBuild.Length);
        foreach (var c in gameBuild.ToLowerInvariant())
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-');
        }

        for (var i = name.Length - 1; i >= 0 && name[i] == '.'; i--)
        {
            name[i] = '-';
        }

        return Path.Combine(root, regionDirectory, name.ToString());
    }

    /// <summary>
    /// Whether an automatic fetch is due: always for a manual check, when there was no attempt, when
    /// the last one is <see cref="AutoFetchInterval"/> or more ago, or when its stamp lies in the future
    /// (the clock moved back, which must not silence fetching for as long as it moved).
    /// </summary>
    /// <param name="lastAttemptUtc">From <see cref="LastFetch"/>, or null.</param>
    /// <param name="nowUtc">Now.</param>
    /// <param name="manual">True for 立即检查.</param>
    public static bool ShouldAutoFetch(DateTimeOffset? lastAttemptUtc, DateTimeOffset nowUtc, bool manual) =>
        manual || lastAttemptUtc is not { } last || last > nowUtc || nowUtc - last >= AutoFetchInterval;

    /// <inheritdoc />
    public SharedStoreWriteResult RecordFetch(
        Region region, string gameBuild, string templateSha256, SharedCalibrationFetchResult result, DateTimeOffset nowUtc)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        RequireSha256(templateSha256, nameof(templateSha256));
        ArgumentNullException.ThrowIfNull(result);
        if (result.IndexAttempts.Count == 0)
        {
            return new SharedStoreWriteResult(false, 0, Array.Empty<string>());
        }

        lock (Gate)
        {
            var written = 0;
            var refused = new List<string>();
            var hints = new List<SharedCodeHints>();
            foreach (var candidate in result.Candidates)
            {
                var write = WriteCode(directory, region, gameBuild, candidate);
                written += write.Written ? 1 : 0;
                if (write.Refusal is { } refusal)
                {
                    refused.Add(refusal);
                    continue;
                }

                hints.Add(new SharedCodeHints(
                    candidate.CodeSha256, candidate.Submitters, candidate.FirstPublishedAtUtc, candidate.Commit));
            }

            var (state, writable) = ReadState(directory);
            if (!writable)
            {
                return new SharedStoreWriteResult(false, written, refused);
            }

            var complete = result.Status is SharedFetchStatus.Ok or SharedFetchStatus.NoneForBuild;
            var record = new SharedFetchRecord(
                templateSha256,
                nowUtc,
                complete ? nowUtc : state.FetchFor(templateSha256)?.LastSuccessAtUtc,
                result.Status,
                result.IndexAttempts.ToArray(),
                result.Discards.ToArray());
            var updated = state.WithFetch(record).WithHints(hints);
            var next = result.IndexWasRead ? updated.WithRevoked(result.RevokedCodeSha256s) : updated;
            return new SharedStoreWriteResult(WriteAtomically(StatePath(directory), next.Serialize(region, gameBuild)), written, refused);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SharedStoredCandidate> LoadCandidates(Region region, string gameBuild, string templateSha256)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        RequireSha256(templateSha256, nameof(templateSha256));
        lock (Gate)
        {
            var (state, _) = ReadState(directory);
            var revoked = state.Revoked.ToHashSet(StringComparer.Ordinal);
            var found = new List<SharedStoredCandidate>();
            foreach (var path in CodeFiles(directory))
            {
                if (ReadCode(path, out var code) != FileState.Valid ||
                    !string.Equals(Path.GetFileName(path), code!.Sha[..12] + SharedCalibrationIndex.CodeExtension, StringComparison.Ordinal) ||
                    code.Payload.Region != region ||
                    !string.Equals(code.Payload.GameBuild, gameBuild, StringComparison.Ordinal) ||
                    !string.Equals(code.Payload.TemplateSha256, templateSha256, StringComparison.Ordinal) ||
                    revoked.Contains(code.Sha) ||
                    IsRejected(state, templateSha256, code.Sha))
                {
                    continue;
                }

                var hints = state.HintsFor(code.Sha);
                found.Add(new SharedStoredCandidate(code.Sha, code.Text, code.Payload, hints?.Submitters ?? 0, hints?.FirstPublishedAtUtc));
            }

            return found
                .OrderByDescending(candidate => candidate.Submitters)
                .ThenBy(candidate => candidate.FirstPublishedAtUtc ?? DateTimeOffset.MaxValue)
                .ThenBy(candidate => candidate.CodeSha256, StringComparer.Ordinal)
                .Take(SharedCalibrationIndex.MaxCandidates)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public SharedFetchRecord? LastFetch(Region region, string gameBuild, string templateSha256)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        RequireSha256(templateSha256, nameof(templateSha256));
        lock (Gate)
        {
            return ReadState(directory).State.FetchFor(templateSha256);
        }
    }

    /// <inheritdoc />
    public SharedContradictionResult RecordContradiction(
        Region region, string gameBuild, string templateSha256, string codeSha256, string sessionId, DateTimeOffset nowUtc)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        RequireSha256(templateSha256, nameof(templateSha256));
        RequireSha256(codeSha256, nameof(codeSha256));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SharedCalibrationState.IsSessionId(sessionId))
        {
            throw new ArgumentException("session id is too long", nameof(sessionId));
        }

        lock (Gate)
        {
            var (state, writable) = ReadState(directory);
            if (!writable)
            {
                return new SharedContradictionResult(0, false, false);
            }

            var existing = state.RejectionFor(templateSha256, codeSha256);
            if (existing is not null && existing.Sessions.Contains(sessionId, StringComparer.Ordinal))
            {
                return new SharedContradictionResult(existing.Sessions.Count, existing.Sessions.Count >= RejectionThreshold, true);
            }

            var sessions = (existing?.Sessions ?? Array.Empty<string>())
                .Append(sessionId)
                .Take(SharedCalibrationState.MaxSessions)
                .ToArray();
            var rejection = new SharedRejection(templateSha256, codeSha256, sessions, existing?.FirstAtUtc ?? nowUtc, nowUtc);
            var persisted = WriteAtomically(StatePath(directory), state.WithRejection(rejection).Serialize(region, gameBuild));
            return new SharedContradictionResult(sessions.Length, sessions.Length >= RejectionThreshold, persisted);
        }
    }

    /// <inheritdoc />
    public bool IsRejected(Region region, string gameBuild, string templateSha256, string codeSha256)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        RequireSha256(templateSha256, nameof(templateSha256));
        RequireSha256(codeSha256, nameof(codeSha256));
        lock (Gate)
        {
            return IsRejected(ReadState(directory).State, templateSha256, codeSha256);
        }
    }

    /// <inheritdoc />
    public bool RecordUserRejection(Region region, string gameBuild, DateTimeOffset nowUtc)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        lock (Gate)
        {
            var (state, writable) = ReadState(directory);
            return writable &&
                   WriteAtomically(StatePath(directory), state.WithUserRejection(nowUtc).Serialize(region, gameBuild));
        }
    }

    /// <inheritdoc />
    public bool IsUserRejected(Region region, string gameBuild)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        lock (Gate)
        {
            return ReadState(directory).State.UserRejectedAtUtc is not null;
        }
    }

    /// <inheritdoc />
    public bool ClearRejections(Region region, string gameBuild)
    {
        var directory = DirectoryFor(Root, region, gameBuild);
        lock (Gate)
        {
            var (state, writable) = ReadState(directory);
            if (!writable)
            {
                return false;
            }

            return (state.Rejections.Count == 0 && state.UserRejectedAtUtc is null) ||
                   WriteAtomically(StatePath(directory), state.WithoutRejections().Serialize(region, gameBuild));
        }
    }

    // ------------------------------------------------------------------------ codes

    private enum FileState
    {
        Missing,
        Valid,
        Invalid,
        Unreadable,
    }

    private sealed record StoredCode(string Sha, string Text, ShareCodePayload Payload);

    private sealed record CodeWrite(bool Written, string? Refusal);

    private static CodeWrite WriteCode(string directory, Region region, string gameBuild, SharedCalibrationCandidate candidate)
    {
        if (!SharedCalibrationIndex.IsSha256(candidate.CodeSha256))
        {
            return new CodeWrite(false, "invalid:INVALID_SHA");
        }

        var label = candidate.CodeSha256[..12];
        var decoded = ShareCode.Decode(candidate.Code);
        if (decoded.Payload is not { } payload)
        {
            return new CodeWrite(false, label + ":UNDECODABLE:" + decoded.Rejection?.Code);
        }

        if (!string.Equals(decoded.CodeSha256, candidate.CodeSha256, StringComparison.Ordinal))
        {
            return new CodeWrite(false, label + ":HASH_MISMATCH");
        }

        if (payload.Region != region)
        {
            return new CodeWrite(false, label + ":PAYLOAD_MISMATCH:region");
        }

        if (!string.Equals(payload.GameBuild, gameBuild, StringComparison.Ordinal))
        {
            return new CodeWrite(false, label + ":PAYLOAD_MISMATCH:game_build");
        }

        var path = Path.Combine(directory, label + SharedCalibrationIndex.CodeExtension);
        return ReadCode(path, out var existing) switch
        {
            FileState.Valid when string.Equals(existing!.Sha, candidate.CodeSha256, StringComparison.Ordinal) => new CodeWrite(false, null),
            FileState.Valid => new CodeWrite(false, label + ":NAME_TAKEN"),
            FileState.Unreadable => new CodeWrite(false, label + ":UNREADABLE"),
            // Missing, or a file that is not a code: writing a verified code over it only improves it.
            _ => WriteAtomically(path, candidate.Code.Trim())
                ? new CodeWrite(true, null)
                : new CodeWrite(false, label + ":UNWRITABLE"),
        };
    }

    private static FileState ReadCode(string path, out StoredCode? code)
    {
        code = null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return FileState.Missing;
            }

            if (file.Length > SharedCalibrationClient.MaxCodeBytes)
            {
                return FileState.Invalid;
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > SharedCalibrationClient.MaxCodeBytes)
            {
                return FileState.Invalid;
            }

            var text = StrictUtf8.GetString(SharedCalibrationIndex.WithoutBom(bytes));
            var decoded = ShareCode.Decode(text);
            if (decoded.Payload is not { } payload || decoded.CodeSha256 is not { } sha)
            {
                return FileState.Invalid;
            }

            code = new StoredCode(sha, text.Trim(), payload);
            return FileState.Valid;
        }
        catch (DecoderFallbackException)
        {
            return FileState.Invalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FileState.Unreadable;
        }
    }

    private static IReadOnlyList<string> CodeFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*" + SharedCalibrationIndex.CodeExtension)
                    .Where(path => CodeFileName.IsMatch(Path.GetFileName(path)))
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------------ state

    private static string StatePath(string directory) => Path.Combine(directory, StateFileName);

    private static bool IsRejected(SharedCalibrationState state, string templateSha256, string codeSha256) =>
        state.RejectionFor(templateSha256, codeSha256) is { } rejection && rejection.Sessions.Count >= RejectionThreshold;

    /// <summary>The state, and whether it may be overwritten: false only when the file exists but could not be opened.</summary>
    private static (SharedCalibrationState State, bool Writable) ReadState(string directory)
    {
        var path = StatePath(directory);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > SharedCalibrationState.MaxBytes)
            {
                return (SharedCalibrationState.Empty, true);
            }

            return (SharedCalibrationState.Parse(File.ReadAllText(path, StrictUtf8)), true);
        }
        catch (DecoderFallbackException)
        {
            return (SharedCalibrationState.Empty, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (SharedCalibrationState.Empty, false);
        }
    }

    private static bool WriteAtomically(string path, string text)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, text, Utf8);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The temp file is left behind; it never has the name of a code or of the state file.
            }

            return false;
        }
    }

    private static void RequireSha256(string? value, string name)
    {
        if (!SharedCalibrationIndex.IsSha256(value))
        {
            throw new ArgumentException("not a lowercase hex SHA-256", name);
        }
    }
}
