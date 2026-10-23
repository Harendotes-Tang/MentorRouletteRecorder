#pragma once

#include "IBackend.h"
#include "LiveText.h"
#include <QPointer>
#include <QQmlEngine>
#include <QTimer>

namespace mr {
// Owns the normal desktop's confirmed recording capability and incident lifecycle.
// One serialized observation reads settings, validation ownership, then the self-contained
// GetStatus.capture snapshot. Connection generations discard every obsolete callback.
class AutomaticRecordingController : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    Q_PROPERTY(QString state READ state NOTIFY changed)
    Q_PROPERTY(QString message READ message NOTIFY changed)
    /// Changes whenever \ref message starts to say something else, and never
    /// on a language switch alone: the banner remembers what was dismissed by
    /// this number, not by text that a switch re-renders.
    Q_PROPERTY(int messageSerial READ messageSerial NOTIFY changed)
    Q_PROPERTY(bool blocked READ blocked NOTIFY changed)
    /// True whenever \ref message is something the player has to act on: a
    /// confirmed blocker, or a capture that is running and decoding nothing.
    /// Badges bind to this, not to \ref blocked, or the silent case renders in
    /// the same neutral grey as "checking".
    Q_PROPERTY(bool attention READ attention NOTIFY changed)
    /// True while the capture is RUNNING but producing nothing usable. The
    /// state token is "listening_silent"; this is the same fact as a property,
    /// for bindings that only need to know that green is not honest.
    Q_PROPERTY(bool silent READ silent NOTIFY changed)
    /// Automatic path recovery is progress, without an incident or manual retry.
    Q_PROPERTY(bool recovering READ recovering NOTIFY changed)
    /// True while the Collector is relearning this client build's messages.
    /// Deliberately not the same thing as blocked: it raises no alert dialog.
    Q_PROPERTY(bool calibrating READ calibrating NOTIFY changed)
    Q_PROPERTY(bool pendingAlert READ pendingAlert NOTIFY changed)
    Q_PROPERTY(bool retryAvailable READ retryAvailable NOTIFY changed)
public:
    explicit AutomaticRecordingController(IBackend *backend, QObject *parent = nullptr);
    QString state() const { return m_state; }
    QString message() const { return m_message.text(); }
    int messageSerial() const { return m_messageSerial; }
    bool blocked() const { return !m_incident.isEmpty(); }
    bool silent() const { return m_state == QLatin1String("listening_silent"); }
    bool recovering() const { return m_state == QLatin1String("recovering"); }
    /// True in the three 本机校准 projections. Calibration is never an
    /// incident - nothing is broken and there is nothing to acknowledge - but
    /// it does mean no run is being recorded, so the banner has to say so.
    bool calibrating() const
    {
        return m_state == QLatin1String("calibrating")
            || m_state == QLatin1String("calibration_ready")
            || m_state == QLatin1String("calibration_blocked");
    }
    bool attention() const { return blocked() || silent() || calibrating() || m_state == QLatin1String("choosing_game"); }
    bool pendingAlert() const { return m_pending; }
    bool retryAvailable() const { return !recovering() && (!m_followError.isEmpty() || !m_validationError.isEmpty()); }
    void setMaintenance(bool enabled);
    /// 本桌面显式采证期间暂停自动初始化；重连保留意图，旧会话仍按原逻辑清理。
    void setUserObservation(bool enabled);
    /// Current poll period in milliseconds. \ref kIdlePollMs while the game is
    /// not running, \ref kActivePollMs otherwise.
    int pollIntervalMs() const { return m_timer.interval(); }
    /// 本进程接纳捕获事实的序号；连接变化也递增，使旧请求不能覆盖新观察。
    quint64 captureRevision() const { return m_captureRevision; }
    /// 接纳其他已确认的捕获回应。只发布有效的游戏运行事实；不会启动采证或写设置。
    void observeCapture(const QVariantMap &capture);
    /// One observation is three serialized IPC requests, hence the long idle
    /// period: while the game is not running nothing this reads can change except
    /// "the game started", which arrives as a status event anyway.
    static constexpr int kActivePollMs = 2000;
    static constexpr int kIdlePollMs = 10000;
    Q_INVOKABLE void refresh();
    Q_INVOKABLE void retry();
    Q_INVOKABLE void acknowledge();
Q_SIGNALS:
    void changed();
    void incidentRaised();
    void settingsConfirmed(const QVariantMap &settings);
    /// Every capture snapshot this poll read; the calibration card follows it so progress
    /// ticks appear within one poll interval instead of on the next state change.
    void captureObserved(const QVariantMap &capture);
private:
    void readSettings(quint64 generation);
    void readValidation(quint64 generation);
    void readCapture(quint64 generation);
    void finishUnknown(const LiveText &message);
    void project(const QVariantMap &capture);
    /// Empty when the capture is healthy; otherwise the Collector's
    /// silent_reason, or NO_STREAM_OWNERSHIP when it was inferred locally.
    static QString silentReason(const QVariantMap &capture);
    /// Plain-Chinese, actionable text for one silent_reason.
    static QString silentMessage(const QVariantMap &capture, const QString &reason);
    /// The 本机校准 projection for \a capture, or false when that capture is
    /// not calibrating and the ordinary profile rules apply.
    bool projectCalibration(const QVariantMap &capture);
    /// Apply the optional recovery progress or an exhausted episode with no
    /// measured silent reason. Other failure facts keep their existing wording.
    bool projectPathRecovery(const QVariantMap &capture);
    /// How long a RUNNING capture may decode nothing before the desktop says
    /// so on its own. Only used when the Collector reports no silent_reason.
    static constexpr qint64 kSilenceGraceMs = 60000;
    void setState(const QString &state, const LiveText &message);
    /// Poll every \a milliseconds from now on. A no-op when that is already
    /// the period, so an unchanged projection never restarts the timer.
    void setPollInterval(int milliseconds);
    void block(const QString &key, const LiveText &message);
    void clearIncident();
    /// A QPointer: IpcClient fails every request still in flight from its own
    /// destructor, and those callbacks land here while the backend is already
    /// being torn down (review finding M-1).
    QPointer<IBackend> m_backend;
    QTimer m_timer;
    quint64 m_generation = 0;
    quint64 m_captureRevision = 0;
    QVariantMap m_latestCapture;
    bool m_initializationPending = false;
    bool m_busy = false;
    bool m_maintenance = false;
    bool m_userObservation = false;
    bool m_followAttempted = false;
    bool m_stopAttempted = false;
    bool m_stopPending = false;
    bool m_pending = false;
    bool m_followReady = false;
    bool m_validationReady = false;
    // Kept as recipes so a language switch re-renders them (LiveText.h).
    LiveText m_followError;
    LiveText m_validationError;
    QString m_incident;
    QString m_state = QStringLiteral("checking");
    LiveText m_message{[] { return tr("正在检查自动记录能力…"); }};
    int m_messageSerial = 0;
};
}
