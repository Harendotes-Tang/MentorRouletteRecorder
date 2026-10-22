using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Ipc;
using MentorRecorder.Collector.Storage;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.Import;

/// <summary>Bounded, read-only previews followed by atomic personal-history merge and durable retry receipts.</summary>
public sealed partial class RunImportService
{
    private sealed record Preview(string Id, string Kind, string? Name, DateTimeOffset Created,
        IReadOnlyList<ImportCandidate> Candidates, JsonObject? FrozenRender = null, int? PageSize = null, int FrozenBytes = 0);
    private readonly SqliteDatabase _database;
    private readonly IClock _clock;
    private readonly RunRepository _runs;
    private readonly RunImportRepository _imports;
    private readonly RunReflectionRepository _reflections;
    private readonly RunRevisionRepository _revisions;
    private readonly IdempotencyRepository _idempotency;
    private readonly SettingsRepository _settings;
    private readonly long _maxPreviewCacheBytes;
    private readonly Dictionary<string, Preview> _previews = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public const int MaxRows = 5000;
    public const int MaxPreviews = 8;
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Longest verbatim argument a <c>$defs/TextRef</c> carries (contracts/ipc-v1.schema.json).</summary>
    private const int MaxVerbatimArgument = 4000;
    private const int MaxPreviousDutyNameFingerprints = 32;

    public RunImportService(SqliteDatabase database, IClock clock) : this(database, clock, MaxPreviewCacheBytes)
    {
    }

