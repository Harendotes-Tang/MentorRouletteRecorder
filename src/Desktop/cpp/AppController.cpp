#include "AppController.h"

#include "AppSettings.h"
#include "CollectorProcess.h"
#include "DutyCatalog.h"
#include "ExportController.h"
#include "Formatters.h"
#include "IBackend.h"
#include "JobCatalog.h"
#include "NoteImageStore.h"
#include "RunListModel.h"
#include "StatsModels.h"
#include "TtsService.h"

#include <QDate>
#include <QDateTime>
#include <QDesktopServices>
#include <QDir>
#include <QFileInfo>
#include <QGuiApplication>
#include <QJsonDocument>
#include <QJsonValue>
#include <QStandardPaths>
#include <QStyleHints>
#include <QUrl>
#include <QTimeZone>

#include <utility>

namespace {

constexpr int kToastMs = 3200;
/// How long a settings switch waits before it is written. Long enough that a
/// spin box being dragged sends one message, short enough to feel immediate.
constexpr int kCaptureSettingsDebounceMs = 400;

QDateTime parseUtc(const QJsonValue &value)
{
    if (!value.isString())
        return {};
    QDateTime dt = QDateTime::fromString(value.toString(), Qt::ISODateWithMs);
    if (!dt.isValid())
        dt = QDateTime::fromString(value.toString(), Qt::ISODate);
    if (dt.isValid() && dt.timeSpec() == Qt::LocalTime)
        dt.setTimeZone(QTimeZone::UTC);
    return dt;
}

/// The keys $defs/UpdateCaptureSettingsRequest accepts. An unknown key is
/// dropped rather than sent into an additionalProperties:false schema, which
/// would be refused outright.
bool isCaptureSettingKey(const QString &key)
{
    static const char *kKeys[] = {"follow_game", "autostart", "adapter_id",
                                  "log_retention_days", "allow_without_profile",
                                  "region_override", "auto_calibration_enabled",
                                  "shared_calibration_enabled", "update_check_enabled"};
    for (const char *name : kKeys) {
        if (key == QLatin1String(name))
            return true;
    }
    return false;
}

} // namespace

