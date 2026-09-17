#pragma once

// ---------------------------------------------------------------------------
// The single object QML talks to.
//
// Borrows the backend and owns the history/statistics workflows, exposing their
// models and derived values as projections. Pages stay declarative: they read
// properties and call slots, never assembling an IPC payload themselves.
//
// Capture validation (CaptureValidationController) and export / backup
// (ExportController) are implemented elsewhere; their projections stay here
// because that is the surface QML binds to.
// ---------------------------------------------------------------------------

#include <QCoreApplication>
#include <functional>

#include <QDateTime>
#include <QJsonArray>
#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QSet>
#include <QString>
#include <QTimer>
#include <QVariantList>
#include <QVariantMap>

#include "CaptureValidationController.h"
#include "AutomaticRecordingController.h"
#include "CalibrationController.h"
#include "CandidateReviewController.h"
#include "RunListModel.h"
#include "StatsModels.h"
#include "HistoryController.h"
#include "SpeechController.h"
#include "StatisticsController.h"

namespace mr {

class AppSettings;
class CollectorProcess;
class ExportController;
class IBackend;
class TtsService;

// `final`: the constructor calls refreshAll(), which reaches the virtual Host slot
// refreshStatus(); sealing guarantees that call can never dispatch to an override.
class AppController final : public QObject, public CaptureValidationController::Host
{
    Q_OBJECT
    QML_ELEMENT
    QML_UNCREATABLE("App is registered as a singleton instance by main()")

    // -- navigation and appearance -----------------------------------------
    Q_PROPERTY(int currentPage READ currentPage WRITE navigate NOTIFY currentPageChanged)
    Q_PROPERTY(QString themeMode READ themeMode WRITE setThemeMode NOTIFY themeChanged)
    Q_PROPERTY(bool dark READ isDark NOTIFY themeChanged)
    // Validation captures, research whitelist, adapter table and raw pipeline
    // counters on the capture page. Off for players; the mock harness turns it on.
    Q_PROPERTY(bool maintainerToolsVisible READ maintainerToolsVisible NOTIFY maintainerToolsChanged)
    Q_PROPERTY(mr::AutomaticRecordingController *recording READ recording CONSTANT)
    Q_PROPERTY(int uiScale READ uiScale WRITE setUiScale NOTIFY uiScaleChanged)

    // -- backend ------------------------------------------------------------
    Q_PROPERTY(QString backendName READ backendName NOTIFY backendChanged)
    Q_PROPERTY(bool backendConnected READ backendConnected NOTIFY backendChanged)
    Q_PROPERTY(QString backendDetail READ backendDetail NOTIFY backendChanged)
    Q_PROPERTY(bool usingMockData READ usingMockData NOTIFY backendChanged)
    Q_PROPERTY(QString collectorStatusText READ collectorStatusText NOTIFY backendChanged)
    /// missing / idle / starting / running / reused / exited / stopped, or
    /// "mock" when the deterministic backend is in use.
    Q_PROPERTY(QString collectorState READ collectorState NOTIFY backendChanged)

    // -- live state ---------------------------------------------------------
    Q_PROPERTY(QVariantMap collectorStatus READ collectorStatus NOTIFY statusChanged)
    Q_PROPERTY(QVariantMap captureStatus READ captureStatus NOTIFY statusChanged)
    Q_PROPERTY(bool capturing READ capturing NOTIFY statusChanged)
    Q_PROPERTY(QVariantMap validationStatus READ validationStatus NOTIFY validationChanged)
    Q_PROPERTY(bool validationStatusLoaded READ validationStatusLoaded NOTIFY validationChanged)
    Q_PROPERTY(bool validationAvailable READ validationAvailable NOTIFY validationChanged)
    Q_PROPERTY(QString validationState READ validationState NOTIFY validationChanged)
    Q_PROPERTY(bool validationActive READ validationActive NOTIFY validationChanged)
    Q_PROPERTY(bool validationRecording READ validationRecording NOTIFY validationChanged)
    Q_PROPERTY(bool validationSaved READ validationSaved NOTIFY validationChanged)
    Q_PROPERTY(QString validationNotice READ validationNotice NOTIFY validationChanged)
    Q_PROPERTY(QString validationError READ validationError NOTIFY validationChanged)
    Q_PROPERTY(QString validationFeedback READ validationFeedback NOTIFY validationChanged)
    Q_PROPERTY(bool captureCommandBusy READ captureCommandBusy NOTIFY validationChanged)
    Q_PROPERTY(bool markerCommandBusy READ markerCommandBusy NOTIFY validationChanged)
    Q_PROPERTY(bool validationMarkerEnabled READ validationMarkerEnabled NOTIFY validationChanged)
    Q_PROPERTY(QString captureActionLabel READ captureActionLabel NOTIFY validationChanged)
    Q_PROPERTY(bool captureActionEnabled READ captureActionEnabled NOTIFY validationChanged)
    Q_PROPERTY(QString captureModeStatusText READ captureModeStatusText NOTIFY validationChanged)
    Q_PROPERTY(QString captureModeCompactText READ captureModeCompactText NOTIFY validationChanged)
    Q_PROPERTY(QString captureAdapterId READ captureAdapterId WRITE setCaptureAdapterId NOTIFY captureAdapterIdChanged)
    Q_PROPERTY(bool npcapInstalled READ npcapInstalled NOTIFY statusChanged)
    Q_PROPERTY(bool ffxivRunning READ ffxivRunning NOTIFY statusChanged)
    Q_PROPERTY(QVariantMap currentRun READ currentRun NOTIFY currentRunChanged)
    Q_PROPERTY(QString currentRunState READ currentRunState NOTIFY currentRunChanged)
    Q_PROPERTY(QVariant liveElapsedMs READ liveElapsedMs NOTIFY tick)

