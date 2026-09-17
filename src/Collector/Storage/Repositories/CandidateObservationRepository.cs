using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Protocol.Parsing;
using MentorRecorder.Collector.Storage.Mutations;

namespace MentorRecorder.Collector.Storage.Repositories;

/// <summary>独立候选观测及最近一次人工核对；Observation 不包含正式事件或字段解释。</summary>
public sealed record CandidateObservationEntry(
    CandidateObservation Observation, string? ReviewVerdict, string? ReviewNote, DateTimeOffset? ReviewedAtUtc);

/// <summary>可幂等重放的核对结果；不保留备注或任何 payload。</summary>
public sealed record CandidateReviewResult(string ObservationId, string ReviewVerdict, DateTimeOffset ReviewedAtUtc);

/// <summary>一次独立核对历史。活跃观测的历史只追加，保留期清理时随观测删除。</summary>
public sealed record CandidateReviewEntry(
    string ReviewId, string ObservationId, string RequestId, string Verdict, string? Note, DateTimeOffset ReviewedAtUtc);

/// <summary>同一事务取得的完整候选证据；供专用导出使用，不进入诊断或正式统计。</summary>
public sealed record CandidateEvidenceSnapshot(
    IReadOnlyList<CandidateObservationEntry> Observations, IReadOnlyList<CandidateReviewEntry> Reviews);

/// <summary>
/// 候选账本唯一写入口。每次添加在数据库事务内重查显式开关，保留至多 30 天、20 000 行。
/// 查询、计数及导出也清理过期数据；所有写入仅涉及候选表与既有 IPC 幂等表。
/// </summary>
public sealed class CandidateObservationRepository
{
    public const int MaxObservations = 20_000;
    public const int RetentionDays = 30;
    public const int MaxReviewNoteLength = 2000;
    private const string ReviewMessage = "ReviewCandidateObservation";
    private const string Columns =
        "observation_id, capture_session_id, profile_id, hypothesis_name, group_name, direction, " +
        "opcode, payload_length, payload_hash12, connection_tag, observed_at_utc, t_ms, " +
        "first_observed_at_utc, last_observed_at_utc, first_t_ms, last_t_ms, " +
        "review_verdict, review_note, reviewed_at_utc, payload_hex, occurrences";
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly SettingsRepository _settings;
    private readonly IdempotencyRepository _idempotency;

    public CandidateObservationRepository(SqliteDatabase database, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(clock);
        _database = database;
        _clock = clock;
        _settings = new SettingsRepository(database, clock);
        _idempotency = new IdempotencyRepository(database, clock);
    }

    /// <summary>添加候选；关闭、重复、过期或被容量清理淘汰时返回 false，绝不更新已有观测。</summary>
    public bool Add(CandidateObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        RequireUuid(observation.ObservationId, "observation_id");
        RequireUuid(observation.CaptureSessionId, "capture_session_id");
        return _database.RunInTransaction(tx =>
        {
            Prune(tx);
            if (!Enabled(tx) || observation.ObservedAtUtc < Cutoff()) return false;
            // 在插入事务里再次读取白名单，避免观察器排队后开关变化导致旧负载仍被保存。
            var permitted = observation with { PayloadHex = ResearchPayload(observation, tx) };
            using var command = Command(tx, "INSERT INTO candidate_observations (" + Columns + ") VALUES (" +
                "$id, $session, $profile, $name, $group, $direction, $opcode, $length, $hash, $connection, " +
                "$observed, $t, $first, $last, $first_t, $last_t, NULL, NULL, NULL, $payload, $occurrences) " +
                "ON CONFLICT(observation_id) DO NOTHING;");
            BindObservation(command, permitted);
            var inserted = command.ExecuteNonQuery() == 1;
            Prune(tx);
            return inserted && GetInternal(observation.ObservationId, tx) is not null;
        });
    }