namespace mr {

AppController::AppController(IBackend *backend, AppSettings *settings, QObject *parent,
                             CollectorProcess *collector, TtsService::EngineMode speechEngine)
    : QObject(parent)
    , m_backend(backend)
    , m_settings(settings)
    , m_collector(collector)
    , m_history(new HistoryController(backend, this))
    , m_statistics(new StatisticsController(backend, this))
    , m_tts(new TtsService(settings, this, speechEngine))
    , m_promptCutoffUtc(QDateTime::currentDateTimeUtc())
{
    m_capture = new CaptureValidationController(this, this);
    m_capture->setBackend(backend);
    m_recording = new AutomaticRecordingController(backend, this);
    m_gameSelection = new GameSelectionController(backend, this);
    connect(this, &AppController::statusChanged, this,
            [this] { m_gameSelection->observe(captureStatus()); });
    connect(m_recording, &AutomaticRecordingController::captureObserved,
            m_gameSelection, &GameSelectionController::observe);
    connect(m_gameSelection, &GameSelectionController::selected, this,
            [this](const QVariantMap &capture) {
        adoptCaptureAnswer(QJsonObject::fromVariantMap(capture));
        emit statusChanged();
        m_recording->refresh();
    });
    connect(m_recording, &AutomaticRecordingController::settingsConfirmed, this,
            [this](const QVariantMap &settings) {
        // A confirmation of what the Collector had when the poll asked, two
        // seconds ago. Adopting it while the user's own edit is still waiting
        // out the debounce, or is on the wire, snaps the switch back.
        if (!m_pendingCaptureSettings.isEmpty() || m_captureSettingsTimer.isActive()
            || m_captureSettingsWritesInFlight > 0) {
            return;
        }
        m_captureSettings = QJsonObject::fromVariantMap(settings);
        m_captureSettingsLoaded = true;
        Q_EMIT captureSettingsChanged();
    });
    m_export = new ExportController(this);
    m_export->setBackend(backend);
    m_candidates = new CandidateReviewController(this);
    m_candidates->setBackend(backend);
    m_calibration = new CalibrationController(this);
    m_calibration->setBackend(backend);
    // Every capture status this object adopts - poll, event or reply - lands
    // in m_collectorStatus and emits statusChanged, so calibration is read from
    // one place and cannot go stale behind a single path.
    connect(this, &AppController::statusChanged, this,
            [this] { m_calibration->refreshFromCaptureStatus(captureStatus()); });
    // The recording poll reads a capture snapshot of its own every two seconds while the
    // game is up. Calibration progress changes without the state changing (a roulette is
    // seen, a zone burst closes), so the card follows that poll as well.
    connect(m_recording, &AutomaticRecordingController::captureObserved, this,
            [this](const QVariantMap &capture) { m_calibration->refreshFromCaptureStatus(capture); });
    connect(m_calibration, &CalibrationController::confirmed, this,
            [this](const QString &, bool) {
        // The Collector has written and bound the profile; the projection the
        // user sees next must come from it, not from this optimism.
        refreshStatus();
        if (m_recording)
            m_recording->refresh();
    });
    // 重新校准 stopped the profile that was recording; which profile is in force now, and
    // whether the calibration card comes back, are the Collector's to say.
    connect(m_calibration, &CalibrationController::refreshRequested, this,
            &AppController::rereadCaptureStatus);
    // 共享校准: every answer is a toast, and a request that changed shared state is
    // followed by one capture-status read so the card does not wait for the next poll.
    connect(m_calibration->shared(), &SharedCalibrationController::notice, this,
            [this](const QString &message) { showToast(message); });
    connect(m_calibration->shared(), &SharedCalibrationController::refreshRequested, this,
            &AppController::rereadCaptureStatus);
    // 在线语音: the Collector synthesizes, TtsService plays the file it wrote
    // into <dir of database_path>\tts-cache\, and a fallback is one toast.
    m_speech = new SpeechController(this);
    m_speech->setBackend(backend);
    // 检查新版本: the Collector decides whether one exists and names the page it
    // lives on; this object shows that decision and acts only on a click. It
    // rides the same single status adoption path as calibration, so it cannot go
    // stale behind one of them.
    m_update = new UpdateController(this);
    m_update->setSettings(settings);
    // The requests it sends: CheckUpdateNow (检查更新, outside the daily throttle,
    // still behind the setting and the kill switch), StartUpdateDownload and
    // CancelUpdateDownload (下载并安装 / 取消, docs/privacy-boundary.md §8.6).
    m_update->setBackend(backend);
    connect(this, &AppController::statusChanged, this,
            [this] { m_update->refreshFromStatus(collectorStatus()); });
    connect(m_update, &UpdateController::toastRequested, this, &AppController::showToast);
    // 下载并安装: while the download runs, its progress comes from the status
    // read every other part of the window uses; an answer to the two download
    // messages is adopted into that same status.
    connect(m_update, &UpdateController::statusRefreshRequested, this,
            &AppController::readCollectorStatus);
    connect(m_update, &UpdateController::updateAnswered, this, [this](const QVariantMap &update) {
        if (m_collectorStatus.isEmpty())
            return;
        m_collectorStatus.insert(QStringLiteral("update"), QJsonObject::fromVariantMap(update));
        Q_EMIT statusChanged();
    });
    // 立即安装 waits for a run to end: matched or in the duty is a run in
    // progress, and until the current run has been read it is not known.
    m_update->setRunProbe([this]() -> std::optional<bool> {
        if (!m_currentRunKnown)
            return std::nullopt;
        const QString state = currentRunState();
        return state == QLatin1String("MENTOR_MATCHED") || state == QLatin1String("ENTERED_DUTY");
    });
    m_update->setErrorFormatter(
        [this](const QString &message, const QString &code) { return errorText(message, code); });
    // The mock's "installer" is a text file it wrote itself: a mock run starts
    // no program, whatever 立即安装's checks find. Every other backend keeps
    // the shell, which UpdateController starts with.
    if (usingMockData())
        m_update->setInstallerLauncher([](const QString &) { return false; });
    m_tts->setBackend(backend);
    m_tts->setSpeech(m_speech);
    connect(m_tts, &TtsService::toastRequested, this, &AppController::showToast);
    connect(this, &AppController::statusChanged, this, [this] {
        // One failed GetStatus on a live pipe does not move the data directory.
        const QString path = m_collectorStatus.value(QStringLiteral("database_path")).toString();
        if (!path.isEmpty() || !m_backend || !m_backend->isConnected())
            m_tts->setCollectorDatabasePath(path);
    });
    connect(m_candidates, &CandidateReviewController::captureSettingsApplied, this,
            [this](const QVariantMap &settings) {
        ++m_candidateSettingsGeneration;
        m_captureSettings = QJsonObject::fromVariantMap(settings);
        m_captureSettingsLoaded = true;
        m_captureSettingsSupported = true;
        m_captureSettingsError.clear();
        Q_EMIT captureSettingsChanged();
    });
    connect(m_candidates, &CandidateReviewController::captureStatusRefreshRequested, this, [this] {
        // m_backend is a QPointer and every callback checks it (review finding
        // M-1); this signal comes from a child object, so nothing else
        // guarantees the backend outlives it.
        if (!m_backend)
            return;
        m_backend->getCaptureStatus()->whenDone(this,
            [this](bool ok, const QVariantMap &payload, const QString &, const QString &) {
                if (!ok) return;
                adoptCaptureAnswer(QJsonObject::fromVariantMap(payload));
                Q_EMIT statusChanged();
            });
    });

    // QML and the tray consume the unified capture projection through
    // validationChanged, but it depends on both validation and formal capture
    // state, so every formal status mutation is forwarded to the same notifier.
    connect(this, &AppController::statusChanged,
            this, &AppController::validationChanged);
    connect(this, &AppController::captureSettingsChanged,
            this, &AppController::validationChanged);
    connect(m_capture, &CaptureValidationController::changed,
            this, &AppController::validationChanged);
    connect(m_capture, &CaptureValidationController::mutationFailed, this,
            [this](const QString &code, const QString &message) {
        Q_EMIT mutationFailed(code, message, QStringLiteral("validation"), QString());
    });
    connect(m_export, &ExportController::toastRequested,
            this, &AppController::showToast);
    connect(m_export, &ExportController::integrityCheckChanged,
            this, &AppController::integrityCheckChanged);
    connect(m_export, &ExportController::backupSucceeded, this, [this] {
        if (m_settings)
            m_settings->setLastAutoBackupDate(m_today().toString(QStringLiteral("yyyy-MM-dd")));
    });

    connect(m_statistics, &StatisticsController::dashboardChanged, this, &AppController::dashboardChanged);
    connect(m_statistics, &StatisticsController::achievementSettingsLoadedChanged, this,
            &AppController::achievementSettingsLoadedChanged);
    connect(m_statistics, &StatisticsController::trendChanged, this, &AppController::trendChanged);
    connect(m_statistics, &StatisticsController::optionsChanged, this, &AppController::optionsChanged);
    connect(m_statistics, &StatisticsController::baselineFailed, this, &AppController::baselineFailed);
    connect(m_statistics, &StatisticsController::baselineSavingChanged, this,
            &AppController::baselineSavingChanged);
    connect(m_statistics, &StatisticsController::baselineSaved, this, &AppController::baselineSaved);
    connect(m_statistics, &StatisticsController::mutationFailed, this,
            [this](const QString &code, const QString &message) {
        Q_EMIT mutationFailed(code, message, QStringLiteral("baseline"), QString());
    });
    connect(m_statistics, &StatisticsController::toastRequested, this, &AppController::showToast);
    connect(m_history, &HistoryController::selectionChanged, this, &AppController::selectionChanged);
    connect(m_history, &HistoryController::runEventsChanged, this, &AppController::runEventsChanged);
    connect(m_history, &HistoryController::pendingReviewRunChanged, this, &AppController::pendingReviewRunChanged);
    connect(m_history, &HistoryController::runRevisionChanged, this, &AppController::runRevisionChanged);
    connect(m_history, &HistoryController::mutationFailed, this, &AppController::mutationFailed);
    connect(m_history, &HistoryController::mutationSucceeded, this, &AppController::mutationSucceeded);
    connect(m_history, &HistoryController::toastRequested, this, &AppController::showToast);
    connect(m_history, &HistoryController::refreshRequested, this, &AppController::refreshAll);
    connect(m_history, &HistoryController::checkedRunsChanged, this, [this] {
        if (m_export)
            m_export->setCheckedRunIds(m_history->checkedRunIds());
        Q_EMIT checkedHistoryRunsChanged();
    });
    connect(m_history, &HistoryController::batchChanged, this, &AppController::historyBatchChanged);
    connect(m_history, &HistoryController::filterConfirmationChanged, this, &AppController::historyFilterConfirmationChanged);
    connect(m_history, &HistoryController::retentionChanged, this, &AppController::historyRetentionChanged);
    connect(m_history, &HistoryController::batchSucceeded, this, [this](const QString &action, const QStringList &) {
        if (action == QLatin1String("purge"))
            retryHistoryImageCleanup();
    });
    m_historyImageCleanupTimer.setInterval(60000);
    connect(&m_historyImageCleanupTimer, &QTimer::timeout, this, &AppController::retryHistoryImageCleanup);
    connect(m_history, &HistoryController::historyFilterChanged, this, [this] {
        if (m_export)
            m_export->setHistoryFilter(QJsonObject::fromVariantMap(m_history->historyFilter()));
        Q_EMIT historyFilterChanged();
    });
    connect(m_statistics, &StatisticsController::dashboardRequestFinished, this, [this](bool ok) {
        if (ok)
            m_history->updatePendingReviewCount(m_statistics->pendingReviewCount());
        if (m_tts)
            m_tts->setContextValues(announcementValues());
    });

    if (m_settings) {
        m_themeMode = m_settings->themeMode();
        m_firstRun = !m_settings->firstRunCompleted();
    }
    applySystemTheme();
    if (auto *hints = QGuiApplication::styleHints()) {
        connect(hints, &QStyleHints::colorSchemeChanged, this, [this](Qt::ColorScheme) {
            applySystemTheme();
            Q_EMIT themeChanged();
        });
    }

    connect(m_backend, &IBackend::connectionChanged, this, [this] {
        Q_EMIT backendChanged();
        if (m_collector)
            m_collector->noteBackendConnected(m_backend->isConnected());

        if (!m_backend->isConnected()) {
            if (m_collector) {
                if (m_collector->state() == CollectorProcess::State::Reused) {
                    if (!m_reusedVacantRelaunch && m_collector->serveLeaseLooksVacant()) {
                        // Ordinary hand-off: the reused Collector quit and
                        // took its serve.pid with it. Nothing holds the lease,
                        // so there is nothing to take over - start our own
                        // child on the first failed connect, silently.
                        m_reusedVacantRelaunch = true;
                        m_reusedConnectFailures = 0;
                        if (!m_collector->isRunning() && !m_collector->restartPending())
                            m_collector->start();
                    } else if (++m_reusedConnectFailures >= kReusedTakeoverAttempts) {
                        // The holder is still there and still not answering;
                        // launching our own child would only exit with
                        // ERR_ALREADY_RUNNING. This is the one branch that may
                        // end another process, so it requires both halves of
                        // the test: the failed connects counted above, and the
                        // holder ignoring its own stop event.
                        m_reusedConnectFailures = 0;
                        m_collector->requestServeLeaseTakeover();
                    }
                } else if (!m_collector->isRunning() && !m_collector->restartPending()
                           && !m_collector->isStartRefused()) {
                    // A second Desktop may have reused another instance's
                    // Collector; when that owner exits, the per-user serve
                    // lease lets this process take over on the next reconnect.
                    // isRunning() prevents a duplicate launch while it boots.
                    // A Collector that refused to start (its database, its
                    // data folder) is not launched again on every failed
                    // connect: the reason is on screen instead (review OH-3).
                    m_collector->start();
                }
            }
            m_collectorStatus = QJsonObject();
            m_captureAnswerAtUtc = QDateTime();
            m_currentRun = QJsonObject();
            m_currentRunKnown = false;
            // The live-event watermark is deliberately NOT reset here: every
            // reconnect to the same Collector replays the same events. Identity
            // comes from event_id, which is stable across that replay and
            // different for a Collector that really restarted (see
            // adoptLiveEventOnce).
            m_capture->reset();
            // The dashboard numbers stay on screen, but the goal and baseline a
            // save would send are unknown until the next connection re-reads them.
            m_statistics->forgetAchievementSettings();
            m_captureSettingsLoaded = false;
            m_captureSettingsTimer.stop();
            m_pendingCaptureSettings = QJsonObject();
            Q_EMIT statusChanged();
            Q_EMIT currentRunChanged();
            Q_EMIT captureSettingsChanged();
            return;
        }

        m_reusedConnectFailures = 0;
        m_reusedVacantRelaunch = false;
        retryHistoryImageCleanup();
        m_backend->subscribeLiveEvents();
        // The daily backup needs a Collector: due at start-up, when there was
        // none yet, or on a day that began while the pipe was down, it runs
        // once the current-run read this refresh starts has come back - not
        // now, while a duty may still be in progress (refreshCurrentRun).
        refreshAll();
    });
    connect(m_backend, &IBackend::liveEvent, this, &AppController::handleLiveEvent);
    if (m_collector) {
        connect(m_collector, &CollectorProcess::leaseTakeover, this,
                [this](bool, const QString &message) { showToast(message); });
        connect(m_collector, &CollectorProcess::stateChanged, this, &AppController::backendChanged);
        connect(m_collector, &CollectorProcess::restarted, this, [this](int attempt, int delayMs) {
            showToast(QString::fromUtf8("Collector 已退出，已自动重启（第 %1 次，退避 %2 秒）")
                          .arg(attempt)
                          .arg(double(delayMs) / 1000.0, 0, 'f', 1));
        });
        // The Collector's own explanation, once, in place of a restart loop.
        connect(m_collector, &CollectorProcess::startRefused, this, [this](const QString &reason) {
            showToast(QString::fromUtf8("Collector 无法启动：%1").arg(reason));
        });
    }

    if (m_collector) {
        // Seeded before the first launch: the client may already have reached
        // an existing Collector before this object was built, and our child
        // would then exit on the serve lease without that ever being a fault.
        m_collector->noteBackendConnected(m_backend->isConnected());
        if (!m_backend->isConnected())
            m_collector->start();
    }
    if (m_backend->isConnected())
        m_backend->subscribeLiveEvents();

    // A one-second heartbeat drives the "已进行" counter on the dashboard.
    m_tickTimer.setInterval(1000);
    connect(&m_tickTimer, &QTimer::timeout, this, &AppController::tick);
    m_tickTimer.start();

    m_toastTimer.setSingleShot(true);
    connect(&m_toastTimer, &QTimer::timeout, this, [this] {
        m_toastMessage.clear();
        Q_EMIT toastChanged();
    });

    m_captureSettingsTimer.setSingleShot(true);
    m_captureSettingsTimer.setInterval(kCaptureSettingsDebounceMs);
    connect(&m_captureSettingsTimer, &QTimer::timeout,
            this, &AppController::flushCaptureSettings);

    // Started by the first runDailyBackupIfDue() only.
    m_dailyBackupTimer.setInterval(kDailyBackupCheckMs);
    connect(&m_dailyBackupTimer, &QTimer::timeout, this, &AppController::runDailyBackupIfDue);
    m_backupAfterDutyTimer.setSingleShot(true);
    m_backupAfterDutyTimer.setInterval(kBackupAfterDutyMs);
    connect(&m_backupAfterDutyTimer, &QTimer::timeout, this, &AppController::runDailyBackupIfDue);

    m_history->runs()->setPageSize(10);
    refreshAll();
    // The daily backup is started by main() for interactive runs only, so a
    // screenshot or a test never writes a file behind the user's back.
}

AppController::~AppController() = default;

CollectorProcess *AppController::collectorForTest() const
{
    return m_collector.data();
}

// ---------------------------------------------------------------------------
// Appearance
// ---------------------------------------------------------------------------

void AppController::applySystemTheme()
{
    if (auto *hints = QGuiApplication::styleHints())
        m_systemDark = hints->colorScheme() != Qt::ColorScheme::Light;
}

bool AppController::isDark() const
{
    if (m_themeMode == QLatin1String("dark"))
        return true;
    if (m_themeMode == QLatin1String("light"))
        return false;
    return m_systemDark;
}

void AppController::setThemeMode(const QString &mode)
{
    const QString normalised =
        (mode == QLatin1String("dark") || mode == QLatin1String("light"))
            ? mode
            : QStringLiteral("system");
    if (normalised == m_themeMode)
        return;
    m_themeMode = normalised;
    if (m_settings)
        m_settings->setThemeMode(normalised);
    Q_EMIT themeChanged();
}

void AppController::toggleTheme()
{
    setThemeMode(isDark() ? QStringLiteral("light") : QStringLiteral("dark"));
}

int AppController::uiScale() const
{
    return m_settings ? m_settings->uiScale() : 100;
}

void AppController::setUiScale(int percent)
{
    if (!m_settings || m_settings->uiScale() == percent)
        return;
    m_settings->setUiScale(percent);
    Q_EMIT uiScaleChanged();
    // QT_SCALE_FACTOR is injected before QApplication exists (main.cpp), so the
    // new value cannot take effect in this process.
    showToast(QString::fromUtf8("界面缩放已设为 %1%，重启后生效。").arg(uiScale()));
}

void AppController::navigate(int page)
{
    if (page == 6 && !m_maintainerToolsVisible) return;
    const int clamped = qBound(0, page, 7);
    if (clamped == m_currentPage)
        return;
    m_currentPage = clamped;
    m_candidates->setActive(clamped == 6);
    Q_EMIT currentPageChanged();
}

// ---------------------------------------------------------------------------
// Backend surface
// ---------------------------------------------------------------------------

QString AppController::backendName() const
{
    return m_backend ? m_backend->backendName() : QString();
}

bool AppController::backendConnected() const
{
    return m_backend && m_backend->isConnected();
}

QString AppController::backendDetail() const
{
    return m_backend ? m_backend->connectionDetail() : QString();
}

bool AppController::usingMockData() const
{
    return backendName() == QLatin1String("mock");
}

QString AppController::collectorStatusText() const
{
    if (usingMockData())
        return QString::fromUtf8("模拟数据");
    if (!m_collector)
        return QString::fromUtf8("未连接");
    // A live pipe is the ground truth: our own child may have exited because
    // another instance already holds the per-user serve lease.
    if (backendConnected() && m_collector->state() != CollectorProcess::State::Running)
        return QString::fromUtf8("Collector 运行中（复用已有实例）");
    return m_collector->statusText();
}

QString AppController::collectorState() const
{
    if (usingMockData())
        return QStringLiteral("mock");
    if (!m_collector)
        return QStringLiteral("missing");
    if (backendConnected() && m_collector->state() != CollectorProcess::State::Running)
        return QStringLiteral("reused");
    return m_collector->stateToken();
}

QVariantMap AppController::captureStatus() const
{
    return m_collectorStatus.value(QStringLiteral("capture")).toObject().toVariantMap();
}

bool AppController::capturing() const
{
    const QString state = m_collectorStatus.value(QStringLiteral("capture"))
                              .toObject()
                              .value(QStringLiteral("state"))
                              .toString();
    return state == QLatin1String("RUNNING") || state == QLatin1String("DEGRADED");
}

QString AppController::captureProfileStatus() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("profile_status"))
        .toString();
}