    // -- capture health (midstream / silent capture) -------------------------
    /// $defs/CaptureStatus.midstream_suspected: capture started after the game
    /// had already logged in, so the connection's Oodle state is unknown and
    /// every frame will be rejected until the client reconnects.
    Q_PROPERTY(bool captureMidstreamSuspected READ captureMidstreamSuspected NOTIFY statusChanged)
    /// The Collector's own hint for that situation, verbatim.
    Q_PROPERTY(QString captureHint READ captureHint NOTIFY statusChanged)
    /// Capture is RUNNING, has decoded nothing at all and is accumulating
    /// decode errors. The status line must not read "监听中" in that state.
    Q_PROPERTY(bool captureSilent READ captureSilent NOTIFY statusChanged)
    Q_PROPERTY(QString captureHealthText READ captureHealthText NOTIFY statusChanged)

    // -- capture settings (GetCaptureSettings / UpdateCaptureSettings) -------
    Q_PROPERTY(QVariantMap captureSettings READ captureSettings NOTIFY captureSettingsChanged)
    Q_PROPERTY(bool captureSettingsLoaded READ captureSettingsLoaded NOTIFY captureSettingsChanged)
    /// False once the Collector has refused the message; the settings page then
    /// disables the switches and says why.
    Q_PROPERTY(bool captureSettingsSupported READ captureSettingsSupported NOTIFY captureSettingsChanged)
    Q_PROPERTY(QString captureSettingsError READ captureSettingsError NOTIFY captureSettingsChanged)
    // -- 完整性校验 (CheckDatabaseIntegrity) ---------------------------------
    Q_PROPERTY(bool integrityCheckRunning READ integrityCheckRunning NOTIFY integrityCheckChanged)
    /// Empty before the first check; see ExportController::integrityCheckResult().
    Q_PROPERTY(QVariantMap integrityCheckResult READ integrityCheckResult NOTIFY integrityCheckChanged)
    Q_PROPERTY(mr::CandidateReviewController *candidates READ candidates CONSTANT)
    /// 本机校准: the capture page card and the confirmation dialog bind to this.
    Q_PROPERTY(mr::CalibrationController *calibration READ calibration CONSTANT)
    /// 在线语音 (docs/privacy-boundary.md §8.3): the Collector's speech
    /// settings and the settings page's 保存 / 清除 / 测试.
    Q_PROPERTY(mr::SpeechController *speech READ speech CONSTANT)

    // -- statistics ---------------------------------------------------------
    Q_PROPERTY(QVariantMap dashboard READ dashboard NOTIFY dashboardChanged)
    Q_PROPERTY(QVariantList resultBuckets READ resultBuckets NOTIFY dashboardChanged)
    Q_PROPERTY(QVariantList statCards READ statCards NOTIFY dashboardChanged)
    Q_PROPERTY(QVariantList trendBuckets READ trendBuckets NOTIFY trendChanged)
    Q_PROPERTY(QString trendMode READ trendMode WRITE setTrendMode NOTIFY trendChanged)
    /// 7 or 30: how many trailing day buckets the chart shows. Only meaningful
    /// for the day granularity; week / month always show the whole series.
    Q_PROPERTY(int trendWindowDays READ trendWindowDays WRITE setTrendWindowDays NOTIFY trendChanged)
    Q_PROPERTY(int completedLast7Days READ completedLast7Days NOTIFY trendChanged)
    Q_PROPERTY(int completedLast30Days READ completedLast30Days NOTIFY trendChanged)
    Q_PROPERTY(int goalCount READ goalCount NOTIFY dashboardChanged)
    Q_PROPERTY(int baselineCount READ baselineCount NOTIFY dashboardChanged)
    /// $defs/DashboardStats.unfinished_pending_review: runs the crash-recovery
    /// service closed without evidence and that a human still has to confirm.
    Q_PROPERTY(int pendingReviewCount READ pendingReviewCount NOTIFY dashboardChanged)
    /// The newest run still awaiting a result, or an empty map when there is
    /// none. Fetched only while pendingReviewCount > 0, so the dashboard can
    /// offer 通关 / 未通关 without opening the history page first.
    Q_PROPERTY(QVariantMap pendingReviewRun READ pendingReviewRun NOTIFY pendingReviewRunChanged)

