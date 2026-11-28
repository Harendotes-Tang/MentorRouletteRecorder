using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Contracts.Texts;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;
using MentorRecorder.Collector.Export;

namespace MentorRecorder.Collector.Ipc;

/// <summary>
/// Maps one request to one response payload.
///
/// Every message type declared in contracts/ipc-v1.schema.json is handled here; an
/// unrecognised one is refused with <c>ERR_BAD_REQUEST</c> rather than ignored, so a client
/// built against a newer contract learns immediately instead of hanging on a reply that
/// never comes. Handlers validate their payload before touching storage.
/// </summary>
public sealed class MessageDispatcher
{
    private readonly CollectorHost _host;
    private readonly RunExporter _exporter;
    // Whole-history exports materialise their results. Keep one active per host; the
    // connection's existing deferred-response bound also limits callers waiting here. The
    // deferred backup, diagnostics report and candidate evidence export take the same slot.
    private readonly SemaphoreSlim _exportGate = new(1, 1);

    /// <summary>
    /// 1 while a full-database scan is running, 0 otherwise.
    ///
    /// <c>CheckDatabaseIntegrity</c> opens a read-only connection of its own and reads every
    /// page of the file. Nothing bounded how many of those could run at once: the connection's
    /// deferred list has no length either, so a client could start one pass over the file per
    /// request. The other deferred message that costs something, <c>SynthesizeSpeech</c>, has
    /// had a queue and a refusal of its own since it shipped (2026-09-21 review finding 14).
    /// </summary>
    private int _integrityCheckInFlight;

    // PipeServer shares this dispatcher across its connections. Preview and commit use
    // the same slot so no client can queue more parsing or database waits behind either.
    private int _importInFlight;

    /// <summary>Creates a dispatcher over a host.</summary>
    /// <param name="host">Open collector host.</param>
    public MessageDispatcher(CollectorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _exporter = host.Exporter;
    }