void AppController::applyFormalCaptureStatus(const QVariantMap &capture)
{
    adoptCaptureAnswer(QJsonObject::fromVariantMap(capture));
    Q_EMIT statusChanged();
}

void AppController::adoptCaptureAnswer(const QJsonObject &capture)
{
    m_collectorStatus.insert(QStringLiteral("capture"), capture);
    m_captureAnswerAtUtc = QDateTime::currentDateTimeUtc();
}

// ---------------------------------------------------------------------------
// Capture health
//
// Capture started after the client has logged in has every frame rejected while
// the state still reads RUNNING (docs/live-validation-guide.md section 6), so
// RUNNING alone must never be shown as 监听中.
// ---------------------------------------------------------------------------

bool AppController::captureMidstreamSuspected() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("midstream_suspected"))
        .toBool(false);
}

QString AppController::captureHint() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("hint"))
        .toString();
}

bool AppController::captureSilent() const
{
    if (!capturing())
        return false;
    const QJsonObject capture =
        m_collectorStatus.value(QStringLiteral("capture")).toObject();
    const QJsonValue decoded = capture.value(QStringLiteral("messages_decoded"));
    const QJsonValue errors = capture.value(QStringLiteral("decode_errors"));
    // Only a measured zero counts. An absent counter means "not measured", and
    // this page never turns that into a claim.
    if (!decoded.isDouble())
        return false;
    if (decoded.toDouble() > 0)
        return false;
    return errors.isDouble() && errors.toDouble() > 0;
}

QString AppController::captureHealthText() const
{
    if (captureMidstreamSuspected()) {
        const QString hint = captureHint();
        return hint.isEmpty()
                   ? tr("当前连接缺少可用的解码上下文，无法确认自动记录。"
                        "请登出到标题画面再重新登录一次（不用关闭游戏）。")
                   : hint;
    }
    if (captureSilent()) {
        return tr("捕获在运行，但至今没有解码出任何报文，解码失败数还在上升。"
                  "当前解码链路异常；请查看诊断，并尝试回到标题画面重新登录。");
    }
    return QString();
}

// ---------------------------------------------------------------------------
// Capture settings
// ---------------------------------------------------------------------------

void AppController::refreshCaptureSettings()
{
    if (!m_backend)
        return;
    const auto candidateGeneration = m_candidateSettingsGeneration;
    m_backend->getCaptureSettings()->whenDone(
        this, [this, candidateGeneration](bool ok, const QVariantMap &payload, const QString &code,
                     const QString &message) {
            if (candidateGeneration != m_candidateSettingsGeneration) return;
            if (ok) {
                m_captureSettings = QJsonObject::fromVariantMap(payload);
                m_captureSettingsLoaded = true;
                m_captureSettingsSupported = true;
                m_captureSettingsError.clear();
            } else if (code == QLatin1String("ERR_UNKNOWN_MESSAGE")
                       || code == QLatin1String("ERR_UNSUPPORTED")
                       || code == QLatin1String("ERR_BAD_REQUEST")) {
                // An older Collector. The switches are disabled and say so;
                // they are never shown as if they did something.
                m_captureSettings = QJsonObject();
                m_captureSettingsLoaded = false;
                m_captureSettingsSupported = false;
                m_captureSettingsError =
                    errorText(tr("当前采集器不支持捕获设置，以下开关不可用。"), code);
            } else {
                m_captureSettingsError =
                    message.isEmpty() ? tr("读取捕获设置失败：%1").arg(code) : message;
            }
            Q_EMIT captureSettingsChanged();
        });
}

void AppController::updateCaptureSetting(const QString &key, const QVariant &value)
{
    if (key == QLatin1String("follow_game") && !m_maintainerToolsVisible) return;
    if (!m_backend || !m_captureSettingsSupported || !isCaptureSettingKey(key))
        return;

    // Optimistic locally so the control does not snap back while the debounce
    // runs; the authoritative object always comes from the reply.
    m_captureSettings.insert(key, QJsonValue::fromVariant(value));
    m_pendingCaptureSettings.insert(key, QJsonValue::fromVariant(value));
    Q_EMIT captureSettingsChanged();
    m_captureSettingsTimer.start();
}

void AppController::flushCaptureSettings()
{
    if (!m_backend || m_pendingCaptureSettings.isEmpty())
        return;
    const QJsonObject changes = m_pendingCaptureSettings;
    m_pendingCaptureSettings = QJsonObject();
    const auto candidateGeneration = m_candidateSettingsGeneration;
    ++m_captureSettingsWritesInFlight;

    m_backend->updateCaptureSettings(changes)->whenDone(
        this, [this, candidateGeneration, changes](bool ok, const QVariantMap &payload,
                     const QString &code, const QString &message) {
            if (m_captureSettingsWritesInFlight > 0)
                --m_captureSettingsWritesInFlight;
            if (!ok) {
                m_captureSettingsError =
                    message.isEmpty() ? tr("保存捕获设置失败：%1").arg(code) : message;
                showToast(m_captureSettingsError);
                Q_EMIT captureSettingsChanged();
                // Re-read, so the page shows what the Collector actually has
                // rather than the value this process optimistically wrote.
                refreshCaptureSettings();
                return;
            }
            auto confirmed = QJsonObject::fromVariantMap(payload);
            if (candidateGeneration != m_candidateSettingsGeneration) {
                // The debounced request owns ordinary settings only. A delayed reply must
                // not overwrite newer confirmed candidate consent or its whitelist.
                for (const auto *name : {"candidate_validation_enabled", "research_payload_opcodes"}) {
                    const auto key = QString::fromLatin1(name);
                    if (m_captureSettings.contains(key)) confirmed.insert(key, m_captureSettings.value(key));
                }
            }
            // Another write is still outstanding, or a newer edit is waiting out
            // the debounce: this reply describes the Collector as it was before
            // those, so only the fields this write itself sent may be adopted.
            // Taking the whole object would bounce the switch the user moved
            // second back to its old value (审查第 11 条).
            if (m_captureSettingsWritesInFlight > 0 || !m_pendingCaptureSettings.isEmpty()) {
                for (auto it = confirmed.begin(); it != confirmed.end(); ++it) {
                    if (!changes.contains(it.key()) && m_captureSettings.contains(it.key()))
                        *it = m_captureSettings.value(it.key());
                }
            }
            m_captureSettings = confirmed;
            m_captureSettingsLoaded = true;
            m_captureSettingsError.clear();
            Q_EMIT captureSettingsChanged();
        });
}

bool AppController::npcapInstalled() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("npcap_installed"))
        .toBool(false);
}

bool AppController::ffxivRunning() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("ffxiv_running"))
        .toBool(false);
}

QString AppController::currentRunState() const
{
    return m_currentRun.value(QStringLiteral("state")).toString(QStringLiteral("IDLE"));
}

