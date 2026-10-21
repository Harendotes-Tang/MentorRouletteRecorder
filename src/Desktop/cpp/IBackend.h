#pragma once

// ---------------------------------------------------------------------------
// Backend abstraction.
//
// Every screen in the UI talks to an IBackend, never to a database and never
// to a socket. Two implementations exist:
//
//   MockBackend - deterministic sample data for screenshots, tests and offline
//                 UI review;
//   IpcBackend  - the real Named Pipe client.
//
// All calls are asynchronous: they return a BackendReply that emits
// finished(ok, payload, errorCode, errorMessage) exactly once.
// ---------------------------------------------------------------------------

#include <QJsonArray>
#include <QJsonObject>
#include <QList>
#include <QObject>
#include <QString>
#include <QStringList>
#include <QVariantMap>

#include <functional>

namespace mr {

/// One in-flight request. Owned by the backend; auto-deleted after it fires.
class BackendReply : public QObject
{
    Q_OBJECT
    Q_PROPERTY(bool finished READ isFinished NOTIFY done)
    Q_PROPERTY(bool ok READ ok NOTIFY done)

public:
    explicit BackendReply(QString requestId, QString messageType,
                          QObject *parent = nullptr);

    QString requestId() const { return m_requestId; }
    QString messageType() const { return m_messageType; }

    bool isFinished() const { return m_finished; }
    bool ok() const { return m_ok; }
    QJsonObject payload() const { return m_payload; }
    QString errorCode() const { return m_errorCode; }
    QString errorMessage() const { return m_errorMessage; }
    /// $defs/ErrorPayload.details of a failed reply: structured, non-sensitive
    /// context such as ERR_SHARE_CODE_UNAVAILABLE's \c reason. Empty on success
    /// and whenever the error carried none. A handler that needs it holds a
    /// QPointer to the reply: whenDone() runs before the reply is deleted.
    QVariantMap errorDetails() const { return m_errorDetails.toVariantMap(); }
    /// $defs/ErrorPayload.message_ref of a failed reply: the error's sentence
    /// as a catalogue key (contracts/collector-texts.md), or an invalid QVariant.
    /// errorMessage() is already that sentence in the language on screen when
    /// the reply arrived; a handler that keeps the text for later holds a
    /// QPointer to the reply and keeps collector_text::live(errorRef(),
    /// errorMessage()) instead, so it follows a language switch.
    QVariant errorRef() const
    {
        return m_errorRef.isEmpty() ? QVariant() : QVariant(m_errorRef.toVariantMap());
    }

    /// QML-friendly view of payload().
    Q_INVOKABLE QVariantMap payloadMap() const { return m_payload.toVariantMap(); }

    /// Complete the reply. Calling either twice is a no-op.
    void succeed(const QJsonObject &payload);
    ///  messageRef is the error's `message_ref`; when this build can render
    /// it, errorMessage() is that sentence in the interface language and
    ///  message (the Collector's Simplified Chinese) is the fallback.
    void fail(const QString &code, const QString &message, const QJsonObject &details = {},
              const QJsonObject &messageRef = {});
    /// Fails a request that never left this process - no connection, a write
    /// the pipe refused, a frame too large to send - so the Collector cannot
    /// have acted on it. \ref neverSent() says so to whoever handles the failure.
    void failUnsent(const QString &code, const QString &message);
    /// A local timeout or lost connection left a sent request unanswered.
    /// Keeps the existing error code; unlike fail(), this never represents a
    /// Collector business refusal. Set before done(), including synchronous handlers.
    void failTransport(const QString &code, const QString &message);
    bool transportFailed() const { return m_transportFailed; }
    /// True only after \ref failUnsent(). Every other failure - a timeout, a
    /// dropped pipe, the Collector's own refusal - may come after the Collector
    /// received the request, and a mutation may then already be applied.
    bool neverSent() const { return m_neverSent; }

    using Handler = std::function<void(bool ok, const QVariantMap &payload,
                                       const QString &errorCode,
                                       const QString &errorMessage)>;