    // -- models -------------------------------------------------------------
    Q_PROPERTY(mr::RunListModel *runs READ runs CONSTANT)
    Q_PROPERTY(mr::DungeonStatsModel *dungeons READ dungeons CONSTANT)
    Q_PROPERTY(mr::JobStatsModel *jobs READ jobs CONSTANT)

    // -- selection and dialogs ---------------------------------------------
    Q_PROPERTY(QVariantMap selectedRun READ selectedRun NOTIFY selectionChanged)
    Q_PROPERTY(bool hasSelection READ hasSelection NOTIFY selectionChanged)
    Q_PROPERTY(QVariantList selectedRunRevisions READ selectedRunRevisions NOTIFY selectionChanged)
    /// True when the newest revision of the selected run can still be undone:
    /// there is one, it is the run's current revision, and it is not revision 1
    /// (the Collector refuses that with ERR_UNDO_NOT_ALLOWED).
    Q_PROPERTY(bool selectedRunCanUndo READ selectedRunCanUndo NOTIFY selectionChanged)
    /// $defs/RunEventEntry rows for the selected run, for the 事件摘要 tab.
    Q_PROPERTY(QVariantList selectedRunEvents READ selectedRunEvents NOTIFY runEventsChanged)
    /// idle / loading / ready / unsupported / error.
    Q_PROPERTY(QString runEventsState READ runEventsState NOTIFY runEventsChanged)
    Q_PROPERTY(QString runEventsMessage READ runEventsMessage NOTIFY runEventsChanged)
    Q_PROPERTY(bool firstRun READ firstRun NOTIFY firstRunChanged)

    // -- 导随心得 -----------------------------------------------------------
    /// The GetReflectionSummary response: reflection_count,
    /// pending_completed_count, recent (list of {run, reflection}) and
    /// next_pending (a run map, or null). Empty until the first load.
    Q_PROPERTY(QVariantMap reflectionSummary READ reflectionSummary NOTIFY reflectionsChanged)
    Q_PROPERTY(bool reflectionSaving READ reflectionSaving NOTIFY reflectionsChanged)

    // -- filter options -----------------------------------------------------
    Q_PROPERTY(QVariantList dutyOptions READ dutyOptions NOTIFY optionsChanged)
    /// Every duty the bundled table knows, for correcting a record by hand.
    /// dutyOptions lists only duties this player has already recorded, so it
    /// cannot correct a record whose duty is unknown.
    Q_PROPERTY(QVariantList dutyCatalogOptions READ dutyCatalogOptions CONSTANT)
    /// Jobs a duty can actually be run on: no crafters, no gatherers.
    Q_PROPERTY(QVariantList battleJobOptions READ battleJobOptions CONSTANT)
    /// 最近打过 in the record wizard: catalogue rows (DutyCatalog keys) of the
    /// distinct duties of the latest runs, newest first, at most five. Filled
    /// by refreshRecentDuties().
    Q_PROPERTY(QVariantList recentDuties READ recentDuties NOTIFY recentDutiesChanged)
    /// This build's version, shown in the title bar.
    Q_PROPERTY(QString appVersion READ appVersion CONSTANT)
    Q_PROPERTY(QVariantList categoryOptions READ categoryOptions NOTIFY optionsChanged)
    Q_PROPERTY(QVariantList jobOptions READ jobOptions NOTIFY optionsChanged)
    Q_PROPERTY(QVariantMap historyFilter READ historyFilter NOTIFY historyFilterChanged)

    // -- feedback -----------------------------------------------------------
    Q_PROPERTY(QString toastMessage READ toastMessage NOTIFY toastChanged)

    // -- capture diagnostics -------------------------------------------------
    Q_PROPERTY(QVariantList parserErrors READ parserErrors NOTIFY statusChanged)
    Q_PROPERTY(QString liveCaptureStatus READ liveCaptureStatus CONSTANT)
    Q_PROPERTY(QString protocolProfileStatus READ protocolProfileStatus NOTIFY statusChanged)
    Q_PROPERTY(bool publicDistributionReady READ publicDistributionReady CONSTANT)
    /// $defs/CaptureAdapter rows from ListCaptureAdapters. IPv4 addresses are
    /// already masked to a /24 by the Collector and are never unmasked here.
    Q_PROPERTY(QVariantList captureAdapters READ captureAdapters NOTIFY adaptersChanged)
    Q_PROPERTY(QVariantMap adapterInfo READ adapterInfo NOTIFY adaptersChanged)
    /// $defs/ProtocolProfileStatus from GetProtocolProfileStatus.
    Q_PROPERTY(QVariantMap protocolProfile READ protocolProfile NOTIFY statusChanged)
    /// True while the profile snapshot on screen is older than the last refresh
    /// attempt, so the page can say "上次已知" instead of pretending it is live.
    Q_PROPERTY(bool protocolProfileStale READ protocolProfileStale NOTIFY validationChanged)
    /// GetStatus extras: the npcap / game facts and the two Oodle disclosures.
    Q_PROPERTY(QVariantMap npcapInfo READ npcapInfo NOTIFY statusChanged)
    Q_PROPERTY(QVariantMap gameInfo READ gameInfo NOTIFY statusChanged)
    Q_PROPERTY(QString oodleMode READ oodleMode NOTIFY statusChanged)
    Q_PROPERTY(bool readsGameExecutable READ readsGameExecutable NOTIFY statusChanged)
    Q_PROPERTY(QVariantList collectorWarnings READ collectorWarnings NOTIFY statusChanged)
    /// True once the Collector has actually sent the parser counters. They are
    /// optional in $defs/CaptureStatus, so the diagnostics page prints an em
    /// dash rather than rendering a zero it never received.
    Q_PROPERTY(bool parserStatsAvailable READ parserStatsAvailable NOTIFY statusChanged)
    /// The $defs/CaptureStatus pipeline counters, copied only where the
    /// Collector sent them. A key that is absent here was not measured.
    Q_PROPERTY(QVariantMap captureCounters READ captureCounters NOTIFY statusChanged)

