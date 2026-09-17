#pragma once

// ---------------------------------------------------------------------------
// The capture button's whole state machine.
//
// It owns four things:
//
//   * the last $defs/CaptureValidationStatus snapshot and whether it is trusted;
//   * the last $defs/ProtocolProfileStatus snapshot and whether it is trusted;
//   * the per-command generation / serial guards;
//   * the projection QML binds to (label, enabled, status text).
//
// Everything it needs from the rest of the application - is a formal capture
// running, what did GetStatus.capture say about the profile, show a toast -
// arrives through Host, so this object can be unit-tested on its own.
// ---------------------------------------------------------------------------

#include <QJsonObject>
#include <QObject>
#include <QPointer>
#include <QString>
#include <QTimer>
#include <QVariantMap>

namespace mr {

class IBackend;

class CaptureValidationController : public QObject
{
    Q_OBJECT

public:
    /// What the state machine needs from the application around it.
    class Host
    {
    public:
        virtual ~Host() = default;
        /// GetStatus.capture.state is RUNNING or DEGRADED.
        virtual bool formalCapturing() const = 0;
        /// Confirmed Collector consent; candidate capture uses the passive ledger path.
        virtual bool candidateValidationEnabled() const { return false; }
        /// GetStatus.capture.profile_status, the second source of truth for
        /// "is this build's protocol profile verified".
        virtual QString captureProfileStatus() const = 0;
        /// Adopt a $defs/CaptureStatus the formal Start/Stop replies returned.
        virtual void applyFormalCaptureStatus(const QVariantMap &capture) = 0;
        virtual void refreshStatus() = 0;
        virtual void showToast(const QString &message) = 0;
    };

    explicit CaptureValidationController(Host *host, QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    // -- projection ---------------------------------------------------------
    QVariantMap status() const { return m_status.toVariantMap(); }
    bool statusLoaded() const { return m_statusLoaded; }
    bool available() const { return m_available; }
    QString state() const;
    bool active() const;
    bool recording() const;
    bool saved() const;
    QString notice() const;
    QString error() const { return m_error; }
    QString feedback() const { return m_feedback; }
    bool commandBusy() const { return m_commandBusy; }
    bool markerBusy() const { return m_markerBusy; }
    bool markerEnabled() const;
    QString actionLabel() const;
    bool actionEnabled() const;
    QString modeStatusText() const;
    QString modeCompactText() const;
    QString adapterId() const { return m_adapterId; }
    void setAdapterId(const QString &adapterId);

    /// $defs/ProtocolProfileStatus, or the last good snapshot while a refresh
    /// is failing transiently.
    QVariantMap protocolProfile() const { return m_profile.toVariantMap(); }
    bool profileLoaded() const { return m_profileLoaded; }
    /// True while the displayed profile snapshot is older than the last
    /// attempt to refresh it.
    bool profileStale() const { return m_profileStale; }
    /// The more conservative of GetProtocolProfileStatus.status and
    /// GetStatus.capture.profile_status. Formal capture is gated on this, so a
    /// build the status object already calls UNSUPPORTED_BUILD can never be
    /// recorded against just because the standalone message is still cached.
    QString effectiveProfileStatus() const;

    // -- commands -----------------------------------------------------------
    /// Refresh GetProtocolProfileStatus. A transient failure keeps the previous
    /// snapshot and schedules a bounded retry instead of disabling the button.
    void refreshProfile();
    /// Refresh GetCaptureValidationStatus.
    void refreshStatusSnapshot();
    void toggle();
    void addMarker(const QString &marker);
    /// Drop every cached snapshot; called when the pipe goes down.
    void reset();

Q_SIGNALS:
    void changed();
    void mutationFailed(const QString &code, const QString &message);

private:
    void startValidation();
    void stopValidation();
    void startFormalCapture();
    void applyStatus(const QVariantMap &payload, bool clearError = true);
    void setFailure(const QString &code, const QString &message);
    void scheduleRetry();
    void clearRetry();

    Host *m_host = nullptr;
    /// A QPointer, for the same reason AppController holds one: a request
    /// failed from IpcClient's destructor runs its callback while the backend
    /// is already going away (review finding M-1).
    QPointer<IBackend> m_backend;

    QJsonObject m_status;
    QJsonObject m_profile;
    QTimer m_pollTimer;
    QString m_adapterId;
    QString m_error;
    QString m_feedback;

    bool m_statusLoaded = false;
    bool m_available = false;
    bool m_statusInFlight = false;
    bool m_commandBusy = false;
    bool m_markerBusy = false;
    bool m_errorFromStatus = false;
    bool m_profileLoaded = false;
    bool m_profileStale = false;
    bool m_profileInFlight = false;
    /// Consecutive transient refresh failures. Drives the backoff and the
    /// terminal "cannot confirm" state, so a wedged Collector is not polled
    /// forever at 1.3 requests per second.
    int m_retryCount = 0;
    bool m_retriesExhausted = false;

    quint64 m_generation = 0;
    quint64 m_statusSerial = 0;
    quint64 m_markerSerial = 0;
    quint64 m_profileSerial = 0;
};

} // namespace mr