    /// <summary>按观察时间倒序分页；时间区间两端包含，page 从 1 开始，pageSize 为 1–200。</summary>
    public Page<CandidateObservationEntry> Query(
        string? sessionId = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null,
        int page = 1, int pageSize = 50)
    {
        if (sessionId is not null) RequireUuid(sessionId, "session_id");
        if (page < 1) throw CollectorException.BadRequest("page 必须大于等于 1。", "payload.page");
        if (pageSize is < 1 or > Paging.MaxPageSize)
            throw CollectorException.BadRequest("page_size 必须在 1–200 之间。", "payload.page_size");
        if (fromUtc is { } from && toUtc is { } to && from > to)
            throw CollectorException.BadRequest("from_utc 不得晚于 to_utc。", "payload.from_utc");
        return _database.RunInTransaction(tx =>
        {
            Prune(tx);
            const string where = "WHERE ($session IS NULL OR capture_session_id = $session) " +
                "AND ($from IS NULL OR observed_at_utc >= $from) AND ($to IS NULL OR observed_at_utc <= $to)";
            using var count = Command(tx, "SELECT COUNT(*) FROM candidate_observations " + where);
            BindFilter(count, sessionId, fromUtc, toUtc);
            var total = Convert.ToInt32(count.ExecuteScalar());
            using var command = Command(tx, "SELECT " + Columns + " FROM candidate_observations " + where +
                " ORDER BY observed_at_utc DESC, observation_id DESC LIMIT $limit OFFSET $offset;");
            BindFilter(command, sessionId, fromUtc, toUtc);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", ((long)page - 1) * pageSize);
            return new Page<CandidateObservationEntry>(ReadItems(command), page, pageSize, total);
        });
    }

    /// <summary>返回仍在保留范围内的总数；可限制为一个捕获会话。</summary>
    public int Count(string? sessionId = null)
    {
        if (sessionId is not null) RequireUuid(sessionId, "session_id");
        return _database.RunInTransaction(tx =>
        {
            Prune(tx);
            using var command = Command(tx, "SELECT COUNT(*) FROM candidate_observations " +
                "WHERE $session IS NULL OR capture_session_id = $session;");
            Bind(command, "$session", sessionId);
            return Convert.ToInt32(command.ExecuteScalar());
        });
    }

    /// <summary>查找未过期的观测；不存在或已淘汰返回 null。</summary>
    public CandidateObservationEntry? Get(string id)
    {
        RequireUuid(id, "observation_id");
        return _database.RunInTransaction(tx => { Prune(tx); return GetInternal(id, tx); });
    }

    /// <summary>事务内更新核对摘要并追加历史；相同 requestId 重放原始结果，异内容复用拒绝。</summary>
    public CandidateReviewResult Review(string requestId, string id, string verdict, string? note = null)
    {
        RequireUuid(requestId, "request_id");
        RequireUuid(id, "observation_id");
        if (verdict is not ("CORRECT" or "WRONG" or "UNSURE"))
            throw CollectorException.BadRequest("verdict 只能为 CORRECT / WRONG / UNSURE。", "payload.verdict");
        if (note?.Length > MaxReviewNoteLength)
            throw CollectorException.BadRequest("核对备注最多 2000 字符。", "payload.note");
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var fingerprint = MutationSnapshotCodec.Fingerprint(ReviewMessage, id, verdict, note);
        return _database.RunInTransaction(tx =>
        {
            Prune(tx);
            if (_idempotency.TryGetResponse(requestId, tx) is { } stored)
                return Replay(stored, fingerprint);
            // 幂等行 24 小时后被清理，但 candidate_reviews.request_id 是 UNIQUE 且只追加。
            // 迟到的重放不再撞约束变成 ERR_INTERNAL，而是按契约码回答（评审 M-7）。
            if (_idempotency.WasAppliedBeforePrune(requestId, tx))
                throw new CollectorException(ErrorCodes.IdempotencyConflict,
                    "该请求编号已经执行过，但可重放的结果已超出保留期，请用新的请求编号重试。",
                    new Dictionary<string, object?>
                    {
                        ["conflict"] = "idempotency",
                        ["request_id"] = requestId,
                        ["reason"] = "RESPONSE_EXPIRED",
                    },
                    field: "request_id");
            if (GetInternal(id, tx) is null)
                throw new CollectorException(ErrorCodes.CandidateObservationNotFound,
                    "该候选观测不存在或已超出保留期，请刷新后重试。",
                    new Dictionary<string, object?> { ["observation_id"] = id });

            var now = UtcTimestamp.Truncate(_clock.UtcNow);
            using (var update = Command(tx, "UPDATE candidate_observations SET review_verdict = $verdict, " +
                "review_note = $note, reviewed_at_utc = $reviewed WHERE observation_id = $id;"))
            {
                BindReview(update, id, verdict, note, now);
                update.ExecuteNonQuery();
            }
            using (var append = Command(tx, "INSERT INTO candidate_reviews " +
                "(review_id, observation_id, request_id, verdict, note, reviewed_at_utc) " +
                "VALUES ($review_id, $id, $request_id, $verdict, $note, $reviewed);"))
            {
                BindReview(append, id, verdict, note, now);
                Bind(append, "$review_id", Guid.NewGuid().ToString("D"));
                Bind(append, "$request_id", requestId);
                append.ExecuteNonQuery();
            }
            var result = new CandidateReviewResult(id, verdict, now);
            _idempotency.Store(requestId, ReviewMessage,
                MutationSnapshotCodec.SerializeOther(new ReviewSnapshot(fingerprint, result)), tx);
            return result;
        });
    }