    // -- first-run disclosure (DEC-OODLE-01) ---------------------------------
    Q_PROPERTY(bool disclosureAcknowledged READ disclosureAcknowledged NOTIFY disclosureChanged)
    Q_PROPERTY(QString disclosureAcknowledgedAt READ disclosureAcknowledgedAt NOTIFY disclosureChanged)

public:
    AppController(IBackend *backend, AppSettings *settings, QObject *parent = nullptr,
                  CollectorProcess *collector = nullptr);
    ~AppController() override;

    AppSettings *settings() const { return m_settings; }
    TtsService *tts() const { return m_tts; }

    /// Test-only: the supervised Collector, so a test can drive it into the
    /// Reused state and check the reconnect loop.
    CollectorProcess *collectorForTest() const;

    /// Runs that ended before this instant are history, not "刚刚完成": the
    /// completion prompt is never offered for them. Defaults to construction
    /// time; tests move it so fixture runs with fixed timestamps still count.
    void setReflectionPromptCutoffForTest(const QDateTime &utc) { m_promptCutoffUtc = utc; }

    int currentPage() const { return m_currentPage; }
    QString themeMode() const { return m_themeMode; }
    bool maintainerToolsVisible() const { return m_maintainerToolsVisible; }
    void setMaintainerToolsVisible(bool visible)
    {
        if (visible == m_maintainerToolsVisible)
            return;
        m_maintainerToolsVisible = visible;
        m_recording->setMaintenance(visible);
        emit maintainerToolsChanged();
    }

    AutomaticRecordingController *recording() const { return m_recording; }
    bool isDark() const;
    int uiScale() const;

    QString backendName() const;
    bool backendConnected() const;
    QString backendDetail() const;
    bool usingMockData() const;
    QString collectorStatusText() const;

    QVariantMap collectorStatus() const { return m_collectorStatus.toVariantMap(); }
    QVariantMap captureStatus() const;
    bool capturing() const;
    bool candidateValidationEnabled() const override
    {
        return m_captureSettingsLoaded && m_captureSettings.value(QStringLiteral("candidate_validation_enabled")).toBool(false);
    }

    QVariantMap validationStatus() const { return m_capture->status(); }
    bool validationStatusLoaded() const { return m_capture->statusLoaded(); }
    bool validationAvailable() const { return m_capture->available(); }
    QString validationState() const { return m_capture->state(); }
    bool validationActive() const { return m_capture->active(); }
    bool validationRecording() const { return m_capture->recording(); }
    bool validationSaved() const { return m_capture->saved(); }
    QString validationNotice() const { return m_capture->notice(); }
    QString validationError() const { return m_capture->error(); }
    QString validationFeedback() const { return m_capture->feedback(); }
    bool captureCommandBusy() const { return m_capture->commandBusy(); }
    bool markerCommandBusy() const { return m_capture->markerBusy(); }
    bool validationMarkerEnabled() const { return m_capture->markerEnabled(); }
    QString captureActionLabel() const { return m_capture->actionLabel(); }
    bool captureActionEnabled() const { return m_capture->actionEnabled(); }
    QString captureModeStatusText() const { return m_capture->modeStatusText(); }
    QString captureModeCompactText() const { return m_capture->modeCompactText(); }
    QString captureAdapterId() const { return m_capture->adapterId(); }

    bool captureMidstreamSuspected() const;
    QString captureHint() const;
    bool captureSilent() const;
    QString captureHealthText() const;

    QVariantMap captureSettings() const { return m_captureSettings.toVariantMap(); }
    bool captureSettingsLoaded() const { return m_captureSettingsLoaded; }
    bool captureSettingsSupported() const { return m_captureSettingsSupported; }
    QString captureSettingsError() const { return m_captureSettingsError; }
    bool integrityCheckRunning() const;
    QVariantMap integrityCheckResult() const;
    CandidateReviewController *candidates() const { return m_candidates; }
    CalibrationController *calibration() const { return m_calibration; }
    SpeechController *speech() const { return m_speech; }

