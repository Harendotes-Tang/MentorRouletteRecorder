using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Storage;

/// <summary>
/// Translates a <see cref="RunFilter"/> into a WHERE fragment plus parameters.
///
/// Every value, including the members of the id lists, becomes a bound parameter. No user
/// input is ever concatenated into SQL text; only column names chosen from a closed set of
/// enum values appear literally.
/// </summary>
public static class RunFilterSql
{
    /// <summary>Only explicit local provenance identifies historical imports.</summary>
    public const string ImportedHistoryPredicate = "(source = 'IMPORT' OR " +
        "EXISTS (SELECT 1 FROM run_import_metadata im WHERE im.run_id = mentor_runs.run_id) OR " +
        "EXISTS (SELECT 1 FROM run_revisions ir WHERE ir.run_id = mentor_runs.run_id " +
        "AND ir.revision = 1 AND ir.change_kind = 'IMPORT'))";

    // Date-only provenance describes an archival day, not a fabricated game instant.
    // SQLite validates the normalized value before it may be used by a date projection.
    private const string SourceDay = "CASE " +
        "WHEN im.source_recorded_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]' " +
        "AND length(im.source_recorded_at) = 10 THEN im.source_recorded_at " +
        "WHEN im.source_recorded_at GLOB '[0-9][0-9][0-9][0-9]/[0-9]*/[0-9]*' " +
        "AND im.source_recorded_at NOT GLOB '*[^0-9/]*' " +
        "AND length(im.source_recorded_at) - length(replace(im.source_recorded_at, '/', '')) = 2 " +
        "AND instr(substr(im.source_recorded_at, 6), '/') BETWEEN 2 AND 3 " +
        "AND length(substr(im.source_recorded_at, 6 + instr(substr(im.source_recorded_at, 6), '/'))) BETWEEN 1 AND 2 " +
        "THEN printf('%04d-%02d-%02d', substr(im.source_recorded_at, 1, 4), " +
        "substr(im.source_recorded_at, 6, instr(substr(im.source_recorded_at, 6), '/') - 1), " +
        "substr(im.source_recorded_at, 6 + instr(substr(im.source_recorded_at, 6), '/'))) END";

    private const string ValidSourceDay = "CASE WHEN date(" + SourceDay +
        ", '+0 days') = (" + SourceDay + ") THEN (" + SourceDay + ") END";

    /// <summary>Known game or source instant; never includes a date-only calendar value.</summary>
    private const string HistoryInstant =
        "COALESCE(matched_at_utc, entered_at_utc, ended_at_utc, " +
        "(SELECT im.source_recorded_at_utc FROM run_import_metadata im WHERE im.run_id = mentor_runs.run_id))";

    // A calendar day is compared independently when the client supplies its picker day.
    // Interpreting that day as UTC midnight would lose it in negative UTC offsets.
    private const string HistorySourceDay =
        "CASE WHEN matched_at_utc IS NULL AND entered_at_utc IS NULL AND ended_at_utc IS NULL THEN " +
        "(SELECT CASE WHEN im.source_recorded_at_utc IS NULL THEN " + ValidSourceDay +
        " END FROM run_import_metadata im WHERE im.run_id = mentor_runs.run_id) END";

    /// <summary>Query-only history ordering and trend key; archival calendar days keep their original day.</summary>
    public static string HistoryDateExpression =>
        "COALESCE(" + HistoryInstant + ", (" + HistorySourceDay + ") || 'T00:00:00.000Z')";

    /// <summary>
    /// Local history sorting anchors an archival day to local midnight only within the query.
    /// The desktop and pipe host share the same computer timezone; game/source facts stay null.
    /// Trends independently retain their UTC calendar-day grouping above.
    /// </summary>
    public static string HistorySortDateExpression =>
        "COALESCE(" + HistoryInstant + ", strftime('%Y-%m-%dT%H:%M:%fZ', (" + HistorySourceDay + "), 'utc'))";
    /// <summary>A compiled fragment and the parameters it needs.</summary>
    /// <param name="Where">SQL boolean expression, without the WHERE keyword.</param>
    /// <param name="Parameters">Parameter name to value.</param>
    public sealed record Fragment(string Where, IReadOnlyList<KeyValuePair<string, object?>> Parameters);

