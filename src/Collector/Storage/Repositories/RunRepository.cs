using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
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
    private const string Columns =
        "run_id, revision, capture_session_id, region, game_build, protocol_profile_id, " +
        "mentor_roulette_id, content_id, territory_id, duty_name, duty_category, job_id, job_name, role, " +
        "matched_at_utc, entered_at_utc, ended_at_utc, duration_ms, result, detection_confidence, source, " +
        "contributes_to_goal, manually_created, manually_corrected, soft_deleted, " +
        "created_at_utc, updated_at_utc, pending_review, note, duty_source";

    private readonly SqliteDatabase _database;
    private readonly RunReflectionRepository _reflections;

    /// <summary>Creates a repository over an open database.</summary>
    /// <param name="database">Open database.</param>
    public RunRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _reflections = new RunReflectionRepository(database);
    }

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
            "$created_at_utc, $updated_at_utc, $pending_review, $note, $duty_source);";
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
            "duty_source = $duty_source " +
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
            $"该记录已被修改（当前 revision = {current.Revision}），请刷新后重试。",
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

    /// <summary>Lists runs matching a filter, ordered and paged.</summary>
    /// <param name="filter">Filter; null constrains nothing.</param>
    /// <param name="sort">Ordering; null uses the default.</param>
    /// <param name="page">One-based page number.</param>
    /// <param name="pageSize">Page size; capped at 200 by the contract.</param>
    public Page<MentorRun> Query(RunFilter? filter, RunSort? sort, int page, int pageSize)
    {
        if (page < 1)
        {
            throw CollectorException.BadRequest("page 必须大于等于 1。", "page");
        }

        if (pageSize < 1 || pageSize > Paging.MaxPageSize)
        {
            throw CollectorException.BadRequest(
                $"page_size 必须在 1 到 {Paging.MaxPageSize} 之间。", "page_size");
        }

        sort ??= RunSort.Default;
        var fragment = RunFilterSql.Build(filter, forStatistics: false);
        var sortColumn = RunFilterSql.ColumnOf(sort.Field);
        var direction = sort.Direction == SortDirection.Asc ? "ASC" : "DESC";

        return _database.Read(_ =>
        {
            int total;
            using (var countCommand = _database.CreateCommand())
            {
                countCommand.CommandText = $"SELECT COUNT(*) FROM mentor_runs WHERE {fragment.Where};";
                RunFilterSql.Bind(countCommand, fragment);
                total = Convert.ToInt32(countCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }

            var items = new List<MentorRun>(Math.Min(pageSize, 128));
            using (var command = _database.CreateCommand())
            {
                // NULLs last in both directions so that unfinished runs never crowd out the
                // rows a user actually wants to see, then run_id as a stable tie-break.
                command.CommandText =
                    $"SELECT {Columns} FROM mentor_runs WHERE {fragment.Where} " +
                    $"ORDER BY ({sortColumn} IS NULL) ASC, {sortColumn} {direction}, run_id ASC " +
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

            AttachAll(items, null);
            return new Page<MentorRun>(items, page, pageSize, total);
        });
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
    /// True when some run recorded under <paramref name="protocolProfileId"/> entered a duty and ended
    /// with its exit observed - not interrupted, disconnected or cancelled before entry. Shared
    /// calibration keeps verifying a shared profile until this holds; reading it from the table
    /// lets that survive a restart.
    /// </summary>
    /// <param name="protocolProfileId">Profile id written on the runs.</param>
    public bool AnyEnteredAndExited(string protocolProfileId)
    {
        ArgumentException.ThrowIfNullOrEmpty(protocolProfileId);
        return _database.Read(_ =>
        {
            using var command = _database.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM mentor_runs WHERE protocol_profile_id = $profile AND soft_deleted = 0 " +
                "AND entered_at_utc IS NOT NULL AND ended_at_utc IS NOT NULL " +
                "AND result NOT IN ($interrupted, $disconnected, $cancelled));";
            command.Parameters.AddWithValue("$profile", protocolProfileId);
            command.Parameters.AddWithValue("$interrupted", EnumWire<RunResult>.Format(RunResult.Interrupted));
            command.Parameters.AddWithValue("$disconnected", EnumWire<RunResult>.Format(RunResult.Disconnected));
            command.Parameters.AddWithValue("$cancelled", EnumWire<RunResult>.Format(RunResult.CancelledBeforeEntry));
            return command.ExecuteScalar() is long found && found == 1;
        });
    }

    /// <summary>
    /// Hydrates <c>MentorRun.Reflection</c> from <c>run_reflections</c>.
    ///
    /// Done here, at the single place runs are materialised, rather than at each site that
    /// serialises a Run; this is what lets the contract promise that every Run on the wire
    /// carries <c>reflection</c>.
    /// </summary>
    /// <param name="run">Run just read from the row.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    private MentorRun Attach(MentorRun run, SqliteTransaction? transaction) =>
        _reflections.Get(run.RunId, transaction) is { } reflection
            ? run with { Reflection = reflection }
            : run;

    /// <summary>Hydrates a whole page in one extra query rather than one per row.</summary>
    /// <param name="runs">Runs to hydrate in place.</param>
    /// <param name="transaction">Enclosing transaction, or null.</param>
    private void AttachAll(List<MentorRun> runs, SqliteTransaction? transaction)
    {
        if (runs.Count == 0)
        {
            return;
        }

        var reflections = _reflections.GetMany(
            runs.Select(run => run.RunId).ToArray(), transaction);
        if (reflections.Count == 0)
        {
            return;
        }

        for (var i = 0; i < runs.Count; i++)
        {
            if (reflections.TryGetValue(runs[i].RunId, out var reflection))
            {
                runs[i] = runs[i] with { Reflection = reflection };
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
        command.Parameters.AddWithValue(
            "$duty_source",
            run.DutySource is { } dutySource ? EnumWire<DutySource>.Format(dutySource) : (object)DBNull.Value);
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
            DutySource = GetNullableString(reader, 29) is { } source
                ? EnumWire<DutySource>.Parse(source)
                : null,
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? GetNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