    bool npcapInstalled() const;
    bool ffxivRunning() const;
    QVariantMap currentRun() const { return m_currentRun.toVariantMap(); }
    QString currentRunState() const;
    QVariant liveElapsedMs() const;

    QVariantMap dashboard() const { return m_statistics->dashboard(); }
    QVariantList resultBuckets() const;
    QVariantList statCards() const;
    QVariantList trendBuckets() const;
    QString trendMode() const { return m_statistics->trendMode(); }
    int trendWindowDays() const { return m_statistics->trendWindowDays(); }
    int completedLast7Days() const { return m_statistics->completedLast7Days(); }
    int completedLast30Days() const { return m_statistics->completedLast30Days(); }
    int goalCount() const;
    int baselineCount() const;
    int pendingReviewCount() const;
    QVariantMap pendingReviewRun() const { return m_history->pendingReviewRun(); }

    /// TTS template kind for one $defs/Run.state, or an empty string when the
    /// state is deliberately silent. Public and static so the mapping can be
    /// pinned by a table-driven test without a speech engine.
    static QString announcementKind(const QString &state);

    RunListModel *runs() const { return m_history->runs(); }
    DungeonStatsModel *dungeons() const { return m_statistics->dungeons(); }
    JobStatsModel *jobs() const { return m_statistics->jobs(); }

    QVariantMap selectedRun() const;
    bool hasSelection() const { return m_history->hasSelection(); }
    QVariantList selectedRunRevisions() const { return m_history->selectedRunRevisions(); }
    bool selectedRunCanUndo() const;
    QVariantList selectedRunEvents() const { return m_history->selectedRunEvents(); }
    QString runEventsState() const { return m_history->runEventsState(); }
    QString runEventsMessage() const { return m_history->runEventsMessage(); }
    bool firstRun() const { return m_firstRun; }

    QVariantMap reflectionSummary() const { return m_reflectionSummary.toVariantMap(); }
    bool reflectionSaving() const { return m_reflectionSaving; }

    QVariantList dutyOptions() const { return m_statistics->dutyOptions(); }
    QVariantList dutyCatalogOptions() const;
    QVariantList battleJobOptions() const;
    QVariantList recentDuties() const { return m_recentDuties; }
    /// Re-reads 最近打过: QueryRuns, matched_at_utc desc, page 1 of 50.
    Q_INVOKABLE void refreshRecentDuties();
    /// The first \a limit distinct content_ids of \a runs (in order) that the
    /// bundled catalogue knows, as catalogue rows. Soft-deleted runs and runs
    /// without a duty are skipped.
    static QVariantList recentDutiesFromRuns(const QJsonArray &runs, int limit);
    QString appVersion() const { return QCoreApplication::applicationVersion(); }
    QVariantList categoryOptions() const;
    QVariantList jobOptions() const;
    QVariantMap historyFilter() const { return m_history->historyFilter(); }

    QString toastMessage() const { return m_toastMessage; }

    QVariantList parserErrors() const;
    /// Never "RUNNING": no live capture has been verified against the real
    /// game protocol yet (docs/live-validation-guide.md).
    static QString liveCaptureStatus() { return QStringLiteral("VERIFIED_POP_TO_EXIT"); }
    QString protocolProfileStatus() const;
    static bool publicDistributionReady() { return true; }

    QString collectorState() const;
    QVariantList captureAdapters() const { return m_captureAdapters; }
    QVariantMap adapterInfo() const { return m_adapterInfo.toVariantMap(); }
    QVariantMap protocolProfile() const { return m_capture->protocolProfile(); }
    bool protocolProfileStale() const { return m_capture->profileStale(); }
    QVariantMap npcapInfo() const;
    QVariantMap gameInfo() const;
    QString oodleMode() const;
    bool readsGameExecutable() const;
    QVariantList collectorWarnings() const;
    bool parserStatsAvailable() const;
    QVariantMap captureCounters() const;

    bool disclosureAcknowledged() const;
    QString disclosureAcknowledgedAt() const;

    /// Where the next export goes when the interactive file chooser is
    /// suppressed. Set by main() from --export-target so a test or a
    /// screenshot run never blocks on a modal dialog.
    void setExportTargetOverride(const QString &directory);
    QString exportTargetOverride() const;

    // -- CaptureValidationController::Host ----------------------------------
    bool formalCapturing() const override { return capturing(); }
    QString captureProfileStatus() const override;
    void applyFormalCaptureStatus(const QVariantMap &capture) override;

public Q_SLOTS:
    // Also the two Host members QML calls directly.
    void refreshStatus() override;
    void showToast(const QString &message) override;

    void navigate(int page);
    void setThemeMode(const QString &mode);
    void toggleTheme();
    void setUiScale(int percent);
    void setTrendMode(const QString &mode);
    void setTrendWindowDays(int days);

    void refreshAll();
    void refreshCaptureValidation();
    void refreshDashboard();
    void refreshTrend();
    void refreshCaptureSettings();

