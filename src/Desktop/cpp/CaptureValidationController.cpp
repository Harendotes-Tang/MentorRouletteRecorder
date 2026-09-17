#include "CaptureValidationController.h"

#include "IBackend.h"

#include <QSet>

namespace {

constexpr int kBasePollMs = 750;
/// After this many consecutive transient failures the controller stops asking
/// and says so, rather than polling a Collector that accepts the pipe but never
/// answers for the rest of the session.
constexpr int kMaxRetries = 6;

const QString kVerified = QStringLiteral("VERIFIED");

bool isUnsupportedCode(const QString &code)
{
    return code == QLatin1String("ERR_BAD_REQUEST")
           || code == QLatin1String("ERR_UNKNOWN_MESSAGE")
           || code == QLatin1String("ERR_UNSUPPORTED");
}

} // namespace

namespace mr {

CaptureValidationController::CaptureValidationController(Host *host, QObject *parent)
    : QObject(parent)
    , m_host(host)
{
    m_pollTimer.setInterval(kBasePollMs);
    connect(&m_pollTimer, &QTimer::timeout, this, [this] {
        if (m_profileStale || !m_profileLoaded)
            refreshProfile();
        refreshStatusSnapshot();
    });
}

void CaptureValidationController::setBackend(IBackend *backend)
{
    m_backend = backend;
}

// ---------------------------------------------------------------------------
// Projection
// ---------------------------------------------------------------------------

QString CaptureValidationController::state() const
{
    return m_status.value(QStringLiteral("state")).toString();
}

bool CaptureValidationController::active() const
{
    const QString value = state();
    return value == QLatin1String("WAITING") || value == QLatin1String("RECORDING")
           || value == QLatin1String("STOPPING");
}

bool CaptureValidationController::recording() const
{
    return state() == QLatin1String("RECORDING");
}

bool CaptureValidationController::saved() const
{
    const QString trace = m_status.value(QStringLiteral("trace_path")).toString();
    const QString hashFile = m_status.value(QStringLiteral("sha256_path")).toString();
    const QString hash = m_status.value(QStringLiteral("sha256")).toString();
    return state() == QLatin1String("COMPLETED") && !trace.isEmpty() && !hashFile.isEmpty()
           && hash.size() == 64;
}

QString CaptureValidationController::notice() const
{
    return tr("仅验证，不自动记录；取证文件仅保存在本机，不会自动上传。");
}

bool CaptureValidationController::markerEnabled() const
{
    return m_backend && m_backend->isConnected() && recording() && !m_markerBusy
           && !m_commandBusy;
}

QString CaptureValidationController::effectiveProfileStatus() const
{
    const QString standalone = m_profile.value(QStringLiteral("status")).toString();
    const QString fromStatus = m_host ? m_host->captureProfileStatus() : QString();
    if (standalone.isEmpty())
        return fromStatus;
    if (fromStatus.isEmpty())
        return standalone;
    if (standalone == kVerified && fromStatus == kVerified)
        return kVerified;
    // The two sources disagree. Take the one that is not VERIFIED; when
    // neither is, prefer the status object, which travels with every GetStatus
    // and is therefore the fresher of the two.
    return fromStatus != kVerified ? fromStatus : standalone;
}

QString CaptureValidationController::actionLabel() const
{
    if (m_commandBusy)
        return tr("处理中…");
    if (state() == QLatin1String("WAITING"))
        return tr("取消等待");
    if (state() == QLatin1String("RECORDING"))
        return tr("停止验证");
    if (state() == QLatin1String("STOPPING"))
        return tr("停止中…");
    if (m_host && m_host->formalCapturing())
        return tr("停止捕获");
    if (m_retriesExhausted)
        return tr("状态未确认");
    if (!m_profileLoaded || !m_statusLoaded)
        return tr("正在确认状态…");
    if (m_host && m_host->candidateValidationEnabled())
        return tr("开始候选捕获");
    return effectiveProfileStatus() == kVerified ? tr("开始捕获") : tr("开始验证");
}

bool CaptureValidationController::actionEnabled() const
{
    if (!m_backend || !m_backend->isConnected() || m_commandBusy)
        return false;
    if (state() == QLatin1String("STOPPING"))
        return false;
    if (active() || (m_host && m_host->formalCapturing()))
        return true;
    if (!m_profileLoaded || !m_statusLoaded)
        return false;
    if ((m_host && m_host->candidateValidationEnabled()) || effectiveProfileStatus() == kVerified)
        return true;
    return m_available;
}

QString CaptureValidationController::modeStatusText() const
{
    if (!m_backend || !m_backend->isConnected())
        return tr("状态未知（Collector 未连接）");
    const QString value = state();
    if (value == QLatin1String("WAITING"))
        return tr("验证等待中（不自动记录）");
    if (value == QLatin1String("RECORDING"))
        return tr("验证取证中（不自动记录）");
    if (value == QLatin1String("STOPPING"))
        return tr("验证停止中（正在排空已接收标记）");
    if (m_host && m_host->formalCapturing()) {
        if (m_host->candidateValidationEnabled()) return tr("候选观测中；VERIFIED 解析可独立生成正式记录");
        return effectiveProfileStatus() == kVerified ? tr("正式记录监听中") : tr("被动监听（不自动记录）");
    }
    if (m_status.value(QStringLiteral("reason")).toString() == QLatin1String("CANCELLED"))
        return tr("已取消等待，未创建取证文件");
    if (value == QLatin1String("COMPLETED"))
        return saved() ? tr("验证取证已保存") : tr("验证已结束，文件不完整");
    if (value == QLatin1String("FAILED"))
        return tr("验证失败");
    if (m_retriesExhausted)
        return tr("无法确认验证状态（已停止重试）");
    if (!m_statusLoaded)
        return tr("正在确认捕获状态");
    return tr("已停止");
}

QString CaptureValidationController::modeCompactText() const
{
    if (!m_backend || !m_backend->isConnected())
        return tr("状态未知");
    const QString value = state();
    if (value == QLatin1String("WAITING"))
        return tr("验证等待中");
    if (value == QLatin1String("RECORDING"))
        return tr("验证取证中");
    if (value == QLatin1String("STOPPING"))
        return tr("验证停止中");
    if (m_host && m_host->formalCapturing()) {
        if (m_host->candidateValidationEnabled()) return tr("候选观测中");
        return effectiveProfileStatus() == kVerified ? tr("正式监听中") : tr("被动监听中");
    }
    if (m_status.value(QStringLiteral("reason")).toString() == QLatin1String("CANCELLED"))
        return tr("已取消验证");
    if (value == QLatin1String("COMPLETED"))
        return saved() ? tr("验证已保存") : tr("验证已结束");
    if (value == QLatin1String("FAILED"))
        return tr("验证失败");
    if (m_retriesExhausted)
        return tr("状态未确认");
    if (!m_statusLoaded)
        return tr("状态未知");
    return tr("已停止");
}

void CaptureValidationController::setAdapterId(const QString &adapterId)
{
    if (active() || m_commandBusy || m_adapterId == adapterId)
        return;
    m_adapterId = adapterId;
    Q_EMIT changed();
}

// ---------------------------------------------------------------------------
// Polling
// ---------------------------------------------------------------------------

void CaptureValidationController::scheduleRetry()
{
    if (m_retryCount >= kMaxRetries) {
        m_retriesExhausted = true;
        m_pollTimer.stop();
        return;
    }
    // 750 / 1500 / 3000 / 6000 ms, then flat. Backoff, not a fixed drum beat.
    const int shift = qMin(m_retryCount, 3);
    m_pollTimer.setInterval(kBasePollMs * (1 << shift));
    if (!m_pollTimer.isActive())
        m_pollTimer.start();
}

void CaptureValidationController::clearRetry()
{
    m_retryCount = 0;
    m_retriesExhausted = false;
    m_pollTimer.setInterval(kBasePollMs);
}

void CaptureValidationController::reset()
{
    ++m_generation;
    ++m_statusSerial;
    ++m_markerSerial;
    ++m_profileSerial;
    m_status = QJsonObject();
    m_profile = QJsonObject();
    m_statusInFlight = false;
    m_profileInFlight = false;
    m_statusLoaded = false;
    m_available = false;
    m_profileLoaded = false;
    m_profileStale = false;
    m_commandBusy = false;
    m_markerBusy = false;
    m_errorFromStatus = false;
    m_error.clear();
    m_feedback.clear();
    clearRetry();
    m_pollTimer.stop();
}

void CaptureValidationController::refreshProfile()
{
    if (!m_backend || m_profileInFlight)
        return;
    m_profileInFlight = true;
    const quint64 serial = ++m_profileSerial;

    m_backend->getProtocolProfileStatus()->whenDone(
        this, [this, serial](bool ok, const QVariantMap &payload, const QString &code,
                             const QString &) {
            m_profileInFlight = false;
            if (serial != m_profileSerial)
                return;

            if (ok) {
                m_profile = QJsonObject::fromVariantMap(payload);
                m_profileLoaded = true;
                m_profileStale = false;
                clearRetry();
            } else if (isUnsupportedCode(code)) {
                // A refusal is an answer: this Collector has no profile status
                // to give, and that is a stable fact, not a hiccup.
                m_profile = QJsonObject();
                m_profileLoaded = true;
                m_profileStale = false;
                clearRetry();
            } else {
                // Transient: keep the last known snapshot, or one timeout
                // disables the capture button permanently, and retry on the
                // shared poll timer.
                m_profileStale = true;
                ++m_retryCount;
                scheduleRetry();
            }
            Q_EMIT changed();
        });
}

void CaptureValidationController::refreshStatusSnapshot()
{
    if (!m_backend || !m_backend->isConnected() || m_statusInFlight)
        return;

    m_statusInFlight = true;
    const quint64 serial = ++m_statusSerial;
    const quint64 generation = m_generation;

    m_backend->getCaptureValidationStatus()->whenDone(
        this, [this, serial, generation](bool ok, const QVariantMap &payload,
                                         const QString &code, const QString &message) {
            // Cleared before the guards: a stale reply that returns early would
            // otherwise leave the in-flight flag set and stall every refresh.
            m_statusInFlight = false;
            if (serial != m_statusSerial)
                return;
            if (!m_backend || !m_backend->isConnected())
                return;
            if (generation != m_generation)
                return;

            if (!ok) {
                m_errorFromStatus = true;
                if (isUnsupportedCode(code)) {
                    m_statusLoaded = true;
                    m_available = false;
                    m_status = QJsonObject();
                    clearRetry();
                    m_pollTimer.stop();
                    m_error = message.isEmpty()
                                  ? tr("当前 Collector 不支持桌面验证（%1）。").arg(code)
                                  : tr("当前 Collector 不支持桌面验证：%1").arg(message);
                } else {
                    // A timeout or transport error is not evidence that a newer
                    // Collector forgot the message. Keep the last canonical
                    // snapshot (and therefore the safe Stop action), make the
                    // uncertainty visible and retry with backoff.
                    ++m_retryCount;
                    scheduleRetry();
                    m_error = m_retriesExhausted
                                  ? tr("验证状态多次刷新失败（%1），已停止自动重试；"
                                       "可点击“重新扫描 FF14”再试一次。")
                                        .arg(code)
                                  : (message.isEmpty()
                                         ? tr("验证状态刷新失败（%1），将自动重试。").arg(code)
                                         : tr("验证状态刷新失败：%1，将自动重试。").arg(message));
                }
                Q_EMIT changed();
                return;
            }
            applyStatus(payload, false);
        });
}

void CaptureValidationController::applyStatus(const QVariantMap &payload, bool clearError)
{
    m_status = QJsonObject::fromVariantMap(payload);
    m_statusLoaded = true;
    m_available = true;
    if (clearError || m_errorFromStatus)
        m_error.clear();
    m_errorFromStatus = false;
    clearRetry();

    if (active()) {
        if (!m_pollTimer.isActive())
            m_pollTimer.start();
    } else if (m_profileStale) {
        scheduleRetry();
    } else {
        m_pollTimer.stop();
    }
    Q_EMIT changed();
}

void CaptureValidationController::setFailure(const QString &code, const QString &message)
{
    m_errorFromStatus = false;
    m_error = message.isEmpty() ? code : message;
    m_feedback.clear();
    if (m_host)
        m_host->showToast(m_error);
    Q_EMIT mutationFailed(code, message);
    Q_EMIT changed();
}

// ---------------------------------------------------------------------------
// Commands
// ---------------------------------------------------------------------------

void CaptureValidationController::toggle()
{
    if (!m_backend || !actionEnabled())
        return;

    m_commandBusy = true;
    m_error.clear();
    Q_EMIT changed();

    if (active()) {
        stopValidation();
        return;
    }

    if (m_host && m_host->formalCapturing()) {
        const quint64 generation = ++m_generation;
        m_backend->stopCapture()->whenDone(
            this, [this, generation](bool ok, const QVariantMap &payload,
                                     const QString &code, const QString &message) {
                m_commandBusy = false;
                if (generation != m_generation) {
                    Q_EMIT changed();
                    return;
                }
                if (!ok) {
                    setFailure(code, message);
                    return;
                }
                if (m_host) {
                    m_host->applyFormalCaptureStatus(payload);
                    m_host->refreshStatus();
                }
                Q_EMIT changed();
            });
        return;
    }

    if ((m_host && m_host->candidateValidationEnabled()) || effectiveProfileStatus() == kVerified) {
        ++m_generation;
        startFormalCapture();
        return;
    }
    startValidation();
}

void CaptureValidationController::startValidation()
{
    ++m_generation;
    m_error.clear();
    m_feedback.clear();
    const quint64 generation = m_generation;

    m_backend->startCaptureValidation(m_adapterId)
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload,
                                            const QString &code, const QString &message) {
            m_commandBusy = false;
            if (generation != m_generation) {
                Q_EMIT changed();
                return;
            }
            if (!ok) {
                setFailure(code, message);
                return;
            }
            applyStatus(payload);
        });
}

