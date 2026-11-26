using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>
/// Reads and writes <c>mentor_runs</c>. Every statement is parameterised; the only literals
/// in the SQL are column names taken from closed enum sets.
///
/// There is no delete. Removal is <c>soft_deleted = 1</c> written through
/// <see cref="Update"/>, which also enforces the optimistic concurrency check.
/// </summary>
public sealed class RunRepository
{
    private const string SelectionTable = "temp.history_selection_ids";
    private const string Columns =
        "run_id, revision, capture_session_id, region, game_build, protocol_profile_id, " +
        "mentor_roulette_id, content_id, territory_id, duty_name, duty_category, job_id, job_name, role, " +
        "matched_at_utc, entered_at_utc, ended_at_utc, duration_ms, result, detection_confidence, source, " +
        "contributes_to_goal, manually_created, manually_corrected, soft_deleted, " +
        "created_at_utc, updated_at_utc, pending_review, note, duty_source, deleted_at_utc, joined_in_progress, " +
        "profile_id";

    private readonly SqliteDatabase _database;
    private readonly RunReflectionRepository _reflections;
    private readonly RunImportRepository _imports;
    private readonly RunRevisionRepository _revisions;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public RunRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _reflections = new RunReflectionRepository(database);
        _imports = new RunImportRepository(database);
        _revisions = new RunRevisionRepository(database);
    }

    /// <summary>仓库使用的数据库；导出据此打开独立快照，不复用该连接读取全量数据。</summary>
    internal SqliteDatabase Database => _database;

    /// <summary>Inserts a new run. The caller supplies revision 1.</summary>
    /// <param name="run">Run to insert.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Insert(MentorRun run, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO mentor_runs ({Columns}) VALUES (" +
            "$run_id, $revision, $capture_session_id, $region, $game_build, $protocol_profile_id, " +
            "$mentor_roulette_id, $content_id, $territory_id, $duty_name, $duty_category, $job_id, $job_name, $role, " +
            "$matched_at_utc, $entered_at_utc, $ended_at_utc, $duration_ms, $result, $detection_confidence, $source, " +
            "$contributes_to_goal, $manually_created, $manually_corrected, $soft_deleted, " +
            "$created_at_utc, $updated_at_utc, $pending_review, $note, $duty_source, $deleted_at_utc, " +
            "$joined_in_progress, $profile_id);";
        BindRun(command, run);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes a new version of a run. Fails with <c>ERR_REVISION_CONFLICT</c> when the stored
    /// revision is not <paramref name="expectedRevision"/>; a later write never silently wins.
    /// </summary>
    /// <param name="run">Run with its new field values and its new revision.</param>
    /// <param name="expectedRevision">Revision the caller believes is current.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public void Update(MentorRun run, int expectedRevision, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE mentor_runs SET revision = $revision, capture_session_id = $capture_session_id, " +
            "region = $region, game_build = $game_build, protocol_profile_id = $protocol_profile_id, " +
            "mentor_roulette_id = $mentor_roulette_id, content_id = $content_id, territory_id = $territory_id, " +
            "duty_name = $duty_name, duty_category = $duty_category, job_id = $job_id, job_name = $job_name, " +
            "role = $role, matched_at_utc = $matched_at_utc, entered_at_utc = $entered_at_utc, " +
            "ended_at_utc = $ended_at_utc, duration_ms = $duration_ms, result = $result, " +
            "detection_confidence = $detection_confidence, source = $source, " +
            "contributes_to_goal = $contributes_to_goal, manually_created = $manually_created, " +
            "manually_corrected = $manually_corrected, " +
            "soft_deleted = $soft_deleted, created_at_utc = $created_at_utc, " +
            "updated_at_utc = $updated_at_utc, pending_review = $pending_review, note = $note, " +
            "duty_source = $duty_source, deleted_at_utc = $deleted_at_utc, " +
            "joined_in_progress = $joined_in_progress, profile_id = $profile_id " +
            "WHERE run_id = $run_id AND revision = $expected_revision;";
        BindRun(command, run);
        command.Parameters.AddWithValue("$expected_revision", expectedRevision);

        if (command.ExecuteNonQuery() == 1)
        {
            return;
        }

        var current = GetInternal(run.RunId, transaction)
            ?? throw CollectorException.NotFound(run.RunId);

        throw new CollectorException(
            ErrorCodes.RevisionConflict,
            CollectorTexts.MutationRevisionConflict(),
            new Dictionary<string, object?>
            {
                ["run_id"] = run.RunId,
                ["current_revision"] = current.Revision,
                ["expected_revision"] = expectedRevision,
            });
    }

    /// <summary>Reads one run, or null when it does not exist.</summary>
    /// <param name="runId">Run identifier.</param>
    public MentorRun? Get(string runId) => _database.Read(_ => GetInternal(runId, null));

    /// <summary>Reads one run inside an open transaction.</summary>
    /// <param name="runId">Run identifier.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    public MentorRun? GetInternal(string runId, SqliteTransaction? transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM mentor_runs WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId);

        MentorRun? run;
        using (var reader = command.ExecuteReader())
        {
            run = reader.Read() ? Read(reader) : null;
        }

        return run is null ? null : Attach(run, transaction);
    }

    /// <summary>Finds an exact timed duplicate in the destination or among the selected records.</summary>
    /// <remarks>Calendar days and approximate timestamp matches cannot establish a duplicate.</remarks>
    internal (string RunId, string MatchId)? FindRegionMigrationDuplicate(
        IReadOnlyList<MentorRun> runs, Region region, long profileId, SqliteTransaction transaction)
    {
        var index = new Dictionary<MigrationKey, List<MigrationIdentity>>();
        foreach (var run in runs)
        {
            var identity = new MigrationIdentity(run.RunId, run.ContentId, run.DutyName, run.JobId,
                UtcTimestamp.ToTextOrNull(run.MatchedAtUtc), UtcTimestamp.ToTextOrNull(run.EnteredAtUtc),
                UtcTimestamp.ToTextOrNull(run.ImportMetadata?.SourceRecordedAtUtc));
            foreach (var key in identity.Keys())
            {
                if (!index.TryGetValue(key, out var matches))
                    index.Add(key, matches = []);
                if (matches.FirstOrDefault(identity.CompatibleWith) is { } duplicate)
                    return (identity.RunId, duplicate.RunId);
                matches.Add(identity);
            }
        }
        if (index.Count == 0) return null;

        // Read the destination once. A long history of one duty/job must not be cross-joined
        // with every selected record before testing its timestamps.
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.run_id, r.content_id, r.duty_name, r.job_id, r.matched_at_utc, r.entered_at_utc, i.source_recorded_at_utc
            FROM mentor_runs r LEFT JOIN run_import_metadata i ON i.run_id = r.run_id
            WHERE r.region = $region AND (r.profile_id = $profile OR r.profile_id IS NULL)
                AND r.job_id IS NOT NULL
                AND (r.matched_at_utc IS NOT NULL OR r.entered_at_utc IS NOT NULL OR i.source_recorded_at_utc IS NOT NULL);
            """;
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(region));
        command.Parameters.AddWithValue("$profile", profileId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var identity = new MigrationIdentity(reader.GetString(0), GetNullableInt(reader, 1),
                GetNullableString(reader, 2), reader.GetInt32(3), GetNullableString(reader, 4),
                GetNullableString(reader, 5), GetNullableString(reader, 6));
            foreach (var key in identity.Keys())
            {
                if (index.TryGetValue(key, out var matches) && matches.FirstOrDefault(identity.CompatibleWith) is { } duplicate)
                    return (duplicate.RunId, identity.RunId);
            }
        }
        return null;
    }

    private enum MigrationTime { Matched, Entered, Source }
    private readonly record struct MigrationKey(int? ContentId, string? DutyName, int JobId, MigrationTime Kind, string Timestamp);

    private sealed record MigrationIdentity(string RunId, int? ContentId, string? DutyName, int? JobId,
        string? Matched, string? Entered, string? Source)
    {
        public IEnumerable<MigrationKey> Keys()
        {
            if (JobId is not { } job || (ContentId is null && string.IsNullOrWhiteSpace(DutyName)))
                yield break;
            var name = ContentId is null ? DutyName : null;
            if (Matched is not null) yield return new(ContentId, name, job, MigrationTime.Matched, Matched);
            if (Entered is not null) yield return new(ContentId, name, job, MigrationTime.Entered, Entered);
            if (Source is not null) yield return new(ContentId, name, job, MigrationTime.Source, Source);
        }

        public bool CompatibleWith(MigrationIdentity other) =>
            (Matched is null || other.Matched is null || Matched == other.Matched) &&
            (Entered is null || other.Entered is null || Entered == other.Entered);
    }

    /// <summary>
    /// Returns sorted distinct history days of the 显示时区 (<see cref="Domain.Time.DisplayTimeZone"/>)
    /// over the full matching set in one month. Date bounds are deliberately removed; archival
    /// day-only records keep their source day in every zone (their sort anchor is that day's first
    /// instant in the same zone). Missing history times stay NULL and cannot fabricate a selectable day.
    /// </summary>
    public IReadOnlyList<string> QueryCalendarDays(string month, RunFilter? filter)
    {
        var nonDateFilter = (filter ?? RunFilter.Empty) with
        {
            FromUtc = null, ToUtc = null, HistoryFromDay = null, HistoryToDay = null,
        };
        var fragment = RunFilterSql.Build(nonDateFilter, forStatistics: false);
        var day = $"{DisplayZoneSqlFunctions.Day}({RunFilterSql.HistorySortDateExpression})";
        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText = $"SELECT DISTINCT {day} AS calendar_day FROM mentor_runs " +
                $"WHERE {fragment.Where} AND substr({day}, 1, 7) = $month ORDER BY calendar_day;";
            RunFilterSql.Bind(command, fragment);
            command.Parameters.AddWithValue("$month", month);
            var days = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read()) days.Add(reader.GetString(0));
            return (IReadOnlyList<string>)days;
        });
    }

    /// <summary>Lists runs matching a filter, ordered and paged.</summary>
    /// <param name="filter">Filter; null constrains nothing.</param>
    /// <param name="sort">Ordering; null uses the default.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size; capped at 200 by the contract.</param>
    public Page<MentorRun> Query(RunFilter? filter, RunSort? sort, int page, int pageSize) =>
        Query(filter, sort, page, pageSize, transaction: null);

    /// <summary>Reads only the immutable fields needed for a check-all snapshot in existing list order.</summary>
    internal IReadOnlyList<HistorySelectionRow> ReadHistorySelection(
        RunFilter? filter, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var fragment = RunFilterSql.Build(filter, forStatistics: false);
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT run_id, revision, soft_deleted, source, region, capture_session_id " +
            $"FROM mentor_runs WHERE {fragment.Where} ORDER BY {OrderBy(RunSort.Default)};";
        RunFilterSql.Bind(command, fragment);
        var rows = new List<HistorySelectionRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add(new HistorySelectionRow(reader.GetString(0), reader.GetInt32(1), reader.GetBoolean(2),
                EnumWire<RunSource>.Parse(reader.GetString(3)), EnumWire<Region>.Parse(reader.GetString(4)),
                GetNullableString(reader, 5)));
        }
        return rows.AsReadOnly();
    }

    /// <summary>Stages an admitted exact export selection in the short-lived local read snapshot.</summary>
    internal void CreateSelectionTable(IReadOnlyList<BatchRunTarget> targets, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"CREATE TEMP TABLE {SelectionTable} (run_id TEXT PRIMARY KEY) WITHOUT ROWID;";
        command.ExecuteNonQuery();
        command.CommandText = $"INSERT INTO {SelectionTable} (run_id) VALUES ($id);";
        var id = command.Parameters.Add("$id", SqliteType.Text);
        command.Prepare();
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = target.RunId;
            command.ExecuteNonQuery();
        }
    }

    /// <summary>同一查询和附件装配逻辑，可在调用者持有的只读快照事务内分页。</summary>
    /// <param name="filter">与普通列表相同的筛选；null 不增加条件。</param>
    /// <param name="sort">排序；null 使用默认排序。</param>
    /// <param name="page">从 1 开始的页码。</param>
    /// <param name="pageSize">每页数量，1 至 200。</param>
    /// <param name="transaction">当前连接的快照事务；null 保持普通列表读取方式。</param>
    internal Page<MentorRun> Query(
        RunFilter? filter, RunSort? sort, int page, int pageSize, SqliteTransaction? transaction)
    {
        if (page < 1)
        {
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldAtLeast("page", 1), "page");
        }

        if (pageSize < 1 || pageSize > Paging.MaxPageSize)
        {
            throw CollectorException.BadRequest(
                CollectorTexts.IpcFieldOutOfRange("page_size", 1, Paging.MaxPageSize), "page_size");
        }

        var fragment = RunFilterSql.Build(filter, forStatistics: false);
        var orderBy = OrderBy(sort ?? RunSort.Default);

        return _database.Read(_ =>
        {
            int total;
            using (var countCommand = _database.CreateCommand())
            {
                countCommand.Transaction = transaction;
                countCommand.CommandText = $"SELECT COUNT(*) FROM mentor_runs WHERE {fragment.Where};";
                RunFilterSql.Bind(countCommand, fragment);
                total = Convert.ToInt32(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            var items = new List<MentorRun>(Math.Min(pageSize, 128));
            using (var command = _database.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"SELECT {Columns} FROM mentor_runs WHERE {fragment.Where} ORDER BY {orderBy} " +
                    "LIMIT $limit OFFSET $offset;";
                RunFilterSql.Bind(command, fragment);
                command.Parameters.AddWithValue("$limit", pageSize);
                command.Parameters.AddWithValue("$offset", ((long)page - 1) * pageSize);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(Read(reader));
                }
            }

            AttachAll(items, transaction);
            return new Page<MentorRun>(items, page, pageSize, total);
        });
    }

    /// <summary>Sorts matching IDs once and visits rows with their attachments in that order.</summary>
    /// <param name="filter">The same filter as the interactive history list.</param>
    /// <param name="sort">The list's ordering, including its NULL and tie rules.</param>
    /// <param name="transaction">The export's private, read-only snapshot transaction.</param>
    /// <param name="restrictToSelection">Whether to intersect with the staged IDs on this connection.</param>
    /// <param name="visit">Consumes a hydrated row before the transaction closes.</param>
    /// <param name="cancellationToken">Checked while reading IDs and emitting rows.</param>
    /// <returns>The number of visited rows.</returns>
    internal int VisitExportRows(RunFilter? filter, RunSort sort, SqliteTransaction transaction,
        bool restrictToSelection, Action<MentorRun> visit, CancellationToken cancellationToken)
    {
        var fragment = RunFilterSql.Build(filter, forStatistics: false);
        var where = restrictToSelection
            ? $"{fragment.Where} AND run_id IN (SELECT run_id FROM {SelectionTable})"
            : fragment.Where;
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        // Sort only keys so SQLite does not buffer long notes as part of every sort record.
        command.CommandText = $"SELECT run_id FROM mentor_runs WHERE {where} ORDER BY {OrderBy(sort)};";
        RunFilterSql.Bind(command, fragment);
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        var ids = new List<string>(Paging.MaxPageSize);
        var count = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids.Add(reader.GetString(0));
            if (ids.Count == Paging.MaxPageSize)
            {
                VisitBatch();
            }
        }

        VisitBatch();
        return count;

        void VisitBatch()
        {
            if (ids.Count == 0) return;
            var runs = LoadExportBatch(ids, transaction);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visit(runs[id]);
            }
            count += ids.Count;
            ids.Clear();
        }
    }

    /// <summary>Hydrates a bounded set of IDs from the same transaction as the ordered cursor.</summary>
    private Dictionary<string, MentorRun> LoadExportBatch(IReadOnlyList<string> ids, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = $"$id{i}";
            command.Parameters.AddWithValue(parameters[i], ids[i]);
        }
        command.CommandText = $"SELECT {Columns} FROM mentor_runs WHERE run_id IN ({string.Join(',', parameters)});";
        var runs = new List<MentorRun>(ids.Count);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) runs.Add(Read(reader));
        }
        AttachAll(runs, transaction);
        return runs.ToDictionary(run => run.RunId, StringComparer.Ordinal);
    }

    private static string OrderBy(RunSort sort)
    {
        var column = RunFilterSql.ColumnOf(sort.Field);
        var direction = sort.Direction == SortDirection.Asc ? "ASC" : "DESC";
        // Keep NULLs last in both directions and break ties by the stable run identity.
        return $"({column} IS NULL) ASC, {column} {direction}, run_id ASC";
    }

    /// <summary>
    /// Finds the runs another capture session left <em>open</em>, for crash recovery
    /// (docs/state-machine.md section 3.9): automatic runs that never reached an end time,
    /// recorded under a session other than <paramref name="currentSessionId"/>.
    ///
    /// <c>ended_at_utc IS NULL</c> is what makes a run unfinished; <c>result = 'UNKNOWN'</c>
    /// is not. A profile without <c>DUTY_RESULT</c> ends every run as <c>UNKNOWN</c> with
    /// pending review by design (section 3.10, the shipping CN profile), so selecting on the
    /// result would rewrite every finished mentor roulette to INTERRUPTED on the next start.
    ///
    /// The owning session need not still be open: a session can be closed while a run under it
    /// stays open, and such a run is still unfinished work only a human may resolve. A run with
    /// no session at all was created by hand and is left alone.
    /// </summary>
    /// <param name="currentSessionId">Session this process just opened; null matches nothing.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public IReadOnlyList<MentorRun> FindOpenRunsFromOtherSessions(
        string? currentSessionId,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM mentor_runs " +
            "WHERE soft_deleted = 0 AND result = 'UNKNOWN' AND ended_at_utc IS NULL " +
            "AND capture_session_id IS NOT NULL " +
            "AND NOT " + RunFilterSql.ImportedHistoryPredicate + " " +
            "AND ($current IS NULL OR capture_session_id <> $current) " +
            "ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$current", (object?)currentSessionId ?? DBNull.Value);

        var runs = new List<MentorRun>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                runs.Add(Read(reader));
            }
        }

        AttachAll(runs, transaction);
        return runs;
    }

    /// <summary>
    /// Runs recorded automatically under <paramref name="protocolProfileId"/> at or after
    /// <paramref name="sinceUtc"/> (every one when null) that are not deleted and not already pending review:
    /// what a withdrawn shared profile leaves behind (plan §18.4). Runs created by hand carry no profile and
    /// never match.
    /// </summary>
    /// <param name="protocolProfileId">Profile id written on the runs.</param>
    /// <param name="sinceUtc">Earliest creation time to include, or null for all.</param>
    /// <param name="transaction">Enclosing transaction.</param>
    public IReadOnlyList<MentorRun> FindRecordedUnder(string protocolProfileId, DateTimeOffset? sinceUtc, SqliteTransaction transaction)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolProfileId);
        ArgumentNullException.ThrowIfNull(transaction);

        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {Columns} FROM mentor_runs " +
            "WHERE protocol_profile_id = $profile AND soft_deleted = 0 AND pending_review = 0 " +
            "AND ($since IS NULL OR created_at_utc >= $since) " +
            "ORDER BY created_at_utc ASC;";
        command.Parameters.AddWithValue("$profile", protocolProfileId);
        command.Parameters.AddWithValue("$since", sinceUtc is { } since ? UtcTimestamp.ToText(since) : DBNull.Value);

        var runs = new List<MentorRun>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                runs.Add(Read(reader));
            }
        }

        AttachAll(runs, transaction);
        return runs;
    }

    /// <summary>
    /// True when some run recorded under <paramref name="protocolProfileId"/> entered a duty and ended
    /// with its exit observed - not interrupted, disconnected or cancelled before entry. One half of
    /// what ends the watch on a shared profile (the other is that nothing is left to audit, plan §18.4);
    /// reading it from the table lets that survive a restart. Every shared code of a build records under
    /// the same profile id, so <paramref name="sinceUtc"/> narrows it to what one binding recorded, by the
    /// same creation time <see cref="FindRecordedUnder"/> uses (audit 2026-10-03, OE-4).
    /// </summary>
    /// <param name="protocolProfileId">Profile id written on the runs.</param>
    /// <param name="sinceUtc">Earliest creation time to count, or null for all.</param>
    public bool AnyEnteredAndExited(string protocolProfileId, DateTimeOffset? sinceUtc = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolProfileId);
        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM mentor_runs WHERE protocol_profile_id = $profile AND soft_deleted = 0 " +
                "AND entered_at_utc IS NOT NULL AND ended_at_utc IS NOT NULL " +
                "AND result NOT IN ($interrupted, $disconnected, $cancelled) " +
                "AND ($since IS NULL OR created_at_utc >= $since));";
            command.Parameters.AddWithValue("$profile", protocolProfileId);
            command.Parameters.AddWithValue("$since", sinceUtc is { } since ? UtcTimestamp.ToText(since) : DBNull.Value);
            command.Parameters.AddWithValue("$interrupted", EnumWire<RunResult>.Format(RunResult.Interrupted));
            command.Parameters.AddWithValue("$disconnected", EnumWire<RunResult>.Format(RunResult.Disconnected));
            command.Parameters.AddWithValue("$cancelled", EnumWire<RunResult>.Format(RunResult.CancelledBeforeEntry));
            return command.ExecuteScalar() is long found && found == 1;
        });
    }

    /// <summary>
    /// Hydrates <c>MentorRun.Reflection</c> from <c>run_reflections</c>, and
    /// <c>MentorRun.ReviewConfirmed</c> from the run's revisions.
    ///
    /// Done here, at the single place runs are materialised, rather than at each site that
    /// serialises a Run; this is what lets the contract promise that every Run on the wire
    /// carries <c>reflection</c> and <c>review_confirmed</c>.
    /// </summary>
    /// <param name="run">Run just read from the row.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    private MentorRun Attach(MentorRun run, SqliteTransaction? transaction) => run with
    {
        Reflection = _reflections.Get(run.RunId, transaction),
        ImportMetadata = _imports.Get(run.RunId, transaction),
        ReviewConfirmed = _revisions.FindReviewConfirmed([run.RunId], transaction).Contains(run.RunId),
    };

    /// <summary>Hydrates a whole page in one extra query rather than one per row.</summary>
    /// <param name="runs">Runs to hydrate in place.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    private void AttachAll(List<MentorRun> runs, SqliteTransaction? transaction)
    {
        if (runs.Count == 0)
        {
            return;
        }

        var ids = runs.Select(run => run.RunId).ToArray();
        var reflections = _reflections.GetMany(ids, transaction);
        var imports = _imports.GetMany(ids, transaction);
        var confirmed = _revisions.FindReviewConfirmed(ids, transaction);

        for (var i = 0; i < runs.Count; i++)
        {
            if (confirmed.Contains(runs[i].RunId))
            {
                runs[i] = runs[i] with { ReviewConfirmed = true };
            }
            if (reflections.TryGetValue(runs[i].RunId, out var reflection))
            {
                runs[i] = runs[i] with { Reflection = reflection };
            }
            if (imports.TryGetValue(runs[i].RunId, out var metadata))
            {
                runs[i] = runs[i] with { ImportMetadata = metadata };
            }
        }
    }

    private static void BindRun(SqliteCommand command, MentorRun run)
    {
        command.Parameters.AddWithValue("$run_id", run.RunId);
        command.Parameters.AddWithValue("$revision", run.Revision);
        command.Parameters.AddWithValue("$capture_session_id", (object?)run.CaptureSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$region", EnumWire<Region>.Format(run.Region));
        command.Parameters.AddWithValue("$game_build", (object?)run.GameBuild ?? DBNull.Value);
        command.Parameters.AddWithValue("$protocol_profile_id", (object?)run.ProtocolProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$mentor_roulette_id", (object?)run.MentorRouletteId ?? DBNull.Value);
        command.Parameters.AddWithValue("$content_id", (object?)run.ContentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$territory_id", (object?)run.TerritoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$duty_name", (object?)run.DutyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$duty_category", (object?)run.DutyCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$job_id", (object?)run.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$job_name", (object?)run.JobName ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", EnumWire<Role>.Format(run.Role));
        command.Parameters.AddWithValue(
            "$matched_at_utc", (object?)UtcTimestamp.ToTextOrNull(run.MatchedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$entered_at_utc", (object?)UtcTimestamp.ToTextOrNull(run.EnteredAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$ended_at_utc", (object?)UtcTimestamp.ToTextOrNull(run.EndedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration_ms", (object?)run.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", EnumWire<RunResult>.Format(run.Result));
        command.Parameters.AddWithValue(
            "$detection_confidence", EnumWire<DetectionConfidence>.Format(run.DetectionConfidence));
        command.Parameters.AddWithValue("$source", EnumWire<RunSource>.Format(run.Source));
        command.Parameters.AddWithValue("$contributes_to_goal", run.ContributesToGoal ? 1 : 0);
        command.Parameters.AddWithValue("$manually_created", run.ManuallyCreated ? 1 : 0);
        command.Parameters.AddWithValue("$manually_corrected", run.ManuallyCorrected ? 1 : 0);
        command.Parameters.AddWithValue("$soft_deleted", run.SoftDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$created_at_utc", UtcTimestamp.ToText(run.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated_at_utc", UtcTimestamp.ToText(run.UpdatedAtUtc));
        command.Parameters.AddWithValue("$pending_review", run.PendingReview ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)run.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$deleted_at_utc", (object?)UtcTimestamp.ToTextOrNull(run.DeletedAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$duty_source",
            run.DutySource is { } dutySource ? EnumWire<DutySource>.Format(dutySource) : (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$joined_in_progress",
            run.JoinedInProgress is { } joined ? (joined ? 1 : 0) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$profile_id", (object?)run.ProfileId ?? DBNull.Value);
    }

    /// <summary>Materialises a run from a reader positioned on a row of <see cref="Columns"/>.</summary>
    /// <param name="reader">Positioned reader.</param>
    public static MentorRun Read(SqliteDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new MentorRun
        {
            RunId = reader.GetString(0),
            Revision = reader.GetInt32(1),
            CaptureSessionId = GetNullableString(reader, 2),
            Region = EnumWire<Region>.Parse(reader.GetString(3)),
            GameBuild = GetNullableString(reader, 4),
            ProtocolProfileId = GetNullableString(reader, 5),
            MentorRouletteId = GetNullableInt(reader, 6),
            ContentId = GetNullableInt(reader, 7),
            TerritoryId = GetNullableInt(reader, 8),
            DutyName = GetNullableString(reader, 9),
            DutyCategory = GetNullableString(reader, 10),
            JobId = GetNullableInt(reader, 11),
            JobName = GetNullableString(reader, 12),
            Role = EnumWire<Role>.Parse(reader.GetString(13)),
            MatchedAtUtc = UtcTimestamp.ParseOrNull(GetNullableString(reader, 14)),
            EnteredAtUtc = UtcTimestamp.ParseOrNull(GetNullableString(reader, 15)),
            EndedAtUtc = UtcTimestamp.ParseOrNull(GetNullableString(reader, 16)),
            DurationMs = reader.IsDBNull(17) ? null : reader.GetInt64(17),
            Result = EnumWire<RunResult>.Parse(reader.GetString(18)),
            DetectionConfidence = EnumWire<DetectionConfidence>.Parse(reader.GetString(19)),
            Source = EnumWire<RunSource>.Parse(reader.GetString(20)),
            ContributesToGoal = reader.GetInt32(21) == 1,
            ManuallyCreated = reader.GetInt32(22) == 1,
            ManuallyCorrected = reader.GetInt32(23) == 1,
            SoftDeleted = reader.GetInt32(24) == 1,
            CreatedAtUtc = UtcTimestamp.Parse(reader.GetString(25)),
            UpdatedAtUtc = UtcTimestamp.Parse(reader.GetString(26)),
            PendingReview = reader.GetInt32(27) == 1,
            Note = GetNullableString(reader, 28),
            DeletedAtUtc = UtcTimestamp.ParseOrNull(GetNullableString(reader, 30)),
            DutySource = GetNullableString(reader, 29) is { } source
                ? EnumWire<DutySource>.Parse(source)
                : null,
            JoinedInProgress = reader.IsDBNull(31) ? null : reader.GetInt32(31) == 1,
            ProfileId = reader.IsDBNull(32) ? null : reader.GetInt64(32),
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
