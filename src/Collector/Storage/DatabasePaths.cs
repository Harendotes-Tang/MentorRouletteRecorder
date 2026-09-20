namespace MentorRecorder.Collector.Storage;

/// <summary>
/// Where the Collector keeps its files. Everything lives under the current user's local
/// application data folder; nothing is written outside it unless the user names a path.
/// </summary>
public static class DatabasePaths
{
    /// <summary>Folder name used under %LOCALAPPDATA%.</summary>
    public const string FolderName = "MentorRecorder";

    /// <summary>Database file name (docs/data-model.md, header).</summary>
    public const string DatabaseFileName = "mentor_recorder.db";

    /// <summary>Folder name of the rotated diagnostic logs, under whichever root is in force.</summary>
    public const string LogFolderName = "logs";

    /// <summary>
    /// Environment variable that moves the whole root -- database, logs, backups and the
    /// default export folders -- somewhere else.
    ///
    /// It exists for test harnesses and scripts/verify.ps1: <c>--db &lt;throw-away&gt;</c> alone
    /// does not isolate a run, because the log folder would still resolve to %LOCALAPPDATA%
    /// and rotate the real user's diagnostics.
    /// </summary>
    public const string DataDirectoryVariable = "MR_DATA_DIR";

    /// <summary>Root folder: %LOCALAPPDATA%\MentorRecorder, or <c>MR_DATA_DIR</c> when set.</summary>
    public static string RootDirectory =>
        ResolveRoot(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    /// <summary>
    /// Root folder for an explicit override. Exposed separately from
    /// <see cref="RootDirectory"/> so the rule can be tested without writing to the process
    /// environment.
    /// </summary>
    /// <param name="dataDirectory">Override; the managed location when null or blank.</param>
    public static string ResolveRoot(string? dataDirectory) =>
        string.IsNullOrWhiteSpace(dataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FolderName)
            : Path.GetFullPath(dataDirectory);

    /// <summary>Default database path.</summary>
    public static string DefaultDatabasePath => Path.Combine(RootDirectory, DatabaseFileName);

    /// <summary>Folder holding rotated diagnostic logs.</summary>
    public static string LogDirectory => Path.Combine(RootDirectory, LogFolderName);

    /// <summary>
    /// Log folder for one run of the Collector, in precedence order: an explicit
    /// <c>--log-dir</c>, then a <c>logs</c> folder beside an explicit <c>--db</c>, then the
    /// managed location.
    ///
    /// The middle rule keeps a run pointed at a throw-away database self-contained: its
    /// diagnostics sit beside that database and its retention sweep never reaches the real
    /// log folder.
    /// </summary>
    /// <param name="logDirectory">Value of <c>--log-dir</c>, or null.</param>
    /// <param name="databasePath">Value of <c>--db</c>, or null for the default database.</param>
    public static string ResolveLogDirectory(string? logDirectory, string? databasePath)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            return Path.GetFullPath(logDirectory);
        }

        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return LogDirectory;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return string.IsNullOrEmpty(directory)
            ? LogDirectory
            : Path.Combine(directory, LogFolderName);
    }

    /// <summary>Default folder for database backups.</summary>
    public static string BackupDirectory => Path.Combine(RootDirectory, "backups");

    /// <summary>
    /// File name of the manifest listing the temporary copies of the game executable this
    /// software created and still owns (docs/privacy-boundary.md section 4.2).
    /// </summary>
    public const string OodleTempManifestFileName = "oodle-temp.json";

    /// <summary>
    /// Manifest of owned Oodle temp copies for one run of the Collector: beside an explicit
    /// <c>--db</c>, otherwise in the managed root.
    ///
    /// It lives next to the database rather than in TEMP so that a run pointed at a throw-away
    /// database cleans up only its own copies, never those of the user's own Collector.
    /// </summary>
    /// <param name="databasePath">Value of <c>--db</c>, or null for the default database.</param>
    public static string ResolveOodleTempManifest(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return Path.Combine(RootDirectory, OodleTempManifestFileName);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return string.IsNullOrEmpty(directory)
            ? Path.Combine(RootDirectory, OodleTempManifestFileName)
            : Path.Combine(directory, OodleTempManifestFileName);
    }

    /// <summary>
    /// File name of the note recording where the game was last seen installed, so the client
    /// version can be read while the game is closed (docs/privacy-boundary.md section 9).
    /// </summary>
    public const string GameInstallFileName = "game-install.json";

    /// <summary>
    /// Where the remembered install path is kept for one run of the Collector: beside an
    /// explicit <c>--db</c>, otherwise in the managed root.
    ///
    /// Beside the database for the same reason as the Oodle manifest: a run pointed at a
    /// throw-away database keeps its own note and never rewrites the user's.
    /// </summary>
    /// <param name="databasePath">Value of <c>--db</c>, or null for the default database.</param>
    public static string ResolveGameInstallMemory(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return Path.Combine(RootDirectory, GameInstallFileName);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return string.IsNullOrEmpty(directory)
            ? Path.Combine(RootDirectory, GameInstallFileName)
            : Path.Combine(directory, GameInstallFileName);
    }

    /// <summary>Creates the directory of <paramref name="filePath"/> when it is missing.</summary>
    /// <param name="filePath">Path of a file that is about to be written.</param>
    public static void EnsureParentDirectory(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