QVariant AppController::liveElapsedMs() const
{
    const QJsonObject run = m_currentRun.value(QStringLiteral("run")).toObject();
    const QDateTime entered = parseUtc(run.value(QStringLiteral("entered_at_utc")));
    if (!entered.isValid())
        return {};

    // The base elapsed value comes from the Collector; we only extrapolate the
    // wall-clock seconds since the answer arrived.
    const QJsonValue reported = m_currentRun.value(QStringLiteral("elapsed_ms"));
    if (reported.isNull() || !reported.isDouble())
        return {};
    const qint64 base = qint64(reported.toDouble());
    const qint64 sinceFetch =
        m_currentRun.value(QStringLiteral("_fetched_at_ms")).toVariant().toLongLong();
    if (sinceFetch <= 0)
        return QVariant::fromValue(base);
    const qint64 drift = QDateTime::currentMSecsSinceEpoch() - sinceFetch;
    return QVariant::fromValue(base + qMax<qint64>(0, drift));
}

// ---------------------------------------------------------------------------
// Refresh
// ---------------------------------------------------------------------------

void AppController::refreshAll()
{
    refreshStatus();
    refreshCaptureSettings();
    refreshDashboard();
    refreshTrend();
    refreshReflections();
    m_history->runs()->reload();
    m_statistics->dungeons()->reload();
    m_statistics->jobs()->reload();
}

void AppController::refreshStatus()
{
    if (!m_backend)
        return;

    readCollectorStatus();
    refreshCurrentRun();
    refreshCaptureDetail();
    refreshCaptureValidation();
}

void AppController::readCollectorStatus()
{
    if (!m_backend)
        return;
    m_backend->getStatus()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &,
                     const QString &) {
            m_collectorStatus = ok ? QJsonObject::fromVariantMap(payload) : QJsonObject();
            m_captureAnswerAtUtc = ok ? QDateTime::currentDateTimeUtc() : QDateTime();
            Q_EMIT statusChanged();
            Q_EMIT backendChanged();
        });
}

void AppController::refreshCaptureValidation()
{
    m_capture->refreshStatusSnapshot();
}

void AppController::refreshCurrentRun()
{
    if (!m_backend)
        return;
    m_backend->getCurrentRun()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &,
                     const QString &) {
            m_currentRun = ok ? QJsonObject::fromVariantMap(payload) : QJsonObject();
            m_currentRunKnown = ok;
            if (ok) {
                m_currentRun.insert(QStringLiteral("_fetched_at_ms"),
                                    double(QDateTime::currentMSecsSinceEpoch()));
            }
            Q_EMIT currentRunChanged();
            // The daily backup waits for a run to end; this is where it learns
            // one has, or that none was going on after a (re)connect.
            if (ok && m_dailyBackupActive)
                runDailyBackupIfDue();
        });
}

void AppController::refreshCaptureDetail()
{
    if (!m_backend)
        return;

    // The adapter list and the profile status are separate messages in ipc-v1;
    // GetStatus carries neither. The diagnostics page needs both, so they are
    // fetched alongside it rather than being invented from the status object.
    m_backend->listCaptureAdapters()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &,
                     const QString &) {
            const QJsonObject object =
                ok ? QJsonObject::fromVariantMap(payload) : QJsonObject();
            m_captureAdapters =
                object.value(QStringLiteral("adapters")).toArray().toVariantList();
            m_adapterInfo = object;
            m_adapterInfo.remove(QStringLiteral("adapters"));
            Q_EMIT adaptersChanged();
        });

    m_capture->refreshProfile();
}

void AppController::refreshDashboard()
{
    return m_statistics->refreshDashboard();
}

void AppController::requestDashboard(std::function<void(bool ok)> then)
{
    // The workflow emits its adopted snapshot first; our connected projection
    // updates dependent review state and speech context before this callback.
    QPointer<AppController> guard(this);
    m_statistics->requestDashboard([guard, then = std::move(then)](bool ok) {
        if (guard && then)
            then(ok);
    });
}

void AppController::refreshPendingReviewRun()
{
    return m_history->refreshPendingReviewRun();
}

void AppController::refreshTrend()
{
    return m_statistics->refreshTrend();
}

void AppController::setTrendMode(const QString &mode)
{
    return m_statistics->setTrendMode(mode);
}

void AppController::setTrendWindowDays(int days)
{
    return m_statistics->setTrendWindowDays(days);
}

QVariantList AppController::trendBuckets() const
{
    return m_statistics->trendBuckets();
}

// ---------------------------------------------------------------------------
// Derived dashboard values
// ---------------------------------------------------------------------------

QVariantList AppController::resultBuckets() const
{
    return m_statistics->resultBuckets();
}

QVariantList AppController::statCards() const
{
    return m_statistics->statCards();
}

int AppController::goalCount() const
{
    return m_statistics->goalCount();
}

int AppController::baselineCount() const
{
    return m_statistics->baselineCount();
}

int AppController::pendingReviewCount() const
{
    return m_statistics->pendingReviewCount();
}

// ---------------------------------------------------------------------------
// Capture diagnostics
// ---------------------------------------------------------------------------

QVariantList AppController::parserErrors() const
{
    return m_collectorStatus.value(QStringLiteral("capture"))
        .toObject()
        .value(QStringLiteral("recent_parser_errors"))
        .toArray()
        .toVariantList();
}

QString AppController::protocolProfileStatus() const
{
    const QJsonObject capture =
        m_collectorStatus.value(QStringLiteral("capture")).toObject();
    // A backend may report a more precise label than the contract enum allows
    // (the mock says SYNTHETIC_ONLY, because nothing was ever verified against
    // live traffic). Prefer it; fall back to the contract field.
    const QString label =
        capture.value(QStringLiteral("profile_status_label")).toString();
    if (!label.isEmpty())
        return label;
    const QString effective = m_capture->effectiveProfileStatus();
    if (!effective.isEmpty())
        return effective;
    return QStringLiteral("NONE");
}

QVariantMap AppController::npcapInfo() const
{
    return m_collectorStatus.value(QStringLiteral("npcap")).toObject().toVariantMap();
}

QVariantMap AppController::gameInfo() const
{
    return m_collectorStatus.value(QStringLiteral("game")).toObject().toVariantMap();
}

QString AppController::oodleMode() const
{
    return m_collectorStatus.value(QStringLiteral("oodle_mode")).toString();
}

bool AppController::readsGameExecutable() const
{
    return m_collectorStatus.value(QStringLiteral("reads_game_executable")).toBool(false);
}

QVariantList AppController::collectorWarnings() const
{
    return m_collectorStatus.value(QStringLiteral("warnings")).toArray().toVariantList();
}

bool AppController::parserStatsAvailable() const
{
    // $defs/CaptureStatus has carried the counters since contracts/CHANGELOG.md
    // entry 14, but they stay optional: a disconnected or older Collector sends
    // none. Presence is the only thing that licenses the page to print a number.
    const QJsonObject capture =
        m_collectorStatus.value(QStringLiteral("capture")).toObject();
    return capture.contains(QStringLiteral("parse_ok_count"))
        || capture.contains(QStringLiteral("parse_success_rate"));
}

QVariantMap AppController::captureCounters() const
{
    // A counter is copied only when the Collector sent it: a missing key reaches
    // QML as undefined, which CapturePage renders as an em dash. A 0 here would
    // turn "not measured" into "measured, and it is zero".
    const QJsonObject capture =
        m_collectorStatus.value(QStringLiteral("capture")).toObject();

    QVariantMap counters;
    const auto copy = [&capture, &counters](const char *name) {
        const QJsonValue value = capture.value(QLatin1String(name));
        if (!value.isUndefined() && !value.isNull())
            counters.insert(QString::fromLatin1(name), value.toVariant());
    };

    for (const char *name : {"packets_observed", "packets_dropped", "queue_depth",
                             "queue_capacity", "connection_count", "messages_decoded",
                             "decode_errors", "parse_ok_count", "parse_fail_count",
                             "duplicate_count", "ignored_count", "message_rate_per_second", "uptime_ms",
                             "last_valid_event_at_utc", "last_valid_event_kind"}) {
        copy(name);
    }

    // The mock backend reports a rate under its own older name; keep reading it
    // so the offline UI review still shows a number rather than a dash.
    if (!counters.contains(QStringLiteral("message_rate_per_second")))
        copy("message_rate_per_s");

    // Derived, not transmitted: the success rate is ok / (ok + failed), and it
    // is undefined rather than 1.0 when the parser has seen nothing at all.
    const QJsonValue ok = capture.value(QStringLiteral("parse_ok_count"));
    const QJsonValue failed = capture.value(QStringLiteral("parse_fail_count"));
    if (ok.isDouble() && failed.isDouble()) {
        const double total = ok.toDouble() + failed.toDouble();
        if (total > 0)
            counters.insert(QStringLiteral("parse_success_rate"), ok.toDouble() / total);
    } else if (capture.contains(QStringLiteral("parse_success_rate"))) {
        counters.insert(QStringLiteral("parse_success_rate"),
                        capture.value(QStringLiteral("parse_success_rate")).toVariant());
    }

    return counters;
}

// ---------------------------------------------------------------------------
// Live events and announcements
// ---------------------------------------------------------------------------

