using System.Globalization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Statistics;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Reference;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>Executes the fixed statistics definitions over the local database.</summary>
public sealed class StatisticsRepository
{
    private readonly SqliteDatabase _database;
    private readonly SettingsRepository _settings;
    private readonly JobCatalog _jobs;
    private readonly DutyCatalog _duties;
    private readonly IClock _clock;

    /// <summary>Creates the repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    /// <param name="settings">Achievement settings source.</param>
    /// <param name="jobs">Job display mapping; the default catalogue when omitted.</param>
    /// <param name="duties">Duty display mapping; the default catalogue when omitted.</param>
    /// <param name="clock">
    /// Clock the trend window ends on. Injected so a test can pin "now", like every other
    /// statistic (review finding L-17).
    /// </param>
    public StatisticsRepository(
        SqliteDatabase database,
        SettingsRepository settings,
        JobCatalog? jobs = null,
        DutyCatalog? duties = null,
        IClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(settings);
        _database = database;
        _settings = settings;
        _jobs = jobs ?? JobCatalog.Default;
        _duties = duties ?? DutyCatalog.Default;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>How many day buckets the trend covers.</summary>
    public const int TrendDayBuckets = 30;

    /// <summary>How many week buckets the trend covers.</summary>
    public const int TrendWeekBuckets = 12;

    /// <summary>How many month buckets the trend covers.</summary>
    public const int TrendMonthBuckets = 6;

    /// <summary>Returns the dashboard using the optional listing filter.</summary>
    /// <param name="filter">Listing filter; null means no constraint.</param>
    /// <param name="granularity">Bucket width of the completion trend series.</param>
    public DashboardStatistics GetDashboard(
        RunFilter? filter = null, TrendGranularity granularity = TrendGranularity.Day) =>
        // All four reads happen under one hold of the database gate; taken separately they
        // could straddle a capture commit and describe different databases (review finding
        // L-17). The gate is re-entrant, so the reads below keep gating themselves as well.
        _database.Read(_ => BuildDashboard(filter, granularity));

    private DashboardStatistics BuildDashboard(RunFilter? filter, TrendGranularity granularity)
    {
        var rows = LoadRows(filter);
        var attemptCount = rows.Count(IsAttempt);
        var completedCount = rows.Count(row => row.Result == RunResult.Completed);
        var leaveCount = rows.Count(row => row.Result == RunResult.LeftOrAbandoned && IsAttempt(row));
        var average = AverageDuration(rows);
        var breakdown = BuildBreakdown(rows, attemptCount);

        // Each of the dashboard's reads is its own trip through the database gate; this
        // repository gates itself, see SettingsRepository.GetAchievementSettings.
        var settings = _settings.GetAchievementSettings();

        // Progress deliberately ignores the dashboard date/filter and always uses the full
        // confirmed, live data set.
        var contributingCompleted = CountContributingCompleted();
        // The wire contract remains Int32. A later completion or an older oversized
        // baseline must not make every dashboard request fail; preserve the stored
        // counts and cap only the displayed aggregate at the representable maximum.
        var progress = settings.BaselineCompletedCount + (int)Math.Min(
            contributingCompleted, int.MaxValue - (long)settings.BaselineCompletedCount);

        return new DashboardStatistics(
            attemptCount,
            completedCount,
            settings.BaselineCompletedCount,
            progress,
            settings.GoalCount,
            Math.Max(settings.GoalCount - progress, 0),
            Ratio(completedCount, attemptCount),
            Ratio(leaveCount, attemptCount),
            average,
            breakdown,
            rows.Count(row => row.PendingReview),
            GetTrend(filter, granularity));
    }

    /// <summary>
    /// Counts confirmed, non-deleted completions that contribute to the achievement.
    /// Baseline validation shares this query with the dashboard so both use the same
    /// formal-record boundary. The caller may supply its update transaction.
    /// </summary>
    /// <param name="transaction">Existing baseline update transaction, or null for a gated read.</param>
    internal long CountContributingCompleted(SqliteTransaction? transaction = null)
    {
        if (transaction is null)
        {
            return _database.Read(_ => CountContributingCompletedCore(null));
        }

        return CountContributingCompletedCore(transaction);
    }

    private long CountContributingCompletedCore(SqliteTransaction? transaction)
    {
        var filter = RunFilterSql.Build(null, forStatistics: true);
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM mentor_runs WHERE {filter.Where} "
            + "AND result = 'COMPLETED' AND contributes_to_goal = 1;";
        RunFilterSql.Bind(command, filter);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the completion trend defined by docs/statistics-definitions.md section 12.1.
    ///
    /// The grouping key is computed by SQLite from <c>matched_at_utc</c>, which is stored as
    /// UTC ISO-8601, so every bucket boundary is midnight UTC. The Desktop renders each
    /// <c>start_utc</c> in local time; it does not re-bucket, because two clients in two time
    /// zones asking the same Collector must see the same series.
    ///
    /// Only COMPLETED runs are counted, soft-deleted rows are excluded unconditionally, and
    /// the caller's filter is applied on top of the trailing window.
    /// </summary>
    /// <param name="filter">Listing filter; null means no constraint.</param>
    /// <param name="granularity">Bucket width.</param>
    public TrendSeries GetTrend(
        RunFilter? filter = null, TrendGranularity granularity = TrendGranularity.Day)
    {
        var starts = BucketStarts(granularity, _clock.UtcNow);
        var counts = LoadTrendCounts(filter, granularity, starts[0]);

        var buckets = new List<TrendBucket>(starts.Count);
        foreach (var start in starts)
        {
            var key = BucketKey(start, granularity);
            buckets.Add(new TrendBucket(start, counts.TryGetValue(key, out var count) ? count : 0));
        }

        return new TrendSeries(granularity, buckets);
    }

    /// <summary>
    /// The inclusive start of every bucket of a series that ends with the one containing
    /// <paramref name="now"/>, oldest first.
    /// </summary>
    /// <param name="granularity">Bucket width.</param>
    /// <param name="now">Instant the series ends on.</param>
    public static IReadOnlyList<DateTimeOffset> BucketStarts(
        TrendGranularity granularity, DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var starts = new List<DateTimeOffset>();

        switch (granularity)
        {
            case TrendGranularity.Week:
                // ISO weeks start on Monday. DayOfWeek.Sunday is 0, so Sunday is 6 days in.
                var offset = ((int)today.UtcDateTime.DayOfWeek + 6) % 7;
                var thisWeek = today.AddDays(-offset);
                for (var i = TrendWeekBuckets - 1; i >= 0; i--)
                {
                    starts.Add(thisWeek.AddDays(-7 * i));
                }

                break;

            case TrendGranularity.Month:
                var thisMonth = new DateTimeOffset(
                    new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
                for (var i = TrendMonthBuckets - 1; i >= 0; i--)
                {
                    starts.Add(thisMonth.AddMonths(-i));
                }

                break;

            default:
                for (var i = TrendDayBuckets - 1; i >= 0; i--)
                {
                    starts.Add(today.AddDays(-i));
                }

                break;
        }

        return starts;
    }

    private IReadOnlyDictionary<string, int> LoadTrendCounts(
        RunFilter? filter, TrendGranularity granularity, DateTimeOffset windowStart)
    {
        var fragment = RunFilterSql.Build(filter, forStatistics: true);

        // The bucket expression is chosen from a closed set of enum values, never composed
        // from anything a client sent; every value in the query is a bound parameter.
        var bucket = granularity switch
        {
            TrendGranularity.Week => "date(substr(matched_at_utc, 1, 10), '-6 days', 'weekday 1')",
            TrendGranularity.Month => "substr(matched_at_utc, 1, 7) || '-01'",
            _ => "substr(matched_at_utc, 1, 10)",
        };

        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                $"SELECT {bucket} AS bucket_start, COUNT(*) FROM mentor_runs WHERE (" +
                fragment.Where +
                ") AND result = 'COMPLETED' AND matched_at_utc IS NOT NULL " +
                "AND matched_at_utc >= $trend_from GROUP BY bucket_start;";
            RunFilterSql.Bind(command, fragment);
            command.Parameters.AddWithValue("$trend_from", UtcTimestamp.ToText(windowStart));

            using var reader = command.ExecuteReader();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    counts[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            return (IReadOnlyDictionary<string, int>)counts;
        });
    }

    private static string BucketKey(DateTimeOffset start, TrendGranularity granularity) =>
        granularity == TrendGranularity.Month
            ? start.UtcDateTime.ToString("yyyy-MM-01", CultureInfo.InvariantCulture)
            : start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Returns all six result buckets, including zero-count buckets.
    ///
    /// Zero-count buckets are part of the contract: the enum is enumerated rather than the
    /// rows grouped, so a never-recorded result still arrives as an explicit 0
    /// (docs/statistics-definitions.md section 9).
    /// </summary>
    public ResultStatistics GetResultStats(RunFilter? filter = null)
    {
        var rows = LoadRows(filter);
        return BuildBreakdown(rows, rows.Count(IsAttempt));
    }

    /// <summary>
    /// Aggregates by duty identity, never by mutable display name.
    ///
    /// The key is the observed <c>content_id</c> when the run has one and the observed
    /// <c>territory_id</c> otherwise; capture does not back-infer a content id from a
    /// territory (review finding M-5), so grouping on <c>content_id</c> alone would fold every
    /// territory-identified run into one nameless "unknown duty" bucket. Territory keys live
    /// in their own space so they cannot collide with content ids, and the wire
    /// <c>content_id</c> of such a group stays null.
    /// </summary>
    /// <param name="filter">Listing filter; null means no constraint.</param>
    public IReadOnlyList<DungeonStatisticsRow> GetDungeonStats(RunFilter? filter = null)
    {
        return LoadRows(filter)
            .GroupBy(DutyKey)
            .Select(group =>
            {
                var attempts = group.Count(IsAttempt);
                var completed = group.Count(row => row.Result == RunResult.Completed);
                var recent = group.OrderByDescending(row => row.UpdatedAtUtc)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(row.DutyName));
                var region = recent?.Region ?? group.First().Region;
                var mapped = group.Key.ContentId is { } contentId
                    ? _duties.Find(contentId, region)
                    : _duties.FindByTerritory(group.Key.TerritoryId, region);
                return new DungeonStatisticsRow(
                    group.Key.ContentId,
                    recent?.DutyName ?? mapped?.LocalizedName ?? DutyCatalog.UnknownDutyName,
                    recent?.DutyCategory ?? mapped?.DutyCategory,
                    attempts,
                    completed,
                    Ratio(completed, attempts),
                    AverageDuration(group),
                    // MAX(matched_at_utc): when this duty was last matched into, not when its
                    // row was last touched, so a correction does not make an old duty recent.
                    group.Max(row => row.MatchedAtUtc));
            })
            .OrderByDescending(row => row.AttemptCount)
            .ThenBy(row => row.ContentId)
            .ThenBy(row => row.DutyName, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Aggregation key of one run: <c>content_id</c> when observed, otherwise
    /// <c>territory_id</c>, and (null, null) for a run with no duty identity at all.
    /// </summary>
    /// <param name="row">Row read for the statistics.</param>
    private static (int? ContentId, int? TerritoryId) DutyKey(StatRow row) =>
        row.ContentId is { } contentId ? (contentId, (int?)null) : (null, row.TerritoryId);

    /// <summary>Aggregates by job id, keeping null as a visible unknown group.</summary>
    public IReadOnlyList<JobStatisticsRow> GetJobStats(RunFilter? filter = null)
    {
        return LoadRows(filter)
            .GroupBy(row => row.JobId)
            .Select(group =>
            {
                var attempts = group.Count(IsAttempt);
                var completed = group.Count(row => row.Result == RunResult.Completed);
                var job = _jobs.Find(group.Key);
                return new JobStatisticsRow(
                    group.Key,
                    job?.NameZh ?? JobCatalog.UnknownJobName,
                    job?.Role ?? Role.Unknown,
                    attempts,
                    completed,
                    Ratio(completed, attempts),
                    AverageDuration(group));
            })
            .OrderByDescending(row => row.AttemptCount)
            .ThenBy(row => row.JobId)
            .ToArray();
    }

    private IReadOnlyList<StatRow> LoadRows(RunFilter? filter)
    {
        var fragment = RunFilterSql.Build(filter, forStatistics: true);
        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                "SELECT content_id, job_id, duty_name, duty_category, entered_at_utc, ended_at_utc, " +
                "duration_ms, result, detection_confidence, contributes_to_goal, manually_corrected, " +
                "region, updated_at_utc, pending_review, matched_at_utc, territory_id " +
                "FROM mentor_runs WHERE " + fragment.Where + ";";
            RunFilterSql.Bind(command, fragment);
            using var reader = command.ExecuteReader();
            var rows = new List<StatRow>();
            while (reader.Read())
            {
                rows.Add(new StatRow(
                    reader.IsDBNull(0) ? null : reader.GetInt32(0),
                    reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : UtcTimestamp.Parse(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : UtcTimestamp.Parse(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    EnumWire<RunResult>.Parse(reader.GetString(7)),
                    EnumWire<DetectionConfidence>.Parse(reader.GetString(8)),
                    reader.GetInt32(9) == 1,
                    reader.GetInt32(10) == 1,
                    EnumWire<Region>.Parse(reader.GetString(11)),
                    UtcTimestamp.Parse(reader.GetString(12)),
                    reader.GetInt32(13) == 1,
                    reader.IsDBNull(14) ? null : UtcTimestamp.Parse(reader.GetString(14)),
                    reader.IsDBNull(15) ? null : reader.GetInt32(15)));
            }

            return (IReadOnlyList<StatRow>)rows;
        });
    }

    private static ResultStatistics BuildBreakdown(IReadOnlyList<StatRow> rows, int attemptCount)
    {
        var buckets = Enum.GetValues<RunResult>()
            .Select(result =>
            {
                var count = rows.Count(row => row.Result == result);
                var share = result == RunResult.CancelledBeforeEntry ? null : Ratio(count, attemptCount);
                return new ResultBucket(result, count, share);
            })
            .ToArray();
        return new ResultStatistics(attemptCount, buckets);
    }

    private static bool IsAttempt(StatRow row) => row.EnteredAtUtc is not null;

    private static double? AverageDuration(IEnumerable<StatRow> rows)
    {
        var values = rows
            .Where(row => row.Result == RunResult.Completed &&
                          row.DurationMs is >= 0 &&
                          row.EnteredAtUtc is not null &&
                          row.EndedAtUtc is not null &&
                          row.EnteredAtUtc <= row.EndedAtUtc)
            .Select(row => (double)row.DurationMs!.Value)
            .ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? Ratio(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;

    private sealed record StatRow(
        int? ContentId,
        int? JobId,
        string? DutyName,
        string? DutyCategory,
        DateTimeOffset? EnteredAtUtc,
        DateTimeOffset? EndedAtUtc,
        long? DurationMs,
        RunResult Result,
        DetectionConfidence Confidence,
        bool ContributesToGoal,
        bool ManuallyCorrected,
        Region Region,
        DateTimeOffset UpdatedAtUtc,
        bool PendingReview,
        DateTimeOffset? MatchedAtUtc,
        int? TerritoryId);
}
