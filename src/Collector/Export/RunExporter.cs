using System.Globalization;
using System.Text;
using System.Text.Json;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Export;

/// <summary>Outcome of one export, mirroring <c>$defs/ExportResult</c>.</summary>
/// <param name="TargetPath">Absolute path written.</param>
/// <param name="RowCount">Number of exported runs.</param>
/// <param name="ByteCount">Size of the written file.</param>
/// <param name="CompletedAtUtc">Completion time.</param>
public sealed record ExportOutcome(
    string TargetPath,
    int RowCount,
    long ByteCount,
    DateTimeOffset CompletedAtUtc);

/// <summary>
/// Writes the run history to a local file the user chose.
///
/// The export honours exactly the same <see cref="RunFilter"/> and <see cref="RunSort"/> as
/// <c>QueryRuns</c>, so what a user exports is what the list in front of them showed, in the
/// order it showed it. Nothing is uploaded anywhere: the only destination is a local path,
/// validated by <see cref="ExportPaths"/>.
/// </summary>
public sealed class RunExporter
{
    /// <summary>CSV header, in the fixed column order (docs/manual-correction.md section 10).</summary>
    public static IReadOnlyList<string> CsvHeader { get; } = new[]
    {
        "date", "matched_at", "entered_at", "ended_at", "duty_name", "duty_category",
        "job_name", "result", "duration", "source", "manually_corrected",
        "run_id", "content_id", "job_id",

        // Appended, never inserted: a spreadsheet or script written against the original
        // fourteen columns keeps working, because every one of them kept its position.
        "reflection_mood", "reflection_text",

        // 1 joined in progress, 0 entered at the start, empty when the software did not see.
        "joined_in_progress",
        "region", "game_build",
    };

    /// <summary>
    /// The header every export before <c>joined_in_progress</c> was appended carried: the first
    /// sixteen columns. An import still reads a file with exactly this header as a native export,
    /// so the formula-protection apostrophe is reversed for it as it is for a current one.
    /// </summary>
    public static IReadOnlyList<string> LegacyCsvHeader { get; } = CsvHeader.Take(16).ToArray();

    private const int JsonFlushThreshold = 64 * 1024;

    /// <summary>
    /// Characters a spreadsheet reads as the start of a formula rather than as text.
    ///
    /// The two whitespace ones are here because Excel strips leading whitespace before
    /// deciding, so <c>"\t=cmd"</c> is a formula to it and an ordinary string to everyone else.
    /// </summary>
    private static readonly char[] FormulaLeaders = { '=', '+', '-', '@', '\t', '\r' };

    /// <summary>
    /// The columns whose content is free text a person or the game chose, and therefore the
    /// only ones a spreadsheet could be tricked into evaluating.
    ///
    /// Neutralising every column would rewrite legitimate data: a reflection of "+1，很顺" or
    /// "-记得先清小怪" would come back with a leading apostrophe and no longer match what
    /// <c>QueryRuns</c> returns. Identifiers, timestamps, enum tokens and numbers come from a
    /// closed vocabulary, can never begin a formula, and are exported verbatim
    /// (review finding M-10).
    /// </summary>
    private static readonly bool[] FreeTextColumns = BuildFreeTextColumns();

    private static bool[] BuildFreeTextColumns()
    {
        var columns = new bool[CsvHeader.Count];
        for (var i = 0; i < CsvHeader.Count; i++)
        {
            columns[i] = CsvHeader[i] is "duty_name" or "duty_category" or "job_name"
                or "reflection_text" or "game_build";
        }

        return columns;
    }

    private readonly IClock _clock;
    private readonly SqliteDatabase _database;

    /// <summary>Creates an exporter over the run repository.</summary>
    /// <param name="runs">Run repository.</param>
    /// <param name="clock">Clock used to stamp the outcome.</param>
    /// <param name="database">
    /// Database used to open an independent read-only snapshot. When omitted, the repository's
    /// database supplies it; both construction paths preserve one snapshot across all rows.
    /// </param>
    public RunExporter(RunRepository runs, IClock clock, SqliteDatabase? database = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _database = database ?? runs.Database;
    }

    /// <summary>Writes an RFC 4180 CSV file with a UTF-8 byte order mark.</summary>
    /// <param name="targetPath">Destination chosen by the user.</param>
    /// <param name="filter">Same filter as QueryRuns; null exports everything visible.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="sort">Same sort as QueryRuns; null writes newest entered first.</param>
    public ExportOutcome ExportCsv(string targetPath, RunFilter? filter, bool overwrite, RunSort? sort = null) =>
        ExportCsv(targetPath, filter, overwrite, sort, CancellationToken.None);