void AppController::handleLiveEvent(const QVariantMap &event)
{
    // "kind" is the finer-grained token (LiveEventBus.KindToken); "event_type"
    // is the coarse contract bucket. Keying on kind keeps stats_invalidated
    // apart from the other DiagnosticsMessage events.
    const QString kind = event.value(QStringLiteral("kind")).toString();
    const QString type = event.value(QStringLiteral("event_type")).toString();

    // A heartbeat says only that the pipe is alive; falling through to the
    // catch-all below would fire a GetStatus every five seconds.
    if (kind == QLatin1String("heartbeat") || type == QLatin1String("Heartbeat"))
        return;

    if (!adoptLiveEventOnce(event))
        return;

    if (kind == QLatin1String("candidate_observed") || type == QLatin1String("CandidateObserved")) {
        m_candidates->observeLiveEvent(event);
        return;
    }

    if (kind == QLatin1String("calibration_changed") || type == QLatin1String("CalibrationChanged")) {
        // The event carries a state token, never the timeline; the contract
        // says to re-read it. One request, and nothing else: a calibration
        // state change invalidates no run, no statistic and no announcement.
        rereadCaptureStatus();
        return;
    }

    if (kind == QLatin1String("run_state_changed")
        || type == QLatin1String("StateChanged")) {
        // The current-run card is re-read first so the rest of the UI catches
        // up; the announcement itself reads the duty off the event's own run
        // rather than waiting for that reply.
        refreshCurrentRun();
        const QString state = event.value(QStringLiteral("state")).toString();
        const QJsonObject run =
            QJsonObject::fromVariantMap(event.value(QStringLiteral("run")).toMap());
        // Only the two in-progress states are announced from here. Terminal
        // states are spoken from run_finished instead: StateChanged can skip a
        // terminal state entirely when one duty pop follows another, and
        // announcing from both would say the same thing twice.
        // A transition the bus replays from before this Desktop started was
        // announced by whoever was listening then; saying it again at launch
        // is a false 「匹配成功」 (review OH-1).
        const QJsonValue matchFromQueue = QJsonValue::fromVariant(event.value(QStringLiteral("match_from_queue")));
        if (!emittedBeforeThisSession(event)) {
            announceState(state, run, matchFromQueue.isBool() && !matchFromQueue.toBool(),
                          event.value(QStringLiteral("match_offer")).toInt());
        }
        refreshDashboard();
        if (state == QLatin1String("COMPLETED"))
            maybePromptForReflection(run);
        return;
    }

    if (kind == QLatin1String("collector_status")
        || type == QLatin1String("CaptureStatusChanged")) {
        const QVariantMap capture = event.value(QStringLiteral("capture")).toMap();
        // Published before the answer the shell already holds - typically
        // while that very request was being worked on, and written to the pipe
        // after its answer - it describes an older state and is not adopted.
        // The re-read below brings whatever is newer.
        const QDateTime emitted =
            parseUtc(QJsonValue::fromVariant(event.value(QStringLiteral("emitted_at_utc"))));
        const bool olderThanAnswer =
            emitted.isValid() && m_captureAnswerAtUtc.isValid() && emitted < m_captureAnswerAtUtc;
        if (!capture.isEmpty() && !olderThanAnswer) {
            m_collectorStatus.insert(QStringLiteral("capture"),
                                     QJsonObject::fromVariantMap(capture));
            if (emitted.isValid()
                && (!m_captureAnswerAtUtc.isValid() || emitted > m_captureAnswerAtUtc)) {
                m_captureAnswerAtUtc = emitted;
            }
            Q_EMIT statusChanged();
        }
        refreshStatus();
        // Published when the capture state changes, which is what happens the
        // moment the game starts; without this the automatic-recording card
        // waits out the 10 s idle poll.
        if (m_recording)
            m_recording->refresh();
        return;
    }

    if (kind == QLatin1String("stats_invalidated")) {
        refreshDashboard();
        refreshTrend();
        refreshReflections();
        m_history->runs()->reload();
        retryHistoryImageCleanup();
        m_statistics->dungeons()->reload();
        m_statistics->jobs()->reload();
        return;
    }

    const bool finished =
        kind == QLatin1String("run_finished") || type == QLatin1String("RunFinished");
    if (kind == QLatin1String("run_created") || kind == QLatin1String("run_updated")
        || finished) {
        m_history->runs()->reload();
        refreshCurrentRun();
        refreshReflections();

        // A run that arrives already finished - the Collector writes the result
        // and the end time in one go - is the second way the 心得 prompt can be
        // reached, so it is read from the event's own run rather than re-fetched.
        const QJsonObject run =
            QJsonObject::fromVariantMap(event.value(QStringLiteral("run")).toMap());
        adoptRunRevisionFromEvent(run);
        if (run.value(QStringLiteral("result")).toString() == QLatin1String("COMPLETED")
            && run.value(QStringLiteral("ended_at_utc")).isString()) {
            maybePromptForReflection(run);
        }

        if (!finished)
            return;

        // run_finished is the one terminal event the bus always publishes, so
        // it - not StateChanged - is where the run is announced and where the
        // result question is asked. The dashboard is re-read first and the
        // announcement runs in its reply: {progress} has to be the number this
        // run just produced, and GetDashboardStats is asynchronous.
        QString state = event.value(QStringLiteral("state")).toString();
        if (state.isEmpty())
            state = run.value(QStringLiteral("state")).toString();
        requestDashboard([this, state, run](bool numbersKnown) {
            announceFinished(state, run, numbersKnown);
            maybeConfirmResult(state, run);
        });
        return;
    }

    // Anything this build does not name yet: a cheap status refresh, never a
    // silent drop.
    refreshStatus();
}

/// True when \a event is new, in which case it is remembered as seen.
///
/// Every (re)connect replays up to 64 recent events without marking them as
/// replays, so identity comes from the event itself: event_id is unique per
/// event, stable across that replay, and a restarted Collector issues ids this
/// process has never seen. The sequence is a second guard, for an event that
/// carries no id.
bool AppController::adoptLiveEventOnce(const QVariantMap &event)
{
    const QString eventId = event.value(QStringLiteral("event_id")).toString();
    if (!eventId.isEmpty() && m_seenEventIds.contains(eventId))
        return false;

    const QVariant sequenceValue = event.value(QStringLiteral("sequence"));
    if (sequenceValue.isValid() && !sequenceValue.isNull()) {
        bool sequenceParsed = false;
        const qint64 sequence = sequenceValue.toLongLong(&sequenceParsed);
        if (sequenceParsed) {
            // Without an id the watermark is the only thing that can spot a
            // replay. With one, a low sequence means the Collector restarted
            // and is counting again, so the watermark follows it down.
            if (eventId.isEmpty() && sequence <= m_lastLiveSequence)
                return false;
            m_lastLiveSequence = sequence;
        }
    }

    if (!eventId.isEmpty()) {
        m_seenEventIds.insert(eventId);
        m_seenEventOrder.append(eventId);
        while (m_seenEventOrder.size() > kSeenEventIdLimit)
            m_seenEventIds.remove(m_seenEventOrder.takeFirst());
    }
    return true;
}

QString AppController::announcementKind(const QString &state)
{
    if (state == QLatin1String("MENTOR_MATCHED"))
        return QStringLiteral("matched");
    // A profile that infers the match from the queue enters this state when the player
    // presses "join", not when the queue pops; announceState() suppresses the spoken line
    // in that case, because announcing a match the player just asked for is a false alarm.
    if (state == QLatin1String("ENTERED_DUTY"))
        return QStringLiteral("entered");
    // COMPLETED means a victory DUTY_RESULT really was observed. A CN duty that
    // ends without the clear signal (docs/protocol-profile-format.md §12) lands
    // in UNKNOWN_FINAL_STATE instead: it is announced, but with its own line that
    // asks for a confirmation rather than claiming a 通关. Such a run does NOT
    // count toward the goal until the user confirms it.
    if (state == QLatin1String("COMPLETED"))
        return QStringLiteral("completed");
    if (state == QLatin1String("UNKNOWN_FINAL_STATE"))
        return QStringLiteral("finished");
    if (state == QLatin1String("LEFT_OR_ABANDONED") || state == QLatin1String("DISCONNECTED")
        || state == QLatin1String("INTERRUPTED")
        || state == QLatin1String("INTERRUPTED_PENDING_REVIEW"))
        return QStringLiteral("aborted");
    // CANCELLED_BEFORE_ENTRY is a declined duty pop. Declining is ordinary
    // play, not an abnormal end, so it stays silent.
    return {};
}

QVariantMap AppController::announcementValues(const QJsonObject &run) const
{
    QVariantMap values;
    // The run the announcement is about wins: the current-run card is fetched
    // asynchronously and, for a run that just ended, is already stale or empty.
    QString dutyName = run.value(QStringLiteral("duty_name")).toString();
    if (dutyName.isEmpty()) {
        dutyName = m_currentRun.value(QStringLiteral("run"))
                       .toObject()
                       .value(QStringLiteral("duty_name"))
                       .toString();
    }
    values.insert(QStringLiteral("duty"),
                  dutyName.isEmpty() ? QString::fromUtf8("未知副本") : dutyName);
    values.insert(QStringLiteral("progress"), m_statistics->achievementProgress());
    values.insert(QStringLiteral("remaining"), m_statistics->remainingCount());
    return values;
}

void AppController::announceState(const QString &state, const QJsonObject &run, bool matchFromServer,
                                  int matchOffer)
{
    // Only the two in-progress states. Terminal states arrive here as well
    // (StateChanged publishes them when it can), but they are announced from
    // run_finished so the numbers are fresh and nothing is said twice.
    if (state != QLatin1String("MENTOR_MATCHED") && state != QLatin1String("ENTERED_DUTY"))
        return;
    if (!m_tts)
        return;

    // A repeated transition for the same run says the same thing twice; a
    // replayed one after a reconnect would say it again a third time.
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (!runId.isEmpty()) {
        // A match offered again (somebody withdrew, the finder re-formed the party) is the same
        // run in the same state with a higher offer number: a new popup, not a replay.
        const QString guard = runId + QLatin1Char('|') + state
            + (matchOffer > 1 ? QLatin1Char('#') + QString::number(matchOffer) : QString());
        if (m_announcedRunStates.contains(guard))
            return;
        m_announcedRunStates.insert(guard);
    }

    const QString kind = announcementKind(state);
    // Only the event's explicit source can establish a server match: calibration may be
    // idle after a restart, or ready with an upgrade while the queue-based parser still
    // runs. A queue-inferred MENTOR_MATCHED fires when the player presses "join", so
    // announcing it would be a false alarm; an event without a source stays silent.
    if (kind == QLatin1String("matched") && !matchFromServer)
        return;

    const QVariantMap values = announcementValues(run);
    m_tts->setContextValues(values);
    m_tts->announce(kind, values);
}

/// The terminal line for \a kind with no counts in it. Used only when the
/// dashboard read that should have supplied {progress} and {remaining} failed
/// and there is no earlier snapshot, so no wrong total is spoken.
QString AppController::numberlessAnnouncement(const QString &kind)
{
    if (kind == QLatin1String("completed"))
        return QString::fromUtf8("导随完成");
    if (kind == QLatin1String("finished"))
        return QString::fromUtf8("导随结束，请确认是否通关");
    if (kind == QLatin1String("aborted"))
        return QString::fromUtf8("导随异常结束");
    return QString();
}