void CaptureValidationController::stopValidation()
{
    ++m_generation;
    m_feedback.clear();
    const quint64 generation = m_generation;

    m_backend->stopCaptureValidation()
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload,
                                            const QString &code, const QString &message) {
            m_commandBusy = false;
            if (generation != m_generation) {
                Q_EMIT changed();
                return;
            }
            if (!ok) {
                setFailure(code, message);
                return;
            }
            applyStatus(payload);
        });
}

void CaptureValidationController::startFormalCapture()
{
    const quint64 generation = m_generation;

    m_backend->startCapture(m_adapterId)
        ->whenDone(this, [this, generation](bool ok, const QVariantMap &payload,
                                            const QString &code, const QString &message) {
            if (generation != m_generation) {
                m_commandBusy = false;
                Q_EMIT changed();
                return;
            }
            m_commandBusy = false;
            if (!ok) {
                setFailure(code, message);
                return;
            }
            if (m_host) {
                m_host->applyFormalCaptureStatus(payload);
                m_host->refreshStatus();
            }
            m_error.clear();
            Q_EMIT changed();
        });
}

void CaptureValidationController::addMarker(const QString &marker)
{
    // The single desktop-side copy of the whitelist. The Collector re-checks it
    // and the trace writer checks it again; this one only keeps the UI honest.
    static const QSet<QString> allowed{QStringLiteral("queued"), QStringLiteral("pop"),
                                       QStringLiteral("entered"), QStringLiteral("victory"),
                                       QStringLiteral("left")};
    if (!allowed.contains(marker) || !recording()) {
        setFailure(QStringLiteral("ERR_BAD_REQUEST"),
                   tr("只能在验证取证中添加五种固定标记。"));
        return;
    }
    if (!m_backend || !m_backend->isConnected() || m_markerBusy || m_commandBusy)
        return;

    m_markerBusy = true;
    m_error.clear();
    m_feedback.clear();
    const quint64 generation = ++m_generation;
    const quint64 markerSerial = ++m_markerSerial;
    Q_EMIT changed();

    m_backend->addCaptureValidationMarker(marker)
        ->whenDone(this, [this, generation, markerSerial](
                             bool ok, const QVariantMap &payload, const QString &code,
                             const QString &message) {
            if (markerSerial != m_markerSerial)
                return;
            m_markerBusy = false;
            if (generation != m_generation) {
                Q_EMIT changed();
                return;
            }
            if (!ok) {
                setFailure(code, message);
                return;
            }
            m_feedback = tr("标记已接收");
            applyStatus(payload);
        });
}

} // namespace mr