    /// <summary>Maps a date field to its column name.</summary>
    /// <param name="field">Field selected by the client.</param>
    public static string ColumnOf(RunDateField field) => field switch
    {
        RunDateField.MatchedAtUtc => "matched_at_utc",
        RunDateField.EndedAtUtc => "ended_at_utc",
        RunDateField.HistoryDate => HistoryDateExpression,
        _ => "entered_at_utc",
    };

    /// <summary>Maps a sort field to its column name.</summary>
    /// <param name="field">Field selected by the client.</param>
    public static string ColumnOf(RunSortField field) => field switch
    {
        RunSortField.MatchedAtUtc => "matched_at_utc",
        RunSortField.EndedAtUtc => "ended_at_utc",
        RunSortField.DurationMs => "duration_ms",
        RunSortField.DutyName => "duty_name",
        RunSortField.HistoryDate => HistorySortDateExpression,
        _ => "entered_at_utc",
    };

    /// <summary>
    /// Builds the fragment.
    /// </summary>
    /// <param name="filter">Filter to translate; null means no constraint.</param>
    /// <param name="forStatistics">
    /// When true, soft-deleted runs are excluded unconditionally and only confirmed mentor
    /// runs are kept, because statistics ignore <c>include_deleted</c> entirely.
    /// </param>
    /// <param name="duties">
    /// Resolves each requested content id to the territories that identify it alone, so a
    /// run that observed only such a territory (capture never back-infers the content id,
    /// docs/data-model.md section 1.4) is still found by the duty's content id. The default
    /// catalogue when omitted.
    /// </param>
    public static Fragment Build(RunFilter? filter, bool forStatistics, DutyCatalog? duties = null)
    {
        filter ??= RunFilter.Empty;
        duties ??= DutyCatalog.Default;
        var clauses = new List<string>(12);
        var parameters = new List<KeyValuePair<string, object?>>(16);

        if (forStatistics || !filter.IncludeDeleted)
        {
            clauses.Add("soft_deleted = 0");
        }

        if (forStatistics)
        {
            // "Confirmed mentor" per docs/statistics-definitions.md section 1: an automatic
            // run counts when capture identified the roulette; imported personal history
            // additionally uses its explicit ownership confirmation and known outcome.
            clauses.Add($"((source = 'MANUAL' AND NOT {ImportedHistoryPredicate}) OR mentor_roulette_id IS NOT NULL OR " +
                "EXISTS (SELECT 1 FROM run_import_metadata im WHERE im.run_id = mentor_runs.run_id AND im.mentor_confirmed = 1))");
            clauses.Add($"NOT ({ImportedHistoryPredicate} AND (pending_review = 1 OR result = 'UNKNOWN'))");
            // A run still in flight is stored as UNKNOWN with no end time and no review flag. It
            // has no outcome yet, so counting it lowers the completion rate for exactly as long
            // as the duty lasts (docs/statistics-definitions.md section 0). An unfinished run
            // that crash recovery handed to the player carries pending_review = 1 and DOES count:
            // it is over, only nobody saw how.
            clauses.Add(
                $"NOT (source = 'AUTO_NETWORK' AND NOT {ImportedHistoryPredicate} " +
                "AND result = 'UNKNOWN' AND ended_at_utc IS NULL AND pending_review = 0)");
        }

        var dateColumn = ColumnOf(filter.DateField);
        if (filter.FromUtc is { } from)
        {
            if (filter.DateField == RunDateField.HistoryDate && filter.HistoryFromDay is { } fromDay)
            {
                clauses.Add($"(({HistoryInstant}) >= $from_utc OR ({HistorySourceDay}) >= $history_from_day)");
                parameters.Add(new("$history_from_day", fromDay));
            }
            else clauses.Add($"{dateColumn} IS NOT NULL AND {dateColumn} >= $from_utc");
            parameters.Add(new("$from_utc", UtcTimestamp.ToText(from)));
        }

        if (filter.ToUtc is { } to)
        {
            if (filter.DateField == RunDateField.HistoryDate && filter.HistoryToDay is { } toDay)
            {
                clauses.Add($"(({HistoryInstant}) <= $to_utc OR ({HistorySourceDay}) <= $history_to_day)");
                parameters.Add(new("$history_to_day", toDay));
            }
            else clauses.Add($"{dateColumn} IS NOT NULL AND {dateColumn} <= $to_utc");
            parameters.Add(new("$to_utc", UtcTimestamp.ToText(to)));
        }

        AddContentIds(clauses, parameters, filter.ContentIds, duties);
        AddTextList(clauses, parameters, "run_id", filter.RunIds, "rid");
        AddIntList(clauses, parameters, "job_id", filter.JobIds, "jid");
        AddTextList(clauses, parameters, "duty_category", filter.DutyCategories, "dcat");
        AddTextList(
            clauses,
            parameters,
            "result",
            filter.Results.Select(EnumWire<RunResult>.Format).ToArray(),
            "res");
        AddTextList(
            clauses,
            parameters,
            "source",
            filter.Sources.Select(EnumWire<RunSource>.Format).ToArray(),
            "src");

        if (filter.CorrectedOnly)
        {
            clauses.Add("manually_corrected = 1");
        }

        if (filter.WithReflection)
        {
            // EXISTS rather than a join: the same fragment is spliced into the run listing,
            // the count query and the statistics queries, and a join would change how many
            // rows each of those sees.
            clauses.Add(
                "EXISTS (SELECT 1 FROM run_reflections WHERE run_reflections.run_id = mentor_runs.run_id)");
        }

        if (filter.PendingReview is { } pendingReview)
        {
            clauses.Add(pendingReview ? "pending_review = 1" : "pending_review = 0");
            if (pendingReview)
                clauses.Add($"NOT {ImportedHistoryPredicate}");
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            clauses.Add(
                "(IFNULL(duty_name, '') LIKE $text ESCAPE '\\' OR IFNULL(job_name, '') LIKE $text ESCAPE '\\' OR IFNULL(note, '') LIKE $text ESCAPE '\\')");
            parameters.Add(new("$text", "%" + EscapeLike(filter.Text) + "%"));
        }

        var where = clauses.Count == 0 ? "1 = 1" : string.Join(" AND ", clauses.Select(c => "(" + c + ")"));
        return new Fragment(where, parameters);
    }