    /// <summary>清理后原子读取完整账本和核对历史，避免导出包含不匹配的摘要与历史。</summary>
    public CandidateEvidenceSnapshot ReadEvidence()
    {
        CandidateEvidenceSnapshot? snapshot = null;
        VisitEvidence((_, _, observations, reviews) =>
            snapshot = new CandidateEvidenceSnapshot(observations.ToArray(), reviews.ToArray()));
        return snapshot!;
    }

    /// <summary>
    /// 在同一清理事务内同步访问计数及逐行证据，供流式导出使用，避免完整账本与历史同时驻留内存。
    /// 回调必须先枚举 observations、关闭其枚举器，再枚举 reviews；两者各自拥有独立命令和 reader。
    /// 禁止将枚举或枚举器带出回调、异步使用或在回调内再次调用仓储；回调异常使本次清理回滚。
    /// </summary>
    /// <param name="write">同步写出回调：观测数、核对历史数、观测序列、历史序列。</param>
    public void VisitEvidence(Action<int, int, IEnumerable<CandidateObservationEntry>, IEnumerable<CandidateReviewEntry>> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        VisitResearchEvidence((_, observations, reviews, items, history) => write(observations, reviews, items, history));
    }

    /// <summary>
    /// 与 VisitEvidence 相同的同步事务约束，额外提供快照内实际保存负载的 opcode 列表。
    /// 列表从保留期清理后的数据派生；不依赖当前开关，关闭研究后旧证据仍需准确声明隐私例外。
    /// 仅专用证据导出可读取并写出负载；禁止把行或枚举逃逸到日志、诊断、普通 IPC 或回调之外。
    /// </summary>
    public void VisitResearchEvidence(Action<IReadOnlyList<string>, int, int,
        IEnumerable<CandidateObservationEntry>, IEnumerable<CandidateReviewEntry>> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        _database.RunInTransaction(tx =>
        {
            Prune(tx);
            var payloadOpcodes = new List<string>();
            using (var command = Command(tx, "SELECT DISTINCT opcode FROM candidate_observations " +
                "WHERE payload_hex IS NOT NULL ORDER BY opcode;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) payloadOpcodes.Add("0x" + reader.GetInt32(0).ToString("x4",
                    System.Globalization.CultureInfo.InvariantCulture));
            int observationCount;
            int reviewCount;
            using (var count = Command(tx, "SELECT COUNT(*) FROM candidate_observations;"))
                observationCount = Convert.ToInt32(count.ExecuteScalar());
            using (var count = Command(tx, "SELECT COUNT(*) FROM candidate_reviews;"))
                reviewCount = Convert.ToInt32(count.ExecuteScalar());
            var active = true;
            try
            {
                write(payloadOpcodes, observationCount, reviewCount,
                    StreamObservations(tx, () => active), StreamReviews(tx, () => active));
            }
            finally { active = false; }
        });
    }