    void toggleCapture();
    void setCaptureAdapterId(const QString &adapterId);
    void addCaptureValidationMarker(const QString &marker);
    void openCaptureValidationFolder();
    /// Write one $defs/CaptureSettings field through UpdateCaptureSettings.
    /// Debounced, so dragging a spin box does not send one message per step.
    void updateCaptureSetting(const QString &key, const QVariant &value);
    /// Re-read the game process facts and the protocol profile status from the
    /// Collector, and report what came back rather than claiming a rescan.
    void rescanGame();
    /// Re-enumerate the adapters through ListCaptureAdapters.
    void rescanAdapters();
    /// Write the Collector's sanitized diagnostics report to a real file.
    void exportDiagnosticsReport();

    void selectRun(const QVariantMap &run);
    void clearSelection();
    /// Load $defs/RunEventEntry rows for the current selection.
    void refreshRunEvents();

    /// Write or clear one run's 心得. An empty \a text deletes it.
    void saveReflection(const QString &runId, const QString &mood, const QString &text);
    /// Re-read GetReflectionSummary.
    void refreshReflections();
    /// Show \a run on the history page with its detail panel open. Unlike
    /// selectRun() this never toggles the selection off: it is the dashboard's
    /// "open this reflection" jump, which always has to land somewhere.
    void openHistoryForRun(const QVariantMap &run);

    void setHistoryFilter(const QVariantMap &filter);
    void resetHistoryFilter();
    /// Jump to the history page pre-filtered by one duty or one job.
    void showHistoryForContent(const QVariant &contentId);
    void showHistoryForJob(const QVariant &jobId);
    /// Jump to the history page showing only the runs awaiting review.
    void showPendingReview();

    void createManualRun(const QVariantMap &fields, const QString &reason);
    void correctSelectedRun(const QVariantMap &changes, const QString &reason);
    void softDeleteSelectedRun(const QString &reason);
    void restoreSelectedRun(const QString &reason);
    /// Undo the newest revision of the selected run through UndoRevision.
    void undoSelectedRunRevision(const QString &reason);
    /// Clear pending_review on the selected run through CorrectRun, which is
    /// what makes the acknowledgement an audited, append-only revision rather
    /// than a silent flag flip.
    void confirmSelectedRunReview(const QString &reason);
    /// Write one run's result through CorrectRun and clear pending_review with
    /// it: the program never decides a result, but a human's answer travels the
    /// ordinary audited correction path. \a result is a $defs/Run.result enum
    /// value; \a revision may be -1, in which case the revision carried by the
    /// run's own record is used. A positive jobId supplements a missing job in
    /// that same revision.
    void resolveRunResult(const QString &runId, int revision, const QString &result,
                          const QString &reason, int jobId = 0);
    void updateAchievementBaseline(int goalCount, int baselineCount, const QString &reason);
    void completeFirstRun();
    /// Persist the acknowledgement of the first-run disclosure.
    void acceptDisclosure();
    /// Forget it, so the page can be reviewed again from the settings page.
    void reopenDisclosure();

    void exportCsv();
    void exportJson();
    void backupDatabase();
    /// 数据 → 完整性校验: a read-only CheckDatabaseIntegrity. The outcome lands
    /// in integrityCheckResult; the button is disabled while it runs.
    Q_INVOKABLE void checkDatabaseIntegrity();
    /// Open the folder holding the database in the system file manager.
    void openDatabaseFolder();
    /// Opens https://npcap.com in the system browser. The only external link in
    /// the product; it is opened only on an explicit user click and nothing is
    /// fetched by this process.
    Q_INVOKABLE void openNpcapWebsite();
    /// One GetCaptureStatus, adopted into the collector status: what a
    /// calibration_changed event, or a shared-calibration request that changed
    /// state, costs. Never a run query, a statistics refresh or an announcement.
    void rereadCaptureStatus();
    /// Run one backup per calendar day while 自动备份 is on. Called once at
    /// start-up; a no-op when the switch is off or a backup already ran today.
    /// With no Collector connected yet it arms itself and runs on the first
    /// successful connection, rather than failing and marking the day attempted.
    void runDailyBackupIfDue();

    /// Told by QML that the 本次导随结果 dialog really opened for \a runId; only
    /// then is the run remembered as asked. The dialog silently drops a request
    /// raised while it is already showing another one.
    Q_INVOKABLE void resultConfirmationShown(const QString &runId);
    /// Told by QML that the dialog closed, however it closed. Any question that
    /// was raised while it was busy is asked now.
    Q_INVOKABLE void resultConfirmationClosed();

Q_SIGNALS:
    void currentPageChanged();
    void themeChanged();
    void maintainerToolsChanged();
    void uiScaleChanged();
    void backendChanged();
    void statusChanged();
    void currentRunChanged();
    void dashboardChanged();
    void trendChanged();
    void selectionChanged();
    void runEventsChanged();
    void optionsChanged();
    void recentDutiesChanged();
    void historyFilterChanged();
    void toastChanged();
    void firstRunChanged();
    void adaptersChanged();
    void validationChanged();
    void captureAdapterIdChanged();
    void captureSettingsChanged();
    void integrityCheckChanged();
    void disclosureChanged();
    void tick();
    void mutationFailed(const QString &code, const QString &message);
    /// Emitted once a mutation is accepted. \a kind is "create", "correct",
    /// "delete", "restore", "undo" or "review"; "review" covers both 确认已复核
    /// and the 本次导随结果 answer, which travel the same audited CorrectRun
    /// path. Dialogs stay open until this arrives, so a refusal can be shown in
    /// the dialog instead of only in a toast.
    void mutationSucceeded(const QString &kind, const QString &runId, int revision,
                           const QString &auditEventId);
    /// UpdateAchievementBaseline refusals, so the settings card can show the
    /// Collector's own Chinese message next to the field instead of only in a
    /// toast that scrolls away.
    void baselineFailed(const QString &code, const QString &message);