    /// The only supported way to observe a reply.
    ///
    /// A BackendReply may already be finished when it comes back from
    /// IBackend::request(): IpcClient::send() fails synchronously when the pipe
    /// is down, and a plain connect() would silently drop that outcome, leaving a
    /// dialog waiting for mutationFailed/mutationSucceeded forever. whenDone()
    /// connects and then reads the terminal value back, with a shared guard so
    /// the handler still runs exactly once.
    void whenDone(QObject *context, Handler handler);

Q_SIGNALS:
    /// Emitted exactly once. \a payload is empty when \a ok is false.
    void done(bool ok, const QVariantMap &payload,
              const QString &errorCode, const QString &errorMessage);

private:
    void finish();

    QString m_requestId;
    QString m_messageType;
    bool m_finished = false;
    bool m_ok = false;
    bool m_neverSent = false;
    bool m_transportFailed = false;
    QJsonObject m_payload;
    QString m_errorCode;
    QString m_errorMessage;
    QJsonObject m_errorDetails;
    QJsonObject m_errorRef;
};

/// $defs/PreviewRunImportSourceRequest. Exactly one of rows (edited candidates, which take
/// precedence), filePath or text is the source. An empty column mapping is left out; pageSize 0
/// leaves out page_size and asks for the whole preview in one answer.
struct RunImportSource
{
    QString kind;
    QString name;
    QString timeZone;
    QJsonArray rows;
    QString filePath;
    QString text;
    QJsonObject columnMapping;
    int pageSize = 0;
};

/// Abstract Collector facade. Mirrors contracts/ipc-v1.schema.json.
class IBackend : public QObject
{
    Q_OBJECT
    Q_PROPERTY(QString backendName READ backendName CONSTANT)
    Q_PROPERTY(bool connected READ isConnected NOTIFY connectionChanged)
    Q_PROPERTY(QString connectionDetail READ connectionDetail NOTIFY connectionChanged)

public:
    explicit IBackend(QObject *parent = nullptr) : QObject(parent) {}
    ~IBackend() override;

    virtual QString backendName() const = 0;
    virtual bool isConnected() const = 0;
    virtual QString connectionDetail() const { return {}; }

    /// Generic entry point. Every wrapper below funnels through this.
    virtual BackendReply *request(const QString &messageType,
                                  const QJsonObject &payload = {}) = 0;
    /// Like request(), but the envelope carries \a requestId rather than a
    /// fresh one, so a mutation the client stopped waiting for can be resent
    /// and answered by the Collector's idempotency instead of being applied
    /// twice (contracts/error-codes.md). An empty id mints one. This default,
    /// for a backend without request ids, ignores it; IpcBackend sends it, and
    /// MockBackend honours it as the Collector does (replay of the first
    /// outcome, ERR_IDEMPOTENCY_CONFLICT for a different payload).
    virtual BackendReply *requestWithId(const QString &messageType, const QJsonObject &payload,
                                        const QString &requestId);

    // -- version and status -------------------------------------------------
    BackendReply *getVersion();
    BackendReply *getStatus();
    BackendReply *getCaptureStatus();
    BackendReply *getProtocolProfileStatus();

    // -- capture control ----------------------------------------------------
    /// $defs/CaptureSettings. Answered by Collectors that carry the message;
    /// an older one refuses with ERR_UNKNOWN_MESSAGE and the settings page then
    /// says so instead of showing a switch that does nothing.
    BackendReply *getCaptureSettings();
    /// Only the keys present in \a changes are sent: the contract treats an
    /// omitted key as "leave it alone", so a partial write must stay partial.
    BackendReply *updateCaptureSettings(const QJsonObject &changes);
    BackendReply *listCaptureAdapters();
    BackendReply *startCapture(const QString &adapterId = {});
    BackendReply *selectGameProcess(int processId, const QString &selectionToken);
    BackendReply *stopCapture();
    BackendReply *startCaptureValidation(const QString &adapterId = {});
    BackendReply *getCaptureValidationStatus();
    BackendReply *addCaptureValidationMarker(const QString &marker);
    BackendReply *stopCaptureValidation();

