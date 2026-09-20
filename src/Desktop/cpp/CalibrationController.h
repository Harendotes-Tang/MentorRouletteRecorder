#pragma once

// ---------------------------------------------------------------------------
// 本机校准 / self-calibration.
//
// A game patch reshuffles opcodes, the shipped profile stops matching and the
// Collector calibrates a new one from the shipped template while the user
// simply plays one roulette. This controller is the desktop half: it projects
// $defs/CalibrationStatus out of the capture status the AppController already
// owns, and it sends the two messages the user's decision produces
// (ConfirmCalibration / DiscardCalibration).
//
// It holds no evidence of its own. Everything it shows arrived inside a
// GetStatus / GetCaptureStatus / CaptureStatusChanged payload, and the only
// thing it ever sends is a list of verdicts the user actually clicked.
// ---------------------------------------------------------------------------

#include "SharedCalibrationController.h"

#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QStringList>
#include <QVariantList>
#include <QVariantMap>

namespace mr {
class IBackend;

class CalibrationController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    /// $defs/CalibrationState: IDLE, OBSERVING, READY, BLOCKED or DONE. IDLE
    /// whenever the Collector sent no calibration object at all, which is the
    /// normal case on a build the shipped profile matches.
    Q_PROPERTY(QString state READ state NOTIFY changed)
    Q_PROPERTY(QString gameBuild READ gameBuild NOTIFY changed)
    /// True while a local profile is already in force AND observation continues: the
    /// Collector could not identify this build's match message, so the profile infers
    /// the match from the player's own queue request and the search goes on underneath
    /// it. Recording works; the card says so instead of saying nothing is recorded.
    Q_PROPERTY(bool provisional READ provisional NOTIFY changed)
    /// Raw player-facing sentences written by the Collector. The card clarifies
    /// known legacy guidance that mistakes retained evidence for current activity.
    Q_PROPERTY(QStringList blockers READ blockers NOTIFY changed)
    Q_PROPERTY(QVariantMap progress READ progress NOTIFY changed)
    /// The timeline in time order, each entry carrying the contract fields plus
    /// \c time_text (local HH:mm) so no view has to parse a timestamp.
    Q_PROPERTY(QVariantList events READ events NOTIFY changed)
    /// How many of those events the user has to rule on.
    Q_PROPERTY(int confirmCount READ confirmCount NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    Q_PROPERTY(QString error READ error NOTIFY changed)
    /// The accepted ConfirmCalibration response: profile_id, profile_path,
    /// bound_in_session. Empty until one is accepted.
    Q_PROPERTY(QVariantMap lastResult READ lastResult NOTIFY changed)
    /// 共享校准: fed from the same capture status, owned by this controller.
    Q_PROPERTY(mr::SharedCalibrationController *shared READ shared CONSTANT)
    /// How a profile that infers the match records, in the card's words. The
    /// shared-calibration consent prompt quotes the same phrase.
    Q_PROPERTY(QString queueInferenceText READ queueInferenceText CONSTANT)

public:
    explicit CalibrationController(QObject *parent = nullptr);

    void setBackend(IBackend *backend);

    QString state() const { return m_state; }
    QString gameBuild() const { return m_gameBuild; }
    bool provisional() const { return m_provisional; }
    QStringList blockers() const { return m_blockers; }
    QVariantMap progress() const { return m_progress; }
    QVariantList events() const { return m_events; }
    int confirmCount() const { return m_confirmCount; }
    bool busy() const { return m_busy; }
    QString error() const { return m_error; }
    QVariantMap lastResult() const { return m_lastResult; }
    SharedCalibrationController *shared() const { return m_shared; }

    /// The one sentence a voided draft is explained with. A constant rather
    /// than a Collector string: ERR_CALIBRATION_REJECTED carries a maintainer
    /// message, and the player only needs to know to play one more roulette.
    static QString rejectedMessage();
    static QString queueInferenceText();

public Q_SLOTS:
    /// Adopt one $defs/CaptureStatus. A payload without a calibration object
    /// resets this controller to IDLE - that is what "not calibrating" is.
    void refreshFromCaptureStatus(const QVariantMap &capture);
    /// \a verdicts maps event_id to "CORRECT" or "WRONG". Only the events that
    /// require confirmation are sent, in timeline order.
    void confirm(const QVariantMap &verdicts);
    void discard();
    /// 重新校准: the same request, asking the Collector to stop using the profile
    /// this machine calibrated as well. The file is kept, renamed; the records it
    /// already made are left exactly as they are.
    void recalibrate();

Q_SIGNALS:
    void changed();
    void confirmed(const QString &profileId, bool boundInSession);
    void rejected(const QString &message);
    /// The profile in force changed, so the capture status has to be re-read.
    void refreshRequested();

private:
    void publish(const QString &state, const QString &gameBuild, const QStringList &blockers,
                 const QVariantMap &progress, const QVariantList &events, bool provisional);
    void sendDiscard(bool retireLocalProfile);

    QPointer<IBackend> m_backend;
    SharedCalibrationController *m_shared = nullptr;
    QString m_state = QStringLiteral("IDLE");
    QString m_gameBuild;
    bool m_provisional = false;
    QStringList m_blockers;
    QVariantMap m_progress;
    QVariantList m_events;
    QVariantMap m_lastResult;
    QString m_error;
    int m_confirmCount = 0;
    bool m_busy = false;
};

} // namespace mr