void AppController::announceFinished(const QString &state, const QJsonObject &run,
                                     bool numbersKnown)
{
    if (!m_tts)
        return;
    const QString kind = announcementKind(state);
    if (kind.isEmpty())
        return;
    // A run that ended before this process started reaches us through the
    // bus's replay; it is history, not something to say out loud.
    if (!endedDuringThisSession(run))
        return;

    const QString runId = run.value(QStringLiteral("run_id")).toString();
    const QString guard = runId + QLatin1Char('|') + state;
    if (!runId.isEmpty() && m_announcedRunStates.contains(guard))
        return;
    if (!runId.isEmpty())
        m_announcedRunStates.insert(guard);

    const QVariantMap values = announcementValues(run);
    m_tts->setContextValues(values);
    // Stale numbers are still true numbers - they are this run behind at worst.
    // Numbers that were never read are not, so that line drops them entirely.
    if (!numbersKnown && m_statistics->dashboardSnapshot().isEmpty()) {
        const QString line = numberlessAnnouncement(kind);
        if (!line.isEmpty())
            m_tts->announceText(kind, line);
        return;
    }
    m_tts->announce(kind, values);
}

/// Refresh the selected record from a live event. Open forms keep the revision
/// they displayed so a later save still checks for intervening changes.
void AppController::adoptRunRevisionFromEvent(const QJsonObject &run)
{
    return m_history->adoptRunRevisionFromEvent(run);
}

// ---------------------------------------------------------------------------
// Selection
// ---------------------------------------------------------------------------

QVariantMap AppController::selectedRun() const
{
    return m_history->selectedRun();
}

bool AppController::selectedRunCanUndo() const
{
    return m_history->selectedRunCanUndo();
}

void AppController::selectRun(const QVariantMap &run)
{
    return m_history->selectRun(run);
}

void AppController::clearSelection()
{
    return m_history->clearSelection();
}

void AppController::refreshRunEvents()
{
    return m_history->refreshRunEvents();
}

// ---------------------------------------------------------------------------
// 导随心得
//
// The summary is a read model: it is re-fetched after anything that can change
// a run or a reflection, never patched by hand here, so the panel cannot show
// a count this process computed instead of one the Collector did.
// ---------------------------------------------------------------------------

void AppController::refreshReflections()
{
    if (!m_backend)
        return;
    m_backend->getReflectionSummary(3)->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &,
                     const QString &) {
            m_reflectionSummary = ok ? QJsonObject::fromVariantMap(payload) : QJsonObject();
            Q_EMIT reflectionsChanged();
        });
}

void AppController::saveReflection(const QString &runId, const QString &mood,
                                   const QString &text)
{
    // A second click while the first request is in flight would race two
    // writes for the same run_id; the later response is not guaranteed to
    // be the later edit, so the second call is dropped instead.
    if (!m_backend || runId.isEmpty())
        return;
    if (m_reflectionSaving) {
        Q_EMIT reflectionFailed(QStringLiteral("ERR_BUSY"),
                                QString::fromUtf8("上一条笔记仍在保存中，请稍候再试。"));
        return;
    }

    const bool cleared = text.trimmed().isEmpty();
    m_reflectionSaving = true;
    Q_EMIT reflectionsChanged();

    m_backend->setRunReflection(runId, mood, text.trimmed())
        ->whenDone(this, [this, runId, cleared](bool ok, const QVariantMap &payload,
                                                const QString &code,
                                                const QString &message) {
            m_reflectionSaving = false;
            if (!ok) {
                Q_EMIT reflectionsChanged();
                Q_EMIT reflectionFailed(code, message);
                showToast(message.isEmpty() ? code : message);
                return;
            }
            applySavedReflection(runId, payload, cleared);
        });
}

void AppController::applySavedReflection(const QString &runId, const QVariantMap &payload,
                                         bool cleared)
{
    // The dialog can be opened from the dashboard as well as from the detail
    // panel, so the selected run is only touched when it is the very same run.
    const QJsonObject run =
        QJsonObject::fromVariantMap(payload.value(QStringLiteral("run")).toMap());
    m_history->adoptReflection(runId, run.isEmpty()
        ? QJsonValue::fromVariant(payload.value(QStringLiteral("reflection")))
        : run.value(QStringLiteral("reflection")));

    // 通关后弹出：a run whose 心得 was just written - or deliberately cleared -
    // must never make the prompt pop up again in this session.
    m_promptedRunIds.insert(runId);
    for (int index = 0; index < m_queuedReflectionRuns.size(); ++index) {
        if (m_queuedReflectionRuns.at(index).value(QStringLiteral("run_id")).toString() == runId) {
            m_queuedReflectionRuns.removeAt(index);
            break;
        }
    }

    m_history->runs()->reload();
    // The just-finished run is usually still the dashboard's current run when
    // the completion prompt is answered; re-read it so its card carries the
    // reflection without waiting for the next unrelated live event.
    refreshCurrentRun();
    refreshReflections();
    Q_EMIT reflectionsChanged();
    Q_EMIT reflectionSaved(runId, cleared);

    if (cleared) {
        showToast(QString::fromUtf8("已清空该记录的笔记"));
        return;
    }
    const QString dutyName = run.value(QStringLiteral("duty_name")).toString();
    showToast(QString::fromUtf8("笔记已保存 · %1")
                  .arg(dutyName.isEmpty() ? QString::fromUtf8("未知副本") : dutyName));
}

bool AppController::endedDuringThisSession(const QJsonObject &run) const
{
    // A replayed run_finished from before this session is not "刚刚完成"; the
    // user reaches such runs through 稍后补录 and the 待复核 list instead. A run
    // without a parseable end time is left to the callers' own checks.
    const QDateTime ended = QDateTime::fromString(
        run.value(QStringLiteral("ended_at_utc")).toString(), Qt::ISODateWithMs);
    return !(ended.isValid() && m_promptCutoffUtc.isValid() && ended < m_promptCutoffUtc);
}

bool AppController::emittedBeforeThisSession(const QVariantMap &event) const
{
    // An event without a parseable time is left alone: nothing proves it old.
    const QDateTime emitted =
        parseUtc(QJsonValue::fromVariant(event.value(QStringLiteral("emitted_at_utc"))));
    return emitted.isValid() && m_promptCutoffUtc.isValid() && emitted < m_promptCutoffUtc;
}

void AppController::maybePromptForReflection(const QJsonObject &run)
{
    if (m_settings && !m_settings->reflectPrompt())
        return;
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty() || m_promptedRunIds.contains(runId))
        return;
    // The 本次导随结果 window already offered this run's 心得 fields; the
    // correction it sent comes back as a COMPLETED run and is not a new prompt.
    if (m_confirmedRunIds.contains(runId))
        return;
    if (run.value(QStringLiteral("reflection")).isObject())
        return;
    if (!endedDuringThisSession(run))
        return;
    for (const QJsonObject &queued : std::as_const(m_queuedReflectionRuns)) {
        if (queued.value(QStringLiteral("run_id")).toString() == runId)
            return;
    }

    // Queued, not marked prompted: the dialog drops a request it receives while
    // it is busy with another run, and a run marked here would never be offered
    // again (review OH-6). reflectionPromptShown() is what marks it.
    m_queuedReflectionRuns.append(run);
    while (m_queuedReflectionRuns.size() > kMaxQueuedReflectionRuns) {
        m_offeredReflectionRunIds.remove(
            m_queuedReflectionRuns.constFirst().value(QStringLiteral("run_id")).toString());
        m_queuedReflectionRuns.removeFirst();
    }
    emitNextReflectionPrompt();
}

void AppController::emitNextReflectionPrompt()
{
    if (m_reflectionPromptBusy || m_resultConfirmationBusy)
        return;
    // Switched off while a prompt waited for the dialog: it is no longer wanted.
    if (m_settings && !m_settings->reflectPrompt()) {
        m_queuedReflectionRuns.clear();
        m_offeredReflectionRunIds.clear();
        return;
    }
    for (const QJsonObject &run : std::as_const(m_queuedReflectionRuns)) {
        const QString runId = run.value(QStringLiteral("run_id")).toString();
        if (m_offeredReflectionRunIds.contains(runId))
            continue;
        m_offeredReflectionRunIds.insert(runId);
        Q_EMIT reflectionPromptRequested(run.toVariantMap());
        return;
    }
}

void AppController::reflectionPromptShown(const QString &runId)
{
    if (runId.isEmpty())
        return;
    m_reflectionPromptBusy = true;
    for (int index = 0; index < m_queuedReflectionRuns.size(); ++index) {
        if (m_queuedReflectionRuns.at(index).value(QStringLiteral("run_id")).toString() == runId) {
            m_queuedReflectionRuns.removeAt(index);
            break;
        }
    }
    m_offeredReflectionRunIds.remove(runId);
    m_promptedRunIds.insert(runId);
}

void AppController::reflectionPromptClosed()
{
    m_reflectionPromptBusy = false;
    // Whatever is still queued was dropped by the dialog while it was busy.
    m_offeredReflectionRunIds.clear();
    emitNextReflectionPrompt();
}

void AppController::maybeConfirmResult(const QString &state, const QJsonObject &run)
{
    // A CN duty that ends without the clear signal (docs/protocol-profile-format.md
    // §12) only tells the Collector that the duty ended. The program never turns
    // that into a 通关 by itself; it asks the player once, right away, instead of
    // leaving the run to the 待复核 list alone.
    if (m_settings && !m_settings->confirmPrompt())
        return;
    if (!run.value(QStringLiteral("pending_review")).toBool(false))
        return;
    // $defs/Run carries no state, so the terminal state is the event's own.
    if (state != QLatin1String("UNKNOWN_FINAL_STATE"))
        return;
    const QString runId = run.value(QStringLiteral("run_id")).toString();
    if (runId.isEmpty() || m_confirmedRunIds.contains(runId))
        return;
    if (!endedDuringThisSession(run))
        return;
    for (const QJsonObject &queued : std::as_const(m_queuedResultRuns)) {
        if (queued.value(QStringLiteral("run_id")).toString() == runId)
            return;
    }

    // Queued, not marked asked: the dialog drops a request it receives while it
    // is already showing one, and a run marked here would never be offered again.
    m_queuedResultRuns.append(run);
    while (m_queuedResultRuns.size() > kMaxQueuedResultRuns) {
        m_offeredResultRunIds.remove(
            m_queuedResultRuns.constFirst().value(QStringLiteral("run_id")).toString());
        m_queuedResultRuns.removeFirst();
    }
    emitNextResultConfirmation();
}

void AppController::emitNextResultConfirmation()
{
    if (m_resultConfirmationBusy)
        return;
    for (const QJsonObject &run : std::as_const(m_queuedResultRuns)) {
        const QString runId = run.value(QStringLiteral("run_id")).toString();
        if (m_offeredResultRunIds.contains(runId))
            continue;
        m_offeredResultRunIds.insert(runId);
        Q_EMIT resultConfirmationRequested(run.toVariantMap());
        return;
    }
}