    /// <summary>为真实数据库/管道测试替换导出读取依赖；其他服务仍使用同一 host。</summary>
    internal MessageDispatcher(CollectorHost host, RunExporter exporter) : this(host)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        _exporter = exporter;
    }

    /// <summary>Every message type this build answers.</summary>
    public static IReadOnlySet<string> KnownMessageTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetVersion", "GetStatus", "GetCaptureStatus", "ListCaptureAdapters", "StartCapture", "SelectGameProcess",
        "StopCapture", "GetProtocolProfileStatus", "GetCurrentRun", "QueryRuns", "QueryHistorySelection", "StageHistorySelection",
        "QueryRunCalendarDays", "GetDashboardStats",
        "GetDungeonStats", "GetJobStats", "GetResultStats", "CreateManualRun", "CorrectRun",
        "SoftDeleteRun", "RestoreRun", "GetRunRevisions", "UpdateAchievementBaseline", "ExportCsv",
        "CreateCharacterProfile", "UpdateCharacterProfile", "DeleteCharacterProfile", "ResolveCharacterIdentity",
        "BatchMutateRuns", "PreviewRunRegionMigration", "GetHistoryRetentionSettings", "UpdateHistoryRetentionSettings", "GetPendingImageCleanup", "AcknowledgeImageCleanup",
        "ExportJson", "BackupDatabase", "PreviewRunImport", "CommitRunImport", "SubscribeLiveEvents", "ExportDiagnosticsReport",
        "SetRunReflection", "GetReflectionSummary", "DutyCommunity",
        "StartCaptureValidation", "GetCaptureValidationStatus", "AddCaptureValidationMarker", "StopCaptureValidation",
        "UndoRevision", "GetRunEvents", "GetCaptureSettings", "UpdateCaptureSettings",
        "QueryCandidateObservations", "ReviewCandidateObservation", "ExportCandidateEvidence",
        "ConfirmCalibration", "DiscardCalibration",
        "GetCalibrationShareCode", "CheckSharedCalibration", "ImportCalibrationCode", "AcceptSharedQueueInference",
        "GetCalibrationEvidence", "GetTemplateObservation",
        "RejectSharedCalibration",
        "GetSpeechSettings", "UpdateSpeechSettings", "SynthesizeSpeech", "CheckDatabaseIntegrity",
        "CheckUpdateNow", "StartUpdateDownload", "CancelUpdateDownload", "ResolveUpdateMirrorOffer",
        "SetDisplayTimeZone",
    };

    /// <summary>
    /// Message types answered off the connection's read loop, because they wait on something slower
    /// than the database: <c>SynthesizeSpeech</c> can wait for a queue slot and a network request, and
    /// <c>CheckUpdateNow</c> waits for the update check's one request. The connection keeps answering
    /// other requests meanwhile and writes this answer when it is ready. <c>StartUpdateDownload</c> and
    /// <c>CancelUpdateDownload</c> are not among them: they wait on nothing - the download runs on a
    /// background task of its own - and answer at once like a status poll.
    /// CSV/JSON exports also run off this loop, with one active snapshot/encoding job at a time;
    /// waiting requests remain covered by the connection's deferred-response limit. The other
    /// requests that read or write whole files - the database backup with its integrity check of
    /// the copy, the diagnostics report and the candidate evidence export - share that one slot.
    /// A batch change or region-move preview over a staged selection (<c>selection_id</c>) is
    /// not bounded by the 2000-row inline limit, so it runs off this loop as well (audit
    /// 2026-11-26, G-1 / Gb-1).
    /// </summary>
    public static IReadOnlySet<string> AsynchronousMessageTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "SynthesizeSpeech", "CheckDatabaseIntegrity", "CheckUpdateNow", "PreviewRunImport", "CommitRunImport",
            "ExportCsv", "ExportJson", "QueryHistorySelection", "DutyCommunity",
            "BackupDatabase", "ExportDiagnosticsReport", "ExportCandidateEvidence",
            "BatchMutateRuns", "PreviewRunRegionMigration",
        };

    /// <summary>
    /// The deferred requests that wait for the one whole-file slot (<see cref="RunFileJobAsync"/>): a
    /// connection's unanswered ones count as one deferred reply toward its limit (audit 2026-11-26, RB-2).
    /// </summary>
    internal static IReadOnlySet<string> FileJobMessageTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ExportCsv", "ExportJson", "BackupDatabase", "ExportDiagnosticsReport", "ExportCandidateEvidence",
        };

    /// <summary>
    /// Defers history snapshot creation only (paging and release keep pipe receive order), and a
    /// batch change or region-move preview only when it names a staged selection: an inline target
    /// list is bounded and stays in receive order.
    /// </summary>
    internal static bool ShouldDefer(IpcRequest request) =>
        AsynchronousMessageTypes.Contains(request.MessageType) && request.MessageType switch
        {
            "QueryHistorySelection" =>
                !request.Payload.ContainsKey("selection_id") && !request.Payload.ContainsKey("release"),
            "BatchMutateRuns" or "PreviewRunRegionMigration" => request.Payload.ContainsKey("selection_id"),
            _ => true,
        };

    /// <summary>Host this dispatcher serves.</summary>
    public CollectorHost Host => _host;

    /// <summary>
    /// Answers one request. <c>SubscribeLiveEvents</c> is not handled here: it changes the
    /// shape of the connection, so the connection loop deals with it.
    /// </summary>
    /// <param name="request">Decoded request.</param>
    public JsonObject Dispatch(IpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reader = new PayloadReader(request.Payload);
        return request.MessageType switch
        {
            "GetVersion" => GetVersion(reader),
            "GetStatus" => GetStatus(reader),
            "GetCaptureStatus" => GetCaptureStatus(reader),
            "ListCaptureAdapters" => ListCaptureAdapters(reader),
            "GetProtocolProfileStatus" => GetProtocolProfileStatus(reader),
            "StartCapture" => StartCapture(reader),
            "SelectGameProcess" => SelectGameProcess(reader),
            "StopCapture" => StopCapture(reader),
            "StartCaptureValidation" => StartCaptureValidation(reader),
            "GetCaptureValidationStatus" => ValidationStatus(reader, stop: false),
            "StopCaptureValidation" => ValidationStatus(reader, stop: true),
            "AddCaptureValidationMarker" => ValidationMarker(reader),
            "GetCurrentRun" => GetCurrentRun(reader),
            "QueryRuns" => QueryRuns(reader),
            "QueryHistorySelection" => HistorySelectionHandlers.Query(_host, reader, CancellationToken.None),
            "StageHistorySelection" => HistorySelectionHandlers.Stage(_host, reader),
            "QueryRunCalendarDays" => QueryRunCalendarDays(reader),
            "GetDashboardStats" => GetDashboardStats(reader),
            "GetDungeonStats" => GetDungeonStats(reader),
            "GetJobStats" => GetJobStats(reader),
            "GetResultStats" => GetResultStats(reader),
            "GetRunRevisions" => GetRunRevisions(reader),
            "GetRunEvents" => GetRunEvents(reader),
            "GetCaptureSettings" => GetCaptureSettings(reader),
            "UpdateCaptureSettings" => UpdateCaptureSettings(reader),
            "CreateManualRun" => CreateManualRun(request.RequestId, reader),
            "CorrectRun" => CorrectRun(request.RequestId, reader),
            "SoftDeleteRun" => SoftDeleteRun(request.RequestId, reader),
            "RestoreRun" => RestoreRun(request.RequestId, reader),
            "BatchMutateRuns" => BatchMutateRuns(request.RequestId, reader),
            "PreviewRunRegionMigration" => PreviewRunRegionMigration(reader),
            "GetHistoryRetentionSettings" => GetHistoryRetentionSettings(reader),
            "UpdateHistoryRetentionSettings" => UpdateHistoryRetentionSettings(reader),
            "GetPendingImageCleanup" => GetPendingImageCleanup(reader),
            "AcknowledgeImageCleanup" => AcknowledgeImageCleanup(reader),
            "UndoRevision" => UndoRevision(request.RequestId, reader),
            "UpdateAchievementBaseline" => UpdateBaseline(request.RequestId, reader),
            "CreateCharacterProfile" => CharacterProfileHandlers.Create(_host, request.RequestId, reader),
            "UpdateCharacterProfile" => CharacterProfileHandlers.Update(_host, request.RequestId, reader),
            "DeleteCharacterProfile" => CharacterProfileHandlers.Delete(_host, request.RequestId, reader),
            "ResolveCharacterIdentity" => CharacterProfileHandlers.ResolveIdentity(_host, request.RequestId, reader),
            "ExportCsv" => Export(reader, csv: true),
            "ExportJson" => Export(reader, csv: false),
            "BackupDatabase" => BackupDatabase(reader),
            "PreviewRunImport" or "CommitRunImport" => DispatchImport(request),
            "ExportDiagnosticsReport" => ExportDiagnosticsReport(reader),
            "SetRunReflection" => ReflectionHandlers.SetRunReflection(_host, request.RequestId, reader),
            "GetReflectionSummary" => ReflectionHandlers.GetReflectionSummary(_host, reader),
            "DutyCommunity" => CommunityHandlers.HandleAsync(_host, request, CancellationToken.None).GetAwaiter().GetResult(),
            "QueryCandidateObservations" => CandidateHandlers.Query(_host, reader),
            "ReviewCandidateObservation" => CandidateHandlers.Review(_host, request.RequestId, reader),
            "ExportCandidateEvidence" => CandidateHandlers.Export(_host, reader),
            "ConfirmCalibration" => CalibrationHandlers.Confirm(_host, reader),
            "DiscardCalibration" => CalibrationHandlers.Discard(_host, reader),
            "GetCalibrationShareCode" => SharedCalibrationHandlers.GetShareCode(_host, reader),
            "GetCalibrationEvidence" => CalibrationEvidenceHandlers.Get(_host, reader),
            "GetTemplateObservation" => GetTemplateObservation(reader),
            "CheckSharedCalibration" => SharedCalibrationHandlers.Check(_host, reader),
            "ImportCalibrationCode" => SharedCalibrationHandlers.Import(_host, reader),
            "AcceptSharedQueueInference" => SharedCalibrationHandlers.AcceptQueueInference(_host, reader),
            "RejectSharedCalibration" => SharedCalibrationHandlers.Reject(_host, reader),
            "GetSpeechSettings" => SpeechHandlers.GetSettings(_host, reader),
            "UpdateSpeechSettings" => SpeechHandlers.UpdateSettings(_host, reader),
            "CheckDatabaseIntegrity" => SpeechHandlers.CheckDatabaseIntegrity(_host, reader),
            "SynthesizeSpeech" => SpeechHandlers.SynthesizeAsync(_host, reader, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "CheckUpdateNow" => UpdateHandlers.CheckNowAsync(_host, reader, CancellationToken.None)
                .GetAwaiter().GetResult(),
            "StartUpdateDownload" => UpdateHandlers.StartDownload(_host, reader),
            "CancelUpdateDownload" => UpdateHandlers.CancelDownload(_host, reader),
            "ResolveUpdateMirrorOffer" => UpdateHandlers.ResolveMirrorOffer(_host, reader),
            "SetDisplayTimeZone" => DisplayTimeZoneHandlers.Set(reader),
            _ => throw UnknownMessageType(request.MessageType),
        };
    }

    /// <summary>
    /// Answers one of <see cref="AsynchronousMessageTypes"/> without holding the caller's thread. Any
    /// other message type is answered synchronously and returned as a completed task.
    /// </summary>
    /// <param name="request">Decoded request.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    public Task<JsonObject> DispatchAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.MessageType switch
        {
            "SynthesizeSpeech" => SpeechHandlers.SynthesizeAsync(
                _host, new PayloadReader(request.Payload), cancellationToken),
            "CheckDatabaseIntegrity" => CheckDatabaseIntegrityAsync(request, cancellationToken),
            "CheckUpdateNow" => UpdateHandlers.CheckNowAsync(_host, new PayloadReader(request.Payload), cancellationToken),
            "DutyCommunity" => CommunityHandlers.HandleAsync(_host, request, cancellationToken),
            "PreviewRunImport" or "CommitRunImport" => DispatchImportAsync(request, cancellationToken),
            "ExportCsv" or "ExportJson" => RunFileJobAsync(() => InvokeExport(
                new PayloadReader(request.Payload), request.MessageType == "ExportCsv", cancellationToken), cancellationToken),
            "BackupDatabase" or "ExportDiagnosticsReport" or "ExportCandidateEvidence" =>
                RunFileJobAsync(() => Dispatch(request), cancellationToken),
            "BatchMutateRuns" when request.Payload.ContainsKey("selection_id") =>
                DispatchStagedBatchAsync(request, cancellationToken),
            "PreviewRunRegionMigration" when request.Payload.ContainsKey("selection_id") =>
                RunOffLoop(PrepareRegionMigrationPreview(new PayloadReader(request.Payload)), cancellationToken),
            "QueryHistorySelection" => Task.Run(() =>
                HistorySelectionHandlers.Query(_host, new PayloadReader(request.Payload), cancellationToken), cancellationToken),
            _ => Task.FromResult(Dispatch(request)),
        };
    }

    /// <summary>
    /// Runs one whole-file job - an export, the backup, the diagnostics report or the candidate
    /// evidence - off the connection's read loop, one at a time across the host. SQLite and file
    /// writing are synchronous; the connection keeps its cancellation and deferred-response admission.
    /// </summary>
    private async Task<JsonObject> RunFileJobAsync(Func<JsonObject> work, CancellationToken cancellationToken)
    {
        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>
    /// Runs prepared work on the thread pool. The token is checked inside the work: a staged batch
    /// that the client left before it started is not applied, one already started commits or rolls
    /// back as a whole, and its receipt answers the retry.
    /// </summary>
    private static Task<JsonObject> RunOffLoop(Func<JsonObject> work, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return work();
        });

    /// <summary>
    /// A <c>BatchMutateRuns</c> naming a staged selection. The request is parsed and the sealed
    /// set is taken here, on the read loop and in receive order, so a release that the client sends
    /// after this request cannot revoke an admitted batch (HistorySelectionStore.RequireStaged). A
    /// selection that is already gone is not refused here: the batch looks for its receipt first,
    /// so a retry of a batch that was applied still replays after its selection expired, and only
    /// a batch with no receipt asks for the selection again and gets <c>ERR_NOT_FOUND</c>.
    /// </summary>
    private Task<JsonObject> DispatchStagedBatchAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        var command = RequestParsers.BatchMutateRuns(request.RequestId, new PayloadReader(request.Payload));
        IReadOnlyList<BatchRunTarget>? admitted = null;
        try { admitted = _host.HistorySelections.RequireStaged(command.SelectionId!); }
        catch (CollectorException ex) when (ex.Code == ErrorCodes.NotFound) { /* decided inside the batch, see above */ }
        return RunOffLoop(() => BatchMutateRuns(command,
            selectionId => admitted ?? _host.HistorySelections.RequireStaged(selectionId)), cancellationToken);
    }

    private JsonObject DispatchImport(IpcRequest request)
    {
        if (!TryBeginImport())
            return ReplayFinishedCommit(request, CancellationToken.None);
        try { return InvokeImport(request, CancellationToken.None); }
        finally { Volatile.Write(ref _importInFlight, 0); }
    }

    private Task<JsonObject> DispatchImportAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryBeginImport())
        {
            if (request.MessageType != "CommitRunImport")
                throw ImportBusy();
            // The lookup opens a read connection of its own: keep it off the read loop too.
            return RunOffLoop(() => ReplayFinishedCommit(request, cancellationToken), cancellationToken);
        }

        // Always run the finally, including cancellation before the delegate starts.
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return InvokeImport(request, cancellationToken);
            }
            finally { Volatile.Write(ref _importInFlight, 0); }
        });
    }

    private bool TryBeginImport() => Interlocked.CompareExchange(ref _importInFlight, 1, 0) == 0;

    private static CollectorException ImportBusy() =>
        new(ErrorCodes.DbBusy, CollectorTexts.ImportBusy(), retryable: true);

    /// <summary>
    /// Answers a <c>CommitRunImport</c> that arrives while the import slot is taken (audit
    /// 2026-11-26, Gb-2). Its <c>request_id</c> is looked up first, on a read-only snapshot that
    /// waits for no writer: a commit that already finished - its response still stored, or its
    /// permanent import receipt kept after that response was pruned (RB-3) - replays its stored result
    /// (the commit path finds the receipt before it writes anything), so a retry after a lost answer is not
    /// refused just because another import is running. A request whose id has no stored response -
    /// the commit still running under that id, or a new one - gets the retryable
    /// <c>ERR_DB_BUSY</c> the contract names, and the client asks again with the same id.
    /// </summary>
    private JsonObject ReplayFinishedCommit(IpcRequest request, CancellationToken cancellationToken)
    {
        if (request.MessageType != "CommitRunImport" || !Guid.TryParseExact(request.RequestId, "D", out _))
            throw ImportBusy();
        var finished = _host.Database.ReadSnapshot((snapshot, transaction) =>
            new Storage.Repositories.IdempotencyRepository(snapshot, _host.Clock).IsFinishedImportCommit(request.RequestId, transaction),
            cancellationToken);
        if (!finished)
            throw ImportBusy();
        return InvokeImport(request, cancellationToken);
    }

    private JsonObject InvokeImport(IpcRequest request, CancellationToken cancellationToken) =>
        request.MessageType == "PreviewRunImport"
            ? RunImportHandlers.Preview(_host, request.Payload, cancellationToken)
            : RunImportHandlers.Commit(_host, request.RequestId, request.Payload, cancellationToken);

    /// <summary>
    /// Answers <c>CheckDatabaseIntegrity</c> off this connection's thread, one scan at a time.
    ///
    /// A full read of the database file, seconds on a large one: answered off this connection's
    /// thread so the status polls queued behind it are not held up. It takes no database gate
    /// either (SqliteDatabase.CheckIntegrity), so capture goes on.
    ///
    /// A request that arrives while a scan is running is refused, not queued: a queue would let
    /// a client stack up passes over the whole file, and the answer it is waiting for is the one
    /// already being computed. <c>ERR_DB_BUSY</c> is the code the check already answers with
    /// when the file is locked elsewhere, and it is retryable, so a client that wants the answer
    /// can simply ask again (2026-09-21 review finding 14).
    /// </summary>
    /// <param name="request">Decoded request.</param>
    /// <param name="cancellationToken">The connection's token.</param>
    private Task<JsonObject> CheckDatabaseIntegrityAsync(IpcRequest request, CancellationToken cancellationToken)
    {
        if (!TryBeginIntegrityCheck())
        {
            throw new CollectorException(
                ErrorCodes.DbBusy, CollectorTexts.DatabaseIntegrityCheckBusy(), retryable: true);
        }

        // The token is checked inside the work rather than given to Task.Run: a task that is
        // cancelled before it starts never runs its body, and the slot would stay taken for the
        // life of the process. The scan itself takes the token too, so a client that leaves
        // mid-scan releases its pipe instance and this slot instead of holding both until the
        // whole file has been read.
        return Task.Run(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return SpeechHandlers.CheckDatabaseIntegrity(
                    _host, new PayloadReader(request.Payload), cancellationToken);
            }
            finally
            {
                EndIntegrityCheck();
            }
        });
    }

    /// <summary>
    /// Takes the single scan slot, or returns false when a scan is already running. Internal so
    /// a test can hold the slot without a database big enough to scan slowly.
    /// </summary>
    internal bool TryBeginIntegrityCheck() =>
        Interlocked.CompareExchange(ref _integrityCheckInFlight, 1, 0) == 0;

    /// <summary>Gives the scan slot back. Internal for the same reason.</summary>
    internal void EndIntegrityCheck() => Volatile.Write(ref _integrityCheckInFlight, 0);

    /// <summary>The refusal for a message type the contract does not declare.</summary>
    /// <param name="messageType">Message type received.</param>
    public static CollectorException UnknownMessageType(string messageType) => new(
        ErrorCodes.BadRequest,
        CollectorTexts.IpcUnknownMessageType(),
        new Dictionary<string, object?> { ["message_type"] = messageType },
        field: "message_type");

    private static JsonObject GetVersion(PayloadReader reader)
    {
        reader.RequireEmpty();
        return new JsonObject
        {
            ["collector_version"] = Program.Version,
            ["protocol_version"] = IpcEnvelope.ProtocolVersion,
            ["build_id"] = null,
        };
    }

    private JsonObject GetStatus(PayloadReader reader)
    {
        reader.RequireEmpty();

        var snapshot = _host.Capture.Snapshot();
        var messages = new List<UserText>(snapshot.Warnings);
        if (_host.Recovery.RecoveredCount > 0)
        {
            messages.Add(CollectorTexts.StatusRecoveredRuns(_host.Recovery.RecoveredCount));
        }

        // Log losses are rare -- two processes appending at the same instant, an unwritable
        // folder -- and silent by design, so this is the only place the user can find out
        // that the diagnostic log has holes in it (review finding M7).
        if (_host.Logger.SuppressedWrites is > 0 and var suppressed)
        {
            messages.Add(CollectorTexts.StatusLogWritesLost(suppressed));
        }

        var status = new JsonObject
        {
            ["collector_version"] = Program.Version,
            ["protocol_version"] = IpcEnvelope.ProtocolVersion,
            ["schema_version"] = _host.Database.SchemaVersion,

            // Constant on purpose: this process answers messages only once the database is
            // open, migrated and integrity-checked, and exits rather than serve without one.
            // The field stays because the contract declares it (review finding L5).
            ["database_ready"] = true,
            ["database_path"] = _host.Database.Path,
            ["uptime_ms"] = _host.UptimeMs,
            ["capture"] = CaptureWire.CaptureStatus(snapshot),
            ["warnings"] = Wire.Strings(messages.Select(message => message.Chinese)),
            ["warning_refs"] = UserText.ToJsonArray(messages),

            // Poll-driven: reading the status is what makes a check fall due, and the answer comes
            // from the cache whether or not one was scheduled (docs/privacy-boundary.md §8.4). The
            // download's progress is read here too: the Desktop polls this while one runs.
            ["update"] = Update.UpdateWire.Status(_host.Updates.Observe(), _host.UpdateDownloads.Snapshot()),
        };

        // The game, Npcap and the two Oodle disclosures. The last two exist because
        // DEC-OODLE-01 requires the user to be told, on the first-run page, that the default
        // decompressor reads the game executable from disk.
        foreach (var (key, value) in CaptureWire.StatusExtras(snapshot))
        {
            status[key] = value?.DeepClone();
        }

        return status;
    }

    private JsonObject GetCaptureStatus(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureWire.CaptureStatus(_host.Capture.Snapshot());
    }

    private JsonObject ListCaptureAdapters(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureWire.Adapters(_host.Capture.Snapshot(), _host.Capture.RescanAdapters());
    }

    private JsonObject GetProtocolProfileStatus(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureWire.Profile(_host.Capture.Snapshot().Profile);
    }

    private JsonObject StartCapture(PayloadReader reader)
    {
        reader.RejectUnknown("adapter_id", "process_id");
        var adapterId = reader.String("adapter_id", 400);
        var processId = reader.Int("process_id", 0);
        return CaptureWire.CaptureStatus(_host.Capture.Start(adapterId, processId));
    }

    private JsonObject SelectGameProcess(PayloadReader reader)
    {
        reader.RejectUnknown("process_id", "selection_token");
        var processId = reader.RequiredInt("process_id", 1);
        return CaptureWire.CaptureStatus(_host.Capture.SelectGameProcess(
            processId, reader.RequiredUuid("selection_token")));
    }

    private JsonObject StopCapture(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureWire.CaptureStatus(_host.Capture.Stop());
    }

    private JsonObject StartCaptureValidation(PayloadReader reader)
    {
        reader.RejectUnknown("adapter_id", "switch_from_unsupported", "identity_probe");
        var adapter = reader.String("adapter_id", 400);
        var identityProbe = reader.Bool("identity_probe") == true;
        return reader.Bool("switch_from_unsupported") == true
            ? _host.Capture.SwitchToMetadataObservation(_host.Validation, adapter, identityProbe)
            : _host.Validation.Start(adapter, identityProbe);
    }

    private JsonObject GetTemplateObservation(PayloadReader reader)
    {
        reader.RequireEmpty();
        return _host.Validation.GetTemplateObservation();
    }

    private JsonObject ValidationStatus(PayloadReader reader, bool stop)
    {
        reader.RequireEmpty();
        return stop ? _host.Validation.Stop() : _host.Validation.Snapshot();
    }

    private JsonObject ValidationMarker(PayloadReader reader)
    {
        reader.RejectUnknown("marker");
        // As long as the longest marker on the allowlist, so a new one cannot be refused here first.
        return _host.Validation.AddMarker(reader.RequiredString(
            "marker", Capture.CaptureTraceMarkerText.Allowed.Max(marker => marker.Length)));
    }

    private JsonObject GetCurrentRun(PayloadReader reader)
    {
        reader.RequireEmpty();

        var current = _host.LiveProtocol?.GetCurrentRun();
        if (current is null)
        {
            // Tests may replace the complete protocol bridge with a diagnostics-only sink.
            // In that shape the capture snapshot remains the authoritative state source.
            var state = _host.Capture.Snapshot().RunState;
            return new JsonObject
            {
                ["state"] = EnumWire<RunState>.Format(state),
                ["run"] = null,
                ["elapsed_ms"] = null,
            };
        }

        var payload = new JsonObject
        {
            ["state"] = EnumWire<RunState>.Format(current.State),
            ["run"] = current.Run is null ? null : Wire.DisplayRun(current.Run),
            ["elapsed_ms"] = current.ElapsedMs,
        };
        if (current.DutyPhase is { } phase)
        {
            payload["duty_phase"] = phase;
        }
        return payload;
    }

    private JsonObject QueryRunCalendarDays(PayloadReader reader)
    {
        reader.RejectUnknown("month", "filter");
        var month = RequestParsers.CalendarMonth(reader);
        var days = _host.Runs.QueryCalendarDays(month, RequestParsers.Filter(reader.Object("filter")));
        return new JsonObject
        {
            ["month"] = month,
            ["days"] = new JsonArray(days.Select(day => (JsonNode)JsonValue.Create(day)!).ToArray()),
        };
    }

    private JsonObject QueryRuns(PayloadReader reader)
    {
        reader.RejectUnknown("filter", "sort", "page", "page_size");
        var filter = RequestParsers.Filter(reader.Object("filter"));
        var sort = RequestParsers.Sort(reader.Object("sort"));
        var paging = RequestParsers.Paging(reader);
        return Wire.Runs(_host.Runs.Query(filter, sort, paging.Page, paging.PageSize));
    }

    private JsonObject GetDashboardStats(PayloadReader reader)
    {
        reader.RejectUnknown("filter", "trend_granularity");
        return Wire.Dashboard(_host.Statistics.GetDashboard(
            RequestParsers.Filter(reader.Object("filter")),
            RequestParsers.TrendGranularity(reader)));
    }

    private JsonObject GetResultStats(PayloadReader reader)
    {
        reader.RejectUnknown("filter");
        return Wire.ResultStats(_host.Statistics.GetResultStats(RequestParsers.Filter(reader.Object("filter"))));
    }

    private JsonObject GetDungeonStats(PayloadReader reader)
    {
        var (filter, paging, sort) = PagedStats(reader);
        var rows = _host.Statistics.GetDungeonStats(filter);
        var ordered = StatsSorting.Order(
            rows,
            sort,
            row => row.AttemptCount,
            row => row.CompletedCount,
            row => row.CompletionRate,
            row => row.AverageDurationMs,
            row => row.DutyName);

        // distinct_count is the number of duties the filter matched, not the number of rows
        // this page happens to carry. It is stated on the wire because a client counting its
        // own rows silently reports the page size once TopN or paging truncates the list.
        var page = Page(ordered, paging, Wire.DungeonRow);
        page["distinct_count"] = ordered.Count;
        return page;
    }

    private JsonObject GetJobStats(PayloadReader reader)
    {
        var (filter, paging, sort) = PagedStats(reader);
        var rows = _host.Statistics.GetJobStats(filter);
        var ordered = StatsSorting.Order(
            rows,
            sort,
            row => row.AttemptCount,
            row => row.CompletedCount,
            row => row.CompletionRate,
            row => row.AverageDurationMs,
            row => row.JobName);
        return Page(ordered, paging, Wire.JobRow);
    }

    private JsonObject GetRunRevisions(PayloadReader reader)
    {
        reader.RejectUnknown("run_id", "page", "page_size");
        var runId = reader.RequiredUuid("run_id");
        var paging = RequestParsers.Paging(reader);

        if (_host.Runs.Get(runId) is null)
        {
            throw CollectorException.NotFound(runId);
        }

        var page = _host.Revisions.ListForRun(runId, paging.Page, paging.PageSize);
        var items = new JsonArray();
        foreach (var revision in page.Items)
        {
            items.Add(Wire.Revision(revision));
        }

        return Wire.PagedItems(items, page.PageNumber, page.PageSize, page.Total);
    }

    private JsonObject GetRunEvents(PayloadReader reader)
    {
        reader.RejectUnknown("run_id");
        var runId = reader.RequiredUuid("run_id");
        var run = _host.Runs.Get(runId) ?? throw CollectorException.NotFound(runId);

        var events = new JsonArray();
        foreach (var runEvent in _host.Events.ListForRun(runId))
        {
            events.Add(Wire.RunEventEntry(runEvent, run.ProtocolProfileId));
        }

        return new JsonObject
        {
            ["run_id"] = runId,
            ["events"] = events,
        };
    }

    private JsonObject GetCaptureSettings(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureSettingsStore.Wire(CaptureSettingsStore.Read(_host.Settings));
    }

    private JsonObject UpdateCaptureSettings(PayloadReader reader)
    {
        var update = RequestParsers.CaptureSettings(reader);
        return CaptureSettingsStore.Wire(_host.UpdateCaptureSettings(update));
    }

    private JsonObject CreateManualRun(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.CreateManualRun(RequestParsers.CreateManualRun(requestId, reader));
        return PublishAndRender(outcome, LiveEventKind.RunCreated);
    }

    private JsonObject CorrectRun(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.CorrectRun(RequestParsers.CorrectRun(requestId, reader));
        return PublishAndRender(outcome, LiveEventKind.RunUpdated);
    }

    private JsonObject SoftDeleteRun(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.SoftDeleteRun(RequestParsers.RunReason(requestId, reader));
        return PublishAndRender(outcome, LiveEventKind.RunUpdated);
    }

    private JsonObject RestoreRun(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.RestoreRun(RequestParsers.RunReason(requestId, reader));
        return PublishAndRender(outcome, LiveEventKind.RunUpdated);
    }

    private JsonObject UndoRevision(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.UndoRevision(RequestParsers.RunReason(requestId, reader));
        return PublishAndRender(outcome, LiveEventKind.RunUpdated);
    }

    private JsonObject BatchMutateRuns(string requestId, PayloadReader reader) =>
        BatchMutateRuns(RequestParsers.BatchMutateRuns(requestId, reader), _host.HistorySelections.RequireStaged);

    private JsonObject BatchMutateRuns(BatchMutateRunsCommand command,
        Func<string, IReadOnlyList<BatchRunTarget>> loadSelection)
    {
        var outcome = _host.Mutations.BatchMutateRuns(command, loadSelection);
        if (!outcome.IdempotentReplay)
            _host.LiveEvents.PublishStatsInvalidated("历史记录已批量更新，请重新查询。");
        var response = new JsonObject
        {
            ["changed_count"] = outcome.ChangedCount,
            ["action"] = outcome.Action,
            ["idempotent_replay"] = outcome.IdempotentReplay,
        };
        if (command.SelectionId is null)
            response["run_ids"] = new JsonArray(outcome.RunIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        return response;
    }

    private JsonObject GetHistoryRetentionSettings(PayloadReader reader)
    { reader.RequireEmpty(); return new JsonObject { ["retention_days"] = _host.HistoryRetention.GetRetentionDays() }; }

    private JsonObject PreviewRunRegionMigration(PayloadReader reader) => PrepareRegionMigrationPreview(reader)();

    /// <summary>
    /// Parses a region-move preview and takes its targets (a staged set is taken now, in receive
    /// order); the returned work reads the database. Deferred for a staged set (G-1).
    /// </summary>
    private Func<JsonObject> PrepareRegionMigrationPreview(PayloadReader reader)
    {
        reader.RejectUnknown("runs", "selection_id", "region", "profile_id");
        var (region, profileId) = RequestParsers.RegionDestination(reader);
        var selectionId = HistorySelectionHandlers.ReadTargetId(reader);
        var targets = selectionId is null ? RequestParsers.BatchRunTargets(reader) : _host.HistorySelections.RequireStaged(selectionId);
        return () =>
        {
            var preview = selectionId is null
                ? _host.Mutations.PreviewRunRegionMigration(targets, region, profileId)
                : _host.Mutations.PreviewStagedRunRegionMigration(targets, region, profileId);
            return new JsonObject
            {
                ["source_region"] = EnumWire<Region>.Format(preview.SourceRegion),
                ["contributing_count"] = preview.ContributingCount,
                ["target_baseline"] = preview.TargetBaseline,
                ["target_completed_count"] = preview.TargetCompletedCount,
            };
        };
    }

    private JsonObject UpdateHistoryRetentionSettings(PayloadReader reader)
    {
        reader.RejectUnknown("retention_days");
        return new JsonObject { ["retention_days"] = _host.HistoryRetention.UpdateRetentionDays(
            reader.RequiredInt("retention_days", 0, Storage.Mutations.HistoryRetentionService.MaxRetentionDays)) };
    }

    private JsonObject GetPendingImageCleanup(PayloadReader reader)
    {
        reader.RequireEmpty(); return new JsonObject { ["run_ids"] = new JsonArray(
            _host.HistoryRetention.PendingImageCleanup().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()) };
    }

    private JsonObject AcknowledgeImageCleanup(PayloadReader reader)
    {
        reader.RejectUnknown("run_ids");
        if (!reader.Has("run_ids")) throw CollectorException.BadRequest(CollectorTexts.IpcFieldMissing("run_ids"), "run_ids");
        return new JsonObject { ["acknowledged_count"] = _host.HistoryRetention.AcknowledgeImageCleanup(RequestParsers.RunIds(reader)) };
    }

    private JsonObject UpdateBaseline(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.UpdateAchievementBaseline(
            RequestParsers.UpdateBaseline(requestId, reader));

        // A replay, or a save that left the goal and the baseline as they were, changed nothing the
        // statistics read (audit 2026-10-03, S33-3).
        if (outcome.Changed)
        {
            _host.LiveEvents.PublishStatsInvalidated("成就基线已更新，统计需要重新查询。");
        }

        return new JsonObject
        {
            ["region"] = EnumWire<Region>.Format(outcome.Settings.Region),
            ["profile_id"] = outcome.Settings.ProfileId,
            ["goal_count"] = outcome.Settings.GoalCount,
            ["baseline_completed_count"] = outcome.Settings.BaselineCompletedCount,
            ["baseline_effective_at"] = UtcTimestamp.ToText(outcome.Settings.BaselineEffectiveAt),
            ["updated_at_utc"] = UtcTimestamp.ToText(outcome.Settings.UpdatedAtUtc),
            ["audit_event_id"] = outcome.AuditEventId,
            ["idempotent_replay"] = outcome.IdempotentReplay,
        };
    }

    private JsonObject Export(PayloadReader reader, bool csv)
    {
        _exportGate.Wait();
        try { return InvokeExport(reader, csv, CancellationToken.None); }
        finally { _exportGate.Release(); }
    }

    private JsonObject InvokeExport(PayloadReader reader, bool csv, CancellationToken cancellationToken)
    {
        // include_revisions was declared and then discarded: no export ever carried revisions. It
        // is no longer part of the contract and is refused like any other undeclared field
        // (audit 2026-10-03, OF-7).
        reader.RejectUnknown("target_path", "filter", "overwrite", "sort", "selection_id");
        var targetPath = reader.RequiredString("target_path", 32000);
        var filterReader = reader.Object("filter");
        var selectionId = HistorySelectionHandlers.ReadStagedId(reader);
        IReadOnlyList<BatchRunTarget>? targets = null;
        if (selectionId is not null)
        {
            filterReader?.RejectUnknown("include_deleted");
            if (filterReader?.Bool("include_deleted") is false)
                throw CollectorException.BadRequest(CollectorTexts.ExportSelectionRequiresDeleted(), "payload.filter.include_deleted");
            targets = _host.HistorySelections.RequireStaged(selectionId);
        }
        var filter = targets is null ? RequestParsers.Filter(filterReader) : new RunFilter { IncludeDeleted = true };
        var overwrite = reader.Bool("overwrite") ?? false;
        // The same RunSort QueryRuns takes: the Desktop sends the history list's current order,
        // so the file reads top to bottom the way the list does (contracts/CHANGELOG.md 2026-11-03).
        var sort = RequestParsers.Sort(reader.Object("sort"));

        var outcome = csv
            ? _exporter.ExportCsv(targetPath, filter, overwrite, sort, cancellationToken, targets)
            : _exporter.ExportJson(targetPath, filter, overwrite, sort, cancellationToken, targets);

        return new JsonObject
        {
            ["target_path"] = outcome.TargetPath,
            ["row_count"] = outcome.RowCount,
            ["byte_count"] = outcome.ByteCount,
            ["completed_at_utc"] = UtcTimestamp.ToText(outcome.CompletedAtUtc),
        };
    }

    private JsonObject BackupDatabase(PayloadReader reader)
    {
        reader.RejectUnknown("target_path", "overwrite");

        // target_path is optional in this build: omitting it selects the managed backup
        // folder next to the database. See the contract change request in docs/data-model.md.
        var targetPath = reader.String("target_path", 32000);
        var result = _host.Backups.CreateBackup(targetPath, reader.Bool("overwrite") ?? false);

        return new JsonObject
        {
            ["target_path"] = result.TargetPath,
            ["byte_count"] = result.ByteCount,
            ["completed_at_utc"] = UtcTimestamp.ToText(result.CompletedAtUtc),
            ["integrity_check_passed"] = result.IntegrityCheckPassed,
            ["pruned_count"] = result.PrunedCount,
        };
    }

    private JsonObject ExportDiagnosticsReport(PayloadReader reader)
    {
        var request = RequestParsers.DiagnosticsReport(reader);

        // The report is built from the same snapshot the diagnostics page reads, taken here
        // rather than passed in, so the file always describes the moment it was asked for.
        var result = _host.DiagnosticsReports.Write(
            _host.Capture.Snapshot() with
            {
                OnlineSpeech = _host.Speech.Diagnostics(),
                UpdateCheck = _host.Updates.Diagnostics(),
                LastValidationFailure = _host.Validation.LastFailure,
                DutyCommunity = _host.CommunityLibrary.Diagnostics(),
            },
            Program.Version,
            request.TargetPath,
            request.Overwrite);

        return new JsonObject
        {
            ["target_path"] = result.TargetPath,
            ["byte_count"] = result.ByteCount,
            ["completed_at_utc"] = UtcTimestamp.ToText(result.CompletedAtUtc),
        };
    }

    private JsonObject PublishAndRender(RunMutationOutcome outcome, LiveEventKind kind)
    {
        if (!outcome.IdempotentReplay && outcome.Run is { } run)
        {
            _host.LiveEvents.PublishRun(kind, run);
            _host.LiveEvents.PublishStatsInvalidated("记录已变更，统计需要重新查询。");
        }

        var payload = new JsonObject
        {
            ["run_id"] = outcome.RunId,
            ["revision"] = outcome.Revision,
            ["audit_event_id"] = outcome.AuditEventId,
            ["idempotent_replay"] = outcome.IdempotentReplay,
        };

        if (outcome.Run is { } rendered)
        {
            payload["run"] = Wire.DisplayRun(rendered);
        }

        return payload;
    }

    private static (RunFilter? Filter, PageRequest Paging, StatsSort Sort) PagedStats(PayloadReader reader)
    {
        reader.RejectUnknown("filter", "page", "page_size", "sort");
        var filter = RequestParsers.Filter(reader.Object("filter"));
        var paging = RequestParsers.Paging(reader);
        var sort = StatsSorting.Read(reader.Object("sort"));
        return (filter, paging, sort);
    }

    private static JsonObject Page<T>(
        IReadOnlyList<T> rows, PageRequest paging, Func<T, JsonObject> render)
    {
        var items = new JsonArray();
        var offset = ((long)paging.Page - 1) * paging.PageSize;
        // A contract-valid page can be far beyond Int32 offsets. Check the long offset
        // against the materialised list before casting; an out-of-range page is empty.
        if (offset < rows.Count)
        {
            foreach (var row in rows.Skip((int)offset).Take(paging.PageSize))
            {
                items.Add(render(row));
            }
        }

        return Wire.PagedItems(items, paging.Page, paging.PageSize, rows.Count);
    }
}