    // -- 本机校准 ---------------------------
    /// $defs/ConfirmCalibrationRequest. \a verdicts is one entry per timeline
    /// event that requires confirmation; all CORRECT writes the local profile,
    /// any WRONG voids the draft with ERR_CALIBRATION_REJECTED.
    BackendReply *confirmCalibration(const QJsonArray &verdicts);
    /// Throws away this session's calibration evidence and observes again.
    /// \a retireLocalProfile is 重新校准: it also stops using the profile this
    /// machine calibrated, whose file is renamed rather than deleted.
    /// \a restoreLocalProfile is 恢复上一份本机校准, the undo of that: it puts the
    /// retired profile back and records with it again. The two are mutually
    /// exclusive and the Collector refuses a request carrying both. Both fields
    /// are optional in $defs/DiscardCalibrationRequest and are put on the wire
    /// only when true, so an unchanged 重新观察 keeps sending an empty payload.
    BackendReply *discardCalibration(bool retireLocalProfile = false,
                                     bool restoreLocalProfile = false);

    // -- 共享校准 -----------------
    /// The share code of the local calibration in force. Refused with
    /// ERR_SHARE_CODE_UNAVAILABLE, details.reason saying why.
    BackendReply *getCalibrationShareCode();
    /// 立即检查: asks the Collector to fetch shared calibrations now.
    BackendReply *checkSharedCalibration();
    /// 导入校准码: \a code is the text the player pasted.
    BackendReply *importCalibrationCode(const QString &code);
    /// Accepts, once for this build, a shared calibration that infers the
    /// match from the player's own queue request.
    BackendReply *acceptSharedQueueInference();
    /// 不用共享的，我自己校准.
    BackendReply *rejectSharedCalibration();

    // -- live ---------------------------------------------------------------
    BackendReply *getCurrentRun();
    BackendReply *subscribeLiveEvents();

    // -- queries ------------------------------------------------------------
    /// Full-month history days, independent of the selected date bounds.
    BackendReply *queryRunCalendarDays(const QString &month, const QJsonObject &filter = {});
    BackendReply *queryRuns(const QJsonObject &filter, int page, int pageSize,
                            const QJsonObject &sort = {});
    /// Without paging the Collector returns the oldest 50 revisions; callers
    /// that need a particular slice of the chain pass page/pageSize (max 200).
    BackendReply *getRunRevisions(const QString &runId, int page = 0, int pageSize = 0);
    /// $defs/RunEventEntry rows for one run: the sanitized opcode-level trail
    /// the Collector already writes to run_events. No payload bytes are ever
    /// carried, only a hash prefix and the parsed fields.
    BackendReply *getRunEvents(const QString &runId);

    // -- candidate evidence (independent of formal runs) ---------------------
    /// Empty session/time filters are sent as null; observations contain metadata only.
    BackendReply *queryCandidateObservations(const QString &sessionId = {},
                                              const QString &fromUtc = {},
                                              const QString &toUtc = {},
                                              int page = 1, int pageSize = 50);
    /// Records a review in the candidate ledger without updating a formal run.
    BackendReply *reviewCandidateObservation(const QString &id, const QString &verdict,
                                              const QString &note = {});
    /// An empty path selects the Collector's managed candidate evidence folder.
    BackendReply *exportCandidateEvidence(const QString &targetPath = {});

    // -- statistics ---------------------------------------------------------
    /// \a trendGranularity is the optional $defs/StatsRequest field: "day",
    /// "week" or "month". An empty string omits it, and the Collector reads an
    /// omitted value as "day".
    BackendReply *getDashboardStats(const QJsonObject &filter = {},
                                    const QString &trendGranularity = {});
    BackendReply *getDungeonStats(const QJsonObject &filter = {},
                                  int page = 1, int pageSize = 200);
    BackendReply *getJobStats(const QJsonObject &filter = {},
                              int page = 1, int pageSize = 200);
    BackendReply *getResultStats(const QJsonObject &filter = {});