    /// <summary>按原 CSV 契约生成文件，并允许连接断开时取消尚未发布的工作。</summary>
    /// <param name="targetPath">用户选择的目标路径。</param>
    /// <param name="filter">与 QueryRuns 相同的筛选；null 导出全部可见记录。</param>
    /// <param name="overwrite">是否允许原子替换已有文件。</param>
    /// <param name="sort">与 QueryRuns 相同的排序；null 按进本时间由新到旧。</param>
    /// <param name="cancellationToken">取消尚未发布的导出；已原子发布的文件仍返回成功。</param>
    /// <param name="targets">已封存的勾选目标；缺省时使用现有筛选协议。</param>
    internal ExportOutcome ExportCsv(
        string targetPath, RunFilter? filter, bool overwrite, RunSort? sort, CancellationToken cancellationToken,
        IReadOnlyList<BatchRunTarget>? targets = null) =>
        WriteExport(targetPath, overwrite, cancellationToken, stream =>
        {
            // The BOM is what makes Excel open a UTF-8 CSV with Chinese duty names correctly.
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                leaveOpen: true) { NewLine = "\r\n" };
            writer.WriteLine(string.Join(',', CsvHeader));
            return WriteRows(filter, sort, targets, run => WriteCsvRow(writer, run), cancellationToken);
        });

    /// <summary>Writes a JSON array of <c>$defs/Run</c> objects.</summary>
    /// <param name="targetPath">Destination chosen by the user.</param>
    /// <param name="filter">Same filter as QueryRuns; null exports everything visible.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="sort">Same sort as QueryRuns; null writes newest entered first.</param>
    public ExportOutcome ExportJson(string targetPath, RunFilter? filter, bool overwrite, RunSort? sort = null) =>
        ExportJson(targetPath, filter, overwrite, sort, CancellationToken.None);

    /// <summary>按原 JSON 契约生成文件，并允许连接断开时取消尚未发布的工作。</summary>
    /// <param name="targetPath">用户选择的目标路径。</param>
    /// <param name="filter">与 QueryRuns 相同的筛选；null 导出全部可见记录。</param>
    /// <param name="overwrite">是否允许原子替换已有文件。</param>
    /// <param name="sort">与 QueryRuns 相同的排序；null 按进本时间由新到旧。</param>
    /// <param name="cancellationToken">取消尚未发布的导出；已原子发布的文件仍返回成功。</param>
    /// <param name="targets">已封存的勾选目标；缺省时使用现有筛选协议。</param>
    internal ExportOutcome ExportJson(
        string targetPath, RunFilter? filter, bool overwrite, RunSort? sort, CancellationToken cancellationToken,
        IReadOnlyList<BatchRunTarget>? targets = null) =>
        WriteExport(targetPath, overwrite, cancellationToken, stream =>
        {
            var options = Wire.IndentedJsonOptions;
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = options.WriteIndented,
                Encoder = options.Encoder,
            });
            writer.WriteStartArray();
            var count = WriteRows(filter, sort, targets, run =>
            {
                Wire.Run(run).WriteTo(writer, options);
                if (writer.BytesPending >= JsonFlushThreshold)
                {
                    writer.Flush();
                }
            }, cancellationToken);
            writer.WriteEndArray();
            return count;
        });

    /// <summary>Publishes only a fully written, validated export and returns its final file metadata.</summary>
    private ExportOutcome WriteExport(
        string targetPath, bool overwrite, CancellationToken cancellationToken, Func<Stream, int> write)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ExportPaths.Resolve(targetPath);
        var rows = 0;
        try
        {
            AtomicExportFile.Write(fullPath, overwrite, stream => rows = write(stream), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CollectorException(
                ErrorCodes.ExportFailed,
                CollectorTexts.ExportWriteFailed(),
                new Dictionary<string, object?> { ["target_path"] = fullPath },
                inner: ex);
        }

        return new ExportOutcome(fullPath, rows, new FileInfo(fullPath).Length, UtcTimestamp.Truncate(_clock.UtcNow));
    }

    /// <summary>
    /// Writes one sorted snapshot of matching rows and attachments to the temporary export.
    /// WAL lets live writes continue while that snapshot is open. Missing checked rows abort publication.
    /// </summary>
    /// <param name="filter">Same filter as QueryRuns; null exports everything visible.</param>
    /// <param name="sort">History list's sort; null selects the default.</param>
    /// <param name="targets">已封存的勾选目标，使用快照连接的临时表避免 SQL 参数数量上限。</param>
    /// <param name="write">Consumes each hydrated row in order.</param>
    /// <param name="cancellationToken">Cancels the read and any pending publication.</param>
    /// <returns>The number of rows written.</returns>
    private int WriteRows(RunFilter? filter, RunSort? sort, IReadOnlyList<BatchRunTarget>? targets,
        Action<MentorRun> write, CancellationToken cancellationToken) =>
        _database.ReadSnapshot((snapshot, transaction) =>
        {
            var runs = new RunRepository(snapshot);
            if (targets is not null) runs.CreateSelectionTable(targets, transaction, cancellationToken);
            var inlineIds = filter?.RunIds;
            var inlineCount = inlineIds?.Count ?? 0;
            var missingIds = inlineIds is { Count: > 0 } ? inlineIds.ToHashSet(StringComparer.Ordinal) : null;
            var count = runs.VisitExportRows(filter, sort ?? RunSort.Default, transaction,
                restrictToSelection: targets is not null, run =>
                {
                    missingIds?.Remove(run.RunId);
                    write(run);
                }, cancellationToken);
            if (targets is not null && count != targets.Count)
            {
                throw SelectionChanged(targets.Count, count);
            }
            if (missingIds is not null && (count != inlineCount || missingIds.Count != 0))
            {
                throw SelectionChanged(inlineCount, count);
            }
            return count;
        }, cancellationToken);

    private static CollectorException SelectionChanged(int requested, int found) =>
        new(ErrorCodes.NotFound, CollectorTexts.ExportSelectionChanged(),
            new Dictionary<string, object?> { ["requested_count"] = requested, ["matched_count"] = found });

    private static void WriteCsvRow(TextWriter writer, MentorRun run)
    {
        var date = run.EnteredAtUtc ?? run.MatchedAtUtc ?? run.EndedAtUtc;
        var cells = new[]
        {
            date is null ? string.Empty : date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            UtcTimestamp.ToTextOrNull(run.MatchedAtUtc) ?? string.Empty,
            UtcTimestamp.ToTextOrNull(run.EnteredAtUtc) ?? string.Empty,
            UtcTimestamp.ToTextOrNull(run.EndedAtUtc) ?? string.Empty,
            run.DutyName ?? string.Empty,
            run.DutyCategory ?? string.Empty,
            run.JobName ?? string.Empty,
            EnumWire<RunResult>.Format(run.Result),
            run.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            EnumWire<RunSource>.Format(run.Source),
            run.ManuallyCorrected ? "1" : "0",
            run.RunId,
            run.ContentId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            run.JobId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            run.Reflection is null ? string.Empty : ReflectionText.Format(run.Reflection.Mood),
            run.Reflection?.Text ?? string.Empty,
            run.JoinedInProgress is { } joined ? (joined ? "1" : "0") : string.Empty,
            EnumWire<Region>.Format(run.Region),
            run.GameBuild ?? string.Empty,
        };

        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Escape(cells[i], FreeTextColumns[i]));
        }

        writer.WriteLine();
    }

    /// <summary>
    /// RFC 4180 field escaping, plus the one thing RFC 4180 does not cover.
    ///
    /// A field is quoted when it contains a comma, a quote or a line break, and an embedded
    /// quote is doubled. On top of that, a <em>free-text</em> cell that <em>begins</em> with
    /// one of <see cref="FormulaLeaders"/> is prefixed with an apostrophe inside its quotes,
    /// because every mainstream spreadsheet treats such a cell as a formula to evaluate rather
    /// than as text to show. A duty name of <c>=HYPERLINK(...)</c> -- text copied out of the
    /// game, not text this software chose -- would otherwise become a live link in the user's
    /// own export, and the same trick reaches DDE (review finding M5).
    ///
    /// The apostrophe is the conventional spreadsheet escape: Excel and LibreOffice consume it
    /// and display the original text, and a tool parsing the CSV sees one extra leading
    /// character it can strip.
    /// </summary>
    /// <param name="value">Raw cell text.</param>
    /// <param name="freeText">
    /// True for a column whose content is text a person or the game chose. Only such a column
    /// is eligible for formula neutralisation; the default keeps the single-argument form of
    /// this method conservative for any caller outside the exporter.
    /// </param>
    public static string Escape(string value, bool freeText = true)
    {
        ArgumentNullException.ThrowIfNull(value);

        var dangerous = freeText && value.Length > 0 &&
            System.Array.IndexOf(FormulaLeaders, value[0]) >= 0;
        if (!dangerous && value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return value;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return dangerous ? "\"'" + escaped + "\"" : "\"" + escaped + "\"";
    }
}
