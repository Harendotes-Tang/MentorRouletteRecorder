using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Import;

/// <summary>Bounded, read-only previews followed by atomic personal-history merge and durable retry receipts.</summary>
public sealed class RunImportService
{
    private sealed record Preview(string Id, string Kind, string? Name, DateTimeOffset Created,
        IReadOnlyList<ImportCandidate> Candidates);
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly RunRepository _runs;
    private readonly RunImportRepository _imports;
    private readonly RunReflectionRepository _reflections;
    private readonly RunRevisionRepository _revisions;
    private readonly IdempotencyRepository _idempotency;
    private readonly SettingsRepository _settings;
    private readonly Dictionary<string, Preview> _previews = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public const int MaxRows = 5000;
    public const int MaxPreviews = 8;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    public RunImportService(SqliteDatabase database, IClock clock)
    {
        _database = database;
        _clock = clock;
        _runs = new RunRepository(database);
        _imports = new RunImportRepository(database);
        _reflections = new RunReflectionRepository(database);
        _revisions = new RunRevisionRepository(database);
        _idempotency = new IdempotencyRepository(database, clock);
        _settings = new SettingsRepository(database, clock);
    }

    /// <summary>No run, reflection, audit or receipt is written while constructing this preview.</summary>
    public JsonObject PreviewSource(string sourceKind, string? filePath = null, string? text = null,
        JsonArray? rows = null, IReadOnlyDictionary<string, string>? columnMapping = null,
        string? timeZone = null, string? sourceName = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceKind = sourceKind.ToUpperInvariant();
        if (!new[] { "CSV", "XLSX", "JSON", "BACKUP", "PASTE", "SCREENSHOT", "ROWS" }.Contains(sourceKind, StringComparer.Ordinal))
            throw CollectorException.BadRequest("不支持的记录来源格式。", "source_kind");
        if (sourceName?.Length > 500) throw CollectorException.BadRequest("来源名称超过 500 字符。", "source_name");
        if (filePath is not null)
        {
            if (!Path.IsPathFullyQualified(filePath)) throw CollectorException.BadRequest("请选择完整的本地文件路径。", "file_path");
            // Import reads only ordinary local sources; it never changes the source or follows a network destination.
            Export.ExportPaths.Resolve(filePath);
        }
        IReadOnlyList<ImportSourceRow> parsed;
        try { parsed = RunImportSourceParser.Parse(sourceKind, filePath, text, rows, columnMapping); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Xml.XmlException or System.Text.Json.JsonException)
        { throw CollectorException.BadRequest("无法解析导入来源：" + failure.Message, "file_path"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (parsed.Count > MaxRows) throw CollectorException.BadRequest("一次最多预览 5000 条记录，请分批选择。", "rows");
        // Hash the bounded parsed snapshot, never reread a file that could change after parsing.
        var snapshot = new JsonArray(parsed.Select(row => (JsonNode)row.Values.DeepClone()).ToArray());
        var inputFingerprint = RunImportCandidateNormalizer.Hash(snapshot.ToJsonString(Wire.JsonOptions));
        foreach (var row in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Values["run_id"] is null && row.Values["source_key"] is null)
                row.Values["source_key"] = inputFingerprint + ":" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
        }
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var preview = new Preview(Guid.NewGuid().ToString("D"), sourceKind, sourceName, now,
            parsed.Select(row =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RunImportCandidateNormalizer.Normalize(row, sourceKind, sourceName, timeZone, now);
            }).ToArray());
        var rendered = _database.Read(_ => Render(preview, cancellationToken), cancellationToken);
        // Retain the existing IPC framing limit, with room for the response envelope.
        if (Encoding.UTF8.GetByteCount(rendered.ToJsonString(Wire.JsonOptions)) > FrameCodec.MaxFrameBytes * 3 / 4)
            throw CollectorException.BadRequest("预览内容超过 4 MiB 通信上限，请拆分来源或减少本批记录。", "rows");
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var id in _previews.Where(pair => now - pair.Value.Created > PreviewLifetime).Select(pair => pair.Key).ToArray())
                _previews.Remove(id);
            if (_previews.Count >= MaxPreviews) _previews.Remove(_previews.Values.OrderBy(value => value.Created).First().Id);
            _previews[preview.Id] = preview;
        }
        return rendered;
    }

    /// <summary>Rechecks all selected identities under the write transaction; conflicts never overwrite local data.</summary>
    /// <param name="deductFromBaseline">
    /// The user said these records are already counted in the achievement baseline (the completions
    /// the game showed before this software was installed). The imported completions that contribute
    /// to the progress are then deducted from <c>baseline_completed_count</c> in the same transaction,
    /// so the progress does not count them twice (docs/statistics-definitions.md section 4).
    /// </param>
    public JsonObject Commit(string previewId, IReadOnlyList<int> rowNumbers, bool confirmOwnRecords, string requestId,
        bool deductFromBaseline = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmOwnRecords) throw CollectorException.BadRequest("请确认本批记录属于本人。", "confirm_own_records");
        if (!Guid.TryParseExact(previewId, "D", out _) || !Guid.TryParseExact(requestId, "D", out _))
            throw CollectorException.BadRequest("预览编号和请求编号必须是标准 UUID。");
        if (rowNumbers.Count == 0 || rowNumbers.Count > MaxRows || rowNumbers.Distinct().Count() != rowNumbers.Count)
            throw CollectorException.BadRequest("请选择不重复的可导入行。", "row_numbers");
        var selected = rowNumbers.OrderBy(number => number).ToArray();
        // The baseline choice is part of the selection: a retry that changes it is a different commit.
        var selectionFingerprint = RunImportCandidateNormalizer.Hash(previewId + ":" + string.Join(',', selected)
            + (deductFromBaseline ? ":deduct_from_baseline" : ""));
        Preview? preview;
        lock (_gate) _previews.TryGetValue(previewId, out preview);
        return _database.RunInTransaction(transaction =>
        {
            _idempotency.ThrowIfPurgedRequest(requestId, transaction, previewId);
            var receipt = FindReceipt(previewId, requestId, transaction);
            if (receipt is not null)
            {
                if (!string.Equals(receipt.Value.Fingerprint, selectionFingerprint, StringComparison.Ordinal))
                    throw new CollectorException(ErrorCodes.IdempotencyConflict, "同一预览或请求编号不能提交不同的选择。", field: "preview_id");
                var replay = JsonNode.Parse(receipt.Value.Json)!.AsObject();
                replay["replayed"] = true;
                return replay;
            }
            if (_idempotency.TryGetResponse(requestId, transaction) is not null || _idempotency.WasAppliedBeforePrune(requestId, transaction))
                throw new CollectorException(ErrorCodes.IdempotencyConflict,
                    "此请求编号已用于另一项变更，请重新生成请求编号。", field: "request_id");
            if (preview is null || _clock.UtcNow - preview.Created > PreviewLifetime)
                throw CollectorException.BadRequest("导入预览已失效，请重新预览后提交。", "preview_id");
            var candidates = selected.Select(number => preview.Candidates.SingleOrDefault(candidate => candidate.RowNumber == number)
                ?? throw CollectorException.BadRequest("所选行不在这次预览中。", "row_numbers")).ToArray();
            if (candidates.Any(candidate => candidate.Run is null || candidate.Errors.Count != 0))
                throw CollectorException.BadRequest("所选行仍有错误，请修正并重新预览；本次没有写入记录。", "row_numbers");
            var outcomes = new JsonArray();
            var accepted = new List<string>();
            var duplicateCount = 0;
            var conflictCount = 0;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (status, matching) = Classify(candidate, transaction);
                if (status == "duplicate") duplicateCount++;
                else if (status == "conflict") conflictCount++;
                else
                {
                    var run = candidate.Run!;
                    // The preview owns the complete validated candidate. Revisions start in this library.
                    _runs.Insert(run, transaction);
                    _imports.Insert(run.RunId, run.ImportMetadata!, transaction);
                    if (run.Reflection is { } reflection)
                        _reflections.Upsert(run.RunId, reflection.Mood, reflection.Text, _clock.UtcNow, transaction);
                    _revisions.Append(new RunRevision
                    {
                        RevisionId = Guid.NewGuid().ToString("D"), RunId = run.RunId, Revision = 1,
                        ChangedAtUtc = UtcTimestamp.Truncate(_clock.UtcNow), ChangeKind = ChangeKind.Import,
                        Actor = RevisionActor.User, Reason = "导入本人的历史记录",
                        RequestId = Guid.ParseExact(RunImportCandidateNormalizer.Hash(requestId + ":" + candidate.RowNumber)[..32], "N").ToString("D"),
                        Changes = Wire.Run(run).Select(pair => new RunFieldChange(pair.Key, null, AuditValue(pair.Value))).ToArray(),
                    }, transaction);
                    accepted.Add(run.RunId);
                    status = "imported";
                    matching = run.RunId;
                }
                outcomes.Add(new JsonObject { ["row_number"] = candidate.RowNumber, ["status"] = status, ["run_id"] = matching });
            }
            var (deducted, baseline) = DeductFromBaseline(deductFromBaseline ? accepted : Array.Empty<string>(), requestId, transaction);
            var response = new JsonObject
            {
                ["preview_id"] = previewId, ["imported_count"] = accepted.Count,
                ["duplicate_count"] = duplicateCount, ["conflict_count"] = conflictCount,
                ["skipped_count"] = 0, ["replayed"] = false,
                ["run_ids"] = Wire.Strings(accepted), ["rows"] = outcomes,
                ["baseline_deducted_count"] = deducted, ["baseline_completed_count"] = baseline,
            };
            StoreReceipt(previewId, requestId, selectionFingerprint, response, transaction);
            _idempotency.Store(requestId, "CommitRunImport",
                new JsonObject { ["fingerprint"] = selectionFingerprint, ["response"] = response.DeepClone() }.ToJsonString(Wire.JsonOptions), transaction);
            return response;
        }, cancellationToken);
    }

    /// <summary>
    /// Deducts the newly imported completions that count towards the progress from the stored
    /// baseline, never below zero, and records the change in the baseline history under the
    /// commit's request id. Only the rows this commit inserted are counted: duplicates were
    /// already in the library, and the eligibility test is the statistics query itself.
    /// </summary>
    /// <returns>How many completions were deducted, and the baseline as stored afterwards.</returns>
    private (int Deducted, int Baseline) DeductFromBaseline(IReadOnlyList<string> importedRunIds, string requestId, SqliteTransaction transaction)
    {
        var stored = _settings.GetAchievementSettings(transaction);
        if (importedRunIds.Count == 0 || stored.BaselineCompletedCount == 0) return (0, stored.BaselineCompletedCount);
        var contributing = CountContributingCompleted(importedRunIds, transaction);
        var deducted = (int)Math.Min(contributing, stored.BaselineCompletedCount);
        if (deducted == 0) return (0, stored.BaselineCompletedCount);
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var settings = stored with
        {
            BaselineCompletedCount = stored.BaselineCompletedCount - deducted,
            BaselineEffectiveAt = now,
            UpdatedAtUtc = now,
        };
        _settings.UpdateAchievementSettings(settings, transaction);
        var reason = deducted == contributing
            ? string.Format(CultureInfo.InvariantCulture, "导入 {0} 条已包含在基数中的历史通关，自基数中扣除", contributing)
            : string.Format(CultureInfo.InvariantCulture, "导入 {0} 条已包含在基数中的历史通关，基数只有 {1}，扣到 0", contributing, stored.BaselineCompletedCount);
        _settings.AppendBaselineAudit(Guid.NewGuid().ToString("D"), settings, reason, requestId, transaction);
        return (deducted, settings.BaselineCompletedCount);
    }

    /// <summary>Counts, among the given runs, those the dashboard adds to the progress: the statistics filter plus COMPLETED and contributing.</summary>
    private long CountContributingCompleted(IReadOnlyList<string> runIds, SqliteTransaction transaction)
    {
        var total = 0L;
        foreach (var chunk in runIds.Chunk(500))
        {
            var filter = RunFilterSql.Build(null, forStatistics: true);
            var names = chunk.Select((_, index) => "$imported" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
            using var command = _database.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT COUNT(*) FROM mentor_runs WHERE {filter.Where} AND result = 'COMPLETED' AND contributes_to_goal = 1 " +
                $"AND run_id IN ({string.Join(',', names)});";
            RunFilterSql.Bind(command, filter);
            for (var index = 0; index < chunk.Length; index++) command.Parameters.AddWithValue(names[index], chunk[index]);
            total += Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        return total;
    }

    private JsonObject Render(Preview preview, CancellationToken cancellationToken)
    {
        var rows = new JsonArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        { ["new"] = 0, ["duplicate"] = 0, ["conflict"] = 0, ["possible_duplicate"] = 0, ["invalid"] = 0, ["incomplete"] = 0, ["total"] = preview.Candidates.Count };
        var seenIds = new Dictionary<string, ImportCandidate>(StringComparer.Ordinal);
        var seenFingerprints = new Dictionary<string, ImportCandidate>(StringComparer.Ordinal);
        foreach (var candidate in preview.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (status, matching) = candidate.Run is null || candidate.Errors.Count != 0
                ? ("invalid", (string?)null) : Classify(candidate, null);
            if (candidate.Run is { } run && status is "new" or "possible_duplicate")
            {
                if (candidate.HasStableId && seenIds.TryGetValue(run.RunId, out var earlier))
                {
                    status = RunImportCandidateNormalizer.SameProvidedFacts(candidate, earlier.Run!) ? "duplicate" : "conflict";
                    matching = earlier.Run!.RunId;
                }
                else if (seenFingerprints.TryGetValue(run.ImportMetadata!.SourceFingerprint, out earlier))
                { status = "duplicate"; matching = earlier.Run!.RunId; }
                else
                {
                    if (candidate.HasStableId) seenIds[run.RunId] = candidate;
                    seenFingerprints[run.ImportMetadata!.SourceFingerprint] = candidate;
                }
            }
            counts[status]++;
            if (candidate.Run?.IsIncompleteImport == true) counts["incomplete"]++;
            rows.Add(new JsonObject
            {
                ["row_number"] = candidate.RowNumber, ["status"] = status,
                ["can_import"] = status is "new" or "possible_duplicate",
                ["incomplete"] = candidate.Run?.IsIncompleteImport ?? false,
                ["candidate"] = candidate.Input.DeepClone(), ["run"] = candidate.Run is null ? null : Wire.Run(candidate.Run),
                ["errors"] = Wire.Strings(candidate.Errors), ["warnings"] = Wire.Strings(candidate.Warnings),
                ["matching_run_id"] = matching,
            });
        }
        var summary = new JsonObject();
        foreach (var pair in counts) summary[pair.Key] = pair.Value;
        return new JsonObject
        {
            ["preview_id"] = preview.Id, ["source_kind"] = preview.Kind,
            ["source_name"] = preview.Name, ["rows"] = rows, ["summary"] = summary,
        };
    }

    private (string Status, string? Matching) Classify(ImportCandidate candidate, SqliteTransaction? transaction)
    {
        var incoming = candidate.Run!;
        if (_idempotency.IsPurgedRun(incoming.RunId, incoming.ImportMetadata?.SourceFingerprint, transaction))
            return ("conflict", incoming.RunId);
        if (candidate.HasStableId && _runs.GetInternal(incoming.RunId, transaction) is { } local)
            return (RunImportCandidateNormalizer.SameProvidedFacts(candidate, local) ? "duplicate" : "conflict", local.RunId);
        if (_imports.FindFingerprint(incoming.ImportMetadata!.SourceFingerprint, transaction) is { } exact)
            return ("duplicate", exact);
        var nearby = FindNearby(incoming, transaction);
        return nearby is null ? ("new", null) : ("possible_duplicate", nearby);
    }

    private string? FindNearby(MentorRun incoming, SqliteTransaction? transaction)
    {
        var sourceTime = incoming.ImportMetadata?.SourceRecordedAtUtc;
        if (incoming.EnteredAtUtc is null && sourceTime is null) return null;
        if (incoming.ContentId is null && string.IsNullOrWhiteSpace(incoming.DutyName)) return null;
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT r.run_id FROM mentor_runs r LEFT JOIN run_import_metadata im ON im.run_id = r.run_id " +
            "WHERE (r.content_id = $content OR ($content IS NULL AND r.duty_name = $name)) AND " +
            "(($entered IS NOT NULL AND r.entered_at_utc BETWEEN $from AND $to) OR " +
            "($source IS NOT NULL AND im.source_recorded_at_utc = $source)) ORDER BY r.run_id LIMIT 1;";
        command.Parameters.AddWithValue("$content", (object?)incoming.ContentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)incoming.DutyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$entered", (object?)UtcTimestamp.ToTextOrNull(incoming.EnteredAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", incoming.EnteredAtUtc is { } entered ? UtcTimestamp.ToText(entered.AddMinutes(-1)) : DBNull.Value);
        command.Parameters.AddWithValue("$to", incoming.EnteredAtUtc is { } enteredAgain ? UtcTimestamp.ToText(enteredAgain.AddMinutes(1)) : DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)UtcTimestamp.ToTextOrNull(sourceTime) ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    private (string Fingerprint, string Json)? FindReceipt(string previewId, string requestId, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT selection_fingerprint, response_json FROM run_import_batches WHERE preview_id = $preview OR request_id = $request;";
        command.Parameters.AddWithValue("$preview", previewId);
        command.Parameters.AddWithValue("$request", requestId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }
    private void StoreReceipt(string previewId, string requestId, string fingerprint, JsonObject response, SqliteTransaction transaction)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO run_import_batches VALUES ($preview,$request,$fingerprint,$response,$time);";
        command.Parameters.AddWithValue("$preview", previewId);
        command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$response", response.ToJsonString(Wire.JsonOptions));
        command.Parameters.AddWithValue("$time", UtcTimestamp.ToText(_clock.UtcNow));
        command.ExecuteNonQuery();
    }
    private static object? AuditValue(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.TryGetValue<bool>(out var flag)) return flag;
            if (value.TryGetValue<long>(out var integer)) return integer;
            if (value.TryGetValue<int>(out var small)) return small;
        }
        return node.ToJsonString(Wire.JsonOptions);
    }
}