    private IEnumerable<CandidateObservationEntry> StreamObservations(SqliteTransaction transaction, Func<bool> active)
    {
        RequireEvidenceScope(active);
        using var command = Command(transaction, "SELECT " + Columns + " FROM candidate_observations " +
            "ORDER BY observed_at_utc DESC, observation_id DESC;");
        using var reader = command.ExecuteReader();
        while (true)
        {
            RequireEvidenceScope(active);
            if (!reader.Read()) yield break;
            yield return Read(reader);
        }
    }

    private IEnumerable<CandidateReviewEntry> StreamReviews(SqliteTransaction transaction, Func<bool> active)
    {
        RequireEvidenceScope(active);
        using var command = Command(transaction, "SELECT review_id, observation_id, request_id, verdict, note, " +
            "reviewed_at_utc FROM candidate_reviews ORDER BY reviewed_at_utc, rowid;");
        using var reader = command.ExecuteReader();
        while (true)
        {
            RequireEvidenceScope(active);
            if (!reader.Read()) yield break;
            yield return new CandidateReviewEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), Text(reader, 4), UtcTimestamp.Parse(reader.GetString(5)));
        }
    }

    private static void RequireEvidenceScope(Func<bool> active)
    {
        if (!active()) throw new InvalidOperationException("候选证据必须在 VisitEvidence 的同步回调内枚举。");
    }

    private DateTimeOffset Cutoff() => UtcTimestamp.Truncate(_clock.UtcNow).AddDays(-RetentionDays);

    private void Prune(SqliteTransaction transaction)
    {
        using (var expired = Command(transaction,
            "DELETE FROM candidate_observations WHERE observed_at_utc < $cutoff;"))
        {
            Bind(expired, "$cutoff", UtcTimestamp.ToText(Cutoff()));
            expired.ExecuteNonQuery();
        }
        using var count = Command(transaction, "SELECT COUNT(*) FROM candidate_observations;");
        var excess = Convert.ToInt64(count.ExecuteScalar()) - MaxObservations;
        if (excess <= 0) return;
        using var command = Command(transaction, "DELETE FROM candidate_observations WHERE observation_id IN " +
            "(SELECT observation_id FROM candidate_observations ORDER BY observed_at_utc, observation_id LIMIT $excess);");
        command.Parameters.AddWithValue("$excess", excess);
        command.ExecuteNonQuery();
    }

    private bool Enabled(SqliteTransaction transaction)
    {
        var raw = _settings.GetSetting(CaptureSettingsStore.CandidateValidationSetting, transaction);
        if (raw is null) return false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>写前最后一道负载门禁；不合法或未列入当前白名单只丢弃负载，保留候选元数据。</summary>
    private string? ResearchPayload(CandidateObservation observation, SqliteTransaction transaction)
    {
        if (observation.PayloadHex is not { } hex || observation.Length is not (>= 0 and <= Capture.ResearchPayloadPolicy.MaxPayloadBytes) ||
            observation.Direction is not ("S2C" or "C2S") || observation.Opcode is not { } opcode ||
            hex.Length != observation.Length.Value * 2 ||
            hex.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            return null;
        var raw = _settings.GetSetting(CaptureSettingsStore.ResearchPayloadOpcodesSetting, transaction);
        if (raw is null) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var canonical = "0x" + opcode.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
            return document.RootElement.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                string.Equals(item.GetString(), canonical, StringComparison.OrdinalIgnoreCase)) ? hex : null;
        }
        catch (JsonException) { return null; }
    }

    private CandidateObservationEntry? GetInternal(string id, SqliteTransaction transaction)
    {
        using var command = Command(transaction, "SELECT " + Columns + " FROM candidate_observations WHERE observation_id = $id;");
        Bind(command, "$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static CandidateReviewResult Replay(string stored, string fingerprint)
    {
        var snapshot = MutationSnapshotCodec.DeserializeOther<ReviewSnapshot>(stored)
            ?? throw new CollectorException(ErrorCodes.Internal, "幂等记录已损坏，无法安全重放核对请求。");
        if (snapshot.Fingerprint != fingerprint)
            throw new CollectorException(ErrorCodes.IdempotencyConflict,
                "同一个 request_id 被用于内容不同的请求，核对未修改。",
                new Dictionary<string, object?> { ["conflict"] = "idempotency" }, field: "request_id");
        return snapshot.Result ?? throw new CollectorException(ErrorCodes.Internal, "核对幂等结果缺失。");
    }

    private static IReadOnlyList<CandidateObservationEntry> ReadItems(SqliteCommand command)
    {
        var items = new List<CandidateObservationEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) items.Add(Read(reader));
        return items;
    }

    private static CandidateObservationEntry Read(SqliteDataReader reader) => new(new CandidateObservation(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetInt32(7), Text(reader, 8), reader.GetString(9),
        UtcTimestamp.Parse(reader.GetString(10)), reader.GetInt64(11),
        UtcTimestamp.ParseOrNull(Text(reader, 12)), UtcTimestamp.ParseOrNull(Text(reader, 13)),
        reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.IsDBNull(15) ? null : reader.GetInt64(15), Text(reader, 19),
        reader.GetInt32(20)),
        Text(reader, 16), Text(reader, 17), UtcTimestamp.ParseOrNull(Text(reader, 18)));

    private static void BindObservation(SqliteCommand command, CandidateObservation observation)
    {
        Bind(command, "$id", observation.ObservationId);
        Bind(command, "$session", observation.CaptureSessionId);
        Bind(command, "$profile", observation.ProfileId);
        Bind(command, "$name", observation.HypothesisName);
        Bind(command, "$group", observation.Group);
        Bind(command, "$direction", observation.Direction);
        Bind(command, "$opcode", observation.Opcode);
        Bind(command, "$length", observation.Length);
        Bind(command, "$hash", observation.PayloadHash12);
        Bind(command, "$connection", observation.ConnectionTag);
        Bind(command, "$observed", UtcTimestamp.ToText(observation.ObservedAtUtc));
        Bind(command, "$t", observation.TMs);
        Bind(command, "$first", UtcTimestamp.ToTextOrNull(observation.FirstObservedAtUtc));
        Bind(command, "$last", UtcTimestamp.ToTextOrNull(observation.LastObservedAtUtc));
        Bind(command, "$first_t", observation.FirstTMs);
        Bind(command, "$last_t", observation.LastTMs);
        Bind(command, "$payload", observation.PayloadHex);
        Bind(command, "$occurrences", observation.Occurrences);
    }

    private static void BindReview(SqliteCommand command, string id, string verdict, string? note, DateTimeOffset reviewed)
    {
        Bind(command, "$id", id);
        Bind(command, "$verdict", verdict);
        Bind(command, "$note", note);
        Bind(command, "$reviewed", UtcTimestamp.ToText(reviewed));
    }

    private static void BindFilter(SqliteCommand command, string? session, DateTimeOffset? from, DateTimeOffset? to)
    {
        Bind(command, "$session", session);
        Bind(command, "$from", UtcTimestamp.ToTextOrNull(from));
        Bind(command, "$to", UtcTimestamp.ToTextOrNull(to));
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static void RequireUuid(string id, string field)
    {
        if (!Guid.TryParseExact(id, "D", out _))
            throw CollectorException.BadRequest(field + " 必须是标准 UUID。", field == "request_id" ? field : "payload." + field);
    }

    private sealed record ReviewSnapshot(
        [property: JsonPropertyName("fingerprint")] string Fingerprint,
        [property: JsonPropertyName("candidate_review")] CandidateReviewResult? Result);
}
