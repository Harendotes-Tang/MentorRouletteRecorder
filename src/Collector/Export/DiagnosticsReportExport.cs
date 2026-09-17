using System.Globalization;
using System.Text;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;

namespace MentorRecorder.Collector.Export;

/// <summary>Outcome of one diagnostics report export.</summary>
/// <param name="TargetPath">Absolute path written.</param>
/// <param name="ByteCount">Size of the written file.</param>
/// <param name="CompletedAtUtc">Completion time.</param>
public sealed record DiagnosticsReportResult(
    string TargetPath, long ByteCount, DateTimeOffset CompletedAtUtc);

/// <summary>
/// Writes the sanitized diagnostics report to a file, for the <c>ExportDiagnosticsReport</c>
/// IPC message.
///
/// It adds no field of its own. The content is whatever
/// <see cref="SanitizedDiagnosticsReport.Build"/> produces, so the whitelist that keeps
/// addresses, paths and packet bytes out of the report lives in exactly one place and this
/// class cannot widen it by accident (docs/capture-diagnostics.md section 9).
///
/// The destination obeys the same rule as every other write the user can ask for: it must be
/// an ordinary local file path, and network shares, device paths and links that end up on one
/// are refused with <c>ERR_EXPORT_FAILED</c> (<see cref="ExportPaths"/>).
///
/// Replacing an existing file is a separate question from being allowed to write there, and it
/// is answered narrowly: the exporter replaces a file only when the name is one it generated
/// itself, <c>diag_&lt;yyyyMMdd_HHmm&gt;.json</c>. Anything else needs <c>overwrite: true</c>
/// in the request, so that <c>ExportDiagnosticsReport {target_path: "D:\thesis.docx"}</c> -- a
/// plausible slip in a file dialog -- cannot destroy the file (review finding H3).
/// </summary>
public sealed class DiagnosticsReportExport
{
    /// <summary>Prefix of an automatically named report file.</summary>
    public const string FileNamePrefix = "diag_";

    /// <summary>Folder name used under the database directory when no path is given.</summary>
    public const string DefaultFolderName = "diagnostics";

    private const string TimestampFormat = "yyyyMMdd_HHmm";

    private readonly string _databasePath;
    private readonly IClock _clock;

    /// <summary>Creates the exporter.</summary>
    /// <param name="databasePath">Path of the open database; its folder anchors the default target.</param>
    /// <param name="clock">Clock used to name and stamp the report.</param>
    public DiagnosticsReportExport(string databasePath, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(clock);
        _databasePath = databasePath;
        _clock = clock;
    }

    /// <summary>Folder reports are written to when the request names no path.</summary>
    public string DefaultDirectory => Path.Combine(
        Path.GetDirectoryName(_databasePath) ?? DatabasePaths.RootDirectory, DefaultFolderName);

    /// <summary>Default file name for a report taken at <paramref name="now"/>.</summary>
    /// <param name="now">Report time.</param>
    public static string FileNameFor(DateTimeOffset now) =>
        FileNamePrefix
        + now.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)
        + ".json";

    /// <summary>Builds and writes the report.</summary>
    /// <param name="snapshot">Capture reading to render.</param>
    /// <param name="collectorVersion">Version stamped into the report.</param>
    /// <param name="targetPath">
    /// Destination. Null, empty or an existing directory selects the generated name
    /// <c>diag_&lt;yyyyMMdd_HHmm&gt;.json</c>; anything else is used verbatim. Either way the
    /// resolved path must be an ordinary local file path.
    /// </param>
    /// <param name="overwrite">
    /// Whether a file the exporter did not name may be replaced. Its own generated name is
    /// always replaced: a report is a fresh observation each time, two reports in the same
    /// minute resolve to the same name, and refusing the second would be an error with no
    /// action behind it.
    /// </param>
    public DiagnosticsReportResult Write(
        CaptureDiagnosticsSnapshot snapshot,
        string collectorVersion,
        string? targetPath = null,
        bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var fullPath = ResolveTarget(targetPath, now);

        var allowOverwrite = overwrite || IsGeneratedName(Path.GetFileName(fullPath));
        ExportPaths.PrepareDestination(fullPath, allowOverwrite);

        var report = SanitizedDiagnosticsReport.Build(snapshot, collectorVersion, now);
        var text = report.ToJsonString(Wire.IndentedJsonOptions) + Environment.NewLine;
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

        try
        {
            AtomicExportFile.Write(fullPath, allowOverwrite, file => file.Write(bytes));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed,
                "无法写入诊断报告，请检查磁盘空间与写入权限。",
                inner: ex);
        }

        return new DiagnosticsReportResult(fullPath, bytes.LongLength, now);
    }

    private string ResolveTarget(string? targetPath, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return ExportPaths.Resolve(Path.Combine(DefaultDirectory, FileNameFor(now)));
        }

        var resolved = ExportPaths.Resolve(targetPath);
        return Directory.Exists(resolved)
            ? ExportPaths.Resolve(Path.Combine(resolved, FileNameFor(now)))
            : resolved;
    }

    /// <summary>
    /// True when a file name is one this exporter would have produced:
    /// <c>diag_&lt;yyyyMMdd_HHmm&gt;.json</c>, and nothing else. The timestamp is parsed rather
    /// than pattern-matched so that a file merely starting with <c>diag_</c> -- which a user
    /// may well have named themselves -- is not mistaken for ours.
    /// </summary>
    /// <param name="fileName">File name of the resolved destination.</param>
    public static bool IsGeneratedName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