    // -- mutations ----------------------------------------------------------
    /// \a requestId: see requestWithId(); empty for a new request.
    BackendReply *createManualRun(const QJsonObject &run, const QString &reason,
                                  const QString &requestId = {});
    BackendReply *correctRun(const QString &runId, int expectedRevision,
                             const QJsonObject &changes, const QString &reason,
                             const QString &requestId = {});
    BackendReply *softDeleteRun(const QString &runId, int expectedRevision,
                                const QString &reason);
    BackendReply *restoreRun(const QString &runId, int expectedRevision,
                             const QString &reason);
    /// Replays the previous revision's old_value as a new, append-only
    /// revision. The Collector refuses revision 1 (ERR_UNDO_NOT_ALLOWED); the
    /// UI only offers the button on the latest revision above 1.
    BackendReply *undoRevision(const QString &runId, int expectedRevision,
                               const QString &reason);
    /// \a baselineEffectiveAtUtc is required by the contract
    /// ($defs/UpdateAchievementBaselineRequest); an empty string means "now".
    BackendReply *updateAchievementBaseline(int goalCount,
                                            int baselineCompletedCount,
                                            const QString &reason,
                                            const QString &baselineEffectiveAtUtc = {});

    // -- 已删除记录 ---------------------------------------------------------
    /// $defs/BatchMutateRunsRequest. \a action is soft_delete, restore or purge;
    /// \a runs holds {run_id, expected_revision} objects. \a requestId: see
    /// requestWithId(); the history page resends an unanswered batch with it.
    BackendReply *batchMutateRuns(const QString &action, const QJsonArray &runs,
                                  const QString &reason, const QString &requestId = {});
    /// {} -> $defs/HistoryRetentionSettings: how long deleted records are kept.
    BackendReply *getHistoryRetentionSettings();
    /// $defs/HistoryRetentionSettings. 0 turns the automatic purge off.
    BackendReply *updateHistoryRetentionSettings(int retentionDays);
    /// {} -> $defs/ImageCleanupIds: purged runs whose note images are still on disk.
    BackendReply *getPendingImageCleanup();
    /// $defs/ImageCleanupIds: the runs whose note images the Desktop has removed.
    BackendReply *acknowledgeImageCleanup(const QStringList &runIds);

    // -- 导入记录 -----------------------------------------------------------
    /// $defs/PreviewRunImportSourceRequest: read and classify a source. With a
    /// page size the answer is page 1 of a frozen preview.
    BackendReply *previewRunImport(const RunImportSource &source);
    /// $defs/PreviewRunImportPageRequest: one more page of a frozen preview.
    BackendReply *previewRunImportPage(const QString &previewId, int page);
    /// $defs/CommitRunImportRequest. confirm_own_records is always true - the
    /// dialog does not commit without it - and deduct_from_baseline travels only
    /// when true, so an older Collector never sees it unasked. \a requestId: see
    /// requestWithId(); an unanswered commit is resent with it.
    BackendReply *commitRunImport(const QString &previewId, const QList<int> &rowNumbers,
                                  bool deductFromBaseline, const QString &requestId = {});
    /// The payload previewRunImport() sends for \a source.
    static QJsonObject previewRunImportPayload(const RunImportSource &source);

    // -- 导随心得 -----------------------------------------------------------
    /// $defs/SetRunReflectionRequest. The Collector trims \a text; an empty
    /// result deletes the reflection. Deliberately no reason and no
    /// expected_revision: a reflection is the user's own diary, not a correction
    /// of a captured fact, and it never bumps a run revision.
    BackendReply *setRunReflection(const QString &runId, const QString &mood,
                                   const QString &text);
    /// $defs/GetReflectionSummaryRequest. \a recentLimit is the number of
    /// recent entries the dashboard panel shows (0..20).
    BackendReply *getReflectionSummary(int recentLimit = 3);

