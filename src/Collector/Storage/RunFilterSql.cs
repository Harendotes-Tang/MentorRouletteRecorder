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
        "substr(im.source_recorded_at, 6 + instr(substr(im.source_recorded_at, 6), '/'))) " +
        "WHEN im.source_recorded_at GLOB '[0-9][0-9][0-9][0-9].[0-9]*.[0-9]*' " +
        "AND im.source_recorded_at NOT GLOB '*[^0-9.]*' " +
        "AND length(im.source_recorded_at) - length(replace(im.source_recorded_at, '.', '')) = 2 " +
        "AND instr(substr(im.source_recorded_at, 6), '.') BETWEEN 2 AND 3 " +
        "AND length(substr(im.source_recorded_at, 6 + instr(substr(im.source_recorded_at, 6), '.'))) BETWEEN 1 AND 2 " +
        "THEN printf('%04d-%02d-%02d', substr(im.source_recorded_at, 1, 4), " +
        "substr(im.source_recorded_at, 6, instr(substr(im.source_recorded_at, 6), '.') - 1), " +
        "substr(im.source_recorded_at, 6 + instr(substr(im.source_recorded_at, 6), '.'))) END";

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

        if (forStatistics || (!filter.DeletedOnly && !filter.IncludeDeleted))
        {
            clauses.Add("soft_deleted = 0");
        }
        else if (filter.DeletedOnly)
        {
            clauses.Add("soft_deleted = 1");
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
        if (filter.DutyCategories.Count > 0)
            AddTextList(clauses, parameters, EffectiveDutyCategory(parameters, duties), filter.DutyCategories, "dcat");
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

        if (filter.JoinedInProgress is { } joinedInProgress)
        {
            // NULL is "not seen", which is neither value: an unqualified comparison would
            // already leave it out, but say so explicitly.
            clauses.Add(joinedInProgress
                ? "joined_in_progress = 1"
                : "joined_in_progress = 0");
        }

        var textMatches = new List<string>();
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            textMatches.Add(
                "(IFNULL(duty_name, '') LIKE $text ESCAPE '\\' OR IFNULL(job_name, '') LIKE $text ESCAPE '\\' OR IFNULL(note, '') LIKE $text ESCAPE '\\')");
            parameters.Add(new("$text", "%" + EscapeLike(filter.Text) + "%"));
            AddChineseJobMatch(textMatches, parameters, filter.Text);
        }
        if (!string.IsNullOrWhiteSpace(filter.NameText))
        {
            textMatches.Add(
                "(IFNULL(duty_name, '') LIKE $name_text ESCAPE '\\' OR IFNULL(job_name, '') LIKE $name_text ESCAPE '\\')");
            parameters.Add(new("$name_text", "%" + EscapeLike(filter.NameText) + "%"));
        }
        if (filter.NameExact.Count > 0)
        {
            // Equality, not LIKE: 骑士 must not find 暗黑骑士. One parameter list serves both
            // columns; a NULL name equals nothing.
            var names = BindAll(parameters, filter.NameExact, "nex");
            textMatches.Add($"(duty_name IN ({names}) OR job_name IN ({names}))");
        }
        if (textMatches.Count > 0)
            clauses.Add("(" + string.Join(" OR ", textMatches) + ")");

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

    /// <summary>Adds one catalog-backed job match alongside literal text, never replacing note search.
    /// Name-only history is matched exactly; an explicit different ID is not reclassified here.</summary>
    private static void AddChineseJobMatch(List<string> matches,
        List<KeyValuePair<string, object?>> parameters, string text)
    {
        var job = JobCatalog.Default.FindByChineseSearchText(text);
        if (job is null) return;
        parameters.Add(new("$text_job_id", job.JobId));
        var names = new[] { job.NameZh, job.NameEn, job.Abbreviation }.Concat(job.AliasesZh)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var placeholders = new List<string>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            var parameter = "$text_job_name" + i.ToString(CultureInfo.InvariantCulture);
            placeholders.Add(parameter);
            parameters.Add(new(parameter, names[i]));
        }
        matches.Add("(job_id = $text_job_id OR (job_id IS NULL AND job_name COLLATE NOCASE IN ("
            + string.Join(", ", placeholders) + ")))");
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

    // This CASE is applied before COUNT, LIMIT and calendar aggregation. Its membership
    // comes from the same resolver as display/statistics, never from translated names.
    private static string EffectiveDutyCategory(List<KeyValuePair<string, object?>> parameters, DutyCatalog duties)
    {
        var regional = new List<string>();
        foreach (var region in Enum.GetValues<Region>())
        {
            var identities = new List<string>();
            var content = duties.Categories.AllianceContentIds(region);
            var territories = duties.Categories.AllianceTerritoryIds(region);
            var suffix = ((int)region).ToString(CultureInfo.InvariantCulture);
            AddIntList(identities, parameters, "content_id", content, "dcat_content" + suffix + "_");
            if (territories.Count > 0)
            {
                var byTerritory = new List<string>();
                AddIntList(byTerritory, parameters, "territory_id", territories, "dcat_territory" + suffix + "_");
                identities.Add("(content_id IS NULL AND " + byTerritory[0] + ")");
            }
            if (identities.Count == 0) continue;
            var regionParameter = "$dcat_region" + suffix;
            parameters.Add(new(regionParameter, EnumWire<Region>.Format(region)));
            regional.Add("(region = " + regionParameter + " AND (" + string.Join(" OR ", identities) + "))");
        }
        if (regional.Count == 0) return "duty_category";
        parameters.Add(new("$dcat_raw", DutyCategoryProjection.RaidCategory));
        parameters.Add(new("$dcat_alliance", DutyCategoryProjection.AllianceCategory));
        return "CASE WHEN (duty_category IS NULL OR trim(duty_category, char(9,10,11,12,13,32,133,160,5760,8192,8193,8194,8195,8196,8197,8198,8199,8200,8201,8202,8232,8233,8239,8287,12288)) = '' OR duty_category = $dcat_raw) AND ("
            + string.Join(" OR ", regional) + ") THEN $dcat_alliance ELSE duty_category END";
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

        clauses.Add($"{column} IN ({BindAll(parameters, values, prefix)})");
    }

    // Binds each value as $<prefix><index> and returns the comma-separated names.
    private static string BindAll(
        List<KeyValuePair<string, object?>> parameters,
        IReadOnlyList<string> values,
        string prefix)
    {
        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = "$" + prefix + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add(new(name, values[i]));
        }

        return string.Join(", ", names);
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