    /// <summary>Binds a fragment onto a command.</summary>
    /// <param name="command">Command to bind onto.</param>
    /// <param name="fragment">Fragment produced by <see cref="Build"/>.</param>
    public static void Bind(SqliteCommand command, Fragment fragment)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(fragment);

        foreach (var (name, value) in fragment.Parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    // content_id IN (...) alone misses the runs that identified the same duty by zone only,
    // which is every automatic run since 1.4.0 that never saw the content id on the wire. A
    // territory that hosts exactly one duty is that duty, so those runs are matched by it;
    // a territory several duties share is never expanded, since it would not say which.
    private static void AddContentIds(
        List<string> clauses,
        List<KeyValuePair<string, object?>> parameters,
        IReadOnlyList<int> contentIds,
        DutyCatalog duties)
    {
        if (contentIds.Count == 0)
        {
            return;
        }

        var byContent = new List<string>();
        AddIntList(byContent, parameters, "content_id", contentIds, "cid");
        var territories = contentIds.SelectMany(duties.UniqueTerritoriesOf).Distinct().ToArray();
        if (territories.Length == 0)
        {
            clauses.AddRange(byContent);
            return;
        }

        var byTerritory = new List<string>();
        AddIntList(byTerritory, parameters, "territory_id", territories, "ctid");
        clauses.Add($"({byContent[0]} OR (content_id IS NULL AND {byTerritory[0]}))");
    }

    private static void AddIntList(
        List<string> clauses,
        List<KeyValuePair<string, object?>> parameters,
        string column,
        IReadOnlyList<int> values,
        string prefix)
    {
        if (values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = "$" + prefix + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add(new(name, values[i]));
        }

        clauses.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static void AddTextList(
        List<string> clauses,
        List<KeyValuePair<string, object?>> parameters,
        string column,
        IReadOnlyList<string> values,
        string prefix)
    {
        if (values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = "$" + prefix + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add(new(name, values[i]));
        }

        clauses.Add($"{column} IN ({string.Join(", ", names)})");
    }

    private static string EscapeLike(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '%' or '_')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