    void reflectionsChanged();
    /// \a cleared is true when the text was empty and the reflection was removed.
    void reflectionSaved(const QString &runId, bool cleared);
    void reflectionFailed(const QString &code, const QString &message);
    /// A run just finished and has no 心得 yet; the QML opens the dialog.
    /// Emitted at most once per run_id and only while 通关后弹出心得窗口 is on.
    void reflectionPromptRequested(const QVariantMap &run);
    /// A mentor duty just finished without an observable result (the shipping
    /// CN profile carries no DUTY_RESULT), so only the player knows whether it
    /// was a 通关. Emitted at most once per run_id, only for runs that ended
    /// during this session and only while 结束后询问本次结果 is on.
    void resultConfirmationRequested(const QVariantMap &run);
    void pendingReviewRunChanged();
    /// The Collector's own revision for \a runId moved on - because a live
    /// run_updated said so, or because a CorrectRun hit ERR_REVISION_CONFLICT
    /// and the fresh number was read back. A dialog holding \a runId must
    /// adopt \a revision, or its next save is refused for the same reason.
    void runRevisionChanged(const QString &runId, int revision);

private:
    void applySystemTheme();

    void handleLiveEvent(const QVariantMap &event);
    /// Adopt one accepted SetRunReflection response: selection, list, summary
    /// and the toast that says which of the two things just happened.
    void applySavedReflection(const QString &runId, const QVariantMap &payload,
                              bool cleared);
    /// Emit reflectionPromptRequested for \a run when the settings allow it,
    /// the run has no 心得 yet and this run_id has not been offered before.
    void maybePromptForReflection(const QJsonObject &run);
    /// Queue resultConfirmationRequested for a run that finished in
    /// UNKNOWN_FINAL_STATE during this session.
    void maybeConfirmResult(const QString &state, const QJsonObject &run);
    /// Raise the oldest queued result question, unless a dialog is showing one.
    void emitNextResultConfirmation();
    /// False for a run that ended before this Desktop started, which is how a
    /// replayed live event looks. Shared by the 心得 prompt, the result
    /// confirmation and the terminal announcements.
    bool endedDuringThisSession(const QJsonObject &run) const;
    /// Re-read the single newest run that is still awaiting a result.
    void refreshPendingReviewRun();
    /// Template values a real announcement would use right now. \a run is the
    /// run the announcement is about; its duty name wins over the (possibly
    /// stale) current-run card.
    QVariantMap announcementValues(const QJsonObject &run = {}) const;
    void announceState(const QString &state, const QJsonObject &run, bool matchFromServer);
    /// The terminal line for one announcement kind with no counts in it, used
    /// when the dashboard read that would have supplied them failed outright.
    static QString numberlessAnnouncement(const QString &kind);
    /// Adopt the revision a run_created / run_updated / run_finished event
    /// carries into whichever held record is about the same run.
    void adoptRunRevisionFromEvent(const QJsonObject &run);
    /// Speak the terminal line for \a state. Called from run_finished only,
    /// after the dashboard has been re-read, so {progress} is this run's number.
    /// \a numbersKnown false (that re-read failed) selects a line without counts
    /// rather than announcing a stale total.
    void announceFinished(const QString &state, const QJsonObject &run, bool numbersKnown);
    /// GetDashboardStats, calling \a then with the reply's own ok once it has
    /// been adopted. A failed read keeps the previous snapshot: the cards are
    /// a moment out of date rather than blanked.
    void requestDashboard(std::function<void(bool ok)> then);
    void refreshCaptureDetail();
    void refreshCurrentRun();

    /// True when \a event has not been seen before; remembers it if so.
    /// Bounded: only the most recent ids are kept.
    bool adoptLiveEventOnce(const QVariantMap &event);
    void flushCaptureSettings();