    internal RunImportService(SqliteDatabase database, IClock clock, long maxPreviewCacheBytes)
    {
        if (maxPreviewCacheBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPreviewCacheBytes));
        _database = database;
        _clock = clock;
        _maxPreviewCacheBytes = maxPreviewCacheBytes;
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
        string? timeZone = null, string? sourceName = null, CancellationToken cancellationToken = default,
        int? pageSize = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageSize is < 1 or > 100)
            throw CollectorException.BadRequest(CollectorTexts.IpcFieldOutOfRange("page_size", 1, 100), "page_size");
        sourceKind = sourceKind.ToUpperInvariant();
        if (!new[] { "CSV", "XLS", "XLSX", "JSON", "BACKUP", "PASTE", "SCREENSHOT", "ROWS" }.Contains(sourceKind, StringComparer.Ordinal))
            throw CollectorException.BadRequest(CollectorTexts.ImportPreviewKindUnsupported(), "source_kind");
        if (sourceName?.Length > 500) throw CollectorException.BadRequest(CollectorTexts.ImportPreviewSourceNameTooLong(), "source_name");
        if (filePath is not null)
        {
            if (!Path.IsPathFullyQualified(filePath)) throw CollectorException.BadRequest(CollectorTexts.ImportPreviewPathNotAbsolute(), "file_path");
            // Import reads only ordinary local sources; it never changes the source or follows a network destination.
            // The path check is the export one; its refusal is an import request refused, in import words (audit 2026-10-20 H2-7).
            try { Export.ExportPaths.Resolve(filePath, "file_path"); }
            catch (CollectorException refused) when (refused.Code == ErrorCodes.ExportFailed)
            {
                throw CollectorException.BadRequest(refused.Text?.Key switch
                {
                    "export.path.uncheckable" => CollectorTexts.ImportPreviewPathUncheckable(),
                    "export.path.empty" or "export.path.invalid" => CollectorTexts.ImportPreviewPathNotAbsolute(),
                    _ => CollectorTexts.ImportPreviewPathNotLocal(),
                }, "file_path");
            }
        }
        IReadOnlyList<ImportSourceRow> parsed;
        try { parsed = RunImportSourceParser.Parse(sourceKind, filePath, text, rows, columnMapping); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.Xml.XmlException or System.Text.Json.JsonException)
        { throw Unreadable(failure); }
        cancellationToken.ThrowIfCancellationRequested();
        if (parsed.Count > MaxRows) throw CollectorException.BadRequest(CollectorTexts.ImportPreviewTooManyRows(), "rows");
        // A row without its own identity is known by its own content and how many identical rows
        // precede it, so adding, removing or moving other rows leaves it recognisable on a later import
        // (audit 2026-10-20 H2-1). Hash the bounded parsed values, never reread a file that could change.
        // The key a row got before (whole parsed snapshot + row number) is still checked, so a file imported
        // by an earlier version and its purge tombstones keep matching.
        var snapshot = new JsonArray(parsed.Select(row => (JsonNode)row.Values.DeepClone()).ToArray());
        var legacyInputFingerprint = RunImportCandidateNormalizer.Hash(snapshot.ToJsonString(Wire.JsonOptions));
        var legacyKeys = new Dictionary<int, string>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Values["run_id"] is null && row.Values["source_key"] is null)
            {
                var own = RunImportCandidateNormalizer.Hash(RunImportCandidateNormalizer.CanonicalValues(row.Values));
                var occurrence = occurrences[own] = occurrences.GetValueOrDefault(own) + 1;
                row.Values["source_key"] = own + ":" + occurrence.ToString(CultureInfo.InvariantCulture);
                legacyKeys[row.RowNumber] = legacyInputFingerprint + ":" + row.RowNumber.ToString(CultureInfo.InvariantCulture);
            }
        }
        var now = UtcTimestamp.Truncate(_clock.UtcNow);
        var previousDutyNames = SnapshotPreviousDutyNameFingerprints(now, cancellationToken);
        var preview = new Preview(Guid.NewGuid().ToString("D"), sourceKind, sourceName, now,
            parsed.Select(row =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = RunImportCandidateNormalizer.Normalize(row, sourceKind, sourceName, timeZone, now,
                    legacyKeys.GetValueOrDefault(row.RowNumber));
                // The normalizer produces these only for an inferred preferred duty. An
                // explicit id can have exactly the same facts hash and must not inherit them.
                if (candidate.PreviousDutyNameFingerprints is { Count: > 0 } current
                    && candidate.Run?.ImportMetadata is { } metadata
                    && previousDutyNames.TryGetValue(metadata.SourceFingerprint, out var cached))
                {
                    candidate = candidate with
                    {
                        PreviousDutyNameFingerprints = cached.Concat(current).Distinct(StringComparer.Ordinal)
                            .Take(MaxPreviousDutyNameFingerprints).ToArray(),
                    };
                }
                return candidate;
            }).ToArray());
        var rendered = _database.Read(_ => Render(preview, cancellationToken), cancellationToken);
        return FreezeAndCache(preview, rendered, pageSize, cancellationToken);
    }

    /// <summary>
    /// Keeps server-computed pre-preference identities through unchanged candidate rechecks.
    /// Only unexpired previews contribute; oldest compatible identities win the bounded set.
    /// Snapshotting is linear in the bounded cache and holds only its gate, never the DB gate.
    /// The caller still normalizes every source fact and reclassifies against the current DB.
    /// </summary>
    private Dictionary<string, List<string>> SnapshotPreviousDutyNameFingerprints(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var preview in _previews.Values.Where(preview => now - preview.Created <= PreviewLifetime)
                         .OrderBy(preview => preview.Created))
            {
                foreach (var candidate in preview.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidate.PreviousDutyNameFingerprints is not { Count: > 0 } fingerprints
                        || candidate.Run?.ImportMetadata is not { } metadata)
                        continue;
                    if (!snapshot.TryGetValue(metadata.SourceFingerprint, out var known))
                        snapshot.Add(metadata.SourceFingerprint, known = new List<string>());
                    foreach (var fingerprint in fingerprints)
                    {
                        if (known.Count >= MaxPreviousDutyNameFingerprints) break;
                        if (!known.Contains(fingerprint, StringComparer.Ordinal)) known.Add(fingerprint);
                    }
                }
            }
        }
        return snapshot;
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
        if (!confirmOwnRecords) throw CollectorException.BadRequest(CollectorTexts.ImportCommitOwnRecordsRequired(), "confirm_own_records");
        if (!Guid.TryParseExact(previewId, "D", out _) || !Guid.TryParseExact(requestId, "D", out _))
            throw CollectorException.BadRequest(CollectorTexts.ImportCommitIdsInvalid());
        if (rowNumbers.Count == 0 || rowNumbers.Count > MaxRows || rowNumbers.Distinct().Count() != rowNumbers.Count)
            throw CollectorException.BadRequest(CollectorTexts.ImportCommitSelectionInvalid(), "row_numbers");
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
                    throw new CollectorException(ErrorCodes.IdempotencyConflict, CollectorTexts.ImportCommitSelectionChanged(), field: "preview_id");
                var replay = JsonNode.Parse(receipt.Value.Json)!.AsObject();
                replay["replayed"] = true;
                return replay;
            }
            if (_idempotency.TryGetResponse(requestId, transaction) is not null)
                throw new CollectorException(ErrorCodes.IdempotencyConflict,
                    CollectorTexts.ImportCommitRequestReused(), field: "request_id");
            if (_idempotency.WasAppliedBeforePrune(requestId, transaction))
                throw new CollectorException(ErrorCodes.IdempotencyConflict,
                    CollectorTexts.IdempotencyResponseExpired(),
                    new Dictionary<string, object?>
                    {
                        ["conflict"] = "idempotency",
                        ["request_id"] = requestId,
                        ["reason"] = "RESPONSE_EXPIRED",
                    }, field: "request_id");
            if (preview is null || _clock.UtcNow - preview.Created > PreviewLifetime)
                throw CollectorException.BadRequest(CollectorTexts.ImportCommitPreviewExpired(), "preview_id");
            var candidates = selected.Select(number => preview.Candidates.SingleOrDefault(candidate => candidate.RowNumber == number)
                ?? throw CollectorException.BadRequest(CollectorTexts.ImportCommitRowNotInPreview(), "row_numbers")).ToArray();
            if (candidates.Any(candidate => candidate.Run is null || candidate.Errors.Count != 0))
                throw CollectorException.BadRequest(CollectorTexts.ImportCommitRowsHaveErrors(), "row_numbers");
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
            ? string.Format(CultureInfo.InvariantCulture, "导入 {0} 条已包含在起始次数中的历史通关，从起始次数中扣除", contributing)
            : string.Format(CultureInfo.InvariantCulture, "导入 {0} 条已包含在起始次数中的历史通关，起始次数只有 {1}，扣到 0", contributing, stored.BaselineCompletedCount);
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
        var seenTwins = new Dictionary<string, ImportCandidate>(StringComparer.Ordinal);
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
                    // The same card read twice (overlapping screenshots, one picture added twice) differs only in
                    // what the reading added; it is offered as a possible duplicate of the first (audit 2026-10-20 I3-1).
                    if (TwinKey(run) is { } twin)
                    {
                        if (status == "new" && seenTwins.TryGetValue(twin, out var first))
                        { status = "possible_duplicate"; matching = first.Run!.RunId; }
                        else seenTwins.TryAdd(twin, candidate);
                    }
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
                ["errors"] = Wire.Strings(candidate.Errors.Select(text => text.Chinese)),
                ["warnings"] = Wire.Strings(candidate.Warnings.Select(text => text.Chinese)),
                ["error_refs"] = ImportRowText.Refs(candidate.Errors), ["warning_refs"] = ImportRowText.Refs(candidate.Warnings),
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

    /// <summary>
    /// What makes two rows of one batch the same record: the duty, the same clock time (game entry or source
    /// time) and the same reflection text. Null without a clock time: one day can hold several runs of a duty.
    /// </summary>
    private static string? TwinKey(MentorRun run) =>
        run.EnteredAtUtc is null && run.ImportMetadata?.SourceRecordedAtUtc is null ? null
            : new JsonArray(
                run.ContentId is { } content ? "content:" + content.ToString(CultureInfo.InvariantCulture) : "name:" + run.DutyName,
                UtcTimestamp.ToTextOrNull(run.EnteredAtUtc), UtcTimestamp.ToTextOrNull(run.ImportMetadata?.SourceRecordedAtUtc),
                run.Reflection?.Text).ToJsonString(Wire.JsonOptions);

    /// <summary>
    /// "无法读取导入内容：" and why. The parser's own sentence nests as a reference; an operating-system, SQLite, XML or
    /// JSON message travels as a verbatim argument, and one longer than a TextRef argument may be stays Chinese only.
    /// </summary>
    private static CollectorException Unreadable(Exception failure)
    {
        if (RunImportSourceParser.TextOf(failure) is { } ours)
            return CollectorException.BadRequest(CollectorTexts.ImportSourceUnreadable(ours), "file_path");
        var verbatim = CollectorTexts.ImportSourceUnreadableSystem(failure.Message);
        return failure.Message.Length <= MaxVerbatimArgument
            ? CollectorException.BadRequest(verbatim, "file_path")
            : CollectorException.BadRequest(verbatim.Chinese, "file_path");
    }

    private (string Status, string? Matching) Classify(ImportCandidate candidate, SqliteTransaction? transaction)
    {
        var incoming = candidate.Run!;
        var previousDutyNames = candidate.PreviousDutyNameFingerprints ?? Array.Empty<string>();
        if (_idempotency.IsPurgedRun(incoming.RunId, incoming.ImportMetadata?.SourceFingerprint, transaction)
            || candidate.LegacyFingerprint is { } purgedBefore && _idempotency.IsPurgedRun(incoming.RunId, purgedBefore, transaction)
            || candidate.PreAliasFingerprint is { } purgedPreAlias && _idempotency.IsPurgedRun(incoming.RunId, purgedPreAlias, transaction)
            || previousDutyNames.Any(fingerprint => _idempotency.IsPurgedRun(incoming.RunId, fingerprint, transaction)))
            return ("conflict", incoming.RunId);
        if (candidate.HasStableId && _runs.GetInternal(incoming.RunId, transaction) is { } local)
            return (RunImportCandidateNormalizer.SameProvidedFacts(candidate, local) ? "duplicate" : "conflict", local.RunId);
        if ((_imports.FindFingerprint(incoming.ImportMetadata!.SourceFingerprint, transaction)
            ?? (candidate.LegacyFingerprint is { } before ? _imports.FindFingerprint(before, transaction) : null)
            ?? (candidate.PreAliasFingerprint is { } preAlias ? _imports.FindFingerprint(preAlias, transaction) : null)
            ?? previousDutyNames.Select(fingerprint => _imports.FindFingerprint(fingerprint, transaction))
                .FirstOrDefault(runId => runId is not null)) is { } exact)
            return ("duplicate", exact);
        var nearby = FindNearby(incoming, transaction);
        return nearby is null ? ("new", null) : ("possible_duplicate", nearby);
    }

    /// <summary>
    /// A local run of the same duty that may be this record: entered within a minute, the same source
    /// time, or - when one of the two has no clock time - the same source day as written in the source
    /// and, where both name one, the same job (audit 2026-10-20 H2-1). Two clock times decide on their own.
    /// </summary>
    private string? FindNearby(MentorRun incoming, SqliteTransaction? transaction)
    {
        var sourceTime = incoming.ImportMetadata?.SourceRecordedAtUtc;
        var sourceDay = RunImportCandidateNormalizer.SourceDay(incoming.ImportMetadata?.SourceRecordedAt);
        if (incoming.EnteredAtUtc is null && sourceTime is null && sourceDay is null) return null;
        if (incoming.ContentId is null && string.IsNullOrWhiteSpace(incoming.DutyName)) return null;
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT r.run_id FROM mentor_runs r LEFT JOIN run_import_metadata im ON im.run_id = r.run_id " +
            "WHERE (r.content_id = $content OR ($content IS NULL AND r.duty_name = $name)) AND " +
            "(($entered IS NOT NULL AND r.entered_at_utc BETWEEN $from AND $to) OR " +
            "($source IS NOT NULL AND im.source_recorded_at_utc = $source) OR " +
            "($day IS NOT NULL AND ($undated = 1 OR (r.entered_at_utc IS NULL AND im.source_recorded_at_utc IS NULL)) " +
            "AND ($job IS NULL OR r.job_id IS NULL OR r.job_id = $job) AND " + SourceDaySql("im.source_recorded_at") + " = $day)) " +
            "ORDER BY r.run_id LIMIT 1;";
        command.Parameters.AddWithValue("$content", (object?)incoming.ContentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)incoming.DutyName ?? DBNull.Value);
        command.Parameters.AddWithValue("$entered", (object?)UtcTimestamp.ToTextOrNull(incoming.EnteredAtUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$from", incoming.EnteredAtUtc is { } entered ? UtcTimestamp.ToText(entered.AddMinutes(-1)) : DBNull.Value);
        command.Parameters.AddWithValue("$to", incoming.EnteredAtUtc is { } enteredAgain ? UtcTimestamp.ToText(enteredAgain.AddMinutes(1)) : DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)UtcTimestamp.ToTextOrNull(sourceTime) ?? DBNull.Value);
        command.Parameters.AddWithValue("$day", (object?)sourceDay ?? DBNull.Value);
        command.Parameters.AddWithValue("$undated", incoming.EnteredAtUtc is null && sourceTime is null ? 1 : 0);
        command.Parameters.AddWithValue("$job", (object?)incoming.JobId ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// SQL twin of <see cref="RunImportCandidateNormalizer.SourceDay"/>: the leading
    /// <c>yyyy-M-d</c> / <c>yyyy/M/d</c> / <c>yyyy.M.d</c> of a stored source text as <c>yyyy-MM-dd</c>, else NULL.
    /// </summary>
    private static string SourceDaySql(string column)
    {
        var text = $"replace(replace({column}, '/', '-'), '.', '-')";
        var rest = $"substr({text}, 6)";
        return $"(CASE WHEN {text} GLOB '[0-9][0-9][0-9][0-9]-[0-9]*' AND instr({rest}, '-') > 1 " +
            $"THEN printf('%04d-%02d-%02d', CAST(substr({text}, 1, 4) AS INTEGER), CAST({rest} AS INTEGER), " +
            $"CAST(substr({rest}, instr({rest}, '-') + 1) AS INTEGER)) END)";
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