void AppController::resultConfirmationShown(const QString &runId)
{
    if (runId.isEmpty())
        return;
    // The dialog is on screen now, so this run really has been asked about and
    // nothing else may be raised until it closes.
    m_resultConfirmationBusy = true;
    for (int index = 0; index < m_queuedResultRuns.size(); ++index) {
        if (m_queuedResultRuns.at(index).value(QStringLiteral("run_id")).toString() == runId) {
            m_queuedResultRuns.removeAt(index);
            break;
        }
    }
    m_offeredResultRunIds.remove(runId);
    m_confirmedRunIds.insert(runId);
}

void AppController::resultConfirmationClosed()
{
    m_resultConfirmationBusy = false;
    // Whatever is still queued was never acknowledged, which means the dialog
    // dropped it while it was showing this one. Now that it is free, offer it.
    m_offeredResultRunIds.clear();
    emitNextResultConfirmation();
}

void AppController::openHistoryForRun(const QVariantMap &run)
{
    if (run.value(QStringLiteral("run_id")).toString().isEmpty())
        return;
    m_history->openRun(run);
    navigate(1);
}

// ---------------------------------------------------------------------------
// Filters
// ---------------------------------------------------------------------------

QVariantList AppController::categoryOptions() const
{
    return {QString::fromUtf8("四人迷宫"), QString::fromUtf8("讨伐歼灭战"),
            QString::fromUtf8("大型任务"), QString::fromUtf8("行会令")};
}

QVariantList AppController::jobOptions() const
{
    return JobCatalog().allJobs();
}

QVariantList AppController::battleJobOptions() const
{
    return JobCatalog().battleJobs();
}

QVariantList AppController::dutyCatalogOptions() const
{
    return DutyCatalog::shared()->allDuties();
}

// 最近打过: one page of the latest runs is plenty for five distinct duties.
// Soft-deleted runs are excluded by the Collector's default filter and skipped again.
void AppController::refreshRecentDuties()
{
    if (!m_backend)
        return;
    QJsonObject sort;
    sort.insert(QStringLiteral("field"), QStringLiteral("matched_at_utc"));
    sort.insert(QStringLiteral("direction"), QStringLiteral("desc"));
    m_backend->queryRuns(QJsonObject(), 1, 50, sort)
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &,
                                const QString &) {
            // A failed query keeps whatever was shown: the chips are a shortcut,
            // and the list below them still has every duty.
            if (!ok)
                return;
            const QVariantList next = recentDutiesFromRuns(
                QJsonObject::fromVariantMap(payload).value(QStringLiteral("items")).toArray(), 5);
            if (next == m_recentDuties)
                return;
            m_recentDuties = next;
            Q_EMIT recentDutiesChanged();
        });
}

QVariantList AppController::recentDutiesFromRuns(const QJsonArray &runs, int limit)
{
    QVariantList out;
    QSet<qint64> seen;
    for (const QJsonValue &value : runs) {
        if (out.size() >= limit)
            break;
        const QJsonObject run = value.toObject();
        if (run.value(QStringLiteral("soft_deleted")).toBool(false))
            continue;
        const QJsonValue id = run.value(QStringLiteral("content_id"));
        if (!id.isDouble())
            continue;
        const qint64 contentId = qint64(id.toDouble());
        if (seen.contains(contentId))
            continue;
        const QVariantMap row = DutyCatalog::shared()->lookup(contentId);
        if (row.isEmpty())
            continue;
        seen.insert(contentId);
        out.append(row);
    }
    return out;
}

void AppController::setHistoryFilter(const QVariantMap &filter)
{
    return m_history->setHistoryFilter(filter);
}

void AppController::resetHistoryFilter()
{
    return m_history->resetHistoryFilter();
}

void AppController::setHistoryRunChecked(const QVariantMap &run, bool checked)
{ m_history->setRunChecked(run, checked); }
void AppController::setCurrentHistoryPageChecked(bool checked)
{ m_history->setCurrentPageChecked(checked); }
void AppController::clearCheckedHistoryRuns()
{ m_history->clearCheckedRuns(); }
void AppController::mutateCheckedHistoryRuns(const QString &action, const QString &reason)
{ m_history->mutateCheckedRuns(action, reason); }
void AppController::confirmHistoryFilterChange(bool apply)
{ m_history->confirmHistoryFilterChange(apply); }
void AppController::refreshHistoryRetentionSettings()
{ m_history->refreshRetentionSettings(); }
void AppController::updateHistoryRetentionSettings(int days)
{ m_history->updateRetentionSettings(days); }

void AppController::setNoteImageStore(NoteImageStore *store)
{
    m_noteImages = store;
    if (store) {
        m_historyImageCleanupTimer.start();
        retryHistoryImageCleanup();
    } else {
        m_historyImageCleanupTimer.stop();
    }
}

void AppController::retryHistoryImageCleanup()
{
    if (!m_noteImages || !m_backend || !m_backend->isConnected() || m_historyImageCleanupRunning)
        return;
    m_historyImageCleanupRunning = true;
    Q_EMIT historyImageCleanupChanged();
    m_backend->request(QStringLiteral("GetPendingImageCleanup"))
        ->whenDone(this, [this](bool ok, const QVariantMap &payload, const QString &code, const QString &message) {
            if (!ok) {
                finishHistoryImageCleanup(tr("无法读取附件清理任务：%1").arg(message.isEmpty() ? code : message));
                return;
            }
            m_historyCleanupIds = payload.value(QStringLiteral("run_ids")).toStringList();
            m_historyCleanupSuccessful.clear();
            m_historyCleanupFailures.clear();
            m_historyCleanupIndex = 0;
            drainHistoryImageCleanup();
        });
}

void AppController::drainHistoryImageCleanup()
{
    if (!m_noteImages) {
        finishHistoryImageCleanup(tr("图片服务已关闭，清理任务仍保留，稍后重试。"));
        return;
    }
    if (m_historyCleanupIndex < m_historyCleanupIds.size()) {
        const QString id = m_historyCleanupIds.at(m_historyCleanupIndex++);
        const QVariantMap result = m_noteImages->cleanupRun(id);
        if (result.value(QStringLiteral("ok")).toBool())
            m_historyCleanupSuccessful.append(id);
        else
            m_historyCleanupFailures.append(id.left(8) + QStringLiteral(": ") + result.value(QStringLiteral("error")).toString());
        // 每个记录之间让出事件循环；一次最多20张普通附件，不阻塞整批2000条。
        QTimer::singleShot(0, this, &AppController::drainHistoryImageCleanup);
        return;
    }
    const QString failures = m_historyCleanupFailures.isEmpty() ? QString()
        : tr("%1 个记录的附件清理失败，任务已保留并将重试。%2")
            .arg(m_historyCleanupFailures.size()).arg(m_historyCleanupFailures.first());
    if (m_historyCleanupSuccessful.isEmpty()) {
        finishHistoryImageCleanup(failures);
        return;
    }
    QJsonArray ids;
    for (const QString &id : m_historyCleanupSuccessful)
        ids.append(id);
    m_backend->request(QStringLiteral("AcknowledgeImageCleanup"), {{QStringLiteral("run_ids"), ids}})
        ->whenDone(this, [this, failures](bool ok, const QVariantMap &, const QString &code, const QString &message) {
            if (!ok) {
                finishHistoryImageCleanup(tr("附件已清理，但确认状态未保存，将自动重试：%1 %2")
                    .arg(message.isEmpty() ? code : message, failures));
                return;
            }
            const bool nextBatch = failures.isEmpty() && m_historyCleanupIds.size() == 2000;
            finishHistoryImageCleanup(failures);
            if (nextBatch)
                QTimer::singleShot(0, this, &AppController::retryHistoryImageCleanup);
        });
}

void AppController::finishHistoryImageCleanup(const QString &feedback)
{
    const bool changed = feedback != m_historyImageCleanupFeedback;
    m_historyImageCleanupRunning = false;
    m_historyImageCleanupFeedback = feedback;
    Q_EMIT historyImageCleanupChanged();
    if (changed && !feedback.isEmpty())
        showToast(feedback);
}

// A statistics row without a duty or job identity (未知副本, a zone several
// duties share, 未知职业) has no filter key: RunFilter cannot say "is null", and
// an empty filter would open every run under that row's numbers. Such a row
// stays where it is and says why (review OD-4).
void AppController::showHistoryForContent(const QVariant &contentId)
{
    if (!contentId.isValid() || contentId.isNull()) {
        showToast(tr("这一行的记录没有确定的副本，历史记录无法单独列出它们。"));
        return;
    }
    QVariantMap filter;
    filter.insert(QStringLiteral("content_id"), QVariantList{contentId});
    setHistoryFilter(filter);
    clearSelection();
    navigate(1);
}

void AppController::showHistoryForJob(const QVariant &jobId)
{
    if (!jobId.isValid() || jobId.isNull()) {
        showToast(tr("这一行的记录没有识别出职业，历史记录无法单独列出它们。"));
        return;
    }
    QVariantMap filter;
    filter.insert(QStringLiteral("job_id"), QVariantList{jobId});
    setHistoryFilter(filter);
    clearSelection();
    navigate(1);
}

void AppController::showPendingReview()
{
    QVariantMap filter;
    filter.insert(QStringLiteral("pending_review"), true);
    setHistoryFilter(filter);
    clearSelection();
    navigate(1);
}

// ---------------------------------------------------------------------------
// Capture commands
// ---------------------------------------------------------------------------

void AppController::setCaptureAdapterId(const QString &adapterId)
{
    const QString before = m_capture->adapterId();
    m_capture->setAdapterId(adapterId);
    if (m_capture->adapterId() != before)
        Q_EMIT captureAdapterIdChanged();
}

void AppController::toggleCapture()
{
    if (!m_maintainerToolsVisible) return;
    m_capture->toggle();
}

void AppController::addCaptureValidationMarker(const QString &marker)
{
    if (!m_maintainerToolsVisible) return;
    m_capture->addMarker(marker);
}

