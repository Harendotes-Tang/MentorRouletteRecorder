using System.Text.Json.Nodes;
using MentorRecorder.Collector.Contracts.Errors;
using MentorRecorder.Collector.Capture;
using MentorRecorder.Collector.Domain;
using MentorRecorder.Collector.Domain.Mutations;
using MentorRecorder.Collector.Domain.Queries;
using MentorRecorder.Collector.Domain.Time;

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

    /// <summary>Creates a dispatcher over a host.</summary>
    /// <param name="host">Open collector host.</param>
    public MessageDispatcher(CollectorHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Every message type this build answers.</summary>
    public static IReadOnlySet<string> KnownMessageTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "GetVersion", "GetStatus", "GetCaptureStatus", "ListCaptureAdapters", "StartCapture",
        "StopCapture", "GetProtocolProfileStatus", "GetCurrentRun", "QueryRuns", "GetDashboardStats",
        "GetDungeonStats", "GetJobStats", "GetResultStats", "CreateManualRun", "CorrectRun",
        "SoftDeleteRun", "RestoreRun", "GetRunRevisions", "UpdateAchievementBaseline", "ExportCsv",
        "ExportJson", "BackupDatabase", "SubscribeLiveEvents", "ExportDiagnosticsReport",
        "SetRunReflection", "GetReflectionSummary",
        "StartCaptureValidation", "GetCaptureValidationStatus", "AddCaptureValidationMarker", "StopCaptureValidation",
        "UndoRevision", "GetRunEvents", "GetCaptureSettings", "UpdateCaptureSettings",
        "QueryCandidateObservations", "ReviewCandidateObservation", "ExportCandidateEvidence",
        "ConfirmCalibration", "DiscardCalibration",
        "GetCalibrationShareCode", "CheckSharedCalibration", "ImportCalibrationCode", "AcceptSharedQueueInference",
        "RejectSharedCalibration",
        "GetSpeechSettings", "UpdateSpeechSettings", "SynthesizeSpeech", "CheckDatabaseIntegrity",
    };

    /// <summary>
    /// Message types answered off the connection's read loop, because they wait on something slower
    /// than the database: <c>SynthesizeSpeech</c> can wait for a queue slot and a network request. The
    /// connection keeps answering other requests meanwhile and writes this answer when it is ready.
    /// </summary>
    public static IReadOnlySet<string> AsynchronousMessageTypes { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "SynthesizeSpeech", "CheckDatabaseIntegrity" };

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
            "StopCapture" => StopCapture(reader),
            "StartCaptureValidation" => StartCaptureValidation(reader),
            "GetCaptureValidationStatus" => ValidationStatus(reader, stop: false),
            "StopCaptureValidation" => ValidationStatus(reader, stop: true),
            "AddCaptureValidationMarker" => ValidationMarker(reader),
            "GetCurrentRun" => GetCurrentRun(reader),
            "QueryRuns" => QueryRuns(reader),
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
            "UndoRevision" => UndoRevision(request.RequestId, reader),
            "UpdateAchievementBaseline" => UpdateBaseline(request.RequestId, reader),
            "ExportCsv" => Export(reader, csv: true),
            "ExportJson" => Export(reader, csv: false),
            "BackupDatabase" => BackupDatabase(reader),
            "ExportDiagnosticsReport" => ExportDiagnosticsReport(reader),
            "SetRunReflection" => ReflectionHandlers.SetRunReflection(_host, request.RequestId, reader),
            "GetReflectionSummary" => ReflectionHandlers.GetReflectionSummary(_host, reader),
            "QueryCandidateObservations" => CandidateHandlers.Query(_host, reader),
            "ReviewCandidateObservation" => CandidateHandlers.Review(_host, request.RequestId, reader),
            "ExportCandidateEvidence" => CandidateHandlers.Export(_host, reader),
            "ConfirmCalibration" => CalibrationHandlers.Confirm(_host, reader),
            "DiscardCalibration" => CalibrationHandlers.Discard(_host, reader),
            "GetCalibrationShareCode" => SharedCalibrationHandlers.GetShareCode(_host, reader),
            "CheckSharedCalibration" => SharedCalibrationHandlers.Check(_host, reader),
            "ImportCalibrationCode" => SharedCalibrationHandlers.Import(_host, reader),
            "AcceptSharedQueueInference" => SharedCalibrationHandlers.AcceptQueueInference(_host, reader),
            "RejectSharedCalibration" => SharedCalibrationHandlers.Reject(_host, reader),
            "GetSpeechSettings" => SpeechHandlers.GetSettings(_host, reader),
            "UpdateSpeechSettings" => SpeechHandlers.UpdateSettings(_host, reader),
            "CheckDatabaseIntegrity" => SpeechHandlers.CheckDatabaseIntegrity(_host, reader),
            "SynthesizeSpeech" => SpeechHandlers.SynthesizeAsync(_host, reader, CancellationToken.None)
                .GetAwaiter().GetResult(),
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
            // A full read of the database file, seconds on a large one: answered off this
            // connection's thread so the status polls queued behind it are not held up. It takes
            // no database gate either (SqliteDatabase.CheckIntegrity), so capture goes on.
            "CheckDatabaseIntegrity" => Task.Run(
                () => SpeechHandlers.CheckDatabaseIntegrity(_host, new PayloadReader(request.Payload)),
                cancellationToken),
            _ => Task.FromResult(Dispatch(request)),
        };
    }

    /// <summary>The refusal for a message type the contract does not declare.</summary>
    /// <param name="messageType">Message type received.</param>
    public static CollectorException UnknownMessageType(string messageType) => new(
        ErrorCodes.BadRequest,
        "收到本版本不支持的消息类型，请确认两端版本一致。",
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
        var messages = new List<string>(snapshot.Warnings);
        if (_host.Recovery.RecoveredCount > 0)
        {
            messages.Add($"启动时恢复了 {_host.Recovery.RecoveredCount} 条未完结记录，请在「待复核」中确认。");
        }

        // Log losses are rare -- two processes appending at the same instant, an unwritable
        // folder -- and silent by design, so this is the only place the user can find out
        // that the diagnostic log has holes in it (review finding M7).
        if (_host.Logger.SuppressedWrites is > 0 and var suppressed)
        {
            messages.Add($"有 {suppressed} 条诊断日志未能写入，本机日志可能不完整。");
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
            ["warnings"] = Wire.Strings(messages),

            // Poll-driven: reading the status is what makes a check fall due, and the answer comes
            // from the cache whether or not one was scheduled (docs/privacy-boundary.md §8.4).
            ["update"] = Update.UpdateWire.Status(_host.Updates.Observe()),
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

    private JsonObject StopCapture(PayloadReader reader)
    {
        reader.RequireEmpty();
        return CaptureWire.CaptureStatus(_host.Capture.Stop());
    }

    private JsonObject StartCaptureValidation(PayloadReader reader)
    {
        reader.RejectUnknown("adapter_id");
        return _host.Validation.Start(reader.String("adapter_id", 400));
    }

    private JsonObject ValidationStatus(PayloadReader reader, bool stop)
    {
        reader.RequireEmpty();
        return stop ? _host.Validation.Stop() : _host.Validation.Snapshot();
    }

    private JsonObject ValidationMarker(PayloadReader reader)
    {
        reader.RejectUnknown("marker");
        return _host.Validation.AddMarker(reader.RequiredString("marker", 7));
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

        return new JsonObject
        {
            ["state"] = EnumWire<RunState>.Format(current.State),
            ["run"] = current.Run is null ? null : Wire.Run(current.Run),
            ["elapsed_ms"] = current.ElapsedMs,
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
        CaptureSettingsSnapshot? applied = null;
        if ((update.CandidateValidationEnabled.HasValue || update.ResearchPayloadOpcodes is not null) && _host.LiveProtocol is { } pipeline)
            pipeline.ApplyCandidateSettings(update.CandidateValidationEnabled,
                () => applied = CaptureSettingsStore.Apply(_host.Settings, update), update.ResearchPayloadOpcodes);
        else
            applied = CaptureSettingsStore.Apply(_host.Settings, update);

        // The two settings that have an effect somewhere other than the settings table are
        // pushed at their owners here, so "saved" and "in force" are the same moment.
        _host.ApplyCaptureSettings(applied!, applyCandidateMode: false);
        return CaptureSettingsStore.Wire(applied!);
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

    private JsonObject UpdateBaseline(string requestId, PayloadReader reader)
    {
        var outcome = _host.Mutations.UpdateAchievementBaseline(
            RequestParsers.UpdateBaseline(requestId, reader));

        if (!outcome.IdempotentReplay)
        {
            _host.LiveEvents.PublishStatsInvalidated("成就基线已更新，统计需要重新查询。");
        }

        return new JsonObject
        {
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
        reader.RejectUnknown("target_path", "filter", "include_revisions", "overwrite");
        var targetPath = reader.RequiredString("target_path", 32000);
        var filter = RequestParsers.Filter(reader.Object("filter"));
        var overwrite = reader.Bool("overwrite") ?? false;
        _ = reader.Bool("include_revisions");

        var outcome = csv
            ? _host.Exporter.ExportCsv(targetPath, filter, overwrite)
            : _host.Exporter.ExportJson(targetPath, filter, overwrite);

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
            payload["run"] = Wire.Run(rendered);
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
        foreach (var row in rows.Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize))
        {
            items.Add(render(row));
        }

        return Wire.PagedItems(items, paging.Page, paging.PageSize, rows.Count);
    }
}
