using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;

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
    public static Fragment Build(RunFilter? filter, bool forStatistics)
    {
        filter ??= RunFilter.Empty;
        var clauses = new List<string>(12);
        var parameters = new List<KeyValuePair<string, object?>>(16);

        if (forStatistics || !filter.IncludeDeleted)
        {
            clauses.Add("soft_deleted = 0");
        }

        if (forStatistics)
        {
            // "Confirmed mentor" per docs/statistics-definitions.md section 1: an automatic
            // or imported run only counts when the state machine identified the roulette,
            // and a manual run counts by definition.
            clauses.Add("(source = 'MANUAL' OR mentor_roulette_id IS NOT NULL)");
            // A run still in flight is stored as UNKNOWN with no end time and no review flag. It
            // has no outcome yet, so counting it lowers the completion rate for exactly as long
            // as the duty lasts (docs/statistics-definitions.md section 0). An unfinished run
            // that crash recovery handed to the player carries pending_review = 1 and DOES count:
            // it is over, only nobody saw how.
            clauses.Add(
                "NOT (source = 'AUTO_NETWORK' AND result = 'UNKNOWN' AND ended_at_utc IS NULL AND pending_review = 0)");
        }

        var dateColumn = ColumnOf(filter.DateField);
        if (filter.FromUtc is { } from)
        {
            clauses.Add($"{dateColumn} IS NOT NULL AND {dateColumn} >= $from_utc");
            parameters.Add(new("$from_utc", UtcTimestamp.ToText(from)));
        }

        if (filter.ToUtc is { } to)
        {
            clauses.Add($"{dateColumn} IS NOT NULL AND {dateColumn} <= $to_utc");
            parameters.Add(new("$to_utc", UtcTimestamp.ToText(to)));
        }

        AddIntList(clauses, parameters, "content_id", filter.ContentIds, "cid");
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
        }

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            clauses.Add(
                "(IFNULL(duty_name, '') LIKE $text ESCAPE '\\' OR IFNULL(job_name, '') LIKE $text ESCAPE '\\')");
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