    // -- export and backup --------------------------------------------------
    BackendReply *exportCsv(const QString &targetPath, const QJsonObject &filter = {});
    BackendReply *exportJson(const QString &targetPath, const QJsonObject &filter = {});
    /// An empty \a targetPath omits the field, which selects the Collector's
    /// managed backups folder.
    BackendReply *backupDatabase(const QString &targetPath = {});
    /// Writes the sanitized diagnostics report (docs/capture-diagnostics.md
    /// section 9). An empty \a targetPath omits the field, which selects
    /// <db dir>/diagnostics/diag_<yyyyMMdd_HHmm>.json on the Collector side.
    BackendReply *exportDiagnosticsReport(const QString &targetPath = {});
    /// Read-only PRAGMA integrity_check of the open database ({} ->
    /// {passed, detail, checked_at_utc}). A Collector older than the message
    /// refuses it with ERR_UNKNOWN_MESSAGE.
    BackendReply *checkDatabaseIntegrity();

    // -- 在线语音 (docs/privacy-boundary.md §8.3) ----------------------------
    /// {} -> $defs/SpeechSettings. Never carries a key, only has_key. A
    /// Collector older than the message refuses it with ERR_UNKNOWN_MESSAGE.
    BackendReply *getSpeechSettings();
    /// $defs/UpdateSpeechSettingsRequest. Only the keys present in \a fields
    /// are sent, so an omitted field stays as it is; unknown keys are dropped.
    /// provider / azure_region / openai_base_url / openai_model / voice go out
    /// as strings (a null value is sent as null, which clears the field), and
    /// api_key only when the map has it: "" deletes the stored key, anything
    /// else replaces it. The key is write-only and never comes back.
    BackendReply *updateSpeechSettings(const QVariantMap &fields);
    /// $defs/SynthesizeSpeechRequest. \a ratePercent is held to 50..200. The
    /// Collector may take up to 8 s in its queue plus 8 s on the wire, so the
    /// IPC client gives this message its own, longer deadline
    /// (IpcClient::kSpeechRequestTimeoutMs).
    BackendReply *synthesizeSpeech(const QString &text, int ratePercent, bool test);

    // -- 检查新版本 (docs/privacy-boundary.md §8.4) ---------------------------
    /// {} -> {outcome: CHECKED|DISABLED|BLOCKED, update: $defs/UpdateStatus}.
    /// The user-triggered check, exempt from the daily throttle but not from
    /// the setting or the kill switch. The Collector performs one HTTP GET
    /// while it answers, so this may take up to ~15 s and the IPC client gives
    /// it its own deadline (IpcClient::kUpdateCheckRequestTimeoutMs); the
    /// connection keeps answering other requests meanwhile. `update` is exactly
    /// the object CollectorStatus.update carries, so the answer is adopted
    /// through the one projection UpdateController already has. A Collector
    /// older than the message refuses it with ERR_UNKNOWN_MESSAGE.
    BackendReply *checkUpdateNow();
    /// $defs/StartUpdateDownloadRequest -> {update: $defs/UpdateStatus}
    /// (docs/privacy-boundary.md §8.6). 下载并安装: the Collector downloads the
    /// installer of latest_version and its published SHA-256, and answers at once
    /// with update.download as it now stands; progress is read from GetStatus. It
    /// never runs the file. \a reinstall is the maintainer tools' 重新下载最新正式版
    /// and is put on the wire only when true, so the ordinary click sends {}.
    /// A Collector older than the message refuses it (ERR_BAD_REQUEST /
    /// ERR_UNKNOWN_MESSAGE), and the browser downloads the installer instead.
    BackendReply *startUpdateDownload(bool reinstall = false);
    /// {} -> {update: $defs/UpdateStatus}. Stops a running download and removes
    /// what was written; changes nothing in any other state.
    BackendReply *cancelUpdateDownload();

Q_SIGNALS:
    void connectionChanged();
    /// One LiveEvent payload (see $defs/LiveEvent).
    void liveEvent(const QVariantMap &event);
};

} // namespace mr