    /// QPointer, not a raw pointer: IpcClient fails every in-flight request
    /// from its own destructor, and those callbacks run while the backend is
    /// already half gone. The QPointer is what makes the "!m_backend" guard in
    /// every callback fire (review finding M-1).
    QPointer<IBackend> m_backend;
    AppSettings *m_settings = nullptr;
    /// Optional borrowed process authority, supplied only by composition.
    QPointer<CollectorProcess> m_collector;
    /// One auto-backup attempt per session; the date itself is stamped on success.
    bool m_autoBackupAttempted = false;
    /// Set when a backup was due before the first connection; the first
    /// connected connectionChanged runs it.
    bool m_autoBackupArmed = false;
    /// Failed connects in a row while the Collector state is Reused. At
    /// \ref kReusedTakeoverAttempts the serve lease is taken over, instead of a
    /// new child being launched on every attempt.
    int m_reusedConnectFailures = 0;
    /// One immediate relaunch per Reused episode, for a holder that quit and
    /// removed its serve.pid. Without it an old Collector that never wrote one
    /// would have a child launched at it on every reconnect.
    bool m_reusedVacantRelaunch = false;
    /// How many consecutive failed connects a Reused holder gets before the
    /// serve lease is taken from it. Only ever reached while a serve.pid still
    /// names a live holder; a vacant lease is taken on the first failure.
    static constexpr int kReusedTakeoverAttempts = 4;
    HistoryController *m_history = nullptr;
    QVariantList m_recentDuties;
    StatisticsController *m_statistics = nullptr;
    TtsService *m_tts = nullptr;
    CaptureValidationController *m_capture = nullptr;
    AutomaticRecordingController *m_recording = nullptr;
    ExportController *m_export = nullptr;
    CandidateReviewController *m_candidates = nullptr;
    CalibrationController *m_calibration = nullptr;
    SpeechController *m_speech = nullptr;

    QJsonObject m_collectorStatus;
    QJsonObject m_currentRun;
    QJsonObject m_adapterInfo;
    QJsonObject m_reflectionSummary;
    QJsonObject m_captureSettings;
    /// Keys still waiting for the debounce timer to send them.
    QJsonObject m_pendingCaptureSettings;
    /// run_ids the completion prompt has already been offered for, so a repeated
    /// live event never re-opens the dialog.
    QSet<QString> m_promptedRunIds;
    /// run_ids whose 本次导随结果 dialog really opened. Added when QML
    /// acknowledges through \ref resultConfirmationShown, never at emit time: a
    /// question the dialog dropped while busy has not been asked.
    QSet<QString> m_confirmedRunIds;
    /// Runs whose result question has been raised but not acknowledged, oldest
    /// first. Bounded, so an old QML build that never acknowledges cannot grow
    /// it without limit.
    QList<QJsonObject> m_queuedResultRuns;
    static constexpr int kMaxQueuedResultRuns = 16;
    /// Queued runs already raised once. A request the dialog silently dropped
    /// is not raised again until the dialog that was busy closes, or the same
    /// finished run would be re-offered by every later one.
    QSet<QString> m_offeredResultRunIds;
    /// True between \ref resultConfirmationShown and \ref
    /// resultConfirmationClosed, i.e. while a dialog is on screen.
    bool m_resultConfirmationBusy = false;
    /// "<run_id>|<state>" pairs already announced. Survives a reconnect, so a
    /// replayed run_finished cannot speak the same run twice even though the
    /// Collector's sequence counter restarts with it.
    QSet<QString> m_announcedRunStates;
    /// The Collector replays its recent events to every client that connects,
    /// so a run that finished before this Desktop started arrives looking live.
    /// Anything that ended before this instant is never offered as 刚刚完成.
    QDateTime m_promptCutoffUtc;
    QVariantList m_captureAdapters;

    QTimer m_tickTimer;
    QTimer m_toastTimer;
    QTimer m_captureSettingsTimer;

    /// event_ids already acted on, and the order they arrived in. The bus
    /// replays up to 64 recent events to every new subscriber without marking
    /// them as replays; event_id is stable across that replay and unique per
    /// event, so it is the only identity that survives both a reconnect and a
    /// restart. Bounded at \ref kSeenEventIdLimit, above the replay window.
    QSet<QString> m_seenEventIds;
    QStringList m_seenEventOrder;
    static constexpr int kSeenEventIdLimit = 256;

    /// Highest live-event sequence adopted so far, a second guard for a
    /// Collector that sends no event_id. Deliberately NOT reset on disconnect,
    /// or every reconnect to the same Collector re-processes its whole replay
    /// window; a restarted Collector sends event_ids this process has never
    /// seen, so its events are adopted and pull this watermark back down.
    qint64 m_lastLiveSequence = -1;
    QString m_themeMode = QStringLiteral("system");
    bool m_maintainerToolsVisible = false;
    QString m_toastMessage;
    QString m_captureSettingsError;
    int m_currentPage = 0;
    bool m_systemDark = true;
    bool m_firstRun = false;
    bool m_reflectionSaving = false;
    bool m_captureSettingsLoaded = false;
    /// True while an UpdateCaptureSettings write is on the wire. The 2 s
    /// automatic-recording poll returns the Collector's *previous* answer;
    /// adopting it while a write is pending or in flight makes a switch the user
    /// just moved snap back (review finding M-8).
    bool m_captureSettingsInFlight = false;
    bool m_captureSettingsSupported = true;
    quint64 m_candidateSettingsGeneration = 0;
};

} // namespace mr