void AppController::openCaptureValidationFolder()
{
    const QString tracePath =
        m_capture->status().value(QStringLiteral("trace_path")).toString();
    if (tracePath.isEmpty()) {
        showToast(tr("当前验证状态没有 Collector 返回的取证路径。"));
        Q_EMIT mutationFailed(QStringLiteral("ERR_OUTPUT_UNAVAILABLE"),
                              tr("当前验证状态没有 Collector 返回的取证路径。"),
                              QStringLiteral("validation"), QString());
        return;
    }
    const QString directory = QFileInfo(tracePath).absolutePath();
    if (!QDir(directory).exists()) {
        // Folded for display: the raw value carries the account name
        // (docs/privacy-boundary.md section 5).
        showToast(tr("取证文件所在目录已不存在：%1")
                      .arg(Formatters::foldUserPath(directory)));
        return;
    }
    // The second and last shell hand-off in this application; the first is
    // ExportController::openDatabaseFolder(). Both point at a local directory and
    // are built with QUrl::fromLocalFile, which backend data cannot re-scheme.
    if (!QDesktopServices::openUrl(QUrl::fromLocalFile(directory))) {
        showToast(tr("无法打开取证文件所在目录：%1")
                      .arg(Formatters::foldUserPath(directory)));
    }
}

void AppController::rescanGame()
{
    if (!m_backend)
        return;
    // The profile status and the validation snapshot are re-read as well: they are
    // the only user-reachable way out of a transient failure, and the exhausted-hint
    // tells the player to 「点重新扫描 FF14 再试」.
    m_capture->refreshProfile();
    m_capture->refreshStatusSnapshot();

    m_backend->getStatus()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &code,
                     const QString &message) {
            if (!ok) {
                showToast(message.isEmpty() ? code : message);
                return;
            }
            m_collectorStatus = QJsonObject::fromVariantMap(payload);
            m_captureAnswerAtUtc = QDateTime::currentDateTimeUtc();
            Q_EMIT statusChanged();
            Q_EMIT backendChanged();

            const QJsonObject game =
                m_collectorStatus.value(QStringLiteral("game")).toObject();
            const QJsonObject capture =
                m_collectorStatus.value(QStringLiteral("capture")).toObject();
            // Only a listed client can be chosen; with none listed this is the
            // "nothing running" answer below (review OD-1).
            if (capture.value(QStringLiteral("game_selection_required")).toBool()
                && !capture.value(QStringLiteral("game_processes")).toArray().isEmpty()) {
                showToast(tr("已重新扫描，请在记录对象中选择要记录的游戏窗口。"));
                return;
            }
            if (!game.value(QStringLiteral("running")).toBool(false)) {
                showToast(QString::fromUtf8("已重新扫描：未找到正在运行的 FF14 进程。"));
                return;
            }
            showToast(QString::fromUtf8("已重新扫描：FF14 PID %1 · 实例 %2 · build %3")
                          .arg(game.value(QStringLiteral("process_id")).toInt())
                          .arg(game.value(QStringLiteral("instance_count")).toInt())
                          .arg(game.value(QStringLiteral("game_build"))
                                   .toString(QString::fromUtf8("未知"))));
        });
}

void AppController::rescanAdapters()
{
    if (!m_backend)
        return;
    m_backend->listCaptureAdapters()->whenDone(
        this, [this](bool ok, const QVariantMap &payload, const QString &code,
                     const QString &message) {
            if (!ok) {
                showToast(message.isEmpty() ? code : message);
                return;
            }
            const QJsonObject object = QJsonObject::fromVariantMap(payload);
            m_captureAdapters =
                object.value(QStringLiteral("adapters")).toArray().toVariantList();
            m_adapterInfo = object;
            m_adapterInfo.remove(QStringLiteral("adapters"));
            Q_EMIT adaptersChanged();

            showToast(object.value(QStringLiteral("npcap_installed")).toBool(false)
                          ? QString::fromUtf8("已重新枚举网络适配器：%1 个")
                                .arg(m_captureAdapters.size())
                          : QString::fromUtf8("已重新枚举网络适配器：%1 个"
                                              "（未安装 Npcap，仍无法开始捕获）")
                                .arg(m_captureAdapters.size()));
        });
}

// ---------------------------------------------------------------------------
// Mutations
// ---------------------------------------------------------------------------

void AppController::createManualRun(const QVariantMap &fields, const QString &reason)
{
    return m_history->createManualRun(fields, reason);
}

void AppController::correctSelectedRun(const QVariantMap &changes, const QString &reason,
                                      const QString &runId, int expectedRevision)
{
    return m_history->correctSelectedRun(changes, reason, runId, expectedRevision);
}

void AppController::resolveRunResult(const QString &runId, int revision,
                                    const QString &result, const QString &reason, int jobId)
{
    return m_history->resolveRunResult(runId, revision, result, reason, jobId);
}

void AppController::confirmSelectedRunReview(const QString &reason)
{
    return m_history->confirmSelectedRunReview(reason);
}

void AppController::supplementRunJob(const QString &runId, int revision, int jobId, const QString &reason)
{
    return m_history->supplementRunJob(runId, revision, jobId, reason);
}

void AppController::undoSelectedRunRevision(const QString &reason)
{
    return m_history->undoSelectedRunRevision(reason);
}

void AppController::softDeleteSelectedRun(const QString &reason)
{
    return m_history->softDeleteSelectedRun(reason);
}

void AppController::restoreSelectedRun(const QString &reason)
{
    return m_history->restoreSelectedRun(reason);
}

void AppController::updateAchievementBaseline(int goal, int baseline, const QString &reason)
{
    return m_statistics->updateAchievementBaseline(goal, baseline, reason);
}

void AppController::completeFirstRun()
{
    if (m_settings)
        m_settings->setFirstRunCompleted(true);
    m_firstRun = false;
    Q_EMIT firstRunChanged();
}

// ---------------------------------------------------------------------------
// Export - delegated to ExportController
// ---------------------------------------------------------------------------

void AppController::setExportTargetOverride(const QString &directory)
{
    m_candidates->setTargetOverride(directory);
    m_export->setTargetOverride(directory);
}

QString AppController::exportTargetOverride() const
{
    return m_export->targetOverride();
}

void AppController::exportCsv()
{
    m_export->exportCsv();
}

void AppController::exportJson()
{
    m_export->exportJson();
}

void AppController::backupDatabase()
{
    m_export->backupDatabase();
}

void AppController::checkDatabaseIntegrity()
{
    m_export->checkDatabaseIntegrity();
}

bool AppController::integrityCheckRunning() const
{
    return m_export->integrityCheckRunning();
}

QVariantMap AppController::integrityCheckResult() const
{
    return m_export->integrityCheckResult();
}

void AppController::exportDiagnosticsReport()
{
    m_export->exportDiagnosticsReport();
}

void AppController::openDatabaseFolder()
{
    m_export->openDatabaseFolder(
        m_collectorStatus.value(QStringLiteral("database_path")).toString());
}

void AppController::rereadCaptureStatus()
{
    if (!m_backend)
        return;
    m_backend->getCaptureStatus()->whenDone(this,
        [this](bool ok, const QVariantMap &payload, const QString &, const QString &) {
            if (!ok) return;
            adoptCaptureAnswer(QJsonObject::fromVariantMap(payload));
            Q_EMIT statusChanged();
        });
}

void AppController::openNpcapWebsite()
{
    static const QUrl kNpcapUrl(QStringLiteral("https://npcap.com/"));
    if (QDesktopServices::openUrl(kNpcapUrl)) {
        showToast(tr("已在系统浏览器中打开 https://npcap.com（由你手动触发，本软件不会自行联网）。"));
    } else {
        showToast(tr("无法调用系统浏览器，请手动访问 https://npcap.com。"));
    }
}

void AppController::runDailyBackupIfDue()
{
    // The first call - main()'s, for an interactive run - switches the daily
    // check on for the rest of the process: every connection and every hour
    // look at the date again (review OH-5). A tray-resident Desktop runs for
    // days; once per process would be once per week.
    if (!m_dailyBackupActive) {
        m_dailyBackupActive = true;
        m_dailyBackupTimer.start();
    }
    // Every current-run answer passes through here, so the end of a duty is
    // noticed whether or not a backup is due yet: from then on the backup keeps
    // away for kBackupAfterDutyMs, the time 本次导随结果, the 心得 prompt and the
    // announcement need the connection for (review V4-3).
    const QString runState = currentRunState();
    const bool dutyInProgress = m_currentRunKnown
        && (runState == QLatin1String("MENTOR_MATCHED") || runState == QLatin1String("ENTERED_DUTY"));
    if (dutyInProgress) {
        m_dutyInProgressSeen = true;
        m_backupAfterDutyTimer.stop();
    } else if (m_currentRunKnown && m_dutyInProgressSeen) {
        m_dutyInProgressSeen = false;
        m_backupAfterDutyTimer.start();
    }
    if (!m_settings || !m_settings->autoBackup())
        return;
    const QString today = m_today().toString(QStringLiteral("yyyy-MM-dd"));
    if (m_settings->lastAutoBackupDate() == today || m_autoBackupAttemptedOn == today)
        return;
    // Before the first connection - the normal case at start-up - wait, and let
    // connectionChanged re-run this. A request now fails with 「Collector 未连接。」,
    // burns the day's attempt and toasts an error on every launch.
    if (!m_backend || !m_backend->isConnected())
        return;
    // Not in the middle of a duty: copying the database competes with recording
    // it. Until the current run has been read back after a connect it is not
    // known to be over; that reply and every later check try again. Nor right
    // after one: m_backupAfterDutyTimer checks again when that while is over.
    if (!m_currentRunKnown || dutyInProgress || m_backupAfterDutyTimer.isActive())
        return;
    // The date is stamped by the backupSucceeded handler wired in the constructor, so a
    // backup that failed or timed out is not recorded as done. This flag only stops a
    // persistently failing backup from toasting on every trigger; the next day tries again.
    m_autoBackupAttemptedOn = today;
    backupDatabase();
}

void AppController::showToast(const QString &message)
{
    m_toastMessage = message;
    Q_EMIT toastChanged();
    m_toastTimer.start(kToastMs);
}

QString AppController::errorText(const QString &message, const QString &code) const
{
    const QString sentence =
        message.isEmpty() ? QString::fromUtf8("操作没有完成，原因不明。") : message;
    if (!m_maintainerToolsVisible || code.isEmpty())
        return sentence;
    return sentence + QStringLiteral(" (") + code + QLatin1Char(')');
}

} // namespace mr
