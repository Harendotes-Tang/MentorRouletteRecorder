using System.Globalization;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Export;

/// <summary>Outcome of one backup.</summary>
/// <param name="TargetPath">Absolute path written.</param>
/// <param name="ByteCount">Size of the written file.</param>
/// <param name="CompletedAtUtc">Completion time.</param>
/// <param name="IntegrityCheckPassed">Whether the copy passed its own integrity check.</param>
/// <param name="PrunedCount">How many older backups the retention policy removed.</param>
public sealed record BackupResult(
    string TargetPath,
    long ByteCount,
    DateTimeOffset CompletedAtUtc,
    bool IntegrityCheckPassed,
    int PrunedCount);

/// <summary>
/// Copies the database with <c>VACUUM INTO</c> and keeps the newest few copies.
///
/// <c>VACUUM INTO</c> is used rather than a file copy because it produces a consistent
/// snapshot from the live connection: no WAL checkpoint dance, no half-written page, and the
/// source file is never moved or truncated. The copy is then opened read-only and integrity
/// checked, so a backup that silently failed cannot be reported as a success.
/// </summary>
public sealed class BackupService
{
    /// <summary>Number of automatic backups kept in the default folder.</summary>
    public const int RetainedBackups = 14;

    /// <summary>Prefix of an automatically named backup file.</summary>
    public const string FileNamePrefix = "mentor_";

    private const string TimestampFormat = "yyyyMMdd_HHmmss";

    private readonly SqliteDatabase _database;
    private readonly IClock _clock;

    /// <summary>Creates the service over the open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="clock">Clock used to name and stamp the backup.</param>
    public BackupService(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
    }

    /// <summary>Folder automatic backups are written to: <c>&lt;db dir&gt;\backups</c>.</summary>
    public string DefaultDirectory =>
        Path.Combine(Path.GetDirectoryName(_database.Path) ?? DatabasePaths.RootDirectory, "backups");

    /// <summary>Writes one backup and prunes older automatic ones.</summary>
    /// <param name="targetPath">
    /// Destination. Null, empty or an existing directory selects the default file name
    /// <c>mentor_&lt;yyyyMMdd_HHmmss&gt;.db</c>; anything else is used verbatim.
    /// </param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    public BackupResult CreateBackup(string? targetPath = null, bool overwrite = false)
    {
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var fullPath = ResolveTarget(targetPath, now);
        ExportPaths.PrepareDestination(fullPath, overwrite);

        var outcome = _database.BackupDatabase(fullPath, overwrite);
        if (!outcome.IntegrityCheckPassed)
        {
            throw new CollectorException(ErrorCodes.ExportFailed,
                "备份完整性校验未通过，未清理既有备份。");
        }
        var pruned = Prune(Path.GetDirectoryName(fullPath), DefaultDirectory);
        PruneAbandonedTemporaries(DefaultDirectory, now);
        return new BackupResult(
            outcome.TargetPath, outcome.ByteCount, now, outcome.IntegrityCheckPassed, pruned);
    }

    /// <summary>Default file name for a backup taken at <paramref name="now"/>.</summary>
    /// <param name="now">Backup time.</param>
    public static string FileNameFor(DateTimeOffset now) =>
        FileNamePrefix + now.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture) + ".db";

    private string ResolveTarget(string? targetPath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return ExportPaths.Resolve(Path.Combine(DefaultDirectory, FileNameFor(now)));
        }

        var resolved = ExportPaths.Resolve(targetPath);

        // The appended file name is validated too: resolving the directory alone would leave
        // the combined path unchecked, and the check is not only about the directory -- it
        // follows links to where a write would physically land.
        return Directory.Exists(resolved)
            ? ExportPaths.Resolve(Path.Combine(resolved, FileNameFor(now)))
            : resolved;
    }

    /// <summary>
    /// Deletes all but the newest <see cref="RetainedBackups"/> automatically named files, and
    /// only ever in the folder this service manages.
    ///
    /// Retention is a promise about the Collector's own backup folder, not a licence to delete
    /// files anywhere the user pointed a backup at. A user who backs up into their documents
    /// folder, or onto a drive holding archives named the same way, must not find fifteen of
    /// them gone because they asked for a sixteenth (review finding M6). A file is removed only
    /// when both its name and its folder match.
    /// </summary>
    /// <param name="directory">Folder the backup was just written to.</param>
    /// <param name="managedDirectory">The only folder retention is allowed to sweep.</param>
    private static int Prune(string? directory, string managedDirectory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        if (!IsSameDirectory(directory, managedDirectory))
        {
            return 0;
        }

        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(directory)
                .GetFiles(FileNamePrefix + "*.db")
                .Where(file => IsGeneratedName(file.Name))
                .OrderByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var pruned = 0;
        for (var i = RetainedBackups; i < files.Length; i++)
        {
            try
            {
                files[i].Delete();
                pruned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup the user has open in another tool stays; retention is best effort
                // and must never fail the backup that just succeeded.
            }
        }

        return pruned;
    }

    /// <summary>Prefix of the staging file an atomic write creates beside its target.</summary>
    public const string TemporaryFilePrefix = AtomicExportFile.TemporaryPrefix;

    /// <summary>How long an abandoned staging file is left alone before it is swept.</summary>
    public static readonly TimeSpan TemporaryFileGrace = TimeSpan.FromHours(1);

    /// <summary>
    /// Deletes staging files a killed process left behind, and only inside the folder this
    /// service manages.
    ///
    /// <see cref="AtomicExportFile"/> stages beside its target and deletes on the way out, but
    /// a process that is terminated rather than stopped never gets there -- and the Desktop's
    /// exit path terminates -- so the backup folder accumulates invisible
    /// <c>.mentor-export-*.tmp</c> files the size of the database (review finding L-18). The
    /// grace period keeps this safe next to a concurrent write: a staging file younger than an
    /// hour may still belong to a live export, and no export takes an hour. Folders the user
    /// chose are never swept, for the same reason retention does not sweep them.
    /// </summary>
    /// <param name="managedDirectory">The only folder this sweep is allowed to touch.</param>
    /// <param name="now">Current time; files older than the grace period are removed.</param>
    private static void PruneAbandonedTemporaries(string managedDirectory, DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(managedDirectory))
            {
                return;
            }

            foreach (var file in new DirectoryInfo(managedDirectory).GetFiles(TemporaryFilePrefix + "*.tmp"))
            {
                if (now - new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) < TemporaryFileGrace)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A staging file another process still holds stays; the sweep is best
                    // effort and must never fail the backup that just succeeded.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Compares two folder paths as the file system would: fully resolved, without a trailing
    /// separator, case-insensitively. A path that cannot be resolved is treated as "not the
    /// managed folder", which fails towards deleting nothing.
    /// </summary>
    /// <param name="left">Folder the backup landed in.</param>
    /// <param name="right">Folder retention manages.</param>
    private static bool IsSameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsGeneratedName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Length == FileNamePrefix.Length + TimestampFormat.Length &&
            stem.StartsWith(FileNamePrefix, StringComparison.Ordinal) &&
            DateTime.TryParseExact(
                stem[FileNamePrefix.Length..],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
    }
}
